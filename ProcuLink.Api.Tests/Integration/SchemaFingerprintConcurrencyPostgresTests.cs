using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Detection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Defer-list #4 — proves on REAL Postgres that
/// <see cref="SchemaFingerprintService.RecordParseSuccessAsync"/> never loses a
/// <c>ParseSuccessCount</c> increment under concurrency. The previous read-modify-write
/// (<c>existing.ParseSuccessCount += 1</c>) lost a bump whenever two parse jobs for DIFFERENT
/// orders with the SAME column layout ran concurrently — both read the same starting count and
/// the second SaveChanges clobbered the first. The fix issues an atomic relative
/// <c>ExecuteUpdateAsync</c> (<c>parse_success_count = parse_success_count + 1</c>) which the EF
/// InMemory provider cannot translate, so this real-Postgres test is the one that exercises it.
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SchemaFingerprintConcurrencyPostgresTests : IAsyncLifetime
{
    private static readonly string[] LayoutAHeaders = { "po_number", "supplier_code", "sku", "qty", "price" };

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private Guid _orgId;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_fp_{Guid.NewGuid():N}")
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

    private static SchemaFingerprintService NewService(ProcuLinkDbContext db) =>
        new(db, NullLogger<SchemaFingerprintService>.Instance);

    /// <summary>Seeds the shared org + a supplier and returns N distinct 'ready' order ids.</summary>
    private async Task<List<Guid>> SeedOrdersAsync(int count)
    {
        _orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var orderIds = new List<Guid>();

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id            = _orgId,
            ClerkOrgId    = $"org_fp_{_orgId:N}",
            Name          = "Fingerprint Org",
            Slug          = $"fp-{_orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = _orgId, Name = "FP Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        for (var i = 0; i < count; i++)
        {
            var orderId = Guid.NewGuid();
            orderIds.Add(orderId);
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id         = orderId,
                OrgId      = _orgId,
                SupplierId = supplierId,
                PoNumber   = $"PO-FP-{i}",
                OrderDate  = new DateOnly(2026, 6, 1),
                Currency   = "EUR",
                Status     = OrderStatusConstants.PendingReview,
                SourceFileKey = $"uploads/fp-{i}.csv",
                CreatedAt  = now,
                UpdatedAt  = now,
            });
        }
        await db.SaveChangesAsync();
        return orderIds;
    }

    [DockerRequiredFact]
    public async Task ParallelSameLayoutParses_CountEveryIncrement_OneRowNoLostUpdate()
    {
        const int workers = 16;
        var orderIds = await SeedOrdersAsync(workers);

        // Each "worker" gets its own DbContext + tracker (a DbContext is not thread-safe),
        // exactly like parallel Hangfire ParseOrderJob activations. All record the SAME layout
        // for DISTINCT orders — several race the first INSERT (23505 → atomic-update recovery),
        // the rest race the relative UPDATE.
        var tasks = orderIds.Select(async orderId =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            await NewService(db).RecordParseSuccessAsync(
                _orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);
        });
        await Task.WhenAll(tasks);

        await using var verify = new ProcuLinkDbContext(_options!);
        var fp = await verify.SchemaFingerprints.AsNoTracking()
            .SingleAsync(f => f.OrganisationId == _orgId); // exactly one row for the layout
        Assert.Equal(workers, fp.ParseSuccessCount);       // pre-fix this under-counted

        // Every order's idempotency guard was stamped, so a retry can short-circuit.
        var stampedOrders = await verify.PurchaseOrders.AsNoTracking()
            .CountAsync(o => o.OrgId == _orgId && o.SchemaFingerprintHash != null);
        Assert.Equal(workers, stampedOrders);
    }

    [DockerRequiredFact]
    public async Task RepeatedSameOrderParse_IsIdempotent_DoesNotDoubleCount()
    {
        var orderIds = await SeedOrdersAsync(1);
        var orderId = orderIds[0];

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = NewService(db);
            await svc.RecordParseSuccessAsync(_orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);
            // Simulate a Hangfire retry re-running the whole job for the same order.
            await svc.RecordParseSuccessAsync(_orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        var fp = await verify.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == _orgId);
        Assert.Equal(1, fp.ParseSuccessCount); // re-running for the same order must not double-count
    }
}
