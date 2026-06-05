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
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;

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

        var orders = new Mock<IOrderService>(MockBehavior.Strict);
        // Strict — CreateStubAsync MUST NOT be called.

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0);
        orders.Verify(o =>
            o.CreateStubAsync(
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

        var orders = new Mock<IOrderService>(MockBehavior.Strict);
        // Strict — CreateStubAsync MUST NOT be called.

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0);
        orders.Verify(o =>
            o.CreateStubAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 5. Enabled config without supplier → skipped before S3 calls ────────

    [Fact]
    public async Task EnabledConfigWithoutDefaultSupplier_Returns0AndMakesNoS3Calls()
    {
        await using var db = CreateDb();
        var orgId = await SeedConfigAsync(
            db,
            isEnabled: true,
            bucket: "my-bucket",
            createDefaultSupplier: false);

        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        var orders = new Mock<IOrderService>(MockBehavior.Strict);

        var svc = MakeService(db, s3.Object, orders.Object);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(0, "pull ingress must not create org-scoped orders without a supplier route");
        orders.Verify(o =>
            o.CreateStubAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 5. Happy path: 2 new objects → CreateStubAsync called twice, ─────────
    //       ImportedS3Object rows inserted

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
        var svc = MakeService(db, s3.Object, orders);

        var count = await svc.PollAsync(orgId, CancellationToken.None);

        count.Should().Be(2);
        orders.CalledWith.Should().HaveCount(2);
        orders.CalledWith.Select(c => c.OrgId).Should().AllBeEquivalentTo(orgId);
        orders.CalledWith.Select(c => c.SupplierId).Should().OnlyContain(
            supplierId => supplierId != Guid.Empty,
            "S3 pull imports must be assigned to the configured supplier");
        orders.CalledWith.Select(c => c.FileName).Should().BeEquivalentTo(
            new[] { "po-a.csv", "po-b.xlsx" });

        // Two ImportedS3Object rows should be persisted.
        var imported = await db.Set<ImportedS3Object>()
            .Where(x => x.OrgId == orgId)
            .ToListAsync();
        imported.Should().HaveCount(2);
        imported.Select(x => x.ObjectKey).Should().BeEquivalentTo(
            new[] { "po-a.csv", "po-b.xlsx" });
        imported.Select(x => x.BucketName).Should().AllBe("my-bucket");
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
            db, orders, encryption, new AmazonS3ClientFactory(),
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
        IOrderService? orders = null)
    {
        var encryption = MakeEncryption();
        return new S3IngressService(
            db,
            orders ?? new FakeOrderService(),
            encryption,
            new FakeAmazonS3ClientFactory(s3),
            NullLogger<S3IngressService>.Instance);
    }

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
        bool createDefaultSupplier = true)
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
            });
        }

        db.Set<S3IngressConfig>().Add(new S3IngressConfig
        {
            Id               = Guid.NewGuid(),
            OrgId            = orgId,
            BucketName       = bucket,
            KeyPrefix        = string.Empty,
            Region           = "eu-west-1",
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

    private sealed class FakeOrderService : IOrderService
    {
        public List<(Guid OrgId, Guid SupplierId, string FileName, string ContentType)> CalledWith { get; } = new();

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            CalledWith.Add((organisationId, supplierId, filename, contentType));

            var stub = new PurchaseOrderEntity
            {
                Id        = Guid.NewGuid(),
                OrgId     = organisationId,
                SupplierId = supplierId,
                Status    = "parsing",
                SourceFileKey = $"{organisationId}/{Guid.NewGuid()}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid organisationId, Guid supplierId, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid organisationId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid organisationId, Guid orderId, OutputFormat format, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid organisationId, Guid orderId, Guid artifactId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid organisationId, Guid orderId, IReadOnlyList<LineResolution> resolutions, bool saveMappings, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid organisationId, Guid orderId, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
            => throw new NotImplementedException();
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
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
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
