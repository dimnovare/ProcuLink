using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-17 — the decision half of the server-side acceptance gate.
///
/// <para><b>Every "it blocked" assertion here is paired with a NEGATIVE CONTROL</b> that changes
/// exactly ONE thing and asserts the order goes through. Without that pairing a green test proves
/// only that something refused the order, not that the gate did: a seeding mistake, a missing
/// supplier, an unresolved line would each produce the same red light. The controls hold the order,
/// the profile, the rule, the field and the operator constant and vary only the property under
/// test — severity, or the value being judged.</para>
/// </summary>
public sealed class AcceptanceGateTests
{
    private static ProcuLinkDbContext NewDb(string? store = null) =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(store ?? Guid.NewGuid().ToString())
            .Options);

    private static IAcceptanceGate Build(ProcuLinkDbContext db) =>
        new AcceptanceGate(db, new SupplierAcceptanceService(db));

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record Seed(Guid OrgId, Guid SupplierId, Guid OrderId);

    /// <summary>
    /// A clean, fully-resolved, transformable order. Every invariant passes (PO number, currency,
    /// positive quantity, positive price) so nothing OTHER than an acceptance rule can be the thing
    /// that refuses it — that is what makes the negative controls below meaningful.
    /// </summary>
    private static async Task<Seed> SeedOrderAsync(ProcuLinkDbContext db, string currency = "EUR", decimal unitPrice = 10m)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Gate Org", Slug = $"gate-{orgId:N}",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-GATE-1", BuyerName = "Buyer Ltd", Currency = currency,
            OrderDate = new DateOnly(2026, 7, 30), Status = "ready", CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = unitPrice, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return new Seed(orgId, supplierId, orderId);
    }

    /// <summary>Activates a one-rule acceptance profile for the seeded supplier.</summary>
    private static async Task SeedRuleAsync(
        ProcuLinkDbContext db, Seed seed,
        string scope, string fieldPath, string @operator, string? expected,
        string severity, bool blockOnFail)
    {
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = seed.OrgId, SupplierId = seed.SupplierId,
            VersionNo = 1, Status = "active", CreatedAt = DateTime.UtcNow,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = scope, FieldPath = fieldPath, Operator = @operator,
                    ExpectedValue = expected, Severity = severity, BlockOnFail = blockOnFail,
                },
            },
        });
        await db.SaveChangesAsync();
    }

    // ── The block, and its negative controls ──────────────────────────────────

    [Fact]
    public async Task ErrorSeverityRuleThatFails_blocksTheOrder()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", severity: "error", blockOnFail: false);

        var decision = await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.True(decision!.Blocked);
        var blocker = Assert.Single(decision.Blockers);
        Assert.Equal("currency.equals", blocker.Code);
        // Plain language: what failed, actual vs expected, how to fix — no dev-rule jargon. This is
        // the workshop's existing un-capitalised wording, which the gate reuses verbatim rather than
        // inventing a second vocabulary for the same failure.
        Assert.Contains("currency must be EUR", blocker.Message);
        Assert.Contains("USD", blocker.Message);
        Assert.Contains("Set currency to EUR", blocker.Message);
        Assert.DoesNotContain("failed rule", blocker.Message);

        // The gate's own paragraph names the supplier, reads as prose, and offers the way out.
        Assert.Contains("Acme GmbH", decision.Reason!);
        Assert.Contains("Currency must be EUR", decision.Reason!);
        Assert.Contains("record an override", decision.Reason!);
    }

    /// <summary>
    /// NEGATIVE CONTROL #1 — same order, same rule, same field, same supplier. The ONLY difference
    /// is that the rule now passes. If this order is refused too, the block above was never about
    /// the rule and the whole suite is worthless.
    /// </summary>
    [Fact]
    public async Task SameRule_whenTheOrderSatisfiesIt_doesNotBlock()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "EUR");   // ← the one difference
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", severity: "error", blockOnFail: false);

        var decision = await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.False(decision!.Blocked);
        Assert.Empty(decision.Blockers);
    }

    /// <summary>
    /// NEGATIVE CONTROL #2 — identical failing order and identical failing rule; the ONLY difference
    /// is severity. A warning is advice, not a refusal, and this is the assertion that keeps the
    /// gate from quietly becoming "any validation finding stops the order".
    /// </summary>
    [Fact]
    public async Task SameFailingRule_atWarningSeverity_doesNotBlock()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", severity: "warning", blockOnFail: false);

        var decision = await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.False(decision!.Blocked);
    }

    /// <summary>A warning rule that OPTS IN via BlockOnFail blocks — the second half of the promise.</summary>
    [Fact]
    public async Task WarningRuleWithBlockOnFail_blocksTheOrder()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", severity: "warning", blockOnFail: true);

        var decision = await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.True(decision!.Blocked);
    }

    /// <summary>
    /// SCOPE GUARD. A failing mandatory invariant (no PO number → <c>invariant.po_number_present</c>,
    /// severity error) must NOT block. Those rows existed long before the gate and orders carrying
    /// them deliver today; enforcing them here would silently start refusing live traffic, which is
    /// the opposite of keeping a stated promise.
    /// </summary>
    [Fact]
    public async Task FailingInvariant_withNoSupplierRule_doesNotBlock()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db);

        var order = await db.PurchaseOrders.FirstAsync(o => o.Id == seed.OrderId);
        order.PoNumber = "";                       // invariant.po_number_present now FAILS at error severity
        await db.SaveChangesAsync();

        // Corroborate that the invariant really is failing, so this test cannot pass vacuously.
        var rows = await new SupplierAcceptanceService(db)
            .ValidateOrderAsync(seed.OrgId, seed.OrderId, CancellationToken.None);
        Assert.Contains(rows!, r => r.Code == "invariant.po_number_present" && r.Status == "fail" && r.Severity == "error");

        var decision = await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);
        Assert.False(decision!.Blocked);
    }

    [Fact]
    public async Task NoAcceptanceProfile_doesNotBlock()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");

        var decision = await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.False(decision!.Blocked);
    }

    [Fact]
    public async Task UnknownOrder_returnsNull_soCallersCan404()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db);

        Assert.Null(await Build(db).EvaluateAsync(seed.OrgId, Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>Tenancy: another org's id must not be able to read (or clear) this order's gate.</summary>
    [Fact]
    public async Task AnotherOrgsId_returnsNull()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

        Assert.Null(await Build(db).EvaluateAsync(Guid.NewGuid(), seed.OrderId, CancellationToken.None));
    }

    // ── The override ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordedOverride_clearsTheBlock_andCarriesWhoAndWhy()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);
        var gate = Build(db);

        var recorded = await gate.RecordOverrideAsync(
            seed.OrgId, seed.OrderId, "user_2abcOPS", "Supplier confirmed USD by phone, ref TCK-4412.", CancellationToken.None);
        Assert.True(recorded.Recorded, recorded.Error);

        var decision = await gate.EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);
        Assert.False(decision!.Blocked);
        Assert.True(decision.Overridden);
        Assert.Equal("user_2abcOPS", decision.OverriddenBy);
        Assert.Equal("Supplier confirmed USD by phone, ref TCK-4412.", decision.OverrideReason);
        // The blockers stay visible even though they no longer stop the order — the operator must be
        // able to see WHAT was waved through.
        Assert.Single(decision.Blockers);
    }

    [Fact]
    public async Task RecordedOverride_isWrittenToTheAuditTrail_withActorReasonAndExcusedFailures()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

        await Build(db).RecordOverrideAsync(
            seed.OrgId, seed.OrderId, "user_2abcOPS", "Supplier confirmed USD by phone.", CancellationToken.None);

        var ev = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.OrgId == seed.OrgId
                           && a.EntityId == seed.OrderId
                           && a.Action == AcceptanceGateAudit.OverriddenAction);

        var payload = ev.Payload!.RootElement;
        Assert.Equal("user_2abcOPS", payload.GetProperty(AcceptanceGateAudit.ActorKey).GetString());
        Assert.Equal("Supplier confirmed USD by phone.", payload.GetProperty(AcceptanceGateAudit.ReasonKey).GetString());
        Assert.Equal("Order", ev.EntityType);

        var excused = payload.GetProperty(AcceptanceGateAudit.ExcusedKey)
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "currency.equals" }, excused);

        // The human-readable copy stands alone even if the rule is later edited or deleted.
        var blocked = payload.GetProperty("blocked").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(blocked, m => m.Contains("currency must be EUR"));
    }

    /// <summary>
    /// The override excuses FAILURES, not the order. A blocker that appears afterwards was never
    /// signed off, so it blocks again — otherwise one override would be a permanent exemption and the
    /// guarantee would be gone for the rest of that order's life.
    /// </summary>
    [Fact]
    public async Task OverrideDoesNotExcuse_aBlockerThatAppearsLater()
    {
        // Three contexts over ONE store, because that is the real shape: the override is granted in
        // one request, the rule is added in another, and the transform evaluates in a third. Reusing
        // a single context would let the gate answer from a change tracker that still holds the
        // old profile — it would pass while proving nothing about a rule added later.
        var store = Guid.NewGuid().ToString();

        Seed seed;
        await using (var db = NewDb(store))
        {
            seed = await SeedOrderAsync(db, currency: "USD");
            await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

            var granted = await Build(db).RecordOverrideAsync(
                seed.OrgId, seed.OrderId, "user_2abcOPS", "Confirmed by phone.", CancellationToken.None);
            Assert.True(granted.Recorded, granted.Error);
        }

        await using (var db = NewDb(store))
            Assert.False((await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None))!.Blocked);

        // A SECOND blocking rule the operator never saw. Inserted as a standalone child row rather
        // than by mutating a loaded profile's Rules collection: the EF InMemory provider treats the
        // collection change as an update to the parent graph and throws
        // "Attempted to update or delete an entity that does not exist in the store". A direct
        // insert with the FK set is both what the rule editor does and what Postgres would see.
        await using (var db = NewDb(store))
        {
            var profileId = await db.SupplierAcceptanceProfiles.AsNoTracking()
                .Where(p => p.OrgId == seed.OrgId && p.SupplierId == seed.SupplierId)
                .Select(p => p.Id)
                .FirstAsync();

            db.SupplierAcceptanceRules.Add(new SupplierAcceptanceRule
            {
                Id = Guid.NewGuid(), ProfileId = profileId, Scope = "line", FieldPath = "unitPrice",
                Operator = "max", ExpectedValue = "5", Severity = "error", BlockOnFail = false,
            });
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(store);
        var after = await Build(check).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.True(after!.Blocked);
        Assert.False(after.Overridden);
        Assert.Contains(after.Blockers, b => b.Code == "unitPrice.max" && b.LineNumber == 1);
        // The already-excused failure is still listed — it did not vanish, it just stopped being
        // the thing standing in the way.
        Assert.Contains(after.Blockers, b => b.Code == "currency.equals");
    }

    // ── The override excuses a FAILURE, and a failure has a VALUE ─────────────
    //
    // The three tests below are the ones the doc comment on IAcceptanceGate / AcceptanceGate always
    // claimed — "a new rule version, an edited line, a re-parse — that failure is not covered" —
    // and which the old key could not keep. The key was `{fieldPath}.{operator}#{line}`: it named
    // the RULE and the LINE and nothing else, so the same rule failing on the same line for a
    // hundred times worse a value, or after the rule itself was tightened, or under a new profile
    // version, matched the excused key exactly and stayed waved through. Each test changes ONE of
    // those three things and asserts the order blocks again.

    /// <summary>
    /// A RE-PARSE that changes the judged value. Line 1's unit price was 50,001 against a 50,000
    /// cap when the operator signed it off; the re-parsed order says 900,000. That is not the
    /// failure anybody excused.
    /// </summary>
    [Fact]
    public async Task OverrideDoesNotExcuse_theSameRuleAfterAReParseChangesTheValue()
    {
        var store = Guid.NewGuid().ToString();

        Seed seed;
        await using (var db = NewDb(store))
        {
            seed = await SeedOrderAsync(db, unitPrice: 50001m);
            await SeedRuleAsync(db, seed, "line", "unitPrice", "max", "50000", "error", false);

            var granted = await Build(db).RecordOverrideAsync(
                seed.OrgId, seed.OrderId, "user_2abcOPS", "One-off, buyer approved 50,001.", CancellationToken.None);
            Assert.True(granted.Recorded, granted.Error);
        }

        // The override does cover the failure it was granted for — otherwise the assertion below
        // would be testing a broken override rather than a value-sensitive key.
        await using (var db = NewDb(store))
            Assert.False((await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None))!.Blocked);

        await using (var db = NewDb(store))
        {
            var line = await db.PurchaseOrderLines.FirstAsync(l => l.OrderId == seed.OrderId && l.LineNumber == 1);
            line.UnitPrice = 900000m;                       // ← the one difference
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(store);
        var after = await Build(check).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.True(after!.Blocked,
            "the excused failure was 'line 1 is 50,001 against a 50,000 cap'; this order now fails the "
          + "same rule at 900,000, which nobody signed off");
        Assert.False(after.Overridden);
    }

    /// <summary>
    /// A TIGHTENED RULE. Same order, same value, same line — the supplier lowered the cap from
    /// 50,000 to 100 after the override was granted. The order now fails a rule the operator never
    /// saw the terms of.
    /// </summary>
    [Fact]
    public async Task OverrideDoesNotExcuse_theSameRuleAfterItIsTightened()
    {
        var store = Guid.NewGuid().ToString();

        Seed seed;
        await using (var db = NewDb(store))
        {
            seed = await SeedOrderAsync(db, unitPrice: 50001m);
            await SeedRuleAsync(db, seed, "line", "unitPrice", "max", "50000", "error", false);

            var granted = await Build(db).RecordOverrideAsync(
                seed.OrgId, seed.OrderId, "user_2abcOPS", "One-off, buyer approved 50,001.", CancellationToken.None);
            Assert.True(granted.Recorded, granted.Error);
        }

        await using (var db = NewDb(store))
            Assert.False((await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None))!.Blocked);

        await using (var db = NewDb(store))
        {
            var rule = await db.SupplierAcceptanceRules.FirstAsync(r => r.FieldPath == "unitPrice");
            rule.ExpectedValue = "100";                     // ← the one difference
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(store);
        var after = await Build(check).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.True(after!.Blocked,
            "the override excused a failure against a 50,000 cap; the supplier's cap is now 100 and "
          + "that is a different failure");
        Assert.False(after.Overridden);
    }

    /// <summary>
    /// A NEW PROFILE VERSION. Nothing about this order changed and the rule text is untouched — the
    /// supplier published a new version of the acceptance profile. An override granted against v1 is
    /// not consent to send under v2.
    /// </summary>
    [Fact]
    public async Task OverrideDoesNotExcuse_theSameFailureUnderANewProfileVersion()
    {
        var store = Guid.NewGuid().ToString();

        Seed seed;
        await using (var db = NewDb(store))
        {
            seed = await SeedOrderAsync(db, currency: "USD");
            await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

            var granted = await Build(db).RecordOverrideAsync(
                seed.OrgId, seed.OrderId, "user_2abcOPS", "Supplier confirmed USD by phone.", CancellationToken.None);
            Assert.True(granted.Recorded, granted.Error);
        }

        await using (var db = NewDb(store))
            Assert.False((await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None))!.Blocked);

        await using (var db = NewDb(store))
        {
            var profile = await db.SupplierAcceptanceProfiles
                .FirstAsync(p => p.OrgId == seed.OrgId && p.SupplierId == seed.SupplierId);
            profile.VersionNo = 2;                          // ← the one difference
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(store);
        var after = await Build(check).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None);

        Assert.True(after!.Blocked,
            "an override is consent to send under the profile version the operator read, not under "
          + "every version the supplier publishes afterwards");
        Assert.False(after.Overridden);
    }

    [Fact]
    public async Task Override_withoutAReason_isRefused()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

        var result = await Build(db).RecordOverrideAsync(seed.OrgId, seed.OrderId, "user_2abcOPS", "   ", CancellationToken.None);

        Assert.False(result.Recorded);
        Assert.Contains("why", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.AuditEvents.Where(a => a.Action == AcceptanceGateAudit.OverriddenAction).ToListAsync());
    }

    [Fact]
    public async Task Override_onAnOrderThatIsNotBlocked_isRefused()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "EUR");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

        var result = await Build(db).RecordOverrideAsync(
            seed.OrgId, seed.OrderId, "user_2abcOPS", "Just in case.", CancellationToken.None);

        Assert.False(result.Recorded);
        Assert.Contains("isn't blocked", result.Error!);
    }

    /// <summary>An override recorded for ANOTHER org's order id must not be accepted.</summary>
    [Fact]
    public async Task Override_fromAnotherOrg_isRefused()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

        var result = await Build(db).RecordOverrideAsync(
            Guid.NewGuid(), seed.OrderId, "user_intruder", "Let me through.", CancellationToken.None);

        Assert.False(result.Recorded);
        Assert.Equal("Order not found.", result.Error);
    }

    /// <summary>
    /// An override row whose payload cannot be read FAILS CLOSED. Shipping a PO the supplier refuses
    /// on the strength of a row we could not parse is the one outcome worse than an over-strict gate.
    /// </summary>
    [Fact]
    public async Task UnreadableOverridePayload_leavesTheOrderBlocked()
    {
        await using var db = NewDb();
        var seed = await SeedOrderAsync(db, currency: "USD");
        await SeedRuleAsync(db, seed, "order", "currency", "equals", "EUR", "error", false);

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = seed.OrgId, EntityType = "Order", EntityId = seed.OrderId,
            Action = AcceptanceGateAudit.OverriddenAction, CreatedAt = DateTime.UtcNow,
            Payload = JsonDocument.Parse("""{"reason":"typed by hand","by":"user_x"}"""), // no `excused`
        });
        await db.SaveChangesAsync();

        Assert.True((await Build(db).EvaluateAsync(seed.OrgId, seed.OrderId, CancellationToken.None))!.Blocked);
    }
}
