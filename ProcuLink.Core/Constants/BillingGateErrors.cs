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
