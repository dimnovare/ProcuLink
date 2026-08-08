using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// The defect, verbatim: a query written WITHOUT an explicit organisation predicate must not
/// return another organisation's rows.
///
/// <para>Every query in this file is deliberately unpredicated — no <c>.Where(x =&gt; x.OrgId ==
/// orgId)</c> anywhere — because a test that writes the predicate proves only that the predicate
/// works, which was never in doubt. Two real organisations are seeded, so "returns nothing" and
/// "returns only mine" are distinguishable; a single-org fixture would pass either way and prove
/// nothing.</para>
/// </summary>
public sealed class OrgQueryFilterIsolationTests
{
    private static DbContextOptions<ProcuLinkDbContext> SharedOptions(string dbName) =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

    /// <summary>Seeds one organisation with a supplier and one order, and returns its id.</summary>
    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string label)
    {
        var now = DateTime.UtcNow;
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{label}",
            Name = $"Org-{label}",
            Slug = $"org-{label.ToLowerInvariant()}",
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
            PoNumber = $"PO-{label}-001",
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

    [Fact]
    public async Task UnpredicatedQuery_OnAScopedContext_CannotSeeAnotherOrganisationsRows()
    {
        var dbName = $"org-filter-{Guid.NewGuid()}";

        Guid orgA, orgB;
        await using (var seed = new ProcuLinkDbContext(SharedOptions(dbName)))
        {
            orgA = await SeedOrgAsync(seed, "A");
            orgB = await SeedOrgAsync(seed, "B");
        }

        await using var db = new ProcuLinkDbContext(SharedOptions(dbName));
        db.ScopeToOrganisation(orgA);

        // No .Where(o => o.OrgId == orgA). This is the query the defect is about.
        var orders = await db.PurchaseOrders.AsNoTracking().ToListAsync();
        var suppliers = await db.Suppliers.AsNoTracking().ToListAsync();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.Equal(orgA, o.OrgId));
        Assert.DoesNotContain(orders, o => o.OrgId == orgB);

        Assert.NotEmpty(suppliers);
        Assert.All(suppliers, s => Assert.Equal(orgA, s.OrgId));
    }

    /// <summary>
    /// A by-id read is the shape that leaks a specific record. Fetching org B's order by its
    /// primary key, with no organisation predicate, must come back null.
    /// </summary>
    [Fact]
    public async Task UnpredicatedByIdRead_CannotFetchAnotherOrganisationsRow()
    {
        var dbName = $"org-filter-byid-{Guid.NewGuid()}";

        Guid orgA;
        Guid orgBOrderId;
        await using (var seed = new ProcuLinkDbContext(SharedOptions(dbName)))
        {
            orgA = await SeedOrgAsync(seed, "A");
            await SeedOrgAsync(seed, "B");

            orgBOrderId = await seed.PurchaseOrders
                .Where(o => o.OrgId != orgA)
                .Select(o => o.Id)
                .SingleAsync();
        }

        await using var db = new ProcuLinkDbContext(SharedOptions(dbName));
        db.ScopeToOrganisation(orgA);

        var leaked = await db.PurchaseOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orgBOrderId);

        Assert.Null(leaked);
    }

    /// <summary>
    /// The single load-bearing assumption in OrgQueryFilters: the filter expression closes over a
    /// DbContext instance, but the model that holds it is built once and cached, then reused by
    /// every later context. If EF did not rewrite that reference to the executing context, the
    /// first instance's scope would silently apply to all of them — every request would read the
    /// organisation of whichever request happened to build the model first.
    ///
    /// <para>Both contexts below share one options instance, so they share one cached model. Each
    /// is armed to a different organisation and must see only its own rows.</para>
    /// </summary>
    [Fact]
    public async Task TwoContextsSharingOneCachedModel_EachSeeOnlyItsOwnOrganisation()
    {
        var dbName = $"org-filter-cache-{Guid.NewGuid()}";
        var options = SharedOptions(dbName);

        Guid orgA, orgB;
        await using (var seed = new ProcuLinkDbContext(options))
        {
            orgA = await SeedOrgAsync(seed, "A");
            orgB = await SeedOrgAsync(seed, "B");
        }

        await using var contextA = new ProcuLinkDbContext(options);
        await using var contextB = new ProcuLinkDbContext(options);
        contextA.ScopeToOrganisation(orgA);
        contextB.ScopeToOrganisation(orgB);

        var seenByA = await contextA.PurchaseOrders.AsNoTracking().Select(o => o.OrgId).ToListAsync();
        var seenByB = await contextB.PurchaseOrders.AsNoTracking().Select(o => o.OrgId).ToListAsync();

        Assert.Equal([orgA], seenByA);
        Assert.Equal([orgB], seenByB);
    }

    /// <summary>
    /// The Worker runs 13 cross-organisation sweeps with no tenant context at all. An unscoped
    /// context must therefore still see every organisation — the filter must be inert, not
    /// empty. A sweep that silently matched nothing would log a successful run over zero rows,
    /// which is the failure mode that does not page anyone.
    /// </summary>
    [Fact]
    public async Task UnscopedContext_StillSeesEveryOrganisation()
    {
        var dbName = $"org-filter-unscoped-{Guid.NewGuid()}";

        Guid orgA, orgB;
        await using (var seed = new ProcuLinkDbContext(SharedOptions(dbName)))
        {
            orgA = await SeedOrgAsync(seed, "A");
            orgB = await SeedOrgAsync(seed, "B");
        }

        await using var db = new ProcuLinkDbContext(SharedOptions(dbName));

        var orgIds = await db.PurchaseOrders.AsNoTracking().Select(o => o.OrgId).ToListAsync();

        Assert.Contains(orgA, orgIds);
        Assert.Contains(orgB, orgIds);
    }

    /// <summary>Same, but with the intent declared rather than left to inference.</summary>
    [Fact]
    public async Task ExplicitCrossOrganisationScope_SeesEveryOrganisation()
    {
        var dbName = $"org-filter-crossorg-{Guid.NewGuid()}";

        Guid orgA, orgB;
        await using (var seed = new ProcuLinkDbContext(SharedOptions(dbName)))
        {
            orgA = await SeedOrgAsync(seed, "A");
            orgB = await SeedOrgAsync(seed, "B");
        }

        await using var db = new ProcuLinkDbContext(SharedOptions(dbName));
        db.UseCrossOrganisationScope("billing reconciliation sweeps every organisation");

        var orgIds = await db.PurchaseOrders.AsNoTracking().Select(o => o.OrgId).ToListAsync();

        Assert.Contains(orgA, orgIds);
        Assert.Contains(orgB, orgIds);
        Assert.Equal("billing reconciliation sweeps every organisation", db.CrossOrganisationReason);
    }

    // ── the arming API refuses the states that would read as clean ───────────

    [Fact]
    public void ScopingToEmptyGuid_IsRefused()
    {
        using var db = new ProcuLinkDbContext(SharedOptions($"org-filter-empty-{Guid.NewGuid()}"));

        var ex = Assert.Throws<ArgumentException>(() => db.ScopeToOrganisation(Guid.Empty));
        Assert.Contains("Guid.Empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReScopingToADifferentOrganisation_IsRefused()
    {
        using var db = new ProcuLinkDbContext(SharedOptions($"org-filter-rescope-{Guid.NewGuid()}"));
        db.ScopeToOrganisation(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => db.ScopeToOrganisation(Guid.NewGuid()));
    }

    [Fact]
    public void DeclaringCrossOrganisationScopeOnAScopedContext_IsRefused()
    {
        using var db = new ProcuLinkDbContext(SharedOptions($"org-filter-mixed-{Guid.NewGuid()}"));
        db.ScopeToOrganisation(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => db.UseCrossOrganisationScope("ops sweep"));
    }

    [Fact]
    public void CrossOrganisationScopeWithoutAReason_IsRefused()
    {
        using var db = new ProcuLinkDbContext(SharedOptions($"org-filter-noreason-{Guid.NewGuid()}"));

        Assert.Throws<ArgumentException>(() => db.UseCrossOrganisationScope("  "));
    }

    // ── the hazard the model-cache split introduces ──────────────────────────

    /// <summary>
    /// The model is resolved once per context instance, so a context that has already queried is
    /// bound to the unfiltered model and can no longer be scoped. Arming it at that point would
    /// leave every subsequent query reading across tenants while the call site says
    /// <c>ScopeToOrganisation(orgId)</c> — a silent fail-open, and the single sharp edge the cache
    /// split adds. It must throw.
    /// </summary>
    [Fact]
    public async Task ScopingAfterTheFirstQuery_IsRefusedRatherThanSilentlyIgnored()
    {
        var dbName = $"org-filter-late-{Guid.NewGuid()}";

        Guid orgA;
        await using (var seed = new ProcuLinkDbContext(SharedOptions(dbName)))
        {
            orgA = await SeedOrgAsync(seed, "A");
            await SeedOrgAsync(seed, "B");
        }

        await using var db = new ProcuLinkDbContext(SharedOptions(dbName));

        // First query runs unscoped, binding this instance to the filter-free model.
        _ = await db.PurchaseOrders.AsNoTracking().CountAsync();

        var ex = Assert.Throws<InvalidOperationException>(() => db.ScopeToOrganisation(orgA));
        Assert.Contains("already resolved its model", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The split must be on scoped-ness, not on the organisation id. Keying the model cache by
    /// org id would build and retain a separate model per tenant — an unbounded cache and a slow
    /// leak on a busy multi-tenant host.
    /// </summary>
    [Fact]
    public void TheModelIsSharedAcrossOrganisations_NotBuiltPerTenant()
    {
        var options = SharedOptions($"org-filter-modelshare-{Guid.NewGuid()}");

        using var contextA = new ProcuLinkDbContext(options);
        using var contextB = new ProcuLinkDbContext(options);
        contextA.ScopeToOrganisation(Guid.NewGuid());
        contextB.ScopeToOrganisation(Guid.NewGuid());

        Assert.Same(contextA.Model, contextB.Model);
    }

    /// <summary>
    /// The unscoped model must carry no filters at all, so cross-organisation sweeps emit exactly
    /// the query they emitted before this mechanism existed.
    /// </summary>
    [Fact]
    public void TheUnscopedModel_CarriesNoQueryFilters()
    {
        using var scoped = new ProcuLinkDbContext(SharedOptions($"org-filter-m1-{Guid.NewGuid()}"));
        using var unscoped = new ProcuLinkDbContext(SharedOptions($"org-filter-m2-{Guid.NewGuid()}"));
        scoped.ScopeToOrganisation(Guid.NewGuid());

        Assert.NotNull(scoped.Model.FindEntityType(typeof(PurchaseOrderEntity))!.GetQueryFilter());
        Assert.Null(unscoped.Model.FindEntityType(typeof(PurchaseOrderEntity))!.GetQueryFilter());
        Assert.NotSame(scoped.Model, unscoped.Model);
    }
}
