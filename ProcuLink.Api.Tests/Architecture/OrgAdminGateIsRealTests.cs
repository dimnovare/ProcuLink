using System.Reflection;
using FluentAssertions;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Controllers;
using Xunit;
using static ProcuLink.Api.Tests.Architecture.OrgAdminGateIlScanner;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The org-admin gate, checked against compiled code rather than against a list of endpoint names.
///
/// <para><b>The defect.</b> Until this packet there was no RBAC anywhere in the product: a bare
/// <c>AddAuthorization()</c>, a bare <c>[Authorize]</c> on every controller, and no role claim read
/// in the entire backend. Any authenticated member of an organisation could repoint a supplier's
/// deliveries, mint an unattributable API key, or open the Stripe portal and cancel the subscription
/// — which stops every ingest path at once.</para>
///
/// <para><b>Why the assertion is set equality and not a checklist.</b> A hand-typed list of gated
/// endpoints is exactly how drifts have survived in this repo:
/// <see cref="BillingGateEnforcementIsRealTests"/> exists because a dictionary of free text asserted
/// with <c>ContainKey</c> stayed green after the gate it named was deleted. So both sides here are
/// computed — the gated set from the attribute on the compiled method, the destructive set from the
/// actions' IL — and asserting they are EQUAL fails in both directions. A thirteenth endpoint that
/// calls a destructive primitive without the attribute fails; so does an attribute placed on
/// something whose destructive character nobody declared.</para>
///
/// <para>The behavioural half — a member is actually refused and an admin actually admitted, over
/// real HTTP, for every endpoint this file discovers — is
/// <c>OrgAdminGateRefusesNonAdminsTests</c>. Structure without behaviour would prove only that an
/// attribute is present.</para>
/// </summary>
public sealed class OrgAdminGateIsRealTests
{
    /// <summary>
    /// The number of endpoints the gate is expected to cover. An anti-vacuity floor: a scanner that
    /// silently returned nothing would satisfy set equality against an equally empty other side and
    /// prove precisely nothing.
    ///
    /// <para>Changing this number is the deliberate act of widening or narrowing what an
    /// organisation reserves to its administrators, and it should never move on its own.</para>
    /// </summary>
    private const int ExpectedGatedEndpointCount = 12;

    /// <summary>
    /// How many operations are declared destructive. Twelve endpoints reach thirteen primitives,
    /// because <c>UpsertDeliveryConfig</c> both writes the delivery row and republishes the live
    /// connection revision.
    /// </summary>
    private const int ExpectedDestructivePrimitiveCount = 13;

    // ── Controls: the scanner must be able to fail ────────────────────────────

    /// <summary>
    /// POSITIVE CONTROL, DIRECT. If the IL scan returned empty for everything — an opcode table
    /// change, an unhandled async shape, an assembly that failed to load — every other assertion
    /// here would pass vacuously. This pins one action whose destructive call is unmistakably in its
    /// own body.
    /// </summary>
    [Fact]
    public void Scanner_Control_SeesADestructiveCallThatIsDefinitelyThere()
    {
        Destructive(typeof(BillingController), nameof(BillingController.CreatePortal))
            .Should().NotBeEmpty(
                "BillingController.CreatePortal calls IBillingService.CreatePortalSessionAsync "
              + "directly. If this fails, the scanner is broken and every other assertion in this "
              + "file is worthless.");
    }

    /// <summary>
    /// POSITIVE CONTROL, ASYNC STATE MACHINE. Every action in this codebase is <c>async</c>, so its
    /// visible body is a compiler-generated stub and the real calls live in the state machine's
    /// <c>MoveNext</c>. A scanner that read the stub would report zero destructive actions and pass
    /// the equality test against an empty gated set the moment someone deleted the attributes.
    /// </summary>
    [Fact]
    public void Scanner_Control_ReadsThroughTheAsyncStateMachine()
    {
        var method = typeof(SuppliersController).GetMethod(nameof(SuppliersController.UpsertDeliveryConfig))!;

        method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()
            .Should().NotBeNull("this control is only meaningful while the action is genuinely async");

        Destructive(typeof(SuppliersController), nameof(SuppliersController.UpsertDeliveryConfig))
            .Select(p => p.Describe)
            .Should().Contain("IDeliveryConfigService.UpsertAsync");
    }

