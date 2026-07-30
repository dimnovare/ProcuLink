using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Ingress;

/// <summary>
/// Unit tests for <see cref="S3IngressService"/> using an in-memory EF store
/// and a mocked <see cref="IAmazonS3"/>.
/// </summary>
public class S3IngressServiceTests
{
    // ── 1. No config for org → returns 0, no S3 calls ───────────────────────

    [Fact]
    public async Task NoConfigForOrg_Returns0AndMakesNoS3Calls()
    {
        await using var db = CreateDb();
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        // Strict — any unexpected call fails the test.

        var svc = MakeService(db, s3.Object);

        var count = await svc.PollAsync(Guid.NewGuid(), CancellationToken.None);

        count.Should().Be(0);
        // No S3 calls because there is no config row.
    }

    // ── 2. Config disabled → returns 0 ──────────────────────────────────────

    [Fact]
    public async Task ConfigDisabled_Returns0AndMakesNoS3Calls()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: false);
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);

        var svc = MakeService(db, s3.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0);
        // No S3 calls because config.IsEnabled = false.
    }

    // ── 3. Object already imported with matching ETag → skipped ─────────────

    [Fact]
    public async Task ExistingObjectSameETag_IsSkippedAndCreateStubNotCalled()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: true, bucket: "my-bucket");

        // Record the object as already imported.
        db.Set<ImportedS3Object>().Add(new ImportedS3Object
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            BucketName = "my-bucket",
            ObjectKey  = "incoming/po-001.csv",
            ETag       = "\"abc123\"",
            ImportedAt = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "incoming/po-001.csv", ETag = "\"abc123\"" },
              },
              IsTruncated = false,
          });

        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);
        // Strict — CreateStubAsync MUST NOT be called.

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0);
        orders.Verify(o =>
            o.CreateStubAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 4. Unsupported extension (.docx) → skipped ──────────────────────────

    [Fact]
    public async Task UnsupportedExtension_IsSkippedAndCreateStubNotCalled()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: true, bucket: "my-bucket");

        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "incoming/contract.docx", ETag = "\"docx-etag\"" },
              },
              IsTruncated = false,
          });

        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);
        // Strict — CreateStubAsync MUST NOT be called.

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0);
        orders.Verify(o =>
            o.CreateStubAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 4b. Oversized object (Size > cap) → skipped before download ─────────

    [Fact]
    public async Task OversizedObject_IsSkippedBeforeDownload_CreateStubNotCalled()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: true, bucket: "my-bucket");

        // Strict s3: only ListObjectsV2Async is set up — a download attempt (GetObjectAsync)
        // would throw, proving the size cap skips the object BEFORE downloading it.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        s3.Setup(c => c.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "incoming/huge.csv", ETag = "\"huge-etag\"", Size = IngressLimits.MaxFileBytes + 1 },
              },
              IsTruncated = false,
          });

        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0, "an oversized object must be skipped before download");
        s3.Verify(c => c.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        orders.Verify(o =>
            o.CreateStubAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── SEC-1: SSRF — private custom ServiceUrl blocked before any S3 call ───

    [Fact]
    public async Task PrivateServiceUrl_IsBlockedBySsrfGuard_NoS3CallsMade()
    {
        await using var db = CreateDb();
        // Custom ServiceUrl pointing at a private RFC-1918 host — the strict guard blocks it.
        var orgId = await SeedConfigAsync(
            db, isEnabled: true, bucket: "my-bucket", serviceUrl: "http://10.0.0.5:9000");

        // Strict s3 mock: ANY S3 call would fail the test, proving the guard short-circuits
        // BEFORE the client is built / used.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);

        var svc = MakeService(db, s3.Object, orders.Object, StrictGuard());

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0, "a poll with a private/internal custom endpoint must be refused");
        s3.Verify(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── SEC-1 / H2: bounded read aborts when the server under-reports Size ────

    [Fact]
    public async Task UnderReportedSize_IsAbortedMidCopy_CreateStubNotCalled()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: true, bucket: "my-bucket");

        // ListObjectsV2 reports a tiny Size (passes the advisory pre-check), but the actual
        // GetObject stream yields cap + 1 MB — the bounded read must abort mid-copy.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "sneaky.csv", ETag = "\"sneaky\"", Size = 10 },
              },
              IsTruncated = false,
          });
        s3.Setup(c => c.GetObjectAsync("my-bucket", It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse
          {
              ResponseStream = new EndlessForwardStream(IngressLimits.MaxFileBytes + 1_048_576),
          });

        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0, "an object exceeding the cap (even with an under-reported Size) must be skipped");
        orders.Verify(o =>
            o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 5. Phase 1b: no default supplier → object imported UNROUTED, not dropped ──

    [Fact]
    public async Task NoDefaultSupplier_NewObject_IsImportedUnrouted_AndParseEnqueued()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(
            db,
            isEnabled: true,
            bucket: "my-bucket",
            createDefaultSupplier: false);

        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "incoming/po-unrouted.csv", ETag = "\"unrouted-etag\"" },
              },
              IsTruncated = false,
          });
        s3.Setup(c => c.GetObjectAsync("my-bucket", It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse
          {
              ResponseStream = new MemoryStream(new byte[] { 1, 2, 3 }),
          });

        var orders = new FakeOrderService();
        var enqueuer = new FakeParseJobEnqueuer();
        var svc = MakeService(db, s3.Object, orders, enqueuer: enqueuer);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(1, "an object arriving with no default supplier must be imported unrouted, not dropped");
        orders.UnroutedCalls.Should().Be(1,
            "the unrouted creation path (CreateUnroutedStubAsync) must be used when no supplier is configured");
        orders.CalledWith.Should().ContainSingle();
        orders.CalledWith[0].OrgId.Should().Be(orgId);
        orders.CalledWith[0].SupplierId.Should().Be(Guid.Empty, "the fake tags unrouted stubs with Guid.Empty");
        orders.CalledWith[0].FileName.Should().Be("po-unrouted.csv");

        enqueuer.EnqueuedOrderIds.Should().ContainSingle(
            "the unrouted order must still get a parse job — the parse parks it 'unrouted' for later assignment");
        enqueuer.EnqueuedOrgIds.Should().ContainSingle().Which.Should().Be(orgId);

        var imported = await db.Set<ImportedS3Object>()
            .Where(x => x.OrgId == orgId)
            .ToListAsync();
        imported.Should().ContainSingle(
            "the processed-object record must be written for unrouted imports too, so re-polls stay idempotent");
        imported[0].ObjectKey.Should().Be("incoming/po-unrouted.csv");
    }

    // ── 5b. Phase 1b: configured supplier soft-deleted → imported UNROUTED ──

    [Fact]
    public async Task SoftDeletedDefaultSupplier_NewObject_IsImportedUnrouted()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(
            db,
            isEnabled: true,
            bucket: "my-bucket",
            softDeleteSupplier: true);

        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "incoming/po-stale.csv", ETag = "\"stale-etag\"" },
              },
              IsTruncated = false,
          });
        s3.Setup(c => c.GetObjectAsync("my-bucket", It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse
          {
              ResponseStream = new MemoryStream(new byte[] { 1, 2, 3 }),
          });

        var orders = new FakeOrderService();
        var svc = MakeService(db, s3.Object, orders);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(1, "a stale (soft-deleted) default supplier must not drop incoming objects");
        orders.UnroutedCalls.Should().Be(1,
            "an order must never be routed to a soft-deleted supplier — it goes to the unrouted hold instead");
    }

    // ── 5. Happy path: 2 new objects → CreateStubAsync called twice, ─────────
    //       ImportedS3Object rows inserted, parse jobs enqueued

    [Fact]
    public async Task TwoNewObjects_CreateStubCalledTwiceAndImportedRowsInserted()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: true, bucket: "my-bucket");

        // ListObjectsV2 returns two new CSV files.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object>
              {
                  new() { Key = "po-a.csv",  ETag = "\"etag-a\"" },
                  new() { Key = "po-b.xlsx", ETag = "\"etag-b\"" },
              },
              IsTruncated = false,
          });

        // GetObjectAsync returns a minimal stream for each key.
        s3.Setup(c => c.GetObjectAsync(
                "my-bucket",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse
          {
              ResponseStream = new MemoryStream(new byte[] { 1, 2, 3 }),
          });

        var orders = new FakeOrderService();
        var enqueuer = new FakeParseJobEnqueuer();
        var svc = MakeService(db, s3.Object, orders, enqueuer: enqueuer);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(2);
        orders.CalledWith.Should().HaveCount(2);
        orders.CalledWith.Select(c => c.OrgId).Should().AllBeEquivalentTo(orgId);
        orders.CalledWith.Select(c => c.SupplierId).Should().OnlyContain(
            supplierId => supplierId != Guid.Empty,
            "S3 pull imports must be assigned to the configured supplier");
        orders.CalledWith.Select(c => c.FileName).Should().BeEquivalentTo(
            new[] { "po-a.csv", "po-b.xlsx" });

        enqueuer.EnqueuedOrderIds.Should().HaveCount(2,
            "a parse job must be enqueued for each successfully imported S3 object");
        enqueuer.EnqueuedOrgIds.Should().AllBeEquivalentTo(orgId,
            "every parse job must be scoped to the correct org");

        // Two ImportedS3Object rows should be persisted.
        var imported = await db.Set<ImportedS3Object>()
            .Where(x => x.OrgId == orgId)
            .ToListAsync();
        imported.Should().HaveCount(2);
        imported.Select(x => x.ObjectKey).Should().BeEquivalentTo(
            new[] { "po-a.csv", "po-b.xlsx" });
        imported.Select(x => x.BucketName).Should().AllBe("my-bucket");
    }

    // ── Claim-first: the ImportedS3Object row is committed BEFORE CreateStubAsync ──
    // Regression guard for the duplicate-PO reliability class. The old order was
    // CreateStub (self-commits the order + fires order.created) THEN write the processed-object
    // row, so a Hangfire retry / concurrent same-org poll landing in that window re-imported the
    // object as a DUPLICATE order. Claim-first inverts it: the ledger row is the FIRST durable
    // write. This probe asserts the ledger row is already committed at the instant
    // CreateStubAsync is invoked.

    [Fact]
    public async Task NewObject_LedgerClaimIsCommittedBeforeCreateStub()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(db, isEnabled: true, bucket: "my-bucket");

        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object> { new() { Key = "incoming/claim-first.csv", ETag = "\"cf-etag\"" } },
              IsTruncated = false,
          });
        s3.Setup(c => c.GetObjectAsync("my-bucket", It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(new byte[] { 1, 2, 3 }) });

        var probe = new LedgerProbingOrderService(db);
        var svc = MakeService(db, s3.Object, probe);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(1);
        probe.CreateStubCalls.Should().Be(1);
        probe.LedgerRowPresentAtStubTime.Should().BeTrue(
            "claim-first: the (OrgId, BucketName, ObjectKey) processed-object row must be committed BEFORE " +
            "CreateStubAsync so a retry or concurrent same-org poll cannot create a duplicate order");
    }

    // ── LIVE: real S3/R2 poll against a real bucket ──────────────────────────
    // Gated behind PROCULINK_LIVE_ENDPOINT_TESTS=1; connects to a real
    // S3-compatible bucket (env PROCULINK_LIVE_S3_*) with the PRODUCTION
    // AmazonS3ClientFactory, lists + downloads a real PO file, and imports it.
    // Proves Cloudflare R2 ingest works end-to-end through the new ServiceUrl
    // column — the gap previously documented in docs/live-endpoint-test-fires.md.
    [Fact]
    [Trait("Category", "LiveEndpoint")]
    public async Task Live_S3Ingress_RealPollImportsFile()
    {
        if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") != "1") return;
        var bucket = Environment.GetEnvironmentVariable("PROCULINK_LIVE_S3_BUCKET") ?? "";
        if (string.IsNullOrEmpty(bucket)) return;

        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var encryption = MakeEncryption();

        db.Set<Supplier>().Add(new Supplier
        { Id = supplierId, OrgId = orgId, Name = "Live S3/R2 supplier", CreatedAt = DateTime.UtcNow });
        db.Set<S3IngressConfig>().Add(new S3IngressConfig
        {
            Id                 = Guid.NewGuid(),
            OrgId              = orgId,
            BucketName         = bucket,
            KeyPrefix          = Environment.GetEnvironmentVariable("PROCULINK_LIVE_S3_PREFIX") ?? string.Empty,
            Region             = Environment.GetEnvironmentVariable("PROCULINK_LIVE_S3_REGION") ?? "auto",
            ServiceUrl         = Environment.GetEnvironmentVariable("PROCULINK_LIVE_S3_ENDPOINT"),
            AccessKeyId        = Environment.GetEnvironmentVariable("PROCULINK_LIVE_S3_ACCESS_KEY") ?? "",
            EncryptedSecretKey = encryption.Encrypt(Environment.GetEnvironmentVariable("PROCULINK_LIVE_S3_SECRET") ?? ""),
            DefaultSupplierId  = supplierId,
            IsEnabled          = true,
            CreatedAt          = DateTime.UtcNow,
            UpdatedAt          = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orders = new FakeOrderService();
        var svc = new S3IngressService(
            db, orders, new FakeParseJobEnqueuer(), encryption, new AmazonS3ClientFactory(AllowPrivateGuard()),
            AllowPrivateGuard(),
            NullLogger<S3IngressService>.Instance);

        var imported = await svc.PollAsync(orgId, default);

        imported.Should().BeGreaterThanOrEqualTo(1,
            "the real S3/R2 poll should import at least one PO file from the bucket");
        orders.CalledWith.Should().NotBeEmpty();
        orders.CalledWith.Select(c => c.SupplierId).Should().AllBeEquivalentTo(
            supplierId, "imports must be routed to the configured default supplier");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static S3IngressService MakeService(
        ProcuLinkDbContext db,
        IAmazonS3 s3,
        IStubOrderCreator? orders = null,
        OutboundRequestGuard? guard = null,
        IParseJobEnqueuer? enqueuer = null)
    {
        var encryption = MakeEncryption();
        return new S3IngressService(
            db,
            orders ?? new FakeOrderService(),
            enqueuer ?? new FakeParseJobEnqueuer(),
            encryption,
            new FakeAmazonS3ClientFactory(s3),
            // Default guard allows private targets so the existing tests (no custom
            // ServiceUrl, or fixture endpoints) never hit a real DNS lookup. The SSRF test
            // below supplies a strict (flag=false) guard plus a private ServiceUrl explicitly.
            guard ?? AllowPrivateGuard(),
            NullLogger<S3IngressService>.Instance);
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

    /// <summary>
    /// Test double for <see cref="IAmazonS3ClientFactory"/> that returns a
    /// pre-built <see cref="IAmazonS3"/> regardless of credentials. Lets the
    /// existing mock-based tests reach the same S3 client through the factory.
    /// </summary>
    private sealed class FakeAmazonS3ClientFactory : IAmazonS3ClientFactory
    {
        private readonly IAmazonS3 _client;

        public FakeAmazonS3ClientFactory(IAmazonS3 client) => _client = client;

        public IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl)
            => _client;
    }

    /// <summary>
    /// Creates a <see cref="DeliveryEncryptionService"/> backed by a known
    /// 32-byte key so Encrypt/Decrypt work without real secrets.
    /// </summary>
    private static DeliveryEncryptionService MakeEncryption()
    {
        // 32 zero bytes → base64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
        var key = Convert.ToBase64String(new byte[32]);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = key,
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static async Task<Guid> SeedConfigAsync(
        ProcuLinkDbContext db,
        bool isEnabled,
        string bucket = "test-bucket",
        bool createDefaultSupplier = true,
        string? serviceUrl = null,
        bool softDeleteSupplier = false)
    {
        var encryption = MakeEncryption();
        var orgId = Guid.NewGuid();
        Guid? supplierId = null;

        if (createDefaultSupplier)
        {
            supplierId = Guid.NewGuid();
            db.Set<Supplier>().Add(new Supplier
            {
                Id = supplierId.Value,
                OrgId = orgId,
                Name = "S3 supplier",
                CreatedAt = DateTime.UtcNow,
                DeletedAt = softDeleteSupplier ? DateTime.UtcNow.AddMinutes(-5) : null,
            });
        }

        db.Set<S3IngressConfig>().Add(new S3IngressConfig
        {
            Id               = Guid.NewGuid(),
            OrgId            = orgId,
            BucketName       = bucket,
            KeyPrefix        = string.Empty,
            Region           = "eu-west-1",
            ServiceUrl       = serviceUrl,
            AccessKeyId      = "AKIAFAKE",
            EncryptedSecretKey = encryption.Encrypt("fake-secret"),
            DefaultSupplierId = supplierId,
            IsEnabled        = isEnabled,
            CreatedAt        = DateTime.UtcNow,
            UpdatedAt        = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return orgId;
    }

    private static ProcuLinkDbContext CreateDb() =>
        new S3IngressTestDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    // ── Doubles ──────────────────────────────────────────────────────────────

    /// <summary>Recording fake for <see cref="IParseJobEnqueuer"/>.</summary>
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

    /// <summary>
    /// Order service double that, at the moment CreateStub/CreateUnroutedStub is invoked,
    /// records whether an <see cref="ImportedS3Object"/> row for the org is already committed.
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
            LedgerRowPresentAtStubTime = _db.Set<ImportedS3Object>().Any(f => f.OrgId == orgId);
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

    private sealed class FakeOrderService : IStubOrderCreator
    {
        public List<(Guid OrgId, Guid SupplierId, string FileName, string ContentType)> CalledWith { get; } = new();
        public int UnroutedCalls { get; private set; }

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            CalledWith.Add((organisationId, supplierId, filename, contentType));

            var stub = new PurchaseOrderEntity
            {
                Id        = orderId,
                OrgId     = organisationId,
                SupplierId = supplierId,
                Status    = "parsing",
                SourceFileKey = $"{organisationId}/{orderId}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        // Unrouted hold path — counted separately, and recorded in CalledWith with a
        // Guid.Empty supplier so tests can assert which path was used.
        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        {
            UnroutedCalls++;
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            CalledWith.Add((organisationId, Guid.Empty, filename, contentType));

            var stub = new PurchaseOrderEntity
            {
                Id        = orderId,
                OrgId     = organisationId,
                SupplierId = null,
                Status    = "parsing",
                SourceFileKey = $"{organisationId}/{orderId}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }
    }

    /// <summary>
    /// Minimal in-memory DbContext that only materialises <see cref="S3IngressConfig"/>
    /// and <see cref="ImportedS3Object"/> — all other entities are ignored.
    /// </summary>
    private sealed class S3IngressTestDbContext : ProcuLinkDbContext
    {
        public S3IngressTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

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
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            modelBuilder.Entity<S3IngressConfig>(b =>
            {
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<ImportedS3Object>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.OrgId, x.BucketName, x.ObjectKey }).IsUnique();
            });
        }
    }
}
