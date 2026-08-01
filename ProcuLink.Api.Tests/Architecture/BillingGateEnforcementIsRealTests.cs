using System.Reflection;
using FluentAssertions;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Worker.Jobs;
using Xunit;
using static ProcuLink.Api.Tests.Architecture.BillingGateIlScanner;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-11 follow-through — <b>the gate table is now checked against compiled code, not against a
/// sentence about compiled code.</b>
///
/// <para><b>The hole this closes.</b> WP-11 replaced sixteen declared-but-unenforced capabilities
/// with ten enforced ones, and left behind
/// <c>BillingFeatureGateCoverageTests.EnforcedBy</c> — a <c>Dictionary&lt;BillingFeature, string&gt;</c>
/// of free text whose only assertion was <c>ContainKey</c>. Delete the <c>HasFeatureAsync</c> call
/// out of <c>AuditController.GetAuditLog</c> and all nine of those tests stay green, because the
/// string still reads "AuditController.GetAuditLog". The guard against declared-but-unchecked gates
/// was itself a declared-but-unchecked claim. That is not a hypothetical: it is the exact failure
/// mode WP-11 documented, one level up.</para>
///
/// <para><b>What replaces it.</b> Each site is named by <see cref="Type"/> + method name, resolved by
/// reflection (so it cannot name a method that does not exist) and then read out of IL (so it cannot
/// name a method that does not gate). See <see cref="BillingGateIlScanner"/> for why IL and not
/// source text, and for the async state-machine wrinkle that would otherwise make every site read
/// as unenforced.</para>
///
/// <para><b>Two kinds of gate, told apart on purpose.</b> A <see cref="GatePrimitive.ServerRefusal"/>
/// reaches <c>IBillingService.HasFeatureAsync</c>, which collapses to false on a read-only account
/// and whose callers turn that into a 403 or an early return. A
/// <see cref="GatePrimitive.PresentationOnly"/> reaches only <c>PlanConstants.PlanHasFeature</c> — a
/// table lookup with no account-status short-circuit, which can drive an availability flag but
/// refuses nobody. Collapsing the two is how "gated" comes to mean "hidden in the UI", so a
/// presentation-only entry must say out loud why no server surface exists.</para>
/// </summary>
public sealed class BillingGateEnforcementIsRealTests
{
    private enum Kind { ServerRefusal, PresentationOnly }

    private sealed record Site(Type Type, string Method, Kind Kind, string Why);

    /// <summary>
    /// Every feature, the production method that enforces it, and why that method is the right
    /// place. Unlike the prose map this supersedes, every field here is checked: the type and
    /// method must resolve, and the method's IL must reach the gate primitive its
    /// <see cref="Kind"/> demands, carrying that exact feature.
    ///
    /// <para>Where a feature has several sites, the one named is the one a customer hits first;
    /// <see cref="EveryGateCallInProduction_IsAccountedFor"/> covers the rest by walking the code in
    /// the opposite direction, so a second site cannot be deleted unnoticed either.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<BillingFeature, Site> Sites =
        new Dictionary<BillingFeature, Site>
        {
            [BillingFeature.BulkMapping] = new(
                typeof(SuppliersController), nameof(SuppliersController.ImportMappings), Kind.ServerRefusal,
                "The bulk lever is the differentiator; single-mapping edits stay open on every plan."),

            [BillingFeature.Cxml] = new(
                typeof(SuppliersController), nameof(SuppliersController.UpsertDeliveryConfig), Kind.ServerRefusal,
                "Reached through DeliveryCapabilityGate, which is why this scanner follows calls "
              + "rather than looking for HasFeatureAsync in the method body itself."),

            [BillingFeature.AdvancedAudit] = new(
                typeof(AuditController), nameof(AuditController.GetAuditLog), Kind.ServerRefusal,
                "The org-wide trail. The per-order audit stays open — gating it would take away "
              + "something Growth already pays for."),

            [BillingFeature.WebhookDelivery] = new(
                typeof(SuppliersController), nameof(SuppliersController.UpsertDeliveryConfig), Kind.ServerRefusal,
                "Protocol = http, via DeliveryCapabilityGate."),

            [BillingFeature.EmailIngestion] = new(
                typeof(SettingsController), nameof(SettingsController.UpdateEmail), Kind.ServerRefusal,
                "Enabling IMAP. EmailPollOrgJob re-checks every cycle so a downgrade stops the poll."),

            [BillingFeature.SftpIngestion] = new(
                typeof(SettingsController), nameof(SettingsController.UpdateSftp), Kind.ServerRefusal,
                "Enabling SFTP pull. SftpPollOrgJob re-checks every cycle."),

            [BillingFeature.S3Ingestion] = new(
                typeof(SettingsController), nameof(SettingsController.UpdateS3), Kind.ServerRefusal,
                "Enabling S3/R2 pull. S3PollOrgJob re-checks every cycle."),

            [BillingFeature.ErpConnectors] = new(
                typeof(SuppliersController), nameof(SuppliersController.UpsertDeliveryConfig), Kind.ServerRefusal,
                "Protocol = erp_erply | erp_directo, via DeliveryCapabilityGate."),

            [BillingFeature.CustomSupplierRules] = new(
                typeof(SupplierAcceptanceController), nameof(SupplierAcceptanceController.CreateVersion),
                Kind.ServerRefusal,
                "Authoring a rule version. Activate is gated too; reading versions stays open."),

            [BillingFeature.Sso] = new(
                typeof(StripeBillingService), nameof(StripeBillingService.GetStatusAsync),
                Kind.PresentationOnly,
                "Enterprise SSO is delivered by Clerk Enterprise Connections, which ProcuLink does "
              + "not sit in front of: the session JWT is identical for a SAML login, so there is no "
              + "request this API could refuse. The flag drives the Settings availability/upsell "
              + "only. Declaring it a server refusal would be the false claim — this says what it is."),
        };

