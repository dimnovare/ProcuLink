namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Tunables for Group O delivery reliability: the automatic retry-queue backoff schedule
/// and the SLA confirmation window. Bound from configuration section
/// <c>Delivery:Reliability</c> where present; otherwise the defaults below apply.
/// </summary>
public sealed class DeliveryReliabilityOptions
{
    public const string SectionName = "Delivery:Reliability";

    /// <summary>
    /// Total delivery attempts allowed (including the first dispatch) before an order is
    /// moved to <c>delivery_dead_letter</c>. Default 3 → first attempt + 2 backoff retries.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Exponential backoff delays (minutes) between automatic retries, indexed by the number
    /// of failed attempts already made. ~30-minute base, doubling: 30 → 60 → 120. The Nth
    /// retry uses <c>BackoffMinutes[min(N-1, last)]</c>, so the schedule is safe for any
    /// <see cref="MaxAttempts"/>.
    /// </summary>
    public int[] BackoffMinutes { get; set; } = { 30, 60, 120 };

    /// <summary>
    /// SLA window (minutes) from the moment a delivery first starts until it must be confirmed
    /// delivered. If it is still unconfirmed past this window, the SLA sweep flags the order.
    /// Default 120 minutes.
    /// </summary>
    public int SlaWindowMinutes { get; set; } = 120;

    /// <summary>Returns the backoff delay to wait before the retry that follows <paramref name="failedAttempts"/> failures.</summary>
    public TimeSpan BackoffFor(int failedAttempts)
    {
        if (BackoffMinutes is null || BackoffMinutes.Length == 0)
            return TimeSpan.FromMinutes(30);

        var index = Math.Clamp(failedAttempts - 1, 0, BackoffMinutes.Length - 1);
        return TimeSpan.FromMinutes(BackoffMinutes[index]);
    }

    /// <summary>The SLA confirmation window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SlaWindow => TimeSpan.FromMinutes(SlaWindowMinutes <= 0 ? 120 : SlaWindowMinutes);
}