    /// <summary>
    /// POSITIVE CONTROL, NON-INTERFACE PRIMITIVE. The webhook subscription is the one destructive
    /// operation with no service seam — the controller mints the entity and adds it to the DbSet
    /// inline. That match resolves a constructed generic (<c>DbSet&lt;IntegrationSubscription&gt;</c>)
    /// or a constructor token, neither of which the interface-shaped matches exercise, so it gets its
    /// own control.
    /// </summary>
    [Fact]
    public void Scanner_Control_SeesTheEntityLevelPrimitiveToo()
    {
        Destructive(typeof(IntegrationController), nameof(IntegrationController.Create))
            .Select(p => p.Describe)
            .Should().Contain(
                d => d.Contains("IntegrationSubscription"),
                "IntegrationController.Create is the redirection primitive with no interface to name; "
              + "if this match stops resolving, the endpoint quietly leaves the destructive set");
    }

    /// <summary>
    /// NEGATIVE CONTROL. A scanner that answered "destructive" for everything would satisfy nothing
    /// while appearing to. This pins a real, deliberately ungated endpoint that reads and mutates
    /// nothing of the kind.
    /// </summary>
    [Fact]
    public void Scanner_Control_DoesNotInventDestructionWhereThereIsNone()
    {
        Destructive(typeof(BillingController), nameof(BillingController.GetStatus))
            .Should().BeEmpty(
                "reading the billing status is not a destructive act. If the scanner reports one "
              + "here, it is reporting them everywhere and proves nothing.");

        GatedActions().Should().NotContain(
            a => a.Method.Name == nameof(BillingController.GetStatus),
            "and it must not be gated either — reading your own plan is not an admin action");
    }

    // ── The real assertions ───────────────────────────────────────────────────

    /// <summary>
    /// Anti-vacuity floor. Both computed sets must be non-empty and exactly the expected size before
    /// any equality between them means anything.
    /// </summary>
    [Fact]
    public void TheGatedSet_IsNonEmpty_AndTheSizeWeExpect()
    {
        var gated = GatedActions();

        gated.Should().NotBeEmpty(
            "an empty gated set would make every other assertion in this file pass while the product "
          + "has no RBAC at all — which is precisely the state this packet was written to end");

        gated.Should().HaveCount(ExpectedGatedEndpointCount,
            "the set of actions reserved to organisation administrators changed. That is a product "
          + "decision, not a refactor: widen or narrow it deliberately and move "
          + $"{nameof(ExpectedGatedEndpointCount)} in the same commit.\nCurrently gated:\n"
          + string.Join("\n", gated.Select(a => "  • " + a.Display)));

        AllActions().Count.Should().BeGreaterThan(ExpectedGatedEndpointCount * 5,
            "the gate is meant to be narrow — if it ever covers a large share of the API, the "
          + "founder's 'gate the destructive few, leave the rest open' decision has been reversed "
          + "by accretion");
    }

    /// <summary>
    /// The assertion a list of endpoint names could never make, in the direction that matters most:
    /// <b>every action that performs a destructive operation carries the gate.</b> A new endpoint
    /// that repoints delivery, mints a key, opens the billing portal or overrides the acceptance
    /// gate turns this red the moment it is written, rather than shipping unguarded.
    /// </summary>
    [Fact]
    public void EveryDestructiveAction_CarriesTheGate()
    {
        var gated = GatedActions().Select(a => a.Display).ToHashSet(StringComparer.Ordinal);

        var ungated = DestructiveActions()
            .Where(x => !gated.Contains(x.Action.Display))
            .Select(x =>
                $"  • {x.Action.Display} reaches {string.Join(", ", x.Reaches.Select(p => p.Describe))}\n"
              + string.Join("\n", x.Reaches.Select(p => $"      why it matters: {p.Why}")))
            .ToList();

        ungated.Should().BeEmpty(
            "these controller actions perform an operation declared destructive but admit any "
          + $"authenticated member. Add [{nameof(RequireOrgAdminAttribute)}], or — if the operation "
          + "genuinely does not warrant it — remove the primitive from OrgAdminGateIlScanner with a "
          + "written reason:\n" + string.Join("\n", ungated));
    }

