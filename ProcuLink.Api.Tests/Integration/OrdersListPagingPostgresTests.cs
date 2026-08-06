using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Offset/limit paging proven on REAL Postgres — not EF InMemory.
///
/// <para>Why a dedicated Postgres test: the user asked for the paging change to be verified
/// on real Postgres, not just EF InMemory. The order list is sorted newest-first by
/// <c>CreatedAt</c>, which is NOT unique — a large bulk API ingest (the scenario that
/// surfaced the bug: ~2000 orders posted through <c>POST /api/ingress/{slug}/orders</c>)
/// stamps many orders with the same <c>CreatedAt</c>. SQL gives NO ordering guarantee for
/// rows with equal sort keys, so <c>Skip/Take</c> over a <c>CreatedAt</c>-only sort can, in
/// principle, let adjacent pages overlap and drop rows. The query adds an <c>Id DESC</c>
/// tiebreaker so the sort is total and every window is provably disjoint, regardless of plan,
/// scan type, or concurrent writes.</para>
///
/// <para>This test seeds 130 orders that ALL share one <c>CreatedAt</c>, pages the whole set
/// with a 50-row window, and asserts the union of pages covers every order exactly once on a
/// real Postgres engine. (Honest note: Postgres often returns a stable scan order for equal
/// keys on a small single-session read, so this test does not by itself force an overlap — it
/// proves correct full coverage on Postgres and guards the deterministic ordering the
/// tiebreaker provides.) Docker-gated (mirrors <see cref="EndToEndPipelineTests"/>) so it
/// skips where Docker is absent instead of failing the suite.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class OrdersListPagingPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_paging");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    [DockerRequiredFact]
    public async Task ListWindow_OverIdenticalCreatedAt_PagesCoverEveryOrderExactlyOnce()
    {
        const int total = 130;
        const int take  = 50;

        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        // One shared timestamp for ALL orders — reproduces the bulk-ingest tie that makes a
        // CreatedAt-only sort non-deterministic on Postgres.
        var sharedCreatedAt = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

        var seededIds = new HashSet<Guid>();
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            // Postgres enforces FK_suppliers_organisations_org_id — seed the parent org first
            // (EF InMemory would silently let this slide).
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_paging_{orgId:N}",
                Name          = "Bulk Ingest Org",
                Slug          = $"bulk-ingest-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = sharedCreatedAt,
            });
            db.Suppliers.Add(new Supplier
            {
                Id = supplierId, OrgId = orgId, Name = "Bulk Ingest Supplier", CreatedAt = sharedCreatedAt,
            });
            // Every order shares one CreatedAt (sharedCreatedAt) — the bulk-ingest tie.
            foreach (var id in OrderListTestSupport.AddOrders(
                         db, orgId, supplierId, total, "pending_review", sharedCreatedAt))
                seededIds.Add(id);
            await db.SaveChangesAsync();
        }

        var collected = new List<Guid>();
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var svc = OrderListTestSupport.BuildOrderService(db);
            for (var skip = 0; skip < total; skip += take)
            {
                var result = await svc.ListWindowAsync(
                    orgId, skip, take, null, null, null, null, null, CancellationToken.None);

                Assert.True(result.IsSuccess);
                var (items, totalCount) = result.Value;
                Assert.Equal(total, totalCount);             // count is the whole set, every page
                collected.AddRange(items.Select(i => i.Id));
            }
        }

        // The union of all windows is the full set, with NO overlap and NO gaps — proven on a
        // real Postgres engine over a tie-heavy dataset.
        Assert.Equal(total, collected.Count);
        Assert.Equal(total, collected.Distinct().Count());
        Assert.Equal(seededIds, collected.ToHashSet());
    }
}
