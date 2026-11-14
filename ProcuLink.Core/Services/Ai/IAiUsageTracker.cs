namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// Per-organisation monthly token cap for OpenAI calls, shared across all AI
/// features. Backed by the <c>ai_usage_monthly</c> table; calendar months reset
/// implicitly by switching to a new composite key (OrgId, Year, Month).
///
/// <para>The limit is resolved PER ORG at check time: the allowance from
/// <c>PlanConstants.GetAiMonthlyTokenAllowance</c> for the billing units the org
/// read this month (or is reading now) and its account status, capped by the
/// emergency brake <c>Ai:OpenAI:EmergencyMonthlyTokenBrake</c>.</para>
/// </summary>
public interface IAiUsageTracker
{
    /// <summary>
    /// Returns true if the org has already met or exceeded its resolved monthly
    /// token limit, i.e. tokensUsed &gt;= tokensLimit. Used as a pre-flight
    /// check before issuing the OpenAI call.
    /// </summary>
    Task<bool> IsAtOrOverLimitAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>
    /// Atomically add <paramref name="tokens"/> to the current month's counter
    /// for <paramref name="organisationId"/>. Inserts the row if missing.
    /// Non-positive values are no-ops.
    /// </summary>
    Task IncrementAsync(Guid organisationId, long tokens, CancellationToken ct = default);

    /// <summary>
    /// Returns current usage snapshot for the org for the current calendar month
    /// in UTC. <see cref="AiUsageSnapshot.TokensLimit"/> is the org's RESOLVED
    /// limit (the document-based allowance for its account status, capped by the emergency brake).
    /// Useful for the billing/ai-usage endpoint and for tests.
    /// </summary>
    Task<AiUsageSnapshot> GetCurrentAsync(Guid organisationId, CancellationToken ct = default);
}

public sealed record AiUsageSnapshot(
    Guid OrganisationId,
    int Year,
    int Month,
    long TokensUsed,
    long TokensLimit);