    /// <summary>
    /// The REVERSE direction, and the reason this file asserts equality rather than containment.
    /// Everything above starts from the declared primitives, so a gate placed on an action that
    /// reaches none of them is invisible to it — and that is not a harmless extra: it means either
    /// a destructive operation nobody declared (so a SECOND endpoint could reach the same primitive
    /// ungated and nothing would notice), or an endpoint restricted to admins for no recorded
    /// reason.
    /// </summary>
    [Fact]
    public void EveryGatedAction_ReachesADeclaredDestructivePrimitive()
    {
        var destructive = DestructiveActions().Select(x => x.Action.Display).ToHashSet(StringComparer.Ordinal);

        var unexplained = GatedActions()
            .Select(a => a.Display)
            .Where(d => !destructive.Contains(d))
            .ToList();

        unexplained.Should().BeEmpty(
            $"these actions carry [{nameof(RequireOrgAdminAttribute)}] but their IL reaches no "
          + "declared destructive primitive. Either the operation they perform belongs in "
          + "OrgAdminGateIlScanner.DestructivePrimitives — which is what makes a future second "
          + "endpoint reaching it fail this guard too — or the gate does not belong here:\n"
          + string.Join("\n", unexplained.Select(d => "  • " + d)));
    }

    /// <summary>
    /// Every declared primitive must actually be reached by something. A primitive that matches
    /// nothing is dead weight that reads as coverage — the exact shape of a guard rotting into
    /// decoration.
    /// </summary>
    [Fact]
    public void EveryDeclaredPrimitive_IsReachedBySomeAction()
    {
        var reached = DestructiveActions().SelectMany(x => x.Reaches).Distinct().ToList();

        var orphans = DestructivePrimitives
            .Where(p => !reached.Contains(p))
            .Select(p => "  • " + p.Describe)
            .ToList();

        orphans.Should().BeEmpty(
            "these primitives are declared destructive but no controller action reaches them. Either "
          + "the endpoint that used to call them is gone (delete the entry), or the match no longer "
          + "resolves (fix it) — a primitive matching nothing silently stops guarding anything:\n"
          + string.Join("\n", orphans));
    }

    /// <summary>
    /// Every primitive states why it earns a gate, where the decision is made. Left to inference,
    /// the list becomes a place things get added to without anyone weighing them.
    /// </summary>
    [Fact]
    public void EveryDeclaredPrimitive_SaysWhyItWarrantsAnAdministrator()
    {
        // Unconditional, and an exact count rather than a non-empty check: every assertion below
        // sits inside the loop, so an emptied — or quietly shrunk — primitive list would otherwise
        // make this pass while checking nothing.
        DestructivePrimitives.Should().HaveCount(ExpectedDestructivePrimitiveCount,
            "the declaration of what counts as destructive changed. That is the decision this whole "
          + "guard turns on, so it moves deliberately and with "
          + $"{nameof(ExpectedDestructivePrimitiveCount)} updated in the same commit.\nDeclared:\n"
          + string.Join("\n", DestructivePrimitives.Select(p => "  • " + p.Describe)));

        foreach (var primitive in DestructivePrimitives)
        {
            primitive.Why.Length.Should().BeGreaterThan(40,
                $"{primitive.Describe} restricts an action to administrators, so the reason belongs "
              + "next to the restriction rather than in a commit message nobody re-reads");
        }
    }

    private static IReadOnlyList<Primitive> Destructive(Type controller, string action) =>
        DestructiveActions()
            .Where(x => x.Action.Method.DeclaringType == controller && x.Action.Method.Name == action)
            .SelectMany(x => x.Reaches)
            .ToList();
}
