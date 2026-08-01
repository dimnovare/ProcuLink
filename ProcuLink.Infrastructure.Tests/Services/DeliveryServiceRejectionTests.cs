using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Supplier-rejection ACK: verifies that a 4xx response sets
/// <c>rejected_by_supplier</c> status and populates <c>RejectionReason</c>,
/// while a 5xx keeps the existing <c>delivery_failed</c> path.
/// </summary>
public class DeliveryServiceRejectionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RejectionTestDbContext(options);
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = key,
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-REJ-001",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.ReadyToDeliver,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = orgId,
            Format = "csv",
            FileKey = "artifact.csv",
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId, artifactId);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService? encryption = null) =>
        new(
            db,
            new FakeFileStorage(),
            encryption ?? CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId,
        Guid supplierId,
        DeliveryEncryptionService encryption) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchArtifactAsync_4xxResponse_SetsRejectedBySupplierAndPopulatesRejectionReason()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Supplier rejected: unknown buyer code", 422)),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.ResponseCode.Should().Be(422);
        attempt.RejectionReason.Should().Be("Supplier rejected: unknown buyer code");
        attempt.ErrorMessage.Should().Be("Supplier rejected: unknown buyer code");
    }

    [Fact]
    public async Task DispatchArtifactAsync_4xxResponse_ClosesSlaWindow()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Supplier rejected: unknown buyer code", 422)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);

        // A terminal supplier rejection settles the order: dispatch opened an SLA window
        // (DeliveryDueAt = dispatchStart + SlaWindow) before the send, and once that deadline
        // passes the sweep would raise a false "delivery overdue" on an order the supplier has
        // already rejected. Closing the window here is what prevents that nag.
        order.DeliveryDueAt.Should().BeNull();
        order.SlaBreached.Should().BeFalse();
    }

    // ── WP-19: the 4xx split ──────────────────────────────────────────────────
    //
    // Every 400–499 used to become rejected_by_supplier — a status with no outgoing transitions,
    // excluded from Redeliver, abandoned by the retry queue. An expired API key (401), a moved
    // endpoint (404) or a rate limit (429) therefore dead-ended a perfectly deliverable PO, and a
    // database edit was the only way out. These are the codes that must NOT do that any more.

    // The second argument is WHERE the copy must send the operator, and it is not the same place
    // for all five. 401/403/404 are cured by a configuration change, so the copy has to name the
    // screen. 408 and 429 are NOT: nothing is wrong with the settings, and sending an operator to
    // go audit them after a rate limit is a wrong answer delivered confidently. Asserting one
    // blanket phrase across all five would have forced exactly that copy — this split is the
    // difference between "names the likely cause" and "says something".
    [Theory]
    [InlineData(401, "delivery settings")]     // credentials expired or rotated
    [InlineData(403, "delivery settings")]     // credentials fine, this account may not post orders here
    [InlineData(404, "delivery settings")]     // the endpoint moved
    [InlineData(408, "keeps trying on its own")] // the endpoint did not answer in time
    [InlineData(429, "keeps trying on its own")] // rate limited
    public async Task DispatchArtifactAsync_TransportRefusal_LandsInDeliveryFailedWithCopyNamingTheCause(
        int code, string expectedNextStep)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(
                false, $"HTTP {code}: supplier endpoint returned an error.", code)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed,
            $"HTTP {code} is the supplier's SYSTEM refusing the request, not a judgement on the " +
            "order — rejected_by_supplier has no way out, so landing here is what keeps Retry, " +
            "Send again, the backoff queue and the dead-letter available to the operator");

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.ResponseCode.Should().Be(code);
        attempt.RejectionReason.Should().BeNull(
            "the supplier rejected nothing — recording a rejection reason would invent one");
        attempt.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        attempt.ErrorMessage.Should().Contain(code.ToString(),
            "the copy must name the actual response so it can be matched against the supplier's logs");
        attempt.ErrorMessage.Should().Contain(expectedNextStep,
            "the operator must be told what happens next — and for a configuration fault, WHERE the " +
            "fix is made");

        // The order still owes a delivery, so its SLA window must keep ticking. Only a settled
        // outcome (delivered, or a genuine rejection) closes it — a transport refusal that the
        // queue will retry is exactly the case the sweep exists to notice.
        order.DeliveryDueAt.Should().NotBeNull();
        order.SlaBreached.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchArtifactAsync_400WithASupplierReason_IsAGenuineRejection()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        const string body = "{\"error\":\"unknown buyer code BC-9\"}";
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(
                false, "HTTP 400: supplier endpoint returned an error. Response summary: " + body,
                400, ResponseBody: body)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.RejectedBySupplier,
            "a 400 carrying a reason IS the supplier telling us what is wrong with the document");

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RejectionReason.Should().NotBeNullOrWhiteSpace();
        attempt.ResponseBody.Should().Contain("unknown buyer code BC-9");

        // A settled outcome closes the SLA window — mirrors the 422 case above.
        order.DeliveryDueAt.Should().BeNull();
    }

    [Fact]
    public async Task DispatchArtifactAsync_400WithNoSupplierReason_IsNotARejection()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        // A bare 400 is indistinguishable from a bad URL, a malformed header, or a proxy sitting in
        // front of the supplier. Calling that a business rejection is the dead end in miniature.
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(
                false, "HTTP 400: supplier endpoint returned an error.", 400)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RejectionReason.Should().BeNull();
        attempt.ErrorMessage.Should().Contain("delivery settings");
    }

    /// <summary>
    /// The same bare 400 from a channel that CANNOT read the response body, end-to-end through
    /// <c>DeliveryService</c> — which is what proves the stamp works, not just the pure table.
    ///
    /// <para><b>The defect.</b> The test above asserts a property of the HTTP dispatcher: it read
    /// the response and there was nothing in it. Three other dispatchers never read one at all —
    /// <c>ErpDeliveryDispatcherBase</c> returned <c>(Success, ErrorMessage, ResponseCode)</c> and
    /// <c>EmailApiDeliveryDispatcher</c> the same — so their 400s took this branch by DEFAULT and
    /// were re-dispatched to the live endpoint up to the cap. Email is the canonical outbound path,
    /// live on production; both ERP channels declare <c>ResendSafety.Unsafe</c>.</para>
    ///
    /// <para>All three capture the body now, so this shape has no production dispatcher today. It is
    /// pinned anyway, because the next dispatcher inherits <c>false</c> and must land here.</para>
    /// </summary>
    [Fact]
    public async Task DispatchArtifactAsync_BareBadRequest_FromAChannelThatCannotSeeTheBody_IsNotRetried()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(
                new DeliveryResult(false, "HTTP 400: endpoint returned an error.", 400),
                capturesSupplierResponseBody: false),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.RejectedBySupplier,
            "we cannot show the supplier said nothing, so the safe reading is that the refusal was " +
            "theirs: no further requests, and an order in a status that (since WP-19) an operator " +
            "can resolve or re-transform out of");

        SupplierResponseClassification.SuppressesAutomaticRetry(result).Should().BeTrue(
            "the status decision and the retry gate are ONE decision — a status the delivery claim " +
            "refuses but the queue keeps retrying burns the backoff budget on 0 claimed rows");

        result.SupplierReasonObservable.Should().BeFalse(
            "DeliveryService must stamp the dispatcher's capability onto the result it returns, or " +
            "the two jobs read a different answer than the status decision did");
    }

    // ── WP-19 recovery: the wait the supplier asked for, persisted ────────────
    //
    // Retry-After was read off the wire, carried on DeliveryResult, spent ONCE computing a Hangfire
    // delay — and then dropped. It reached no column, no DTO and no JSON, so the product could tell
    // an operator "we'll keep trying" but never "for the next 4 minutes". The number the queue is
    // already obeying is now recorded on the attempt that observed it.

    [Fact]
    public async Task DispatchArtifactAsync_RateLimitWithRetryAfter_PersistsTheWaitInWholeSeconds()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(
                false, "HTTP 429: supplier endpoint returned an error.", 429,
                RetryAfter: TimeSpan.FromSeconds(90))),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RetryAfterSeconds.Should().Be(90,
            "the supplier told us how long to wait and the queue is already obeying it — a client " +
            "that can only say 'we'll keep trying' is withholding the one number the operator wants");
    }

    [Fact]
    public async Task DispatchArtifactAsync_NoRetryAfter_LeavesTheWaitNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Gateway timeout", 503)),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RetryAfterSeconds.Should().BeNull(
            "null means the supplier asked for no particular wait — inventing a default here would " +
            "show the operator a countdown nobody committed to");
    }

    [Fact]
    public async Task DispatchArtifactAsync_RetryAfterBeyondTheCeiling_IsClampedToWhatTheQueueWillActuallyWait()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        // A supplier asking for 24 hours. RetryAfterHeader.MaxHonoured bounds what the scheduler
        // will honour, so persisting the raw ask would publish a wait the queue has no intention of
        // keeping — the stranded-delivery sweep re-drives the order long before it elapses.
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(
                false, "HTTP 429: supplier endpoint returned an error.", 429,
                RetryAfter: TimeSpan.FromHours(24))),
            encryption);

        await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RetryAfterSeconds.Should().Be((int)RetryAfterHeader.MaxHonoured.TotalSeconds,
            "the stored number is a PROMISE to the operator, so it must be the wait the scheduler " +
            "will actually keep, not the one the supplier asked for");
    }

    [Fact]
    public async Task DispatchArtifactAsync_5xxResponse_SetsDeliveryFailedAndLeavesRejectionReasonNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Gateway timeout", 503)),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Status.Should().Be("failed");
        attempt.ResponseCode.Should().Be(503);
        attempt.RejectionReason.Should().BeNull();
        attempt.ErrorMessage.Should().Be("Gateway timeout");
    }

    [Fact]
    public async Task DispatchArtifactAsync_NetworkFailureNoCode_SetsDeliveryFailedAndLeavesRejectionReasonNull()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption));
        await db.SaveChangesAsync();

        // Simulate network failure — no response code
        var service = CreateService(
            db,
            new FakeDispatcher(new DeliveryResult(false, "Connection refused", null)),
            encryption);

        var result = await service.DispatchArtifactAsync(
            ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync();
        order.Status.Should().Be(OrderStatusConstants.DeliveryFailed);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.RejectionReason.Should().BeNull();
    }

    // ── Helpers shared inside this fixture ────────────────────────────────────

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for the <c>http</c> dispatcher, which DOES read the supplier's response body — so
    /// it must say so, exactly as the real one does. <c>DeliveryService</c> stamps
    /// <c>SupplierReasonObservable</c> onto the result from this declaration, and the bare-400 split
    /// turns on it: a fake that stayed silent would quietly turn every 400 in this fixture into a
    /// business rejection, which is the RIGHT answer for a channel that cannot see the body and the
    /// wrong one for the channel this fake represents.
    /// </summary>
    private sealed class FakeDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public string Protocol => "http";
        public bool CapturesSupplierResponseBody { get; }

        public FakeDispatcher(DeliveryResult result, bool capturesSupplierResponseBody = true)
        {
            _result = result;
            CapturesSupplierResponseBody = capturesSupplierResponseBody;
        }

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null) => Task.FromResult(_result);
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RejectionTestDbContext : ProcuLinkDbContext
    {
        public RejectionTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });

            modelBuilder.Entity<OutboundArtifact>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            modelBuilder.Entity<SupplierDeliveryConfig>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
            });

            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            // OrderExceptionService.ReconcileAsync (invoked from DeliveryService) queries
            // lines + exceptions, so both must be mapped in this trimmed model.
            modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
            });

            modelBuilder.Entity<OrderException>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
