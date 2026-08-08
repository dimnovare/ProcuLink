using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The EF InMemory twin of this file (<c>OrgQueryFilterIsolationTests</c>) proves the filter is
/// applied by the query pipeline. It cannot prove the predicate TRANSLATES: InMemory evaluates
/// everything client-side, so a filter shape Npgsql could not turn into SQL would still pass
/// there and then fail — or, worse, silently degrade — against the real database.
///
/// <para>These tests run the same unpredicated reads against real Postgres over the real migrated
/// schema, with two real organisations.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class OrgQueryFilterPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_orgfilter");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_databaseConnectionString);

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string label)
    {
        var now = DateTime.UtcNow;
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{label}_{orgId:N}",
            Name = $"Org-{label}",
            Slug = $"org-{label.ToLowerInvariant()}-{orgId:N}",
            Plan = "pilot",
            AccountStatus = "trialing",
            CreatedAt = now,
            TrialStartedAt = now,
            TrialEndsAt = now.AddDays(14),
        });

        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = $"Supplier-{label}",
            CreatedAt = now,
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = $"PO-{label}-{orgId:N}",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = "ready",
            SourceFileKey = $"{orgId}/{Guid.NewGuid()}/file.csv",
            CreatedAt = now,
            UpdatedAt = now,
            Lines = [],
            OutboundArtifacts = [],
        });

        await db.SaveChangesAsync();
        return orgId;
    }

    [DockerRequiredFact]
    public async Task UnpredicatedQuery_AgainstRealPostgres_CannotSeeAnotherOrganisationsRows()
    {
        Guid orgA, orgB;
        await using (var seed = new ProcuLinkDbContext(_options!))
        {
            orgA = await SeedOrgAsync(seed, "a");
            orgB = await SeedOrgAsync(seed, "b");
        }

        await using var db = new ProcuLinkDbContext(_options!);
        db.ScopeToOrganisation(orgA);

        // No organisation predicate — the filter is the only thing standing between this query
        // and org B's rows.
        var orders = await db.PurchaseOrders.AsNoTracking().ToListAsync();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.Equal(orgA, o.OrgId));
        Assert.DoesNotContain(orders, o => o.OrgId == orgB);
    }

    /// <summary>
    /// Proves the filter reaches the database rather than being applied after the rows arrive. If
    /// the predicate were only enforced client-side, every tenant's rows would still cross the
    /// wire before being discarded.
    ///
    /// <para>Also pins the SHAPE. The rejected single-model design emitted
    /// <c>WHERE @p IS NULL OR org_id = @p</c>, which is not sargable and would have cost the
    /// <c>(org_id, …)</c> indexes on 47 tables. The scoped predicate must be a bare equality.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task TheOrganisationPredicate_IsTranslatedIntoSargableSql()
    {
        await using var db = new ProcuLinkDbContext(_options!);
        db.ScopeToOrganisation(Guid.NewGuid());

        var sql = db.PurchaseOrders.AsNoTracking().ToQueryString();

        Assert.Contains("org_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IS NULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half, and the reason the model cache is split: an UNSCOPED context must emit the
    /// same SQL it emitted before the filters existed. Every cross-organisation sweep in the
    /// Worker runs on an unscoped context over the largest tables in the schema, and none of them
    /// should pay for a filter that is not protecting them.
    /// </summary>
    [DockerRequiredFact]
    public async Task UnscopedContext_EmitsNoOrganisationPredicateAtAll()
    {
        await using var db = new ProcuLinkDbContext(_options!);

        var sql = db.PurchaseOrders.AsNoTracking().ToQueryString();

        Assert.DoesNotContain("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ef_filter", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cross-organisation sweeps are the Worker's normal mode. Against the real provider, an
    /// unscoped context must return every organisation's rows — the sweep must not quietly become
    /// a no-op.
    /// </summary>
    [DockerRequiredFact]
    public async Task UnscopedContext_AgainstRealPostgres_StillSeesEveryOrganisation()
    {
        Guid orgA, orgB;
        await using (var seed = new ProcuLinkDbContext(_options!))
        {
            orgA = await SeedOrgAsync(seed, "a");
            orgB = await SeedOrgAsync(seed, "b");
        }

        await using var db = new ProcuLinkDbContext(_options!);
        db.UseCrossOrganisationScope("worker sweeps every organisation");

        var orgIds = await db.PurchaseOrders.AsNoTracking().Select(o => o.OrgId).ToListAsync();

        Assert.Contains(orgA, orgIds);
        Assert.Contains(orgB, orgIds);
    }
}
