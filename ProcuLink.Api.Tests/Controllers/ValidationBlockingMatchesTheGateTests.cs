using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-17 — the badge that says "this blocks delivery" must mean what the server does.
///
/// <para><b>The defect.</b> <c>GET /api/orders/{id}/validation</c> computed
/// <c>Blocking = failed &amp;&amp; severity == "error"</c> over EVERY row it returned, including the
/// mandatory invariants. <c>invariant.po_number_present</c>, <c>invariant.currency_present</c> and
/// <c>invariant.quantity_positive</c> are all severity <c>error</c>, so an order missing its PO
/// number came back with a blocking badge. The frontend counts those rows
/// (<c>fieldBadgesModel.blockingReviewCount</c>) into <c>canDeliver</c> and an "N blockers" label —
/// while the gate deliberately does NOT enforce invariants and transforms and delivers that order
/// anyway. That is the same class of untruth this work package exists to close, one layer along:
/// the product telling the operator something is blocked when the server will happily send it.</para>
///
/// <para><b>The decision.</b> The gate's scope stays as documented — only supplier ACCEPTANCE rules
/// refuse an order; invariants and output-render rows stay advisory. So the UI moves to match the
/// server, not the other way round: <c>Blocking</c> is now derived from
/// <see cref="ISupplierAcceptanceService.GetBlockingFailuresAsync"/>, the same answer the gate acts
/// on. Promoting invariants into the gate instead would start refusing orders that deliver fine
/// today, silently, for every existing customer.</para>
///
/// <para>The load-bearing assertion is the SET COMPARISON: the blocking badges and the gate's
/// blockers are derived from two different calls and must come out equal. A test that only checked
/// "the invariant is not blocking" would pass just as well if nothing were ever blocking.</para>
/// </summary>
public sealed class ValidationBlockingMatchesTheGateTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task AFailingInvariant_isShownForReview_butNeverMarkedBlocking()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db);
        var ctrl = Build(db, orgId);

        var states = await ReadValidationAsync(ctrl, orderId);

        // Corroboration first, so this cannot pass vacuously: the invariant really is failing at
        // error severity — the exact input the old `failed && severity == "error"` rule saw.
        var invariant = states.Single(s => s.Key == "invariant.po_number_present");
        Assert.Equal("review", invariant.State);
        Assert.False(invariant.Blocking,
            "a failing mandatory invariant is advisory — the gate transforms and delivers this order, "
          + "so the UI must not tell the operator it is blocked");
    }

    [Fact]
    public async Task AFailingSupplierErrorRule_isStillMarkedBlocking()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db);
        var ctrl = Build(db, orgId);

        var states = await ReadValidationAsync(ctrl, orderId);

        var rule = states.Single(s => s.Key == "currency.equals");
        Assert.Equal("review", rule.State);
        Assert.True(rule.Blocking,
            "a failing error-severity SUPPLIER rule is exactly what the gate refuses on — dropping "
          + "its badge would swap one untruth for the opposite one");
    }

    /// <summary>
    /// THE ASSERTION THAT MATTERS. Two independently-computed sets — what the badge calls blocking,
    /// and what the gate refuses on — must be identical. Change either side alone and this fails.
    /// </summary>
    [Fact]
    public async Task TheBlockingBadges_areExactlyTheGatesBlockers()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db);
        var ctrl = Build(db, orgId);

        var badged = (await ReadValidationAsync(ctrl, orderId))
            .Where(s => s.Blocking)
            .Select(s => s.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var refused = (await new SupplierAcceptanceService(db)
                .GetBlockingFailuresAsync(orgId, orderId, CancellationToken.None))!
            .Select(b => b.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(refused, badged);

        // The fixture must exercise the difference in both directions, or equality proves nothing.
        Assert.NotEmpty(badged);
        var all = await ReadValidationAsync(ctrl, orderId);
        Assert.Contains(all, s => s.State == "review" && !s.Blocking && s.Key.StartsWith("invariant.", StringComparison.Ordinal));
        Assert.Contains(all, s => s.State == "review" && !s.Blocking && s.Key == "incoterms.required");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<FieldValidationStateDto>> ReadValidationAsync(
        MapperEnrichmentController ctrl, Guid orderId)
    {
        var result = await ctrl.GetValidation(orderId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<IReadOnlyList<FieldValidationStateDto>>(ok.Value!);
    }

    private static MapperEnrichmentController Build(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new MapperEnrichmentController(
            db,
            tenant.Object,
            new SupplierAcceptanceService(db),           // the REAL evaluator — invariants included
            new Mock<IPoMappingService>().Object,
            new Mock<IFieldMappingSuggester>().Object,
            new AiSuggestionDecisionService(db),
            NullLogger<MapperEnrichmentController>.Instance);
    }

    /// <summary>
    /// One order that produces all three row kinds at once:
    ///   a FAILING invariant at error severity (blank PO number)  → shown, must NOT block
    ///   a FAILING supplier rule at error severity (currency)     → shown, MUST block
    ///   a FAILING supplier rule at warning severity (incoterms)  → shown, must NOT block
    /// </summary>
    private static async Task<(Guid OrgId, Guid OrderId)> SeedAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Badge Org", Slug = $"badge-{orgId:N}",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "",                     // ← failing invariant, error severity, carries no rule
            BuyerName = "Buyer Ltd",
            Currency = "USD",                  // ← failing supplier rule, error severity
            Incoterms = null,                  // ← failing supplier rule, warning severity
            OrderDate = new DateOnly(2026, 7, 30), Status = "ready", CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "active", CreatedAt = now, EffectiveFrom = now,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "equals",
                    ExpectedValue = "EUR", Severity = "error", BlockOnFail = false,
                },
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "incoterms", Operator = "required",
                    ExpectedValue = null, Severity = "warning", BlockOnFail = false,
                },
            },
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }
}
