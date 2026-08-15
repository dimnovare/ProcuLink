using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves the claim-first + resume-on-conflict SFTP ingress on REAL Postgres, where the
/// (OrgId, RemotePath) unique index is actually enforced (EF InMemory ignores it):
/// <list type="bullet">
/// <item><description>a re-poll / Hangfire retry of an already-imported file creates NO second
/// order (the committed claim's order exists → skip);</description></item>
/// <item><description>many concurrent same-org polls of one file create EXACTLY ONE order — losers
/// hit the unique-index claim (23505) and skip;</description></item>
/// <item><description>a TRANSIENT stub failure after the claim does NOT lose the order — a retry
/// RESUMES and imports it (the rework's core fix);</description></item>
/// <item><description>an order committed but whose post-step crashed is NOT re-created on retry
/// (existence-by-id skips);</description></item>
/// <item><description>a genuinely-permanent-bad file is bounded (marked terminal, not retried
/// forever).</description></item>
/// </list>
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SftpIngressClaimFirstPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_sftp_cf");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private async Task<Guid> SeedOrgSupplierConfigAsync(string remoteDir)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_sftp_{orgId:N}", Name = "SFTP CF Org",
            Slug = $"sftp-cf-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "SFTP Supplier", CreatedAt = now });
        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, Host = "sftp.example.com", Port = 22, Username = "u",
            EncryptedPassword = Enc().Encrypt(
                "pw", CredentialScope.ForOrg(orgId, CredentialPurpose.OrgIngressSftpPassword)),
            RemoteDirectory = remoteDir, DefaultSupplierId = supplierId,
            IsEnabled = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private SftpIngressService NewService(ProcuLinkDbContext db, CountingOrderService orders, IParseJobEnqueuer enqueuer, ISftpClientFactory factory) =>
        new(db, orders, enqueuer, Enc(), factory, AllowPrivateGuard(), NullLogger<SftpIngressService>.Instance);

    private async Task<int> OrderCountAsync(Guid orgId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        return await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId);
    }

    /// <summary>
    /// The exact digest <c>SftpIngressService</c> stores and compares — same algorithm, same
    /// casing. Derived, never a literal, so a dedupe fixture cannot silently stop colliding.
    /// </summary>
    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    [DockerRequiredFact]
    public async Task RePoll_AfterImport_CreatesNoSecondOrder()
    {
        const string remotePath = "/incoming/po-retry.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the first poll imports the new file");

        // Second poll = a Hangfire retry / next scheduled poll with the same file still present.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the committed claim's order from poll 1 must make the re-poll a no-op");

        orders.TotalCreates.Should().Be(1, "the file must produce exactly ONE order stub across re-polls");
        enqueuer.Count.Should().Be(1);
        (await OrderCountAsync(orgId)).Should().Be(1, "exactly ONE purchase order exists across re-polls");

        await using var verify = new ProcuLinkDbContext(_options!);
        var ledger = await verify.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        // Anti-vacuity for the content-aware dedupe: the skip above must be a REAL content
        // collision. Before B-7 the stored hash was never compared, so this test would have
        // passed with any string at all in the column.
        ledger.FileHash.Should().Be(Sha256Hex("h1,h2\r\nv1,v2"u8.ToArray()),
            "the ledger holds the true SHA-256 of the file on the server, so the re-poll skip is a genuine hash match");
    }

    // ── B-7: a CORRECTED re-send at an already-imported path must IMPORT ─────
    // "They corrected the PO and re-sent it, and it never arrived." Dedupe was path-only:
    // same path → claim satisfied → `continue`. Never imported, no audit row, no retry, no
    // notification. Mirrors S3IngressService's ETag-change re-claim.

    [DockerRequiredFact]
    public async Task CorrectedResend_SamePath_ChangedContent_IsImported_NotSilentlyDropped()
    {
        const string remotePath = "/incoming/po-001.csv";
        var original  = "po,qty\r\n7781,100"u8.ToArray();
        var corrected = "po,qty\r\n7781,120"u8.ToArray();   // same path, ONE digit different

        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, original);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the first poll imports the original PO");

        Guid originalOrderId;
        await using (var afterFirst = new ProcuLinkDbContext(_options!))
        {
            var claim = await afterFirst.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
            claim.FileHash.Should().Be(Sha256Hex(original), "the claim records the original bytes");
            originalOrderId = claim.OrderId;
        }

        // The supplier corrects the PO and overwrites the SAME remote path.
        factory.Content = corrected;
        Sha256Hex(corrected).Should().NotBe(Sha256Hex(original),
            "the two versions must genuinely differ — a dedupe test that never collides proves nothing");

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "a corrected re-send at an already-seen path is a NEW order, not a duplicate");

        (await OrderCountAsync(orgId)).Should().Be(2,
            "both the original and the correction exist — the correction is not silently dropped, and the " +
            "original is not destroyed behind the operator's back");
        orders.TotalCreates.Should().Be(2);
        enqueuer.Count.Should().Be(2, "the corrected order gets its own parse job");

        await using var verify = new ProcuLinkDbContext(_options!);
        var updated = await verify.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        updated.FileHash.Should().Be(Sha256Hex(corrected),
            "the claim is UPDATED in place to the new content — (OrgId, RemotePath) is unique, so a second row is impossible");
        updated.OrderId.Should().NotBe(originalOrderId, "the re-send is claimed under a FRESH pre-generated order id");
        (await verify.PurchaseOrders.AnyAsync(o => o.Id == updated.OrderId)).Should().BeTrue();
        (await verify.PurchaseOrders.AnyAsync(o => o.Id == originalOrderId)).Should().BeTrue();
    }

    // ── B-7: a corrected file at a TERMINAL claim's path must import too ─────
    // "We rejected the file that used to be at this path" must not become "we will never look
    // at this path again". A permanently-bad file the supplier then FIXED is the same lost order.

    [DockerRequiredFact]
    public async Task TerminalClaim_ThenCorrectedFile_IsImported()
    {
        const string remotePath = "/incoming/po-was-bad.csv";
        var bad = "h1,h2\r\nbad,bad"u8.ToArray();
        var fixedUp = "h1,h2\r\ngood,good"u8.ToArray();

        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var behavior = StubBehavior.FailPermanent;
        var orders = new CountingOrderService(_options!, () => behavior);
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, bad);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the permanently-bad file imports nothing and its claim is marked terminal");

        await using (var afterBad = new ProcuLinkDbContext(_options!))
            (await afterBad.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath))
                .OrderId.Should().Be(Guid.Empty, "precondition: the claim really is terminal");

        // Re-polling the SAME bad file must still be bounded — terminal means terminal for it.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0);
        orders.TotalCreates.Should().Be(1, "the unchanged bad file is attempted ONCE, then bounded");

        // The supplier fixes the file and re-drops it at the same path.
        behavior = StubBehavior.Succeed;
        factory.Content = fixedUp;

        await using (var db3 = new ProcuLinkDbContext(_options!))
            (await NewService(db3, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "a terminal claim bounds the BAD FILE, not the PATH — the corrected file must import");

        (await OrderCountAsync(orgId)).Should().Be(1, "the corrected file produced the order the bad one never could");
        enqueuer.Count.Should().Be(1);

        await using var verify = new ProcuLinkDbContext(_options!);
        var claim = await verify.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        claim.FileHash.Should().Be(Sha256Hex(fixedUp));
        claim.OrderId.Should().NotBe(Guid.Empty, "the terminal marker is replaced by the corrected file's real order id");
    }

    // ── B-7 duplicate-path half: identical bytes at TWO paths are TWO imports ──
    // Deliberately NOT suppressed. See the claim-first comment in SftpIngressService.

    [DockerRequiredFact]
    public async Task IdenticalContentAtTwoPaths_ImportsBoth()
    {
        var content = "h1,h2\r\nsame,same"u8.ToArray();
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var factory = new TwoFileSftpFactory("/incoming/po-001.csv", "/incoming/po-001-copy.csv", content);

        await using (var db = new ProcuLinkDbContext(_options!))
            (await NewService(db, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(2, "two remote paths are two supplier drops — content dedupe is per-path, never cross-path");

        (await OrderCountAsync(orgId)).Should().Be(2);

        await using var verify = new ProcuLinkDbContext(_options!);
        var claims = await verify.Set<ImportedSftpFile>().Where(f => f.OrgId == orgId).ToListAsync();
        claims.Should().HaveCount(2, "one ledger row per path");
        claims.Select(c => c.FileHash).Distinct().Should().ContainSingle(
            "both rows hold the SAME content hash — a genuine content collision that was deliberately not suppressed");
    }

    [DockerRequiredFact]
    public async Task ConcurrentPolls_OfSameFile_CreateExactlyOneOrder()
    {
        const int workers = 8;
        const string remotePath = "/incoming/po-concurrent.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService(_options!);   // shared across all polls
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            return await NewService(db, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None);
        });
        var results = await Task.WhenAll(tasks);

        results.Sum().Should().BeGreaterThanOrEqualTo(1, "at least one concurrent poll imports the file");
        // The real invariant: find-or-create on the order primary key means exactly ONE order exists,
        // no matter how the concurrent claim/resume race interleaves.
        (await OrderCountAsync(orgId)).Should().Be(1,
            "concurrent polls of the same file must create EXACTLY ONE purchase order");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.Set<ImportedSftpFile>().CountAsync(f => f.OrgId == orgId && f.RemotePath == remotePath))
            .Should().Be(1, "exactly one dedupe ledger row survives the concurrent race");
    }

    [DockerRequiredFact]
    public async Task TransientStubFailure_ThenRetry_ImportsOrder_NotLost()
    {
        const string remotePath = "/incoming/po-transient.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());

        // First stub-creation attempt fails transiently (an R2/DB blip / SIGTERM after the claim
        // commits); every later attempt succeeds.
        var attempt = 0;
        var orders = new CountingOrderService(_options!,
            () => Interlocked.Increment(ref attempt) == 1 ? StubBehavior.ThrowTransient : StubBehavior.Succeed);
        var enqueuer = new CountingParseEnqueuer();

        // Poll 1: the claim commits, then the stub throws — the poll fails (Hangfire would retry).
        await using (var db1 = new ProcuLinkDbContext(_options!))
        {
            var svc = NewService(db1, orders, enqueuer, factory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PollAsync(orgId, CancellationToken.None));
        }

        // The claim is committed with its pre-generated order id, but NO order exists yet.
        await using (var afterFail = new ProcuLinkDbContext(_options!))
        {
            var claim = await afterFail.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
            claim.OrderId.Should().NotBe(Guid.Empty, "the claim carries a real pre-generated order id to resume under");
            (await afterFail.PurchaseOrders.CountAsync(o => o.OrgId == orgId))
                .Should().Be(0, "the transient failure means the order was never created");
        }

        // Poll 2 = the Hangfire retry: it must RESUME and import the order — not silently skip it.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the retry must RESUME the abandoned claim and import the order (never lost)");

        (await OrderCountAsync(orgId)).Should().Be(1, "the order is eventually created exactly once — not lost");
        enqueuer.Count.Should().Be(1, "the parse job is enqueued once, on the successful resume");

        await using var verify = new ProcuLinkDbContext(_options!);
        var finalClaim = await verify.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        (await verify.PurchaseOrders.AnyAsync(o => o.Id == finalClaim.OrderId))
            .Should().BeTrue("the resumed order was created under the claim's pre-generated id");
    }

    [DockerRequiredFact]
    public async Task OrderCommittedButPostStepCrashed_Retry_CreatesNoSecondOrder()
    {
        const string remotePath = "/incoming/po-poststep.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());
        var orders = new CountingOrderService(_options!);   // always succeeds (commits the order)

        // Poll 1: the order commits, then the parse-enqueue step throws (models a SIGTERM AFTER the
        // order was committed but BEFORE the claim was known complete) — the naive "resume when
        // claim has no completed marker" would duplicate here.
        await using (var db1 = new ProcuLinkDbContext(_options!))
        {
            var svc = NewService(db1, orders, new ThrowingParseEnqueuer(), factory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PollAsync(orgId, CancellationToken.None));
        }
        (await OrderCountAsync(orgId)).Should().Be(1, "the order WAS committed before the post-step crashed");

        // Poll 2 = the retry with a working enqueuer: the order already exists under the claim's id →
        // it must SKIP, not create a second order.
        var enqueuer = new CountingParseEnqueuer();
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the existing order (by id) must make the retry a no-op — no duplicate");

        (await OrderCountAsync(orgId)).Should().Be(1, "still exactly ONE order — the retry created no duplicate");
        orders.TotalCreates.Should().Be(1, "the retry must not call stub creation again for the completed order");
    }

    [DockerRequiredFact]
    public async Task PermanentlyBadFile_IsBounded_NotRetriedForever()
    {
        const string remotePath = "/incoming/po-bad.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());
        var orders = new CountingOrderService(_options!, () => StubBehavior.FailPermanent);   // always Result.Fail
        var enqueuer = new CountingParseEnqueuer();

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "a permanently-bad file imports nothing");

        // Second poll: the claim was marked terminal, so the bad file is NOT re-attempted.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0);

        orders.TotalCreates.Should().Be(1, "a genuinely-bad file is attempted ONCE, then bounded — never retried forever");
        (await OrderCountAsync(orgId)).Should().Be(0, "no order is ever created for a permanently-bad file");

        await using var verify = new ProcuLinkDbContext(_options!);
        var claim = await verify.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        claim.OrderId.Should().Be(Guid.Empty, "the claim is marked terminal (Guid.Empty) after the permanent failure");
    }

    [DockerRequiredFact]
    public async Task ErasedOrder_IsNotResurrected_OnNextPoll()
    {
        const string remotePath = "/incoming/po-erase.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());

        // Poll 1: import the file → one order + one ledger row (OrderId = the created order).
        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1);

        Guid erasedOrderId;
        await using (var v = new ProcuLinkDbContext(_options!))
            erasedOrderId = (await v.PurchaseOrders.SingleAsync(o => o.OrgId == orgId)).Id;

        // GDPR / bulk erase removes the order. The source file still lives on the customer's SFTP
        // (ProcuLink never deletes it), so the ledger row MUST be tombstoned, not left dangling.
        await using (var eraseDb = new ProcuLinkDbContext(_options!))
        {
            var erase = new DataErasureService(eraseDb, new NoOpFileStorage(), NullLogger<DataErasureService>.Instance);
            (await erase.EraseOrderAsync(orgId, erasedOrderId, CancellationToken.None)).Found.Should().BeTrue();
        }
        (await OrderCountAsync(orgId)).Should().Be(0, "the order was erased");

        // Poll 2: the same file is STILL on the SFTP server. It must NOT resurrect the erased order.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "an erased order must never be resurrected by a re-poll of the surviving source file");

        (await OrderCountAsync(orgId)).Should().Be(0,
            "no order exists after erase + re-poll — the erased order stays erased (right-to-erasure)");

        await using var verify = new ProcuLinkDbContext(_options!);
        var ledger = await verify.Set<ImportedSftpFile>().SingleAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);
        ledger.OrderId.Should().Be(Guid.Empty,
            "the ledger row survives but is tombstoned (Guid.Empty) so the file is permanently skipped");
    }

    // ── SFTP test doubles ─────────────────────────────────────────────────────

    /// <summary>Parse-job enqueuer that throws — models a crash in the post-order-commit step.</summary>
    private sealed class ThrowingParseEnqueuer : IParseJobEnqueuer
    {
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
            => throw new InvalidOperationException("parse-enqueue failure after order commit (test double)");
    }

    /// <summary>
    /// One file at a fixed path. <see cref="Content"/> is settable so a test can overwrite the file
    /// BETWEEN polls — a supplier correcting and re-dropping the PO at the same path, which is the
    /// exact motion B-7 describes. The content is read at Connect time, so each poll sees whatever
    /// the server holds now.
    /// </summary>
    private sealed class OneFileSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        public byte[] Content { get; set; }
        public OneFileSftpFactory(string remotePath, byte[] content) { _remotePath = remotePath; Content = content; }
        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
            => new OneFileSftpSession(_remotePath, Content);
    }

    /// <summary>Two remote paths holding byte-identical content.</summary>
    private sealed class TwoFileSftpFactory : ISftpClientFactory
    {
        private readonly string _pathA;
        private readonly string _pathB;
        private readonly byte[] _content;
        public TwoFileSftpFactory(string pathA, string pathB, byte[] content)
        { _pathA = pathA; _pathB = pathB; _content = content; }

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
            => new TwoFileSftpSession(_pathA, _pathB, _content);
    }

    private sealed class TwoFileSftpSession : ISftpSession
    {
        private readonly string _pathA;
        private readonly string _pathB;
        private readonly byte[] _content;
        public TwoFileSftpSession(string pathA, string pathB, byte[] content)
        { _pathA = pathA; _pathB = pathB; _content = content; }

        public IEnumerable<string> ListFileNames(string remoteDirectory) => new[] { _pathA, _pathB };
        public MemoryStream DownloadFile(string remotePath) => new(_content);
        public Stream OpenRead(string remotePath) => new MemoryStream(_content);
        public void Dispose() { }
    }

    private sealed class OneFileSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly byte[] _content;
        public OneFileSftpSession(string remotePath, byte[] content) { _remotePath = remotePath; _content = content; }
        public IEnumerable<string> ListFileNames(string remoteDirectory) => new[] { _remotePath };
        public MemoryStream DownloadFile(string remotePath) => new(_content);
        public Stream OpenRead(string remotePath) => new MemoryStream(_content);
        public void Dispose() { }
    }
}
