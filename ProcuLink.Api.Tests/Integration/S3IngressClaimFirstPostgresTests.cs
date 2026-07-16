using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves the claim-first + resume-on-conflict S3/R2 ingress on REAL Postgres, where the
/// (OrgId, BucketName, ObjectKey) unique index is actually enforced (EF InMemory ignores it):
/// re-poll creates no second order; concurrent polls create exactly one; a transient stub failure
/// is RESUMED (not lost); an order whose post-step crashed is not re-created; and a permanently-bad
/// object is bounded. Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class S3IngressClaimFirstPostgresTests : IAsyncLifetime
{
    private const string Bucket = "cf-bucket";

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_s3_cf_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
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

    private async Task<Guid> SeedOrgSupplierConfigAsync()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_s3_{orgId:N}", Name = "S3 CF Org",
            Slug = $"s3-cf-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S3 Supplier", CreatedAt = now });
        db.Set<S3IngressConfig>().Add(new S3IngressConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, BucketName = Bucket, KeyPrefix = string.Empty, Region = "eu-west-1",
            ServiceUrl = null, AccessKeyId = "AKIA", EncryptedSecretKey = Enc().Encrypt("secret"),
            DefaultSupplierId = supplierId, IsEnabled = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static IAmazonS3 OneObjectS3(string key, string etag, byte[] content)
    {
        var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
        s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new ListObjectsV2Response
          {
              S3Objects = new List<S3Object> { new() { Key = key, ETag = etag } },
              IsTruncated = false,
          });
        s3.Setup(c => c.GetObjectAsync(Bucket, It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(content) });
        return s3.Object;
    }

    private S3IngressService NewService(ProcuLinkDbContext db, CountingOrderService orders, IParseJobEnqueuer enqueuer, IAmazonS3 s3) =>
        new(db, orders, enqueuer, Enc(), new SingleClientFactory(s3), AllowPrivateGuard(), NullLogger<S3IngressService>.Instance);

    private async Task<int> OrderCountAsync(Guid orgId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        return await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId);
    }

    [DockerRequiredFact]
    public async Task RePoll_AfterImport_CreatesNoSecondOrder()
    {
        const string key = "incoming/po-retry.csv";
        var orgId = await SeedOrgSupplierConfigAsync();
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var s3 = OneObjectS3(key, "\"etag-1\"", new byte[] { 1, 2, 3 });

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the first poll imports the new object");

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the committed claim's order (same ETag) must make the re-poll a no-op");

        orders.TotalCreates.Should().Be(1, "the object must produce exactly ONE order stub across re-polls");
        enqueuer.Count.Should().Be(1);
        (await OrderCountAsync(orgId)).Should().Be(1, "exactly ONE purchase order exists across re-polls");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.Set<ImportedS3Object>().CountAsync(f => f.OrgId == orgId && f.ObjectKey == key))
            .Should().Be(1, "exactly one processed-object ledger row");
    }

    [DockerRequiredFact]
    public async Task ConcurrentPolls_OfSameObject_CreateExactlyOneOrder()
    {
        const int workers = 8;
        const string key = "incoming/po-concurrent.csv";
        var orgId = await SeedOrgSupplierConfigAsync();
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var s3 = OneObjectS3(key, "\"etag-1\"", new byte[] { 1, 2, 3 });

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            return await NewService(db, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None);
        });
        var results = await Task.WhenAll(tasks);

        results.Sum().Should().BeGreaterThanOrEqualTo(1, "at least one concurrent poll imports the object");
        (await OrderCountAsync(orgId)).Should().Be(1,
            "concurrent polls of the same object must create EXACTLY ONE purchase order");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.Set<ImportedS3Object>().CountAsync(f => f.OrgId == orgId && f.ObjectKey == key))
            .Should().Be(1, "exactly one processed-object ledger row survives the concurrent race");
    }

    [DockerRequiredFact]
    public async Task TransientStubFailure_ThenRetry_ImportsOrder_NotLost()
    {
        const string key = "incoming/po-transient.csv";
        var orgId = await SeedOrgSupplierConfigAsync();
        var s3 = OneObjectS3(key, "\"etag-1\"", new byte[] { 1, 2, 3 });

        var attempt = 0;
        var orders = new CountingOrderService(_options!,
            () => Interlocked.Increment(ref attempt) == 1 ? StubBehavior.ThrowTransient : StubBehavior.Succeed);
        var enqueuer = new CountingParseEnqueuer();

        await using (var db1 = new ProcuLinkDbContext(_options!))
        {
            var svc = NewService(db1, orders, enqueuer, s3);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PollAsync(orgId, CancellationToken.None));
        }

        await using (var afterFail = new ProcuLinkDbContext(_options!))
        {
            var claim = await afterFail.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key);
            claim.OrderId.Should().NotBe(Guid.Empty, "the claim carries a real pre-generated order id to resume under");
            (await afterFail.PurchaseOrders.CountAsync(o => o.OrgId == orgId))
                .Should().Be(0, "the transient failure means the order was never created");
        }

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the retry must RESUME the abandoned claim and import the order (never lost)");

        (await OrderCountAsync(orgId)).Should().Be(1, "the order is eventually created exactly once — not lost");
        enqueuer.Count.Should().Be(1, "the parse job is enqueued once, on the successful resume");
    }

    [DockerRequiredFact]
    public async Task OrderCommittedButPostStepCrashed_Retry_CreatesNoSecondOrder()
    {
        const string key = "incoming/po-poststep.csv";
        var orgId = await SeedOrgSupplierConfigAsync();
        var s3 = OneObjectS3(key, "\"etag-1\"", new byte[] { 1, 2, 3 });
        var orders = new CountingOrderService(_options!);

        await using (var db1 = new ProcuLinkDbContext(_options!))
        {
            var svc = NewService(db1, orders, new ThrowingParseEnqueuer(), s3);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PollAsync(orgId, CancellationToken.None));
        }
        (await OrderCountAsync(orgId)).Should().Be(1, "the order WAS committed before the post-step crashed");

        var enqueuer = new CountingParseEnqueuer();
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the existing order (by id) must make the retry a no-op — no duplicate");

        (await OrderCountAsync(orgId)).Should().Be(1, "still exactly ONE order — the retry created no duplicate");
        orders.TotalCreates.Should().Be(1, "the retry must not call stub creation again for the completed order");
    }

    [DockerRequiredFact]
    public async Task PermanentlyBadObject_IsBounded_NotRetriedForever()
    {
        const string key = "incoming/po-bad.csv";
        var orgId = await SeedOrgSupplierConfigAsync();
        var s3 = OneObjectS3(key, "\"etag-1\"", new byte[] { 1, 2, 3 });
        var orders = new CountingOrderService(_options!, () => StubBehavior.FailPermanent);
        var enqueuer = new CountingParseEnqueuer();

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "a permanently-bad object imports nothing");

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0);

        orders.TotalCreates.Should().Be(1, "a genuinely-bad object is attempted ONCE, then bounded — never retried forever");
        (await OrderCountAsync(orgId)).Should().Be(0, "no order is ever created for a permanently-bad object");

        await using var verify = new ProcuLinkDbContext(_options!);
        var claim = await verify.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key);
        claim.OrderId.Should().Be(Guid.Empty, "the claim is marked terminal (Guid.Empty) after the permanent failure");
    }

    /// <summary>Parse-job enqueuer that throws — models a crash in the post-order-commit step.</summary>
    private sealed class ThrowingParseEnqueuer : IParseJobEnqueuer
    {
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
            => throw new InvalidOperationException("parse-enqueue failure after order commit (test double)");
    }

    /// <summary>Factory that hands every caller the same pre-built <see cref="IAmazonS3"/>.</summary>
    private sealed class SingleClientFactory : IAmazonS3ClientFactory
    {
        private readonly IAmazonS3 _client;
        public SingleClientFactory(IAmazonS3 client) => _client = client;
        public IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl) => _client;
    }
}
