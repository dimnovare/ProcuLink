using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// BE-5 P0 on REAL Postgres — the schema fingerprint must learn the supplier an operator picks
/// on an unrouted order. The full chain matters and only Postgres runs it: the recorder's atomic
/// count bump and <c>assign-supplier</c>'s atomic status claim are both
/// <c>ExecuteUpdateAsync</c>, which EF InMemory cannot translate, so the relational branch of
/// <see cref="SchemaFingerprintService.RecordParseSuccessAsync"/> — the one production runs — is
/// only exercised here.
///
/// Sequence under test (each step in its own DbContext, as separate Hangfire activations and an
/// HTTP request really are): unrouted parse → operator assigns → re-parse. The re-parse must add
/// the chosen supplier to <c>SupplierIdsCsv</c> and must NOT bump <c>ParseSuccessCount</c>.
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SchemaFingerprintLearnsOnAssignPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly string[] LayoutHeaders = { "po_number", "supplier_code", "sku", "qty", "price" };

    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_fplearn");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── Harness (mirrors AssignSupplierPostgresTests) ───────────────────────────
    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());
    }

    private static SchemaFingerprintService NewService(ProcuLinkDbContext db) =>
        new(db, NullLogger<SchemaFingerprintService>.Instance);

    private async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedUnroutedOrderAsync(string slug)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{slug}_{orgId:N}", Name = slug, Slug = $"{slug}-{orgId:N}",
            Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Corrected Supplier", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = null,          // arrived on a content-routed channel
            PoNumber = "PO-UNROUTED-1", Currency = "EUR", Status = OrderStatusConstants.Unrouted,
            OrderDate = new DateOnly(2026, 7, 24), SourceFileKey = $"uploads/{slug}.csv",
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task AssignSupplier_ThenReParse_BindsChosenSupplierToLayout_WithoutRecounting()
    {
        var (orgId, supplierId, orderId) = await SeedUnroutedOrderAsync("fplearn");

        // 1. ParseOrderJob records the layout while the order is still unrouted.
        await using (var db = new ProcuLinkDbContext(_options!))
            await NewService(db).RecordParseSuccessAsync(orgId, orderId, LayoutHeaders, "csv", CancellationToken.None);

        await using (var check = new ProcuLinkDbContext(_options!))
        {
            var fp = await check.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);
            Assert.Equal(string.Empty, fp.SupplierIdsCsv);   // nothing to bind yet — correct
        }

        // 2. The operator resolves the routing through the real endpoint (atomic claim + re-enqueue).
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await BuildController(db, orgId)
                .AssignSupplier(orderId, new AssignSupplierRequest(supplierId), CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        // 3. The re-enqueued ParseOrderJob re-enters the recorder for the same order.
        await using (var db = new ProcuLinkDbContext(_options!))
            await NewService(db).RecordParseSuccessAsync(orgId, orderId, LayoutHeaders, "csv", CancellationToken.None);

        await using var verify = new ProcuLinkDbContext(_options!);
        var learned = await verify.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);

        Assert.Equal(supplierId.ToString(), learned.SupplierIdsCsv);
        Assert.Equal(1, learned.ParseSuccessCount);   // the same document re-parsed is not a new sighting

        // The binding must be visible through the API the future auto-routing reads.
        var match = await NewService(verify).LookupAsync(orgId, LayoutHeaders, CancellationToken.None);
        Assert.True(match!.IsBoundTo(supplierId));
        Assert.False(match.IsSharedLayout);
    }

    [DockerRequiredFact]
    public async Task ReParse_WithNoNewSupplier_LeavesFingerprintUntouched()
    {
        var (orgId, supplierId, orderId) = await SeedUnroutedOrderAsync("fpnoop");

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            // Route this one at ingest, so the first parse already binds the supplier.
            var order = await db.PurchaseOrders.FirstAsync(o => o.Id == orderId);
            order.SupplierId = supplierId;
            order.Status = OrderStatusConstants.PendingReview;
            await db.SaveChangesAsync();
            await NewService(db).RecordParseSuccessAsync(orgId, orderId, LayoutHeaders, "csv", CancellationToken.None);
        }

        DateTime firstSeenAt;
        await using (var check = new ProcuLinkDbContext(_options!))
            firstSeenAt = (await check.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId)).LastSeenAt;

        // A Hangfire retry re-runs the job for the same, already-bound order.
        await using (var db = new ProcuLinkDbContext(_options!))
            await NewService(db).RecordParseSuccessAsync(orgId, orderId, LayoutHeaders, "csv", CancellationToken.None);

        await using var verify = new ProcuLinkDbContext(_options!);
        var fp = await verify.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);
        Assert.Equal(1, fp.ParseSuccessCount);
        Assert.Equal(supplierId.ToString(), fp.SupplierIdsCsv);   // bound once, not duplicated
        Assert.Equal(firstSeenAt, fp.LastSeenAt);                 // learning is the only re-entry side effect
    }
}
