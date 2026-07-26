using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Email;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves on REAL Postgres (not EF InMemory) that the Postmark inbound-email webhook parks a
/// supplier-less message instead of rejecting it. Before this, <c>InboundEmailRouter</c> answered
/// 422 <c>inbound_email.rejected_no_supplier</c> and the mail was lost from the product's view.
///
/// <para>What only real Postgres can show here: the router runs against the FULL migrated schema
/// (the unit tests use a trimmed DbContext that Ignores most entities), so the supplier-resolution
/// SQL, the NULL <c>supplier_id</c> column write and the jsonb audit payload are all exercised as
/// they are in production. The stub row the router creates is written exactly as
/// <c>OrderService.CreateUnroutedStubAsync</c> writes it (<c>supplier_id</c> NULL, status
/// <c>parsing</c>); the follow-on <c>parsing → unrouted</c> park is the parse job's single write
/// (<c>OrderIngestionService</c>, <c>if (entity.SupplierId is null) newStatus = Unrouted</c>) and
/// its persistence is proven by <see cref="UnroutedOrderNullSupplierPersistencePostgresTests"/> —
/// one case below applies that same transition to a router-created row so the whole chain is
/// pinned end to end on real Postgres. Skips cleanly where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class InboundEmailUnroutedPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_inbound_unrouted_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    // ── 1. No supplier at all → unrouted hold, NULL supplier_id, 200 ─────────

    [DockerRequiredFact]
    public async Task NoSupplier_ImportsUnrouted_OrderPersistsWithNullSupplier()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 0);

        var orders = new PersistingOrderService(_options!);
        var enqueuer = new CountingParseEnqueuer();

        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, enqueuer);

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        Assert.True(result.Success,
            "a supplier-less org is not an unprocessable message — the webhook answers 200 so Postmark stops retrying");
        Assert.Equal(orgId, result.OrgId);
        var orderId = Assert.Single(result.CreatedOrderIds);
        Assert.Equal(1, orders.UnroutedCalls);
        Assert.Equal(0, orders.RoutedCalls);
        Assert.Equal(1, enqueuer.Count);

        await using var read = new ProcuLinkDbContext(_options!);
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Null(order.SupplierId);
        Assert.Equal(orgId, order.OrgId);
        Assert.Equal(OrderStatusConstants.Parsing, order.Status);
    }

    // ── 2. The parse-job park lands on a router-created row ─────────────────

    [DockerRequiredFact]
    public async Task UnroutedImport_ParksUnrouted_AndReloadsWithNullSupplier()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 0);

        var orders = new PersistingOrderService(_options!);
        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, new CountingParseEnqueuer());

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);
        var orderId = Assert.Single(result.CreatedOrderIds);

        // Apply the park the parse job applies to any supplier-less order — the single writer is
        // OrderIngestionService ("if (entity.SupplierId is null) newStatus = Unrouted"). Driving
        // the real parse here would need storage + parsers; what this pins is that the router's
        // row accepts that exact transition on the real schema and reloads with a NULL supplier.
        await using (var park = new ProcuLinkDbContext(_options!))
        {
            var updated = await park.PurchaseOrders
                .Where(o => o.OrgId == orgId && o.Id == orderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Unrouted)
                    .SetProperty(o => o.UpdatedAt, DateTime.UtcNow));
            Assert.Equal(1, updated);
        }

        await using var read = new ProcuLinkDbContext(_options!);
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.Unrouted, order.Status);
        Assert.Null(order.SupplierId);
    }

    // ── 3. Audit trail: held, not rejected ──────────────────────────────────

    [DockerRequiredFact]
    public async Task NoSupplier_WritesUnroutedAudit_NotRejectedAudit()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 0);

        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, new PersistingOrderService(_options!), new CountingParseEnqueuer());

        await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        await using var read = new ProcuLinkDbContext(_options!);
        var actions = await read.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Contains("inbound_email.unrouted_no_supplier", actions);
        Assert.DoesNotContain("inbound_email.rejected_no_supplier", actions);
        Assert.Contains("inbound_email.processed", actions);
    }

    // ── 4. Soft-deleted-only supplier → unrouted (real SQL null semantics) ──

    [DockerRequiredFact]
    public async Task OnlySoftDeletedSupplier_ImportsUnrouted()
    {
        var (_, slug) = await SeedOrgAsync(suppliers: 0, softDeletedSupplier: true);

        var orders = new PersistingOrderService(_options!);
        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, new CountingParseEnqueuer());

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, orders.UnroutedCalls);
        Assert.Equal(0, orders.RoutedCalls);
    }

    // ── 5. The org's configured default supplier routes the mail ────────────
    //
    // This is now the ONLY thing that routes an inbound email. The org sets it in
    // Settings → Email intake (EmailConfigJson defaultSupplierId), and the router
    // honours it — unchanged behaviour, and the single supported way to get
    // zero-touch email ingest.

    [DockerRequiredFact]
    public async Task ConfiguredDefaultSupplier_Routes()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 1);
        var supplierId = Assert.Single(await ActiveSupplierIdsAsync(orgId));
        await SetEmailDefaultSupplierAsync(orgId, supplierId);

        var orders = new PersistingOrderService(_options!);
        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, new CountingParseEnqueuer());

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, orders.RoutedCalls);
        Assert.Equal(0, orders.UnroutedCalls);

        var orderId = Assert.Single(result.CreatedOrderIds);
        await using var read = new ProcuLinkDbContext(_options!);
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(supplierId, order.SupplierId);
        Assert.Equal(orgId, order.OrgId);
    }

    // ── 6. One active supplier, no default → parks. Never guesses. ──────────
    //
    // The single-supplier case is where guessing looks most defensible — there is only
    // one answer available. It is still a guess the operator never made, so the order
    // parks. This case is what the removed fallback used to route silently, and it is
    // the one measured on production (routing-matrix proof, F1): mail landed on
    // "ProcuLink Sample Supplier", a counterparty nobody chose.

    [DockerRequiredFact]
    public async Task SingleActiveSupplier_NoDefault_ImportsUnrouted_NeverGuesses()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 1);

        var orders = new PersistingOrderService(_options!);
        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, new CountingParseEnqueuer());

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        Assert.True(result.Success,
            "parking is not a rejection — the webhook still answers 200 so Postmark stops retrying");
        Assert.Equal(1, orders.UnroutedCalls);
        Assert.Equal(0, orders.RoutedCalls);

        var orderId = Assert.Single(result.CreatedOrderIds);
        await using var read = new ProcuLinkDbContext(_options!);
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Null(order.SupplierId);
    }

    // ── 7. Several active suppliers, no default → parks ─────────────────────

    [DockerRequiredFact]
    public async Task MultipleActiveSuppliers_NoDefault_ImportsUnrouted()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 3);

        var orders = new PersistingOrderService(_options!);
        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, new CountingParseEnqueuer());

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, orders.UnroutedCalls);
        Assert.Equal(0, orders.RoutedCalls);

        var orderId = Assert.Single(result.CreatedOrderIds);
        await using var read = new ProcuLinkDbContext(_options!);
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Null(order.SupplierId);
    }

    // ── 8. The park is audited even when the org HAS suppliers ──────────────
    //
    // Case 3 pins this audit row for a supplier-LESS org, which was reachable before.
    // An org with suppliers and no default never reached the row at all while the
    // fallback existed, so the operator-visible trail for the case that actually
    // happens in production needs its own pin.

    [DockerRequiredFact]
    public async Task ActiveSuppliersButNoDefault_WritesUnroutedAudit_NotRejectedAudit()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 2);

        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, new PersistingOrderService(_options!), new CountingParseEnqueuer());

        await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        await using var read = new ProcuLinkDbContext(_options!);
        var actions = await read.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Contains("inbound_email.unrouted_no_supplier", actions);
        Assert.DoesNotContain("inbound_email.rejected_no_supplier", actions);
        Assert.Contains("inbound_email.processed", actions);
    }

    // ── 9. A default pointing at a soft-deleted supplier parks — it does not
    //      slide onto the surviving active supplier ───────────────────────────
    //
    // The sharpest anti-guess case: the org DID configure a default, and that default
    // has since been soft-deleted, while another perfectly usable supplier is active.
    // Sliding onto it is exactly the failure mode being removed.

    [DockerRequiredFact]
    public async Task DefaultSupplierSoftDeleted_Parks_DoesNotSlideToSurvivingSupplier()
    {
        var (orgId, slug) = await SeedOrgAsync(suppliers: 1, softDeletedSupplier: true);
        var retiredId = await SoftDeletedSupplierIdAsync(orgId);
        await SetEmailDefaultSupplierAsync(orgId, retiredId);

        var orders = new PersistingOrderService(_options!);
        await using var db = new ProcuLinkDbContext(_options!);
        var router = MakeRouter(db, orders, new CountingParseEnqueuer());

        var result = await router.RouteAsync(PayloadFor(slug, "po.csv"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, orders.UnroutedCalls);
        Assert.Equal(0, orders.RoutedCalls);

        var orderId = Assert.Single(result.CreatedOrderIds);
        await using var read = new ProcuLinkDbContext(_options!);
        var order = await read.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Null(order.SupplierId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(Guid OrgId, string Slug)> SeedOrgAsync(int suppliers, bool softDeletedSupplier = false)
    {
        var orgId = Guid.NewGuid();
        var slug = $"inbound-{orgId:N}";
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_inbound_{orgId:N}",
            Name = "Inbound Email Org",
            Slug = slug,
            Plan = "operations",
            AccountStatus = AccountStatusConstants.Active,
            EmailConfigJson = "{}",
            CreatedAt = now,
        });

        for (var i = 0; i < suppliers; i++)
        {
            db.Suppliers.Add(new Supplier
            {
                Id = Guid.NewGuid(), OrgId = orgId, Name = $"Supplier {i}", CreatedAt = now.AddDays(-i),
            });
        }

        if (softDeletedSupplier)
        {
            db.Suppliers.Add(new Supplier
            {
                Id = Guid.NewGuid(), OrgId = orgId, Name = "Retired supplier",
                CreatedAt = now.AddDays(-30), DeletedAt = now.AddMinutes(-5),
            });
        }

        await db.SaveChangesAsync();
        return (orgId, slug);
    }

    /// <summary>
    /// Writes the org's Email-intake default supplier the way the product writes it —
    /// through <see cref="EmailPollingConfig"/>, the same record
    /// <c>EmailSettingsService.UpdateAsync</c> serialises into the <c>email_config</c>
    /// jsonb column — so these tests bind to the real config contract, not to a
    /// hand-written JSON literal that could drift from it.
    /// </summary>
    private async Task SetEmailDefaultSupplierAsync(Guid orgId, Guid supplierId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var org = await db.Organisations.SingleAsync(o => o.Id == orgId);
        org.EmailConfigJson = (EmailPollingConfig.Empty with { DefaultSupplierId = supplierId }).ToJson();
        await db.SaveChangesAsync();
    }

    private async Task<List<Guid>> ActiveSupplierIdsAsync(Guid orgId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        return await db.Suppliers.AsNoTracking()
            .Where(s => s.OrgId == orgId && s.DeletedAt == null)
            .Select(s => s.Id)
            .ToListAsync();
    }

    private async Task<Guid> SoftDeletedSupplierIdAsync(Guid orgId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        return await db.Suppliers.AsNoTracking()
            .Where(s => s.OrgId == orgId && s.DeletedAt != null)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private static InboundEmailPayload PayloadFor(string slug, string fileName) => new(
        FromEmail: "buyer@example.com",
        ToEmail: $"orders@{slug}.proculink.eu",
        Subject: "PO attached",
        Attachments: new[]
        {
            new InboundAttachment(fileName, "text/csv", Encoding.UTF8.GetBytes("po,qty\r\nUNROUTED-1,5\r\n")),
        });

    private static InboundEmailRouter MakeRouter(
        ProcuLinkDbContext db, IOrderService orders, IParseJobEnqueuer enqueuer)
    {
        // No TenantMapping entries: resolution goes through the org's own Slug column,
        // which is what production does.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new InboundEmailRouter(
            db, orders, enqueuer, NoOpBodyExtractor.Instance, config,
            NullLogger<InboundEmailRouter>.Instance);
    }

    // ── Doubles ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IOrderService"/> double that SELF-COMMITS the order row to the real database,
    /// mirroring <c>OrderService</c>: routed stubs carry the supplier, unrouted stubs carry NULL —
    /// both at status <c>parsing</c>, with the parse job doing the later park. Only the two stub
    /// entry points the router uses are implemented; the rest throw so a silent extra call fails
    /// the test rather than passing unnoticed.
    /// </summary>
    private sealed class PersistingOrderService : IOrderService
    {
        private readonly DbContextOptions<ProcuLinkDbContext> _options;
        private int _routed;
        private int _unrouted;

        public PersistingOrderService(DbContextOptions<ProcuLinkDbContext> options) => _options = options;

        public int RoutedCalls => Volatile.Read(ref _routed);
        public int UnroutedCalls => Volatile.Read(ref _unrouted);

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        {
            Interlocked.Increment(ref _routed);
            return PersistAsync(organisationId, supplierId, filename, ct);
        }

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        {
            Interlocked.Increment(ref _unrouted);
            return PersistAsync(organisationId, supplierId: null, filename, ct);
        }

        private async Task<Result<PurchaseOrderEntity>> PersistAsync(
            Guid orgId, Guid? supplierId, string filename, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var order = new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplierId,
                PoNumber = "PO-STUB",
                Currency = "EUR",
                Status = OrderStatusConstants.Parsing,
                OrderDate = DateOnly.FromDateTime(now),
                SourceFileKey = $"{orgId}/{Guid.NewGuid()}/{filename}",
                CreatedAt = now,
                UpdatedAt = now,
            };

            await using var db = new ProcuLinkDbContext(_options);
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync(ct);

            return Result<PurchaseOrderEntity>.Ok(order);
        }

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid organisationId, Guid supplierId, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        // Body-NLP paths, routed and unrouted: these cases are all about attachments and the
        // extractor here is a no-op, so either call means the router took a path it should not.
        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubFromParsedOrderAsync(Guid organisationId, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid organisationId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid organisationId, Guid orderId, OutputFormat format, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid organisationId, Guid orderId, Guid artifactId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid organisationId, Guid orderId, IReadOnlyList<LineResolution> resolutions, bool saveMappings, CancellationToken ct, ResolveHeaderFields? header = null)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid organisationId, Guid orderId, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>Body-NLP extractor that never yields an order — these cases are about attachments.</summary>
    private sealed class NoOpBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly NoOpBodyExtractor Instance = new();

        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
            => Task.FromResult(new EmailBodyExtractionResult(
                Success: false, Confidence: 0, Order: null, FailureReason: "no-op fake"));
    }
}