    // ── Controls: the scanner must be able to fail ────────────────────────────

    /// <summary>
    /// POSITIVE CONTROL. If the scanner ever returns empty for everything — a bad opcode table, an
    /// unhandled async shape, an assembly that failed to load — every other test in this file would
    /// pass or fail for reasons that have nothing to do with billing. This pins one site whose gate
    /// is directly in the method body.
    /// </summary>
    [Fact]
    public void Scanner_Control_FindsAGateThatIsDefinitelyThere()
    {
        var features = FeaturesGatedBy(Resolve(typeof(SettingsController), nameof(SettingsController.UpdateSftp)),
            GatePrimitive.ServerRefusal);

        features.Should().Contain(BillingFeature.SftpIngestion,
            "SettingsController.UpdateSftp calls HasFeatureAsync(SftpIngestion) directly. If this "
          + "fails, the scanner is broken and every other assertion in this file is worthless.");
    }

    /// <summary>
    /// POSITIVE CONTROL, INDIRECT. The delivery gates reach the primitive two calls away
    /// (UpsertDeliveryConfig → DeliveryCapabilityGate.FirstUnmetAsync → HasFeatureAsync). A scanner
    /// that only read the method's own body would report these unenforced and demand a duplicated
    /// gate — undoing the single-decision design WP-11 landed.
    /// </summary>
    [Fact]
    public void Scanner_Control_FollowsCallsIntoTheSharedDeliveryGate()
    {
        var features = FeaturesGatedBy(
            Resolve(typeof(SuppliersController), nameof(SuppliersController.UpsertDeliveryConfig)),
            GatePrimitive.ServerRefusal);

        features.Should().Contain(BillingFeature.WebhookDelivery,
            "the http gate lives in DeliveryCapabilityGate, one call away from the controller");
    }

    /// <summary>
    /// NEGATIVE CONTROL. A scanner that answered "yes, gated" for everything would pass every test
    /// here while proving nothing. This pins a real endpoint that is deliberately NOT feature-gated.
    /// </summary>
    [Fact]
    public void Scanner_Control_DoesNotInventAGateWhereThereIsNone()
    {
        ReachesGatePrimitive(
            Resolve(typeof(OrdersController), nameof(OrdersController.GetAudit)),
            GatePrimitive.ServerRefusal)
            .Should().BeFalse(
                "the per-order audit trail is deliberately open on every paid plan. If the scanner "
              + "reports a gate here, it is reporting gates everywhere and proves nothing.");
    }

    // ── The real assertions ───────────────────────────────────────────────────

