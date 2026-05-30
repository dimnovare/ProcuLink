using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Services.Email;

namespace ProcuLink.Infrastructure.Tests.Services.Email;

/// <summary>
/// Verifies that the inbound-email router routes attachments to the right
/// tenant, skips unsupported types, gates on account status, and creates
/// one order stub + parse job per accepted attachment.
/// </summary>
public class InboundEmailRouterTests
{
    private const string Slug = "acme";

    // ── 1. Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_SingleCsvAttachment_CreatesOneOrderAndEnqueuesParseJob()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        var supplierId = await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "PO #12345",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,date\r\n001,2026-05-28")),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1);

        orders.CalledWith.Should().HaveCount(1);
        orders.CalledWith[0].OrgId.Should().Be(orgId);
        orders.CalledWith[0].SupplierId.Should().Be(supplierId);
        orders.CalledWith[0].FileName.Should().Be("po.csv");

        enqueuer.Calls.Should().HaveCount(1);
        enqueuer.Calls[0].OrderId.Should().Be(result.CreatedOrderIds[0]);
        enqueuer.Calls[0].OrgId.Should().Be(orgId);
    }

    // ── 2. Multiple attachments ──────────────────────────────────────────────

    [Fact]
    public async Task MultipleSupportedAttachments_CreatesMultipleOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Trialing);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "Multiple POs",
            Attachments: new[]
            {
                new InboundAttachment("po-a.csv",  "text/csv", new byte[] { 1, 2, 3 }),
                new InboundAttachment("po-b.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new byte[] { 4, 5, 6 }),
                new InboundAttachment("po-c.pdf",  "application/pdf", new byte[] { 7, 8, 9 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.CreatedOrderIds.Should().HaveCount(3);
        orders.CalledWith.Should().HaveCount(3);
        enqueuer.Calls.Should().HaveCount(3);

        // Each attachment produced a distinct order id.
        result.CreatedOrderIds.Distinct().Should().HaveCount(3);
    }

    // ── 3. Unsupported attachment ────────────────────────────────────────────

    [Fact]
    public async Task UnsupportedAttachment_IsSkippedReturnsSuccessWithEmptyList()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "Word doc disguised as PO",
            Attachments: new[]
            {
                new InboundAttachment("po.docx",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue("the message itself was valid even if no attachment was usable");
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().BeEmpty();

        orders.CalledWith.Should().BeEmpty("the .docx attachment must not reach CreateStubAsync");
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 4. Unknown recipient ─────────────────────────────────────────────────

    [Fact]
    public async Task UnknownRecipient_ReturnsFailureWithoutCreatingOrders()
    {
        await using var db = CreateDb();
        // No org seeded — the mapping config also points nowhere useful.

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "orders@unknown-tenant.proculink.app",
            Subject:   "Mystery PO",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull();
        result.CreatedOrderIds.Should().BeEmpty();
        result.Error.Should().NotBeNullOrWhiteSpace();

        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 5. Read-only tenant gate ─────────────────────────────────────────────

    [Fact]
    public async Task ReadOnlyTenant_ReturnsFailureAndCreatesNoOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.ReadOnly);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "PO during read-only",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().Be(orgId, "the tenant resolved — we just refused to ingest");
        result.CreatedOrderIds.Should().BeEmpty();
        result.Error.Should().Contain("read_only");

        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 6. Trial-expired tenant gate ─────────────────────────────────────────

    [Fact]
    public async Task TrialExpiredTenant_ReturnsFailureAndCreatesNoOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.TrialExpired);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "PO after trial",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().BeEmpty();
        result.Error.Should().Contain("trial_expired");

        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 7. No attachments at all ─────────────────────────────────────────────

    [Fact]
    public async Task NoAttachments_ReturnsSuccessWithEmptyList()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "Just a note, no file",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().BeEmpty();
        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 8. Mixed supported + unsupported attachments ─────────────────────────

    [Fact]
    public async Task MixedAttachments_OnlySupportedTypesCreateOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "Mixed bag",
            Attachments: new[]
            {
                new InboundAttachment("po.csv",         "text/csv",                   new byte[] { 1, 2, 3 }),
                new InboundAttachment("signature.png",  "image/png",                  new byte[] { 4, 5, 6 }),
                new InboundAttachment("notes.docx",     "application/msword",         new byte[] { 7, 8, 9 }),
                new InboundAttachment("backup.xml",     "application/xml",            new byte[] { 10, 11 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.CreatedOrderIds.Should().HaveCount(2,
            "only the .csv and .xml attachments are in the supported set");
        orders.CalledWith.Select(c => c.FileName).Should().BeEquivalentTo(new[] { "po.csv", "backup.xml" });
    }

    // ── 9. Email-body NLP fallback ───────────────────────────────────────────

    [Fact]
    public async Task BodyExtractionPath_NoAttachments_CreatesStubFromExtractedOrder()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();

        // Extractor returns a successful extraction — the router must call
        // CreateStubFromParsedOrderAsync (NOT CreateStubAsync) and report the
        // resulting order id back to the caller.
        var extractedOrder = new ExtractedOrder(
            PoNumber:  "PO-FROM-BODY-001",
            OrderDate: new DateTime(2026, 5, 28),
            BuyerName: "Acme Buyer",
            Currency:  "EUR",
            Lines: new[]
            {
                new ExtractedOrderLine(1, "WIDGET-A", "Widget A blue", 10m, "pcs", 2.50m),
                new ExtractedOrderLine(2, "WIDGET-B", "Widget B red",   5m, "pcs", 3.00m),
            });
        var extractor = new FakeBodyExtractor(
            new EmailBodyExtractionResult(Success: true, Confidence: 0.85, Order: extractedOrder, FailureReason: null));

        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId, extractor: extractor);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.app",
            Subject:   "Order request (no attachment)",
            Attachments: Array.Empty<InboundAttachment>(),
            Body: "Hi team, please send 10 of WIDGET-A at 2.50 EUR and 5 of WIDGET-B at 3.00 EUR. Thanks!");

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1,
            "the extractor returned a usable order from the email body");

        // The body path uses the new CreateStubFromParsedOrderAsync — the
        // attachment path (CreateStubAsync) must not have been invoked.
        orders.CalledWith.Should().BeEmpty();
        orders.ParsedOrderCalls.Should().HaveCount(1);
        orders.ParsedOrderCalls[0].OrgId.Should().Be(orgId);
        orders.ParsedOrderCalls[0].Source.Should().Be("email_body_nlp");
        orders.ParsedOrderCalls[0].Order.PoNumber.Should().Be("PO-FROM-BODY-001");
        orders.ParsedOrderCalls[0].Order.Lines.Should().HaveCount(2);

        // No parse job — the order is already populated, there is nothing to parse.
        enqueuer.Calls.Should().BeEmpty();

        extractor.Calls.Should().Be(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InboundEmailRouter MakeRouter(
        ProcuLinkDbContext db,
        IOrderService orders,
        IParseJobEnqueuer enqueuer,
        string slug,
        Guid orgId,
        IEmailBodyOrderExtractor? extractor = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Inbound:Postmark:TenantMapping:{slug}"] = orgId.ToString(),
            })
            .Build();

        return new InboundEmailRouter(
            db, orders, enqueuer,
            extractor ?? FakeBodyExtractor.NoOp,
            config,
            NullLogger<InboundEmailRouter>.Instance);
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string accountStatus)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Acme Distribution",
            AccountStatus = accountStatus,
            CreatedAt = DateTime.UtcNow,
            EmailConfigJson = "{}",
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static async Task<Guid> SeedSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Acme Components",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InboundEmailTestDbContext(options);
    }

    // ── Doubles ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the args passed to <see cref="IOrderService.CreateStubAsync"/>
    /// and returns a successful stub. Other methods throw — the router must
    /// not touch them.
    /// </summary>
    private sealed class FakeOrderService : IOrderService
    {
        public List<(Guid OrgId, Guid SupplierId, string FileName, string ContentType, long Size)> CalledWith { get; } = new();
        public List<(Guid OrgId, Guid SupplierId, ExtractedOrder Order, string Source)> ParsedOrderCalls { get; } = new();

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            // Drain the stream so we record the actual byte count the router sent.
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            CalledWith.Add((organisationId, supplierId, filename, contentType, ms.Length));

            var stub = new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                SupplierId = supplierId,
                Status = "parsing",
                SourceFileKey = $"{organisationId}/{Guid.NewGuid()}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
            Guid organisationId, Guid supplierId, ExtractedOrder order, string source, CancellationToken ct)
        {
            ParsedOrderCalls.Add((organisationId, supplierId, order, source));

            var stub = new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                SupplierId = supplierId,
                Status = "pending_review",
                SourceFileKey = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
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
    /// Test double for <see cref="IEmailBodyOrderExtractor"/>. Returns a fixed
    /// result on every call; <see cref="NoOp"/> short-circuits with
    /// <c>Success=false</c> so tests that don't care about the body path see
    /// the router behave exactly as before.
    /// </summary>
    private sealed class FakeBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly FakeBodyExtractor NoOp = new(
            new EmailBodyExtractionResult(Success: false, Confidence: 0, Order: null, FailureReason: "no-op fake"));

        private readonly EmailBodyExtractionResult _result;
        public int Calls { get; private set; }

        public FakeBodyExtractor(EmailBodyExtractionResult result) { _result = result; }

        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingEnqueuer : IParseJobEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory DbContext that materialises only what the router
    /// touches: Organisations, Suppliers, and AuditEvents. Other entities are
    /// ignored to avoid fabricating fixtures.
    /// </summary>
    private sealed class InboundEmailTestDbContext : ProcuLinkDbContext
    {
        public InboundEmailTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.SupplierProfiles);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.PoMappings);
                b.Ignore(x => x.DeliveryConfigs);
            });

            modelBuilder.Entity<AuditEvent>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.User);
                // JsonDocument is not supported by InMemory; ignore the payload
                // column so audit writes don't blow up. The router writes audit
                // best-effort and swallows exceptions either way.
                b.Ignore(x => x.Payload);
            });
        }
    }
}
