using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V4 — the unified validation model must keep evaluation BYTE-IDENTICAL. These tests prove
/// the executor (<see cref="SupplierAcceptanceService.EvaluateProfile"/>) ignores the binding and
/// reads only the rule's own scalar columns, and that a per-binding override (a severity / expected
/// value diverging from the definition default) is honoured because the executor reads the rule, not
/// the definition. Also covers the read service (definitions + supplier bindings) and that newly
/// created profiles bind their rules.
/// </summary>
public class RuleDefinitionBindingTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PurchaseOrderEntity OrderWithUnresolvedLine(Guid orgId, Guid supplierId, Guid orderId) => new()
    {
        Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-1",
        Status = "pending_review", Currency = "EUR",
        OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        Lines = new List<PurchaseOrderLineEntity>
        {
            new() { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1, BuyerItemCode = "B1",
                    SupplierItemCode = null, Quantity = 1, UnitPrice = 1, NeedsReview = true },
        },
    };

    [Fact]
    public void EvaluateProfile_BoundRule_ProducesSameResultAsUnboundRule()
    {
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid(); var now = DateTime.UtcNow;
        var order = OrderWithUnresolvedLine(orgId, supplierId, orderId);

        var unbound = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, VersionNo = 1, Status = "active",
            Rules = new List<SupplierAcceptanceRule>
            {
                new() { Id = Guid.NewGuid(), Scope = "line", FieldPath = "supplierItemCode",
                        Operator = "required", Severity = "error", BlockOnFail = true },
            },
        };
        var bound = new SupplierAcceptanceProfile
        {
            Id = unbound.Id, OrgId = orgId, SupplierId = supplierId, VersionNo = 1, Status = "active",
            Rules = new List<SupplierAcceptanceRule>
            {
                // Identical scalar values; only the binding metadata differs.
                new() { Id = unbound.Rules[0].Id, Scope = "line", FieldPath = "supplierItemCode",
                        Operator = "required", Severity = "error", BlockOnFail = true,
                        RuleDefinitionId = Guid.NewGuid(), RuleCode = "supplierItemCode.required" },
            },
        };

        var unboundResults = SupplierAcceptanceService.EvaluateProfile(orgId, orderId, unbound, order, now);
        var boundResults   = SupplierAcceptanceService.EvaluateProfile(orgId, orderId, bound, order, now);

        Assert.Equal(unboundResults.Count, boundResults.Count);
        var u = Assert.Single(unboundResults);
        var b = Assert.Single(boundResults);
        Assert.Equal(u.Status, b.Status);     // both "fail" (unresolved line)
        Assert.Equal(u.Severity, b.Severity);
        Assert.Equal(u.Code, b.Code);
        Assert.Equal(u.Message, b.Message);
        Assert.Equal(u.LineNumber, b.LineNumber);
    }

    [Fact]
    public void EvaluateProfile_BindingOverridesDefinitionSeverity_ExecutorUsesRuleSeverity()
    {
        // Definition default is "error"; the binding overrides to "warning". The executor must use
        // the rule's severity, because it reads the rule — not the definition.
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid(); var now = DateTime.UtcNow;
        var order = OrderWithUnresolvedLine(orgId, supplierId, orderId);

        var profile = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, VersionNo = 1, Status = "active",
            Rules = new List<SupplierAcceptanceRule>
            {
                new() { Id = Guid.NewGuid(), Scope = "line", FieldPath = "supplierItemCode",
                        Operator = "required", Severity = "warning", BlockOnFail = false, // OVERRIDE
                        RuleDefinitionId = Guid.NewGuid(), RuleCode = "supplierItemCode.required" },
            },
        };

        var results = SupplierAcceptanceService.EvaluateProfile(orgId, orderId, profile, order, now);

        var r = Assert.Single(results);
        Assert.Equal("fail", r.Status);
        Assert.Equal("warning", r.Severity); // honoured the per-binding override, not the "error" default
    }

    [Fact]
    public async Task CreateVersion_BindsRulesToDefinitions_AndSeedsDefinitionRow()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var p = await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { new AcceptanceRuleInput("line", "supplierItemCode", "required", null, "error", true) },
            "user@x.example", CancellationToken.None);

        var rule = Assert.Single(p.Rules);
        Assert.NotNull(rule.RuleDefinitionId);
        Assert.Equal("supplierItemCode.required", rule.RuleCode);
        var def = await db.RuleDefinitions.FirstAsync(d => d.Id == rule.RuleDefinitionId);
        Assert.Equal(orgId, def.OrgId);
        Assert.True(def.IsSystem); // matched the seed catalog
    }

    [Fact]
    public async Task CreateVersion_TwoVersionsSameField_ReuseSameDefinitionRow()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        var input = new[] { new AcceptanceRuleInput("order", "currency", "required", null, "error", false) };

        var v1 = await svc.CreateVersionAsync(orgId, supplierId, null, "xml", input, null, CancellationToken.None);
        var v2 = await svc.CreateVersionAsync(orgId, supplierId, null, "xml", input, null, CancellationToken.None);

        // Both versions bind to the SAME org definition (UNIQUE(org_id, code)).
        Assert.Equal(v1.Rules[0].RuleDefinitionId, v2.Rules[0].RuleDefinitionId);
        Assert.Equal(1, await db.RuleDefinitions.CountAsync(d => d.OrgId == orgId && d.Code == "currency.required"));
    }

    [Fact]
    public async Task ReadService_ListDefinitions_IsOrgScoped()
    {
        var db = MakeDb();
        var backfill = new RuleDefinitionBackfillService(db);
        var read = new RuleDefinitionService(db);
        var orgA = Guid.NewGuid(); var orgB = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), OrgId = orgA, Name = "A", CreatedAt = DateTime.UtcNow });
        db.Suppliers.Add(new Supplier { Id = Guid.NewGuid(), OrgId = orgB, Name = "B", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await backfill.SeedOrgCatalogAsync(orgA, CancellationToken.None);

        var defsA = await read.ListDefinitionsAsync(orgA, CancellationToken.None);
        var defsB = await read.ListDefinitionsAsync(orgB, CancellationToken.None);

        Assert.Equal(RuleCatalog.Entries.Count, defsA.Count);
        Assert.Empty(defsB); // orgB was never seeded → no leakage
        Assert.All(defsA, d => Assert.Equal(orgA, d.OrgId));
    }

    [Fact]
    public async Task ReadService_ListSupplierBindings_ReturnsRulesJoinedToDefinitions()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var read = new RuleDefinitionService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var p = await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { new AcceptanceRuleInput("line", "supplierItemCode", "required", null, "error", true) },
            null, CancellationToken.None);
        await svc.ActivateVersionAsync(orgId, supplierId, p.VersionNo, CancellationToken.None);

        var bindings = await read.ListSupplierBindingsAsync(orgId, supplierId, CancellationToken.None);

        var b = Assert.Single(bindings);
        Assert.Equal("supplierItemCode", b.Rule.FieldPath);
        Assert.NotNull(b.Definition);
        Assert.Equal("supplierItemCode.required", b.Definition!.Code);
        Assert.NotNull(b.Definition.UblRef); // standards visibility flows through
    }

    [Fact]
    public async Task ReadService_ListSupplierBindings_NoProfile_ReturnsEmpty()
    {
        var db = MakeDb();
        var read = new RuleDefinitionService(db);
        var bindings = await read.ListSupplierBindingsAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(bindings);
    }
}
