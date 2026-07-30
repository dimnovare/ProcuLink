using System.Reflection;
using FluentAssertions;
using ProcuLink.Core.Constants;
using Xunit;

namespace ProcuLink.Api.Tests.Constants;

/// <summary>
/// WP-11 — <b>the regression guard for the whole paid ladder</b>.
///
/// <para>Before WP-11 the gate table declared sixteen-plus capabilities and the codebase
/// enforced <b>three</b> of them. Every other "differentiator" was unlocked on every plan,
/// so a Pilot or Growth org silently had features sold as belonging to Integration or
/// Enterprise. A gate that is declared but never checked is not a gate — it is a claim
/// about the price list that nothing keeps true.</para>
///
/// <para>This class is the structural half of the fix. It asserts, for EVERY member of
/// <see cref="BillingFeature"/> without exception:</para>
/// <list type="number">
///   <item>the feature has a minimum-plan entry (an entry-less feature silently reads as
///   "nobody has it", which is how a capability disappears from every tier at once);</item>
///   <item><see cref="PlanConstants.PlanHasFeature"/> is true at that minimum;</item>
///   <item>it is false on the plan directly BELOW the minimum — the boundary itself, not
///   just some plan far away;</item>
///   <item>it is true on every plan above the minimum (no gap in the middle of the ladder);</item>
///   <item>a named enforcement site is registered for it in <see cref="EnforcedBy"/>.</item>
/// </list>
///
/// <para>The behavioural half — proving each of those sites actually returns 403 / refuses —
/// lives in <c>BillingFeatureEnforcementTests</c>. <see cref="EveryFeature_HasANamedEnforcementSite"/>
/// fails the build when a new <see cref="BillingFeature"/> is added without one, which is the
/// only thing that stops the ladder rotting again.</para>
/// </summary>
public class BillingFeatureGateCoverageTests
{
    /// <summary>
    /// The production code path that enforces each feature. Every entry is exercised by a
    /// behavioural test in <c>BillingFeatureEnforcementTests</c>; this map exists so adding
    /// an enum member without wiring a gate breaks the build rather than quietly shipping
    /// an unenforced promise.
    ///
    /// <para>A feature with NO real enforcement point does not belong in this enum. WP-11
    /// deleted five that had none: <c>Xml</c> and <c>Pdf</c> (the Pilot card sells
    /// "CSV/XLSX/PDF/XML upload", so gating them at Growth contradicted published copy),
    /// <c>MappingLibrary</c> (no such surface exists anywhere in the product),
    /// <c>DeliveryHistory</c> (no plan card sells it, and hiding "did my PO actually go
    /// out?" from a paying customer is not a tier differentiator), and <c>SlaOnboarding</c>
    /// (a contractual commitment, not a software capability — nothing in code could ever
    /// check it). Two more are being removed by other work in flight — see
    /// <see cref="RetiredElsewhere"/>.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<BillingFeature, string> EnforcedBy =
        new Dictionary<BillingFeature, string>
        {
            [BillingFeature.BulkMapping]         = "SuppliersController.ImportMappings",
            [BillingFeature.Cxml]                = "SuppliersController.UpsertDeliveryConfig (OutputFormat = cxml)",
            [BillingFeature.AdvancedAudit]       = "AuditController.GetAuditLog (org-wide trail; per-order audit stays open)",
            [BillingFeature.WebhookDelivery]     = "SuppliersController.UpsertDeliveryConfig (Protocol = http)",
            [BillingFeature.EmailIngestion]      = "SettingsController.UpdateEmail + EmailPollOrgJob",
            [BillingFeature.SftpIngestion]       = "SettingsController.UpdateSftp + SuppliersController.UpsertCatalogSource + SftpPollOrgJob",
            [BillingFeature.S3Ingestion]         = "SettingsController.UpdateS3 + S3PollOrgJob",
            [BillingFeature.ErpConnectors]       = "SuppliersController.UpsertDeliveryConfig (Protocol = erp_*)",
            [BillingFeature.CustomSupplierRules] = "SupplierAcceptanceController.CreateVersion",
            [BillingFeature.Sso]                 = "StripeBillingService.GetStatusAsync -> BillingStatus.SsoAvailable",
        };

    /// <summary>
    /// Features whose REMOVAL is owned by other work in flight, so WP-11 must neither enforce
    /// them nor delete them — three concurrent edits to one enum is how a member gets silently
    /// reassigned. They are exempt from the enforcement and boundary checks below, and ONLY
    /// this one: <see cref="ExemptionList_IsExactlyTheOneOwnedElsewhere"/> fails if the
    /// exemption is ever used to wave through a genuinely unenforced gate.
    ///
    /// <para>It is a dead gate today, so exempting it hides nothing that is being sold:</para>
    /// <list type="bullet">
    ///   <item><c>ValidationRules</c> — BE #75 retires the whole <c>ValidationRule</c>
    ///   subsystem (working CRUD, no evaluator) along with this flag.</item>
    /// </list>
    ///
    /// <para><c>CustomTemplates</c> was the second entry and is gone: BE #80 deleted the member
    /// outright, which is why it no longer needs exempting. When BE #75 lands, this set empties
    /// and the exemption branch becomes dead code — delete it then rather than letting it become
    /// a parking space.</para>
    /// </summary>
    public static readonly IReadOnlySet<BillingFeature> RetiredElsewhere =
        new HashSet<BillingFeature> { BillingFeature.ValidationRules };

