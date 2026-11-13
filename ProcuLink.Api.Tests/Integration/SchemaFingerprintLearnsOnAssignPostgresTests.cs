using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// BE-5 P0 on REAL Postgres — the schema fingerprint recorder's re-entry path. The recorder's
/// atomic count bump is <c>ExecuteUpdateAsync</c>, which EF InMemory cannot translate, so the
/// relational branch of <see cref="SchemaFingerprintService.RecordParseSuccessAsync"/> — the one
/// production runs — is only exercised here.
///
/// The operator-assigns-then-re-parse fact retired with <c>POST /api/orders/{id}/assign-supplier</c>
/// (plan 13b: the order API is a read-only archive). What is left is the Hangfire-retry shape: a
/// re-run for an already-bound order must not bump <c>ParseSuccessCount</c> or duplicate the binding.
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

    // ── Harness ─────────────────────────────────────────────────────────────────
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
