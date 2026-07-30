using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-17 — the two acceptance answers must be the SAME answer.
///
/// <para>The order workshop shows the operator a validation list
/// (<see cref="ISupplierAcceptanceService.ValidateOrderAsync"/>); the transform now refuses on
/// <see cref="ISupplierAcceptanceService.GetBlockingFailuresAsync"/>. If those two ever disagree the
/// product is back to lying — the panel would say "blocked" while the order ships, or the reverse,
/// and either way the operator cannot trust the screen.</para>
///
/// <para><b>This asserts the DIFFERENCE, not either side.</b> The expectation is DERIVED from
/// <c>ValidateOrderAsync</c>'s own output joined to the rules table, so it is not a hand-written
/// list that can be updated to match a regression: change the evaluator, the profile resolution, or
/// the blocking predicate on ONE side only and the two sets diverge and this fails. The order is
/// seeded so the two would diverge in BOTH directions if the filter were wrong — it carries failing
/// error rules, failing warning rules, a failing warning rule with BlockOnFail, passing error rules,
/// and failing non-rule rows (invariants) that must never block.</para>
/// </summary>
public sealed class AcceptanceGateMatchesValidationTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task TheGateBlocksOnExactlyTheFailingRuleRows_theValidationPanelShows()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedMixedOrderAsync(db);

        var acceptance = new SupplierAcceptanceService(db);

        // ── Side A: what the operator SEES ────────────────────────────────────
        var shown = await acceptance.ValidateOrderAsync(orgId, orderId, CancellationToken.None);
        Assert.NotNull(shown);

        var rules = await db.SupplierAcceptanceRules.AsNoTracking().ToDictionaryAsync(r => r.Id);

        // Compared as (code, line) — the FAILURE's identity, not the override key's. The key also
        // carries the profile version and a digest of the judged values, which is exactly what makes
        // an override specific; folding that in here would turn a comparison of "which rules refuse
        // this order" into a comparison of key formatting.
        var expected = shown!
            .Where(r => r.Status == "fail"
                     && r.RuleId is Guid id
                     && rules.TryGetValue(id, out var rule)
                     && (rule.BlockOnFail || string.Equals(rule.Severity, "error", StringComparison.OrdinalIgnoreCase)))
            .Select(r => (r.Code, r.LineNumber))
            .OrderBy(k => k.Code, StringComparer.Ordinal).ThenBy(k => k.LineNumber)
            .ToList();

        // ── Side B: what the transform ENFORCES ───────────────────────────────
        var blockers = await acceptance.GetBlockingFailuresAsync(orgId, orderId, CancellationToken.None);
        Assert.NotNull(blockers);

        var actual = blockers!
            .Select(b => (b.Code, b.LineNumber))
            .OrderBy(k => k.Code, StringComparer.Ordinal).ThenBy(k => k.LineNumber)
            .ToList();

        Assert.Equal(expected, actual);

        // The fixture must actually exercise the filter in both directions, or the equality above is
        // vacuous — a suite where both sides are empty proves nothing at all.
        Assert.NotEmpty(actual);
        Assert.Contains(shown!, r => r.Status == "fail" && r.Severity == "warning");        // shown, never blocking
        Assert.Contains(shown!, r => r.Status == "fail" && r.RuleId is null);               // invariant/output row
        Assert.Contains(shown!, r => r.Status == "pass" && r.RuleId is not null);           // a rule that passes
        Assert.True(actual.Count < shown!.Count(r => r.Status == "fail"),
            "the gate must be a strict subset of what the panel shows failing, otherwise this fixture "
          + "does not distinguish 'blocking' from 'failing' at all");
    }

    /// <summary>
    /// Every blocker carries the identity its override key is built from. Without this the key
    /// silently degrades to the old rule-and-line-only shape and the "a re-parse is not covered"
    /// promise quietly stops holding — with every test in <c>AcceptanceGateTests</c> still green,
    /// because they exercise the key end to end and would simply see the same key on both sides.
    /// </summary>
    [Fact]
    public async Task EveryBlocker_carriesTheRuleTheProfileVersionAndTheJudgedValues()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedMixedOrderAsync(db);

        var blockers = (await new SupplierAcceptanceService(db)
            .GetBlockingFailuresAsync(orgId, orderId, CancellationToken.None))!;

        Assert.NotEmpty(blockers);
        Assert.All(blockers, b =>
        {
            Assert.NotNull(b.RuleId);
            Assert.Equal(1, b.ProfileVersion);           // the seeded profile's VersionNo
        });

        // The line-scoped price rule fails on line 2 only, and the blocker carries BOTH halves of
        // what makes that failure specific: the cap that was demanded and the price that was found.
        var price = Assert.Single(blockers.Where(b => b.Code == "unitPrice.max"));
        Assert.Equal(2, price.LineNumber);
        Assert.Equal("20", price.ExpectedValue);
        Assert.Equal("99", price.ActualValue);

        // …and every blocker's key differs from every other's, which is the property the override
        // relies on to excuse one failure without excusing its neighbours.
        Assert.Equal(blockers.Count, blockers.Select(b => b.Key).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The messages are shared too — the operator must read the same words in both places.</summary>
    [Fact]
    public async Task BlockerMessages_areTheSameSentences_thePanelShows()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedMixedOrderAsync(db);
        var acceptance = new SupplierAcceptanceService(db);

        var shown = (await acceptance.ValidateOrderAsync(orgId, orderId, CancellationToken.None))!;
        var blockers = (await acceptance.GetBlockingFailuresAsync(orgId, orderId, CancellationToken.None))!;

        foreach (var b in blockers)
            Assert.Contains(shown, r => r.Code == b.Code && r.LineNumber == b.LineNumber && r.Message == b.Message);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One order that trips every branch of the blocking predicate at once:
    ///   currency  equals EUR   severity error   → FAILS  (blocks)
    ///   unitPrice max 20       severity error   → FAILS on line 2 only (blocks that line, not line 1)
    ///   buyerName required     severity warning → PASSES (never blocks, and does not fail either)
    ///   incoterms required     severity warning → FAILS  (shown, does NOT block)
    ///   description required   severity warning + BlockOnFail → FAILS on line 2 (blocks)
    /// plus a failing invariant (blank PO number) that carries no rule at all.
    /// </summary>
    private static async Task<(Guid OrgId, Guid OrderId)> SeedMixedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Mixed Org", Slug = $"mixed-{orgId:N}", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "",                    // ← failing invariant, carries no RuleId
            BuyerName = "Buyer Ltd",          // ← passing rule
            Currency = "USD",                 // ← failing error rule
            Incoterms = null,                 // ← failing warning rule
            OrderDate = new DateOnly(2026, 7, 30), Status = "ready", CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "",   // ← BlockOnFail warning
                    Quantity = 2m, Unit = "EA", UnitPrice = 99m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });

        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "active", CreatedAt = now,
            Rules =
            {
                Rule("order", "currency",    "equals",   "EUR", "error",   false),
                Rule("line",  "unitPrice",   "max",      "20",  "error",   false),
                Rule("order", "buyerName",   "required", null,  "warning", false),
                Rule("order", "incoterms",   "required", null,  "warning", false),
                Rule("line",  "description", "required", null,  "warning", true),
            },
        });

        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static SupplierAcceptanceRule Rule(
        string scope, string field, string op, string? expected, string severity, bool blockOnFail) => new()
    {
        Id = Guid.NewGuid(), Scope = scope, FieldPath = field, Operator = op,
        ExpectedValue = expected, Severity = severity, BlockOnFail = blockOnFail,
    };
}
