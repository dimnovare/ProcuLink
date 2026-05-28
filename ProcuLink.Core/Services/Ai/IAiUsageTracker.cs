namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// Per-organisation monthly token cap for OpenAI calls.
/// Backed by the <c>ai_usage_monthly</c> table; calendar months reset implicitly
/// by switching to a new composite key (OrgId, Year, Month).
/// </summary>
public interface IAiUsageTracker
{
    /// <summary>
    /// Returns true if the org has already met or exceeded the monthly token
    /// limit, i.e. tokensUsed &gt;= tokensLimit. Used as a pre-flight check
    /// before issuing the OpenAI call.
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
    /// in UTC. Useful for the billing/ai-usage endpoint and for tests.
    /// </summary>
    Task<AiUsageSnapshot> GetCurrentAsync(Guid organisationId, CancellationToken ct = default);

    /// <summary>Configured monthly token limit per org (read from configuration).</summary>
    long MonthlyLimit { get; }
}

public sealed record AiUsageSnapshot(
    Guid OrganisationId,
    int Year,
    int Month,
    long TokensUsed,
    long TokensLimit);