    /// <summary>Ladder order, lowest → highest. Mirrors <c>PlanConstants.PlanOrder</c>.</summary>
    private static readonly string[] Ladder =
    [
        PlanConstants.Pilot,
        PlanConstants.Growth,
        PlanConstants.Operations,
        PlanConstants.Integration,
        PlanConstants.Distributor,
        PlanConstants.Enterprise,
    ];

    /// <summary>Every feature WP-11 owns — the enum minus <see cref="RetiredElsewhere"/>.</summary>
    public static TheoryData<BillingFeature> AllFeatures()
    {
        var data = new TheoryData<BillingFeature>();
        foreach (var f in Enum.GetValues<BillingFeature>().Where(f => !RetiredElsewhere.Contains(f)))
            data.Add(f);
        return data;
    }

    [Fact]
    public void ExemptionList_IsExactlyTheOneOwnedElsewhere()
    {
        // The exemption exists for a specific, temporary reason. Anything else appearing here
        // means an unenforced gate was waved through instead of being enforced or deleted.
        RetiredElsewhere.Should().BeEquivalentTo(new[]
        {
            BillingFeature.ValidationRules,   // BE #75 — subsystem retired
        });
    }

    // ── 1. Every feature is declared ─────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryFeature_DeclaresAMinimumPlan(BillingFeature feature)
    {
        PlanConstants.GetMinimumPlan(feature).Should().NotBeNull(
            $"{feature} has no gate-table entry, so PlanHasFeature returns false for EVERY plan — " +
            "the capability would silently vanish from the whole ladder, Enterprise included");
    }

    // ── 2/3/4. The boundary itself ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryFeature_IsOn_AtItsMinimumPlan(BillingFeature feature)
    {
        var min = PlanConstants.GetMinimumPlan(feature)!;
        PlanConstants.PlanHasFeature(min, feature).Should().BeTrue(
            $"{feature} is sold from {min} upward");
    }

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryFeature_IsOff_OnThePlanDirectlyBelowItsMinimum(BillingFeature feature)
    {
        var min = PlanConstants.GetMinimumPlan(feature)!;
        var minIdx = Array.IndexOf(Ladder, min);
        minIdx.Should().BeGreaterThan(-1, $"{min} must be a real plan on the ladder");

        if (minIdx == 0)
            return; // minimum is Pilot: there is no plan below to test.

        var below = Ladder[minIdx - 1];
        PlanConstants.PlanHasFeature(below, feature).Should().BeFalse(
            $"{feature} must be OFF on {below} — the tier directly below its {min} minimum. " +
            "Testing a far-away plan would pass even if the boundary were off by one.");
    }

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryFeature_IsOn_AtEveryPlanAboveItsMinimum(BillingFeature feature)
    {
        var minIdx = Array.IndexOf(Ladder, PlanConstants.GetMinimumPlan(feature)!);

        for (var i = minIdx; i < Ladder.Length; i++)
        {
            PlanConstants.PlanHasFeature(Ladder[i], feature).Should().BeTrue(
                $"{feature} must stay available on {Ladder[i]} — paying MORE must never take a capability away");
        }
    }

    // ── 5. Every feature has a real enforcement site ─────────────────────────

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void EveryFeature_HasANamedEnforcementSite(BillingFeature feature)
    {
        EnforcedBy.Should().ContainKey(feature,
            $"{feature} is declared in the gate table but nothing is recorded as enforcing it. " +
            "Either wire a gate (and a behavioural test in BillingFeatureEnforcementTests) or " +
            "delete the enum member — a declared-but-unchecked gate is a false claim about the price list.");
    }

    [Fact]
    public void EnforcementMap_HasNoEntriesForFeaturesThatNoLongerExist()
    {
        var live = Enum.GetValues<BillingFeature>().ToHashSet();
        EnforcedBy.Keys.Should().OnlyContain(f => live.Contains(f));
    }

    // ── The five WP-11 deletions must not come back ──────────────────────────

    [Theory]
    [InlineData("Xml")]
    [InlineData("Pdf")]
    [InlineData("MappingLibrary")]
    [InlineData("DeliveryHistory")]
    [InlineData("SlaOnboarding")]
    public void DeletedFeatures_StayDeleted(string removed)
    {
        Enum.GetNames<BillingFeature>().Should().NotContain(removed,
            $"{removed} was removed in WP-11 because there was nothing honest to gate. " +
            "Re-adding it re-creates a promise the ladder cannot keep — if the capability now " +
            "exists, add the enforcement site and its behavioural test in the same change.");
    }

    // ── Pilot may not be sold paid capabilities ──────────────────────────────

    [Theory]
    [MemberData(nameof(AllFeatures))]
    public void NoFeature_IsAvailableOnPilot(BillingFeature feature)
    {
        // Every remaining feature is a PAID differentiator; the trial gets the core
        // upload → review → export loop, not the ladder's add-ons.
        PlanConstants.PlanHasFeature(PlanConstants.Pilot, feature).Should().BeFalse(
            $"{feature} must not be unlocked on the free Pilot trial");
    }

    // ── The enum must not grow silently ──────────────────────────────────────

    [Fact]
    public void EveryEnumMember_IsEitherEnforcedOrExplicitlyExempt()
    {
        // Belt-and-braces against the theory-based checks above being skipped or filtered:
        // every member must be accounted for by name, so a new one cannot slip through
        // un-enforced and un-noticed.
        var members = typeof(BillingFeature)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Length;

        (EnforcedBy.Count + RetiredElsewhere.Count).Should().Be(members,
            "every BillingFeature must be either enforced by a named site or listed in " +
            "RetiredElsewhere with the work item that removes it");
    }
}
