using ProcuLink.Core.Services;

namespace ProcuLink.Core.Constants;

/// <summary>
/// Builds the machine-readable error code a gated endpoint returns with its 403.
///
/// <para><b>Why this exists (WP-11).</b> Four endpoints hardcoded
/// <c>"…_requires_integration"</c> while the feature they gate on had a minimum plan of
/// <b>Growth</b>. A customer on the €149 tier was told to buy the €999 tier to unlock
/// something they already had. Free-form strings drift the instant a plan is re-tiered,
/// and nothing catches it — three tests were even pinning the wrong plan name.</para>
///
/// <para>Deriving the plan segment from <see cref="PlanConstants.GetMinimumPlan"/> makes
/// the code structurally incapable of naming the wrong plan: re-tier the feature in
/// <c>PlanConstants</c> and every 403 that mentions it re-words itself.</para>
///
/// <para>These are machine codes for the frontend to branch on, never user-facing copy —
/// the client maps them to a plain sentence.</para>
/// </summary>
public static class BillingGateErrors
{
    /// <summary>
    /// <c>{capability}_requires_{minimum plan of <paramref name="feature"/>}</c>, e.g.
    /// <c>sftp_ingestion_requires_growth</c>. Falls back to <c>_requires_upgrade</c> when
    /// the feature has no gate-table entry (no plan includes it), so the caller still gets
    /// a stable, non-misleading code instead of naming a plan that would not help.
    /// </summary>
    public static string RequiresPlan(string capability, BillingFeature feature) =>
        PlanConstants.GetMinimumPlan(feature) is { } plan
            ? $"{capability}_requires_{plan}"
            : $"{capability}_requires_upgrade";
}

/// <summary>
/// The single decision for "does this delivery channel + output format need a paid tier?".
///
/// <para>Two write paths reach the same persisted delivery behaviour: the live row
/// (<c>PUT /api/suppliers/{id}/delivery-config</c>) and a versioned connection revision
/// (<c>POST|PUT /api/connections/{id}/revisions…</c>), which is what a pinned order actually
/// delivers through. Gating only the first would be theatre — a Pilot org could set
/// <c>DeliveryProtocol = "http"</c> on a draft revision and deliver through it. Both call
/// this, so the two paths cannot drift apart.</para>
/// </summary>
public static class DeliveryCapabilityGate
{
    /// <summary>
    /// EVERY feature a delivery configuration requires — the channel AND the output format are
    /// sold separately, so a config can need both. Empty when nothing about it is plan-restricted
    /// (plain <c>sftp</c>/<c>ftps</c>/<c>email</c> with a stock output format, all included on
    /// every paid plan).
    ///
    /// <para><b>Returning a list, not one winner, is load-bearing.</b> An earlier version returned
    /// the first match and checked only that: <c>http</c> + <c>cxml</c> on a Growth org matched
    /// WebhookDelivery (Growth — which they have), passed, and saved a cXML config without the
    /// Operations tier that sells cXML. Checking one gate out of two is not a gate.</para>
    ///
    /// <para>Inputs are trimmed and invariant-lowercased, identical to
    /// <c>DeliveryConfigService</c>'s own normalisation, so a value cannot clear the gate as one
    /// thing and be persisted as another.</para>
    /// </summary>
    public static IReadOnlyList<(BillingFeature Feature, string Capability)> RequiredFeatures(
        string? protocol, string? outputFormat)
    {
        var required = new List<(BillingFeature, string)>(2);
        var p = protocol?.Trim().ToLowerInvariant();
        var f = outputFormat?.Trim().ToLowerInvariant();

        if (p is DeliveryProtocolConstants.ErpErply or DeliveryProtocolConstants.ErpDirecto)
            required.Add((BillingFeature.ErpConnectors, "erp_delivery"));
        else if (p == DeliveryProtocolConstants.Http)
            required.Add((BillingFeature.WebhookDelivery, "webhook_delivery"));

        if (f == "cxml")
            required.Add((BillingFeature.Cxml, "cxml_output"));

        return required;
    }

    /// <summary>
    /// The gate this org fails for the given delivery configuration, or null when it may save.
    ///
    /// <para>When more than one requirement is unmet, the HIGHEST-tier one is reported: naming
    /// the cheaper gate would send the customer to a plan that still would not let them save.
    /// This is the single decision both delivery write paths call — the live delivery-config row
    /// and the versioned connection revision — so the two cannot drift.</para>
    /// </summary>
    public static async Task<(BillingFeature Feature, string Capability)?> FirstUnmetAsync(
        IBillingService billing,
        Guid orgId,
        string? protocol,
        string? outputFormat,
        CancellationToken ct)
    {
        (BillingFeature Feature, string Capability)? worst = null;
        var worstRank = -1;

        foreach (var required in RequiredFeatures(protocol, outputFormat))
        {
            if (await billing.HasFeatureAsync(orgId, required.Feature, ct)) continue;

            var rank = Array.IndexOf(PlanConstants.All, PlanConstants.GetMinimumPlan(required.Feature));
            if (rank <= worstRank) continue;

            worstRank = rank;
            worst = required;
        }

        return worst;
    }
}
