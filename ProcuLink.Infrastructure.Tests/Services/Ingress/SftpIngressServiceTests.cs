using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.TestSupport;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Ingress;

/// <summary>
/// Unit tests for <see cref="SftpIngressService"/> using an in-memory DbContext and
/// test-double replacements for SFTP connectivity and order creation.
/// </summary>
public class SftpIngressServiceTests
{
    // ── 1. No config → returns 0, no SFTP attempted ──────────────────────────

    [Fact]
    public async Task NullConfig_ReturnsZero_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new NoOpOrderService();
        var svc = MakeService(db, orders, sftpFactory);

        var orgId = Guid.NewGuid(); // no config seeded for this org
        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0);
        sftpFactory.ConnectCalls.Should().Be(0, "no SFTP connection must be attempted when config is absent");
    }

    // ── 2. Config disabled → returns 0 ───────────────────────────────────────

    [Fact]
    public async Task DisabledConfig_ReturnsZero_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: false);

        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new NoOpOrderService();
        var svc = MakeService(db, orders, sftpFactory);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0);
        sftpFactory.ConnectCalls.Should().Be(0, "disabled config must not trigger a connection");
    }

    // ── 3. Already imported file, UNCHANGED content → skipped ────────────────
    // The genuine-duplicate direction of B-7. It must keep colliding for real: the seeded
    // FileHash is the actual SHA-256 of the bytes the fake serves, asserted below, because
    // this fixture used to seed the literal "aabbcc" and passed anyway — nothing read the
    // column, so the dedupe test never actually collided on content.

    [Fact]
    public async Task AlreadyImportedFile_UnchangedContent_IsSkipped_CountIsZero()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-001.csv";
        var content = "po,date\r\n001,2026-05-28"u8.ToArray();

        // Seed the dedupe record so the service thinks it was already imported.
        db.Set<ImportedSftpFile>().Add(new ImportedSftpFile
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            RemotePath = remotePath,
            FileHash = Sha256Hex(content),
            ImportedAt = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, content);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "already-imported file must not produce a new order stub");
        orders.CreateStubCalls.Should().Be(0);

        // Anti-vacuity: the skip above must be a decision made against the REAL content hash,
        // not a claim row that happens to hold whatever string the fixture felt like.
        var claim = await db.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        claim.FileHash.Should().Be(Sha256Hex(content),
            "the skip must be a genuine content collision — the stored hash IS the hash of the file on the server");
    }

    // ── 4. Phase 1b: no default supplier → file imported UNROUTED, not dropped ──

    [Fact]
    public async Task NoDefaultSupplier_NewFile_IsImportedUnrouted_AndParseEnqueued()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true, createDefaultSupplier: false);

        const string remotePath = "/incoming/po-unrouted.csv";
        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, "header1,header2\r\nval1,val2"u8.ToArray());
        var orders = new RecordingOrderService();
        var enqueuer = new FakeParseJobEnqueuer();
        var svc = MakeService(db, orders, fakeSftp, enqueuer: enqueuer);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(1, "a file arriving with no default supplier must be imported unrouted, not dropped");
        orders.UnroutedStubCalls.Should().Be(1,
            "the unrouted creation path (CreateUnroutedStubAsync) must be used when no supplier is configured");
        orders.SupplierIds.Should().ContainSingle().Which.Should().Be(
            Guid.Empty, "the recording fake tags unrouted stubs with Guid.Empty");

        enqueuer.EnqueuedOrderIds.Should().ContainSingle(
            "the unrouted order must still get a parse job — the parse parks it 'unrouted' for later assignment");
        enqueuer.EnqueuedOrgIds.Should().ContainSingle().Which.Should().Be(orgId);

        var dedupe = await db.Set<ImportedSftpFile>()
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        dedupe.Should().NotBeNull("dedupe record must be written for unrouted imports too, so re-polls stay idempotent");
    }

    // ── 4b. Phase 1b: configured supplier soft-deleted → imported UNROUTED ──

    [Fact]
    public async Task SoftDeletedDefaultSupplier_NewFile_IsImportedUnrouted()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true, softDeleteSupplier: true);

        const string remotePath = "/incoming/po-stale-supplier.csv";
        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, "header1,header2\r\nval1,val2"u8.ToArray());
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(1, "a stale (soft-deleted) default supplier must not drop incoming files");
        orders.UnroutedStubCalls.Should().Be(1,
            "an order must never be routed to a soft-deleted supplier — it goes to the unrouted hold instead");
    }

    // ── 4c. Phase 1b: unrouted mode still respects the dedupe record ────────

    [Fact]
    public async Task NoDefaultSupplier_AlreadyImportedFile_IsSkipped()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true, createDefaultSupplier: false);

        const string remotePath = "/incoming/po-dup.csv";
        var content = "header1,header2\r\nval1,val2"u8.ToArray();
        db.Set<ImportedSftpFile>().Add(new ImportedSftpFile
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            RemotePath = remotePath,
            FileHash = Sha256Hex(content),
            ImportedAt = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, content);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "re-polling the same file in unrouted mode must not duplicate the order");
        orders.UnroutedStubCalls.Should().Be(0);
        orders.CreateStubCalls.Should().Be(0);
    }

    // ── B-7: a claim whose stored hash cannot be compared falls back to path-only ──
    // Defensive: no production writer leaves file_hash blank. Pinned anyway because the
    // fallback direction is the one that matters — an uncomparable hash must SKIP, never
    // re-import, because the cost of guessing wrong is a duplicate supplier delivery.

    [Fact]
    public async Task ClaimWithBlankHash_IsStillSkipped_NeverReImported()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-blank-hash.csv";
        db.Set<ImportedSftpFile>().Add(new ImportedSftpFile
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            RemotePath = remotePath,
            FileHash = "",                       // uncomparable
            ImportedAt = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, "header1,header2\r\nval1,val2"u8.ToArray());
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        (await svc.PollAsync(orgId, default)).Should().Be(0,
            "a claim whose hash cannot be compared must fall back to path-only semantics and SKIP");
        orders.CreateStubCalls.Should().Be(0);
        orders.UnroutedStubCalls.Should().Be(0);
    }

    // ── B-7 duplicate-path half: identical bytes at TWO paths are TWO imports ─────
    // The decision this fix deliberately does NOT make. Content dedupe is scoped to a single
    // remote path; it is not a cross-path content identity. Suppressing the second drop would
    // be a new silent drop of exactly the class B-7 is about.

    [Fact]
    public async Task IdenticalContentAtTwoPaths_ImportsBoth()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        var content = "po,date\r\n7781,2026-08-15"u8.ToArray();
        var fakeSftp = new MultiFileFakeSftpFactory(
            new Dictionary<string, byte[]>
            {
                ["/incoming/po-001.csv"]      = content,
                ["/incoming/po-001-copy.csv"] = content,   // byte-identical, different path
            });
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        (await svc.PollAsync(orgId, default)).Should().Be(2,
            "two paths are two supplier drops — content dedupe is per-path, never cross-path");
        orders.CreateStubCalls.Should().Be(2);

        var claims = await db.Set<ImportedSftpFile>().Where(f => f.OrgId == orgId).ToListAsync();
        claims.Should().HaveCount(2);
        claims.Select(c => c.FileHash).Distinct().Should().ContainSingle(
            "the two claims really do hold the SAME content hash — this is a genuine content " +
            "collision that was deliberately not suppressed, not two files that merely differ");
        claims.Select(c => c.OrderId).Distinct().Should().HaveCount(2, "each drop gets its own order");
    }

    // ── 4. Unsupported extension → skipped ───────────────────────────────────

    [Fact]
    public async Task UnsupportedExtension_IsSkipped_CountIsZero()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/proposal.docx";

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, content: new byte[] { 1, 2, 3 });
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, ".docx is not an accepted extension");
        orders.CreateStubCalls.Should().Be(0, "unsupported file must never reach CreateStubAsync");
    }

    // ── Oversized file → skipped before CreateStubAsync ──────────────────────

    [Fact]
    public async Task OversizedFile_IsSkipped_NeverReachesCreateStub()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/huge.csv";
        var huge = new byte[IngressLimits.MaxFileBytes + 1];

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, huge);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "an oversized file must not be imported");
        orders.CreateStubCalls.Should().Be(0, "oversized file must never reach CreateStubAsync");
    }

    // ── SEC-1: SSRF — private host blocked BEFORE connect ────────────────────

    [Fact]
    public async Task PrivateHost_IsBlockedBySsrfGuard_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        // Private literal IP (RFC-1918) — the strict guard blocks it without any DNS lookup.
        await SeedConfigAsync(db, orgId, isEnabled: true, host: "10.0.0.5");

        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, sftpFactory, StrictGuard());

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "a poll against a private/internal host must be refused");
        sftpFactory.ConnectCalls.Should().Be(0,
            "the SSRF guard must reject the host BEFORE the SFTP factory is ever invoked");
        orders.CreateStubCalls.Should().Be(0);
    }

    // ── SEC-1 / H2: bounded read aborts on a stream that lies about its length ─

    [Fact]
    public async Task LyingOversizedStream_IsAbortedMidCopy_NeverReachesCreateStub()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/lying.csv";
        // A stream that yields cap + 1 MB of bytes regardless of any declared size — the
        // bounded read must abort at cap+1 instead of materializing the whole thing.
        var lyingFactory = new LyingStreamSftpFactory(remotePath, totalBytes: IngressLimits.MaxFileBytes + 1_048_576);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, lyingFactory);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "a file exceeding the cap must be skipped, not imported");
        orders.CreateStubCalls.Should().Be(0, "the oversized file must never reach CreateStubAsync");
    }

    // ── 5. Happy path: new CSV file → imported, dedupe record written, parse job enqueued ──

    [Fact]
    public async Task NewCsvFile_IsImported_DedupeRecordWrittenAndCountIsOne()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var supplierId = await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-new.csv";
        var csvBytes = "header1,header2\r\nval1,val2"u8.ToArray();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, csvBytes);
        var orders = new RecordingOrderService();
        var enqueuer = new FakeParseJobEnqueuer();
        var svc = MakeService(db, orders, fakeSftp, enqueuer: enqueuer);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(1, "one new CSV file should produce an import count of 1");
        orders.CreateStubCalls.Should().Be(1, "one stub must be created for the new file");
        orders.SupplierIds.Should().ContainSingle().Which.Should().Be(
            supplierId!.Value,
            "SFTP pull imports must be assigned to the configured supplier, not Guid.Empty");

        enqueuer.EnqueuedOrderIds.Should().ContainSingle(
            "a parse job must be enqueued for the imported order");
        enqueuer.EnqueuedOrgIds.Should().ContainSingle().Which.Should().Be(
            orgId, "parse job must be scoped to the correct org");

        var dedupe = await db.Set<ImportedSftpFile>()
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);

        dedupe.Should().NotBeNull("a dedupe record must be written after successful import");
        dedupe!.FileHash.Should().NotBeNullOrEmpty("SHA-256 hash must be stored");
    }

    // ── Claim-first: the dedupe ledger row is committed BEFORE CreateStubAsync ──
    // Regression guard for the duplicate-PO reliability class. The old order was
    // CreateStub (self-commits the order + fires order.created) THEN write the ledger row,
    // so a Hangfire retry / concurrent same-org poll landing in that window re-imported the
    // file as a DUPLICATE order. Claim-first inverts it: the ledger row is the FIRST durable
    // write. This probe asserts the ledger row is already committed at the instant
    // CreateStubAsync is invoked.

    [Fact]
    public async Task NewFile_LedgerClaimIsCommittedBeforeCreateStub()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-claim-first.csv";
        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, "header1,header2\r\nval1,val2"u8.ToArray());
        var probe = new LedgerProbingOrderService(db);
        var svc = MakeService(db, probe, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(1);
        probe.CreateStubCalls.Should().Be(1);
        probe.LedgerRowPresentAtStubTime.Should().BeTrue(
            "claim-first: the (OrgId, RemotePath) dedupe ledger row must be committed BEFORE CreateStubAsync " +
            "so a retry or concurrent same-org poll cannot create a duplicate order");
    }

    // ── SSH host-key verification (WP-38) ────────────────────────────────────
    // Before this, the poller connected to, listed and imported files from ANY server that answered
    // on the configured host and port with the configured password. Proven live against a throwaway
    // OpenSSH 10.3p1 container whose host key was swapped mid-experiment: same result, no warning,
    // no log line (docs/ops/2026-08-01-wp38-delivery-channel-proof.md §1).

    private static readonly byte[] ServerKeyA = "sftp-ingress-host-key-A"u8.ToArray();
    private static readonly byte[] ServerKeyB = "sftp-ingress-host-key-B"u8.ToArray();

    [Fact]
    public async Task FirstPoll_RecordsTheServersHostKey()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        var fakeSftp = new HostKeyFakeSftpFactory(ServerKeyA, "/incoming/po.csv", "a,b\r\n1,2"u8.ToArray());
        var svc = MakeService(db, new RecordingOrderService(), fakeSftp);

        await svc.PollAsync(orgId, default);

        var stored = await db.Set<SftpIngressConfig>().FirstAsync(c => c.OrgId == orgId);
        stored.HostKeyFingerprints.Should().Be(
            SshHostKeyPolicy.Fingerprint(ServerKeyA),
            "trust-on-first-use: the very first poll must pin whatever the server presented, so every " +
            "poll after it is verified — otherwise nothing is ever protected without an operator visit");
    }

    /// <summary>
    /// The whole packet, on the ingress path: same host, same port, same username, same password —
    /// a different server identity. Before this it imported the files anyway.
    /// </summary>
    [Fact]
    public async Task AChangedHostKey_RefusesToPoll_AndImportsNothing()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(
            db, orgId, isEnabled: true,
            hostKeyFingerprints: SshHostKeyPolicy.Fingerprint(ServerKeyA));

        const string remotePath = "/incoming/po-from-a-stranger.csv";
        var fakeSftp = new HostKeyFakeSftpFactory(ServerKeyB, remotePath, "a,b\r\n1,2"u8.ToArray());
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var act = async () => await svc.PollAsync(orgId, default);

        (await act.Should().ThrowAsync<SshHostKeyRejectedException>())
            .Which.Observed.Should().Be(SshHostKeyPolicy.Fingerprint(ServerKeyB));

        orders.CreateStubCalls.Should().Be(0, "no file may be imported from a server we could not identify");
        (await db.Set<ImportedSftpFile>().CountAsync(f => f.OrgId == orgId))
            .Should().Be(0, "and nothing may be recorded as imported either");
    }

    /// <summary>
    /// A refusal must not pin what it refused. If it did, the second attempt would sail through and
    /// the feature would disarm itself on first contact with the thing it exists to catch.
    /// </summary>
    [Fact]
    public async Task AChangedHostKey_DoesNotRepinTheNewKey()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var pinned = SshHostKeyPolicy.Fingerprint(ServerKeyA);
        await SeedConfigAsync(db, orgId, isEnabled: true, hostKeyFingerprints: pinned);

        var fakeSftp = new HostKeyFakeSftpFactory(ServerKeyB, "/incoming/po.csv", "a,b\r\n1,2"u8.ToArray());
        var svc = MakeService(db, new RecordingOrderService(), fakeSftp);

        try { await svc.PollAsync(orgId, default); } catch (SshHostKeyRejectedException) { /* expected */ }

        var stored = await db.Set<SftpIngressConfig>().FirstAsync(c => c.OrgId == orgId);
        stored.HostKeyFingerprints.Should().Be(pinned);
    }

    [Fact]
    public async Task ThePinnedServer_StillPolls()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(
            db, orgId, isEnabled: true,
            hostKeyFingerprints: SshHostKeyPolicy.Fingerprint(ServerKeyA));

        var fakeSftp = new HostKeyFakeSftpFactory(ServerKeyA, "/incoming/po.csv", "a,b\r\n1,2"u8.ToArray());
        var svc = MakeService(db, new RecordingOrderService(), fakeSftp);

        (await svc.PollAsync(orgId, default)).Should().Be(1,
            "verification must not break the connections it is protecting");
    }

    [Fact]
    public async Task AnyKeyFromAPinnedSetIsAccepted()
    {
        // A supplier behind a load balancer legitimately answers with more than one host key.
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(
            db, orgId, isEnabled: true,
            hostKeyFingerprints:
                $"{SshHostKeyPolicy.Fingerprint(ServerKeyA)}\n{SshHostKeyPolicy.Fingerprint(ServerKeyB)}");

        var fakeSftp = new HostKeyFakeSftpFactory(ServerKeyB, "/incoming/po.csv", "a,b\r\n1,2"u8.ToArray());
        var svc = MakeService(db, new RecordingOrderService(), fakeSftp);

        (await svc.PollAsync(orgId, default)).Should().Be(1);
    }

    // ── LIVE: real SFTP poll against a real SFTP server ──────────────────────
    // Gated behind PROCULINK_LIVE_ENDPOINT_TESTS=1; connects to a real SFTP
    // server (env PROCULINK_LIVE_SFTP_*) with the PRODUCTION RenciSftpClientFactory,
    // lists + downloads a real PO file, and imports it via the in-memory DbContext.
    [EnvironmentGatedFact(
        "requires a live SFTP server holding a PO file to poll",
        LiveTestEnvironment.EndpointOptIn,
        "PROCULINK_LIVE_SFTP_HOST", "PROCULINK_LIVE_SFTP_USER", "PROCULINK_LIVE_SFTP_PASS")]
    [Trait("Category", "LiveEndpoint")]
    public async Task Live_SftpIngress_RealPollImportsFile()
    {
        var host = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_HOST")!;

        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var encConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        var encryption = new DeliveryEncryptionService(encConfig);

        db.Set<Supplier>().Add(new Supplier
        { Id = supplierId, OrgId = orgId, Name = "Live SFTP supplier", CreatedAt = DateTime.UtcNow });
        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Host = host,
            Port = int.TryParse(Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_PORT"), out var p) ? p : 22,
            Username = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_USER") ?? "",
            EncryptedPassword = encryption.Encrypt(
                Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_PASS") ?? "",
                CredentialScope.ForOrg(orgId, CredentialPurpose.OrgIngressSftpPassword)),
            RemoteDirectory = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_INGEST_DIR") ?? "/upload",
            DefaultSupplierId = supplierId,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, new RenciSftpClientFactory(), enqueuer: new FakeParseJobEnqueuer());

        var imported = await svc.PollAsync(orgId, default);

        imported.Should().BeGreaterThanOrEqualTo(1, "the real SFTP poll should import at least one PO file from the server");
        orders.CreateStubCalls.Should().BeGreaterThanOrEqualTo(1);
        orders.SupplierIds.Should().Contain(supplierId, "imports must be routed to the configured default supplier");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The exact digest <c>SftpIngressService</c> stores and compares — same algorithm, same
    /// casing. Tests seed this, never a literal, so a dedupe fixture cannot go vacuous again.
    /// </summary>
    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static SftpIngressService MakeService(
        ProcuLinkDbContext db,
        IStubOrderCreator orders,
        ISftpClientFactory sftpFactory,
        OutboundRequestGuard? guard = null,
        IParseJobEnqueuer? enqueuer = null)
    {
        // DeliveryEncryptionService requires a real 32-byte key.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var encryption = new DeliveryEncryptionService(config);

        return new SftpIngressService(
            db,
            orders,
            enqueuer ?? new FakeParseJobEnqueuer(),
            encryption,
            sftpFactory,
            // Default guard allows private targets so the fixture host (sftp.example.com / a
            // private literal) passes the network-range check without a real DNS lookup — the
            // SSRF-blocked test below supplies a strict (flag=false) guard explicitly.
            guard ?? AllowPrivateGuard(),
            NullLogger<SftpIngressService>.Instance);
    }

    /// <summary>Guard with AllowPrivateNetworkTargets=true — skips range validation (no DNS).</summary>
    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    /// <summary>Strict guard (flag=false) — enforces the SSRF network-range blocklist.</summary>
    private static OutboundRequestGuard StrictGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "false" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private static async Task<Guid?> SeedConfigAsync(
        ProcuLinkDbContext db,
        Guid orgId,
        bool isEnabled,
        bool createDefaultSupplier = true,
        string host = "sftp.example.com",
        bool softDeleteSupplier = false,
        string? hostKeyFingerprints = null)
    {
        // The password is the empty string encrypted with the all-zero 32-byte key.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var encryption = new DeliveryEncryptionService(config);

        Guid? supplierId = null;
        if (createDefaultSupplier)
        {
            supplierId = Guid.NewGuid();
            db.Set<Supplier>().Add(new Supplier
            {
                Id = supplierId.Value,
                OrgId = orgId,
                Name = "SFTP supplier",
                CreatedAt = DateTime.UtcNow,
                DeletedAt = softDeleteSupplier ? DateTime.UtcNow.AddMinutes(-5) : null,
            });
        }

        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Host = host,
            Port = 22,
            Username = "testuser",
            EncryptedPassword = encryption.Encrypt(
                "hunter2", CredentialScope.ForOrg(orgId, CredentialPurpose.OrgIngressSftpPassword)),
            RemoteDirectory = "/incoming",
            DefaultSupplierId = supplierId,
            IsEnabled = isEnabled,
            HostKeyFingerprints = hostKeyFingerprints,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SftpIngressTestDbContext(options);
    }

    // ── Test-double SFTP factory ──────────────────────────────────────────────

    /// <summary>Counts Connect() calls; never returns a session (returns no-op).</summary>
    private sealed class RecordingFakeSftpFactory : ISftpClientFactory
    {
        public int ConnectCalls { get; private set; }

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
        {
            ConnectCalls++;
            return new EmptySftpSession();
        }
    }

    /// <summary>
    /// SFTP factory that behaves like a real server: it STATES AN IDENTITY before handing over a
    /// session, and refuses when the verifier says no.
    ///
    /// <para>
    /// The two lines in <see cref="Connect"/> are the same two, in the same order, that
    /// <c>RenciSftpClientFactory</c> runs — <c>Observe</c> is the real policy code, not a
    /// re-implementation of it, so a decision this fake gets right is a decision production gets
    /// right. What it cannot prove is the SSH.NET subscription itself; that is what the live test at
    /// the top of this file exists for.
    /// </para>
    /// </summary>
    private sealed class HostKeyFakeSftpFactory : ISftpClientFactory
    {
        private readonly byte[] _hostKeyBlob;
        private readonly string _remotePath;
        private readonly byte[] _content;

        public HostKeyFakeSftpFactory(byte[] hostKeyBlob, string remotePath, byte[] content)
        {
            _hostKeyBlob = hostKeyBlob;
            _remotePath = remotePath;
            _content = content;
        }

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
        {
            if (!verifier.Observe(_hostKeyBlob)) verifier.ThrowIfRejected();
            return new SingleFileSftpSession(_remotePath, _content);
        }
    }

    /// <summary>SFTP factory that presents a single file to the service.</summary>
    private sealed class SingleFileFakeSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        private readonly byte[] _content;

        public SingleFileFakeSftpFactory(string remotePath, byte[] content)
        {
            _remotePath = remotePath;
            _content = content;
        }

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
            => new SingleFileSftpSession(_remotePath, _content);
    }

    /// <summary>SFTP factory presenting several paths, each with its own content.</summary>
    private sealed class MultiFileFakeSftpFactory : ISftpClientFactory
    {
        private readonly IReadOnlyDictionary<string, byte[]> _files;

        public MultiFileFakeSftpFactory(IReadOnlyDictionary<string, byte[]> files) => _files = files;

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
            => new MultiFileSftpSession(_files);
    }

    private sealed class MultiFileSftpSession : ISftpSession
    {
        private readonly IReadOnlyDictionary<string, byte[]> _files;

        public MultiFileSftpSession(IReadOnlyDictionary<string, byte[]> files) => _files = files;

        public IEnumerable<string> ListFileNames(string remoteDirectory) => _files.Keys;
        public MemoryStream DownloadFile(string remotePath) => new(_files[remotePath]);
        public Stream OpenRead(string remotePath) => new MemoryStream(_files[remotePath]);
        public void Dispose() { }
    }

    private sealed class EmptySftpSession : ISftpSession
    {
        public IEnumerable<string> ListFileNames(string remoteDirectory)
            => Enumerable.Empty<string>();

        public MemoryStream DownloadFile(string remotePath)
            => new MemoryStream();

        public Stream OpenRead(string remotePath)
            => new MemoryStream();

        public void Dispose() { }
    }

    /// <summary>
    /// SFTP factory whose <c>OpenRead</c> returns a forward-only stream that yields
    /// <c>totalBytes</c> bytes — used to prove the bounded read aborts at cap+1 instead
    /// of buffering the whole (oversized) file (H2 regression).
    /// </summary>
    private sealed class LyingStreamSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        private readonly long _totalBytes;

        public LyingStreamSftpFactory(string remotePath, long totalBytes)
        {
            _remotePath = remotePath;
            _totalBytes = totalBytes;
        }

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
            => new LyingStreamSftpSession(_remotePath, _totalBytes);
    }

    private sealed class LyingStreamSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly long _totalBytes;

        public LyingStreamSftpSession(string remotePath, long totalBytes)
        {
            _remotePath = remotePath;
            _totalBytes = totalBytes;
        }

        public IEnumerable<string> ListFileNames(string remoteDirectory) => new[] { _remotePath };
        public MemoryStream DownloadFile(string remotePath) => throw new NotSupportedException();
        public Stream OpenRead(string remotePath) => new EndlessForwardStream(_totalBytes);
        public void Dispose() { }
    }

    /// <summary>Forward-only read stream that produces <c>length</c> zero bytes; not seekable.</summary>
    private sealed class EndlessForwardStream : Stream
    {
        private readonly long _length;
        private long _produced;

        public EndlessForwardStream(long length) => _length = length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _produced;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            _produced += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SingleFileSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly byte[] _content;

        public SingleFileSftpSession(string remotePath, byte[] content)
        {
            _remotePath = remotePath;
            _content = content;
        }

        public IEnumerable<string> ListFileNames(string remoteDirectory)
            => new[] { _remotePath };

        public MemoryStream DownloadFile(string remotePath)
        {
            var ms = new MemoryStream(_content);
            ms.Position = 0;
            return ms;
        }

        public Stream OpenRead(string remotePath)
            => new MemoryStream(_content);

        public void Dispose() { }
    }

    // ── Test-double parse-job enqueuer ────────────────────────────────────────

    private sealed class FakeParseJobEnqueuer : IParseJobEnqueuer
    {
        public List<Guid> EnqueuedOrderIds { get; } = new();
        public List<Guid> EnqueuedOrgIds   { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            EnqueuedOrderIds.Add(orderId);
            EnqueuedOrgIds.Add(orgId);
            return Task.CompletedTask;
        }
    }

    // ── Test-double order-stub creators (IStubOrderCreator; explicit order id) ─────────────────

    private sealed class NoOpOrderService : IStubOrderCreator
    {
        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException("NoOpOrderService must not be called.");

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException("NoOpOrderService must not be called.");
    }

    /// <summary>
    /// Order-stub creator double that, at the moment CreateStub/CreateUnroutedStub is invoked,
    /// records whether an <see cref="ImportedSftpFile"/> row for the org is already committed.
    /// Proves the claim-first ordering (ledger committed BEFORE order creation).
    /// </summary>
    private sealed class LedgerProbingOrderService : IStubOrderCreator
    {
        private readonly ProcuLinkDbContext _db;
        public LedgerProbingOrderService(ProcuLinkDbContext db) => _db = db;

        public int CreateStubCalls { get; private set; }
        public bool? LedgerRowPresentAtStubTime { get; private set; }

        private void Probe(Guid orgId)
        {
            CreateStubCalls++;
            LedgerRowPresentAtStubTime = _db.Set<ImportedSftpFile>().Any(f => f.OrgId == orgId);
        }

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            Probe(organisationId);
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = organisationId, SupplierId = supplierId, Status = "parsing",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }));
        }

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        {
            Probe(organisationId);
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = organisationId, SupplierId = null, Status = "parsing",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }));
        }
    }

    private sealed class RecordingOrderService : IStubOrderCreator
    {
        public int CreateStubCalls { get; private set; }
        public int UnroutedStubCalls { get; private set; }
        public List<Guid> SupplierIds { get; } = new();

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            CreateStubCalls++;
            SupplierIds.Add(supplierId);
            var stub = new PurchaseOrderEntity
            {
                Id = orderId,
                OrgId = organisationId,
                SupplierId = supplierId,
                Status = "parsing",
                SourceFileKey = $"{organisationId}/{orderId}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        // Unrouted hold path — counted separately, and records a Guid.Empty supplier
        // in SupplierIds so tests can assert which path was used.
        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        {
            UnroutedStubCalls++;
            SupplierIds.Add(Guid.Empty);
            var stub = new PurchaseOrderEntity
            {
                Id = orderId,
                OrgId = organisationId,
                SupplierId = null,
                Status = "parsing",
                SourceFileKey = $"{organisationId}/{orderId}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }
    }

    // ── In-memory DbContext ───────────────────────────────────────────────────

    /// <summary>
    /// Minimal in-memory DbContext that materialises only what the SFTP ingress
    /// service touches: SftpIngressConfig and ImportedSftpFile.
    /// Other entities are ignored to avoid fabricating unnecessary fixtures,
    /// following the same <c>modelBuilder.Ignore&lt;T&gt;()</c> pattern used by
    /// <c>InboundEmailTestDbContext</c>.
    /// </summary>
    private sealed class SftpIngressTestDbContext : ProcuLinkDbContext
    {
        public SftpIngressTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            // Only materialise the two new entities.
            modelBuilder.Entity<SftpIngressConfig>(b =>
            {
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<ImportedSftpFile>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.OrgId, x.RemotePath }).IsUnique();
            });
        }
    }
}
