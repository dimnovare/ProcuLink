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
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
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
public sealed class S3IngressClaimFirstPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const string Bucket = "cf-bucket";

    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_s3_cf");

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
            ServiceUrl = null, AccessKeyId = "AKIA", EncryptedSecretKey = Enc().Encrypt(
                "secret", CredentialScope.ForOrg(orgId, CredentialPurpose.OrgIngressS3SecretKey)),
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

    [DockerRequiredFact]
    public async Task ErasedOrder_IsNotResurrected_OnNextPoll()
    {
        const string key = "incoming/po-erase.csv";
        var orgId = await SeedOrgSupplierConfigAsync();
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var s3 = OneObjectS3(key, "\"etag-1\"", new byte[] { 1, 2, 3 });

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1);

        Guid erasedOrderId;
        await using (var v = new ProcuLinkDbContext(_options!))
            erasedOrderId = (await v.PurchaseOrders.SingleAsync(o => o.OrgId == orgId)).Id;

        // GDPR / bulk erase removes the order. The object still lives in the customer's bucket
        // (ProcuLink never deletes it), so the ledger row MUST be tombstoned, not left dangling.
        await using (var eraseDb = new ProcuLinkDbContext(_options!))
        {
            var erase = new DataErasureService(eraseDb, new NoOpFileStorage(), NullLogger<DataErasureService>.Instance);
            (await erase.EraseOrderAsync(orgId, erasedOrderId, CancellationToken.None)).Found.Should().BeTrue();
        }
        (await OrderCountAsync(orgId)).Should().Be(0, "the order was erased");

        // Re-poll: the same object is STILL in the bucket. It must NOT resurrect the erased order.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, s3).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "an erased order must never be resurrected by a re-poll of the surviving object");

        (await OrderCountAsync(orgId)).Should().Be(0,
            "no order exists after erase + re-poll — the erased order stays erased (right-to-erasure)");

        await using var verify = new ProcuLinkDbContext(_options!);
        var ledger = await verify.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key);
        ledger.OrderId.Should().Be(Guid.Empty,
            "the processed-object row survives but is tombstoned (Guid.Empty) so the object is permanently skipped");
    }

    // ── re-upload (ETag changed) at an already-imported key ─────────────────
    // A supplier who corrects a PO overwrites the SAME object key; S3/R2 stamp a new ETag on the
    // re-upload. Treating an already-seen key as SEEN FOREVER would silently drop the correction,
    // leaving the buyer to send the superseded quantities. The re-claim path must import it as a
    // NEW order under a FRESH pre-generated id, without destroying the original.

    [DockerRequiredFact]
    public async Task ReUploadedObject_ChangedETag_IsImported_NotSilentlyDropped()
    {
        const string key = "incoming/po-reupload.csv";
        var original  = "po,qty\r\n7781,100"u8.ToArray();
        var corrected = "po,qty\r\n7781,120"u8.ToArray();   // same key, ONE digit different

        var orgId = await SeedOrgSupplierConfigAsync();
        var orders = new CountingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();
        var bucket = new MutableObjectS3(key, "\"etag-1\"", original);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the first poll imports the original PO");

        Guid originalOrderId;
        await using (var afterFirst = new ProcuLinkDbContext(_options!))
        {
            var claim = await afterFirst.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key);
            claim.ETag.Should().Be("\"etag-1\"", "the claim records the ETag of the original object");
            originalOrderId = claim.OrderId;
        }

        // The supplier corrects the PO and overwrites the SAME key — a new ETag.
        bucket.ETag = "\"etag-2\"";
        bucket.Content = corrected;

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "a re-upload at an already-imported key is a NEW order, not a duplicate");

        (await OrderCountAsync(orgId)).Should().Be(2,
            "both the original and the correction exist — the correction is not silently dropped, and the " +
            "original is not destroyed behind the operator's back");
        orders.TotalCreates.Should().Be(2);
        enqueuer.Count.Should().Be(2, "the corrected order gets its own parse job");

        await using var verify = new ProcuLinkDbContext(_options!);
        var updated = await verify.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key);
        updated.ETag.Should().Be("\"etag-2\"",
            "the claim is UPDATED in place to the new ETag — (OrgId, BucketName, ObjectKey) is unique, so a second row is impossible");
        updated.OrderId.Should().NotBe(originalOrderId, "the re-upload is claimed under a FRESH pre-generated order id");
        (await verify.PurchaseOrders.AnyAsync(o => o.Id == updated.OrderId)).Should().BeTrue();
        (await verify.PurchaseOrders.AnyAsync(o => o.Id == originalOrderId)).Should().BeTrue();
    }

    // ── concurrent polls of ONE re-upload create ONE new order ──────────────
    // The re-claim is an UPDATE ... WHERE etag = <old>. Without that guard every concurrent poll
    // would stamp its own fresh order id over the row and import the re-upload N times — N supplier
    // deliveries and N x EUR0.50 overage for one corrected PO.

    [DockerRequiredFact]
    public async Task ConcurrentPolls_OfOneReUpload_CreateExactlyOneNewOrder()
    {
        const int workers = 8;
        const string key = "incoming/po-concurrent-reupload.csv";
        var original  = "po,qty\r\n9001,10"u8.ToArray();
        var corrected = "po,qty\r\n9001,11"u8.ToArray();

        var orgId = await SeedOrgSupplierConfigAsync();
        var orders = new CountingOrderService(_options!);   // shared across all polls
        var enqueuer = new CountingParseEnqueuer();
        var bucket = new MutableObjectS3(key, "\"etag-1\"", original);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1);

        // One re-upload, many simultaneous pollers racing to claim it.
        bucket.ETag = "\"etag-2\"";
        bucket.Content = corrected;
        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            return await NewService(db, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None);
        });
        await Task.WhenAll(tasks);

        (await OrderCountAsync(orgId)).Should().Be(2,
            "one original + ONE re-upload — the losers of the re-claim race must skip, not re-import");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.Set<ImportedS3Object>().CountAsync(f => f.OrgId == orgId && f.ObjectKey == key))
            .Should().Be(1, "exactly one ledger row survives the concurrent re-claim race");
        (await verify.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key))
            .ETag.Should().Be("\"etag-2\"");
    }

    // ── a re-upload at a TERMINAL claim's key must import too ───────────────
    // "We rejected the object that used to be at this key" must not become "we will never look at
    // this key again". A permanently-bad object the supplier then FIXED is the same lost order.

    [DockerRequiredFact]
    public async Task TerminalClaim_ThenReUploadedObject_IsImported()
    {
        const string key = "incoming/po-was-bad.csv";
        var bad = "h1,h2\r\nbad,bad"u8.ToArray();
        var fixedUp = "h1,h2\r\ngood,good"u8.ToArray();

        var orgId = await SeedOrgSupplierConfigAsync();
        var behavior = StubBehavior.FailPermanent;
        var orders = new CountingOrderService(_options!, () => behavior);
        var enqueuer = new CountingParseEnqueuer();
        var bucket = new MutableObjectS3(key, "\"etag-bad\"", bad);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the permanently-bad object imports nothing and its claim is marked terminal");

        await using (var afterBad = new ProcuLinkDbContext(_options!))
            (await afterBad.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key))
                .OrderId.Should().Be(Guid.Empty, "precondition: the claim really is terminal");

        // Re-polling the SAME object must still be bounded — terminal means terminal for it.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0);
        orders.TotalCreates.Should().Be(1, "the unchanged bad object is attempted ONCE, then bounded");

        // The supplier fixes the file and re-uploads it to the same key — a new ETag.
        behavior = StubBehavior.Succeed;
        bucket.ETag = "\"etag-fixed\"";
        bucket.Content = fixedUp;

        await using (var db3 = new ProcuLinkDbContext(_options!))
            (await NewService(db3, orders, enqueuer, bucket.Client).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "a terminal claim bounds the BAD OBJECT, not the KEY — the re-upload must import");

        (await OrderCountAsync(orgId)).Should().Be(1, "the corrected object produced the one and only order");

        await using var verify = new ProcuLinkDbContext(_options!);
        var claim = await verify.Set<ImportedS3Object>().SingleAsync(f => f.OrgId == orgId && f.ObjectKey == key);
        claim.ETag.Should().Be("\"etag-fixed\"");
        claim.OrderId.Should().NotBe(Guid.Empty, "the terminal marker is replaced by the re-upload's fresh order id");
        (await verify.PurchaseOrders.AnyAsync(o => o.Id == claim.OrderId)).Should().BeTrue();
    }

    /// <summary>
    /// One-object bucket whose ETag and bytes can CHANGE between polls — models a supplier
    /// overwriting the same key with a corrected PO (S3/R2 stamp a fresh ETag on re-upload).
    /// Both the listing and the download are evaluated per call, so a mutation between polls is
    /// what the service actually sees.
    /// </summary>
    private sealed class MutableObjectS3
    {
        private readonly string _key;
        private string _etag;
        private byte[] _content;

        public MutableObjectS3(string key, string etag, byte[] content)
        {
            _key = key;
            _etag = etag;
            _content = content;

            var s3 = new Mock<IAmazonS3>(MockBehavior.Loose);
            s3.Setup(c => c.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => new ListObjectsV2Response
              {
                  S3Objects = new List<S3Object> { new() { Key = _key, ETag = ETag } },
                  IsTruncated = false,
              });
            s3.Setup(c => c.GetObjectAsync(Bucket, It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(Content) });
            Client = s3.Object;
        }

        /// <summary>Current ETag; volatile because the concurrency proof reads it from many pollers.</summary>
        public string ETag
        {
            get => Volatile.Read(ref _etag);
            set => Volatile.Write(ref _etag, value);
        }

        public byte[] Content
        {
            get => Volatile.Read(ref _content);
            set => Volatile.Write(ref _content, value);
        }

        public IAmazonS3 Client { get; }
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
