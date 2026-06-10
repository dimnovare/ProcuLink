using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V4 backfill tests. Prime directive: seeding + linking changes the SOURCE of the rules
/// (definition + binding) with ZERO change to evaluation — the executor still reads the rule scalar
/// columns, which are never mutated. Also covers idempotency, org-scoping, and derived definitions
/// for rules with no matching catalog entry.
/// </summary>
public class RuleDefinitionBackfillServiceTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(Guid orgId, Guid supplierId, Guid profileId, Guid ruleId)> SeedProfileWithRule(
        ProcuLinkDbContext db, string fieldPath = "supplierItemCode", string @operator = "required",
        string severity = "error", string? expected = null)
    {
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        var profileId = Guid.NewGuid(); var ruleId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = now });
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = profileId, OrgId = orgId, SupplierId = supplierId, VersionNo = 1, Status = "active",
            CreatedAt = now,
            Rules = new List<SupplierAcceptanceRule>
            {
                new() { Id = ruleId, ProfileId = profileId, Scope = "line", FieldPath = fieldPath,
                        Operator = @operator, ExpectedValue = expected, Severity = severity, BlockOnFail = true },
            },
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, profileId, ruleId);
    }

    [Fact]
    public async Task SeedOrgCatalog_CreatesAllWellKnownDefinitions()
    {
        var db = MakeDb();
        var svc = new RuleDefinitionBackfillService(db);
        var orgId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "S", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var created = await svc.SeedOrgCatalogAsync(orgId, CancellationToken.None);

        Assert.Equal(RuleCatalog.Entries.Count, created);
        var codes = await db.RuleDefinitions.Where(d => d.OrgId == orgId).Select(d => d.Code).ToListAsync();
        Assert.All(RuleCatalog.Entries, e => Assert.Contains(e.Code, codes));
        // Seeded definitions carry standards refs (the trust surface).
        var supplierReq = await db.RuleDefinitions.FirstAsync(d => d.OrgId == orgId && d.Code == "supplierItemCode.required");
        Assert.True(supplierReq.IsSystem);
        Assert.NotNull(supplierReq.UblRef);
    }

    [Fact]
    public async Task SeedOrgCatalog_IsIdempotent()
    {
        var db = MakeDb();
        var svc = new RuleDefinitionBackfillService(db);
        var orgId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "S", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var first = await svc.SeedOrgCatalogAsync(orgId, CancellationToken.None);
        var second = await svc.SeedOrgCatalogAsync(orgId, CancellationToken.None);

        Assert.Equal(RuleCatalog.Entries.Count, first);
        Assert.Equal(0, second); // no duplicates on re-run
        Assert.Equal(RuleCatalog.Entries.Count, await db.RuleDefinitions.CountAsync(d => d.OrgId == orgId));
    }

    [Fact]
    public async Task BackfillAll_LinksExistingRuleToSeededDefinition_WithoutChangingRuleScalars()
    {
        var db = MakeDb();
        var svc = new RuleDefinitionBackfillService(db);
        var (orgId, _, _, ruleId) = await SeedProfileWithRule(db, "supplierItemCode", "required", "error");

        // Capture the rule's scalar state BEFORE backfill.
        var before = await db.SupplierAcceptanceRules.AsNoTracking().FirstAsync(r => r.Id == ruleId);

        var (defs, links) = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.True(defs >= RuleCatalog.Entries.Count); // catalog seeded
        Assert.Equal(1, links);

        var after = await db.SupplierAcceptanceRules.AsNoTracking().FirstAsync(r => r.Id == ruleId);
        // Binding metadata set...
        Assert.NotNull(after.RuleDefinitionId);
        Assert.Equal("supplierItemCode.required", after.RuleCode);
        // ...but the scalar columns the executor reads are BYTE-IDENTICAL.
        Assert.Equal(before.Scope, after.Scope);
        Assert.Equal(before.FieldPath, after.FieldPath);
        Assert.Equal(before.Operator, after.Operator);
        Assert.Equal(before.ExpectedValue, after.ExpectedValue);
        Assert.Equal(before.Severity, after.Severity);
        Assert.Equal(before.BlockOnFail, after.BlockOnFail);

        // The link points at a real seeded definition.
        var def = await db.RuleDefinitions.FirstAsync(d => d.Id == after.RuleDefinitionId);
        Assert.Equal("supplierItemCode.required", def.Code);
        Assert.True(def.IsSystem);
    }

    [Fact]
    public async Task BackfillAll_RuleWithNoCatalogMatch_GetsDerivedDefinition()
    {
        var db = MakeDb();
        var svc = new RuleDefinitionBackfillService(db);
        // A custom field/operator combo that is NOT in the seed catalog.
        var (orgId, _, _, ruleId) = await SeedProfileWithRule(db, "customField", "max_length", "warning", "20");

        var (_, links) = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.Equal(1, links);
        var after = await db.SupplierAcceptanceRules.AsNoTracking().FirstAsync(r => r.Id == ruleId);
        Assert.Equal("customField.max_length", after.RuleCode);
        var def = await db.RuleDefinitions.FirstAsync(d => d.Id == after.RuleDefinitionId);
        Assert.False(def.IsSystem);                 // derived, not a system seed
        Assert.Equal("customField", def.FieldPath);
        Assert.Equal("max_length", def.Operator);
        Assert.Equal("warning", def.DefaultSeverity); // derived from the rule
        Assert.Equal("20", def.DefaultExpectedValue);
    }

    [Fact]
    public async Task BackfillAll_IsIdempotent_SecondRunLinksNothing()
    {
        var db = MakeDb();
        var svc = new RuleDefinitionBackfillService(db);
        await SeedProfileWithRule(db, "currency", "required", "error");

        var first = await svc.BackfillAllAsync(CancellationToken.None);
        var second = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.Equal(1, first.rulesLinked);
        Assert.Equal(0, second.definitionsCreated);
        Assert.Equal(0, second.rulesLinked);
    }

    [Fact]
    public async Task BackfillAll_IsOrgScoped_DefinitionsDoNotLeakAcrossOrgs()
    {
        var db = MakeDb();
        var svc = new RuleDefinitionBackfillService(db);
        var (orgA, _, _, ruleA) = await SeedProfileWithRule(db, "supplierItemCode", "required");
        var (orgB, _, _, ruleB) = await SeedProfileWithRule(db, "supplierItemCode", "required");

        await svc.BackfillAllAsync(CancellationToken.None);

        var afterA = await db.SupplierAcceptanceRules.AsNoTracking().FirstAsync(r => r.Id == ruleA);
        var afterB = await db.SupplierAcceptanceRules.AsNoTracking().FirstAsync(r => r.Id == ruleB);
        var defA = await db.RuleDefinitions.FirstAsync(d => d.Id == afterA.RuleDefinitionId);
        var defB = await db.RuleDefinitions.FirstAsync(d => d.Id == afterB.RuleDefinitionId);

        // Same code, but each org owns its own definition row.
        Assert.Equal(defA.Code, defB.Code);
        Assert.NotEqual(defA.Id, defB.Id);
        Assert.Equal(orgA, defA.OrgId);
        Assert.Equal(orgB, defB.OrgId);
        // Each definition is unique within its org.
        Assert.Equal(1, await db.RuleDefinitions.CountAsync(d => d.OrgId == orgA && d.Code == defA.Code));
        Assert.Equal(1, await db.RuleDefinitions.CountAsync(d => d.OrgId == orgB && d.Code == defB.Code));
    }
}