    public static TheoryData<BillingFeature> AllFeatures()
    {
        var data = new TheoryData<BillingFeature>();
        foreach (var f in Enum.GetValues<BillingFeature>()) data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryFeature_NamesASiteThatExists(BillingFeature feature)
    {
        Sites.Should().ContainKey(feature,
            $"{feature} is declared in the gate table but no production site is named for it. "
          + "Wire a gate, or delete the enum member — a declared-but-unchecked gate is a false "
          + "claim about the price list.");

        var site = Sites[feature];
        Resolve(site.Type, site.Method).Should().NotBeNull(
            $"{site.Type.Name}.{site.Method} does not exist. A gate table that names a method "
          + "nobody can find is prose, not a guard.");
    }

    /// <summary>
    /// The assertion the string map could never make: the named method's compiled IL really does
    /// consult the ladder about THIS feature. Deleting the check now turns this red.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryNamedSite_ProvablyGatesOnItsOwnFeature(BillingFeature feature)
    {
        var site = Sites[feature];
        var method = Resolve(site.Type, site.Method);
        var primitive = site.Kind == Kind.ServerRefusal
            ? GatePrimitive.ServerRefusal
            : GatePrimitive.PresentationOnly;

        var gated = FeaturesGatedBy(method, primitive);

        // Separate the two failures: "consults nothing" and "consults the wrong thing" have
        // different causes and different fixes.
        ReachesGatePrimitive(method, primitive).Should().BeTrue(
            $"{site.Type.Name}.{site.Method} is named as the {feature} enforcement site but its IL "
          + $"never reaches {(site.Kind == Kind.ServerRefusal ? "IBillingService.HasFeatureAsync" : "PlanConstants.PlanHasFeature")}. "
          + $"Why it was named: {site.Why}");

        gated.Should().Contain(feature,
            $"{site.Type.Name}.{site.Method} consults the ladder, but not about {feature}. "
          + $"It checks: {(gated.Count == 0 ? "(no compile-time constant feature)" : string.Join(", ", gated))}. "
          + $"Why it was named: {site.Why}");
    }

    /// <summary>
    /// A <see cref="Kind.PresentationOnly"/> entry is a documented exception, and exceptions rot
    /// into parking spaces. This keeps the set small and deliberate: today exactly one member earns
    /// it, and adding a second is a decision someone has to make on purpose.
    /// </summary>
    [Fact]
    public void PresentationOnlyGates_StayExceptional_AndSayWhy()
    {
        var presentation = Sites.Where(kv => kv.Value.Kind == Kind.PresentationOnly).ToArray();

        presentation.Select(kv => kv.Key).Should().BeEquivalentTo([BillingFeature.Sso],
            "SSO is the only capability whose delivery sits outside this API (Clerk owns the auth), "
          + "so it is the only one that cannot be a server refusal. A second presentation-only gate "
          + "is far more likely to be a gate someone forgot to enforce than a genuine second Clerk.");

        foreach (var (feature, site) in presentation)
        {
            site.Why.Length.Should().BeGreaterThan(80,
                $"{feature} is exempt from server enforcement, so the reason has to be written down "
              + "where the exemption is granted, not inferred later");
        }
    }

    /// <summary>
    /// The REVERSE direction. Everything above starts from the declared map, so a gate that exists
    /// in production but is absent from the map is invisible to it — and an enforcement site that
    /// is quietly deleted from one of the several-site features (the poll jobs, the second delivery
    /// write path, the catalog source) would go unnoticed. This walks the code instead and demands
    /// every real gate call belong to a feature we claim to enforce.
    /// </summary>
    [Fact]
    public void EveryGateCallInProduction_IsAccountedFor()
    {
        var callSites = DirectGateCallSites();

        callSites.Should().NotBeEmpty(
            "production must contain at least one HasFeatureAsync call — an empty result means the "
          + "scanner failed to load the production assemblies, not that the ladder is clean");

        var gatedFeatures = callSites.Select(c => c.Feature).OfType<BillingFeature>().Distinct().ToArray();

        gatedFeatures.Should().BeSubsetOf(Enum.GetValues<BillingFeature>(),
            "a gate call naming a feature outside the enum cannot happen — but if the scanner ever "
          + "misreads a constant, this is where it shows up rather than silently passing");

        // A gate call naming a feature nobody declares means an enforcement site was added or moved
        // without updating the map — the map going stale in the direction the per-feature tests
        // cannot see. (The delivery gates are absent from this list by design: they pass the feature
        // through a list rather than as a constant at the call site, which is why the per-feature
        // tests follow the call graph instead of reading call sites.)
        gatedFeatures.Should().OnlyContain(f => Sites.ContainsKey(f),
            "every HasFeatureAsync call in production must be for a feature the gate table declares. "
          + "An undeclared one is a capability being refused that no plan card admits to gating.");

        var serverRefusals = Sites.Where(kv => kv.Value.Kind == Kind.ServerRefusal).Select(kv => kv.Key).ToArray();
        serverRefusals.Should().NotBeEmpty("the ladder must refuse something, or it is not a ladder");
    }

    /// <summary>
    /// The pull jobs are the only gates that re-check AFTER the org was allowed to configure the
    /// channel, which is what makes a downgrade actually stop the ingest rather than merely stop
    /// re-enabling it. They are easy to "simplify" away, and nothing else would notice.
    /// </summary>
    [Theory]
    [InlineData(typeof(SftpPollOrgJob), BillingFeature.SftpIngestion)]
    [InlineData(typeof(S3PollOrgJob), BillingFeature.S3Ingestion)]
    [InlineData(typeof(EmailPollOrgJob), BillingFeature.EmailIngestion)]
    public void ThePullJobs_ReCheckTheGateOnEveryCycle(Type jobType, BillingFeature feature)
    {
        var execute = Resolve(jobType, "ExecuteAsync");

        FeaturesGatedBy(execute, GatePrimitive.ServerRefusal).Should().Contain(feature,
            $"{jobType.Name} must re-check {feature} every cycle. Gating only the settings write "
          + "would let an org that downgrades keep ingesting for as long as its stored config "
          + "survives — the configuration gate and the runtime gate are different guarantees.");
    }

    private static MethodBase Resolve(Type type, string method) =>
        type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                             | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        ?? throw new InvalidOperationException(
            $"{type.Name}.{method} does not exist — the gate table names a method nobody can find.");
}
