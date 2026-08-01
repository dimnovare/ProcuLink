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

    /// <summary>
    /// How far ABOVE the scheduled step a retry may be pushed, as a percentage, to break up a
    /// synchronised burst. Upward-only on purpose — see <see cref="NextRetryDelay"/>.
    /// <para>20% keeps the worst case (120 min + 20% = 144 min) comfortably inside the
    /// stranded-delivery sweep's 3h window, so jitter can never turn a healthy backoff into a strand
    /// the sweep re-drives.</para>
    /// </summary>
    public int RetryJitterPercent { get; set; } = 20;

    /// <summary>Returns the backoff delay to wait before the retry that follows <paramref name="failedAttempts"/> failures.</summary>
    public TimeSpan BackoffFor(int failedAttempts)
    {
        if (BackoffMinutes is null || BackoffMinutes.Length == 0)
            return TimeSpan.FromMinutes(30);

        var index = Math.Clamp(failedAttempts - 1, 0, BackoffMinutes.Length - 1);
        return TimeSpan.FromMinutes(BackoffMinutes[index]);
    }

    /// <summary>
    /// THE delay the retry queue actually schedules: the fixed step, raised to whatever the supplier
    /// asked for, then spread by jitter. A pure function of its three inputs so the whole policy can
    /// be asserted without a clock or a random source.
    ///
    /// <para><b>Why <see cref="BackoffFor"/> alone was not enough.</b> The schedule is FIXED
    /// (30 → 60 → 120) and nothing anywhere jittered it. Every cause that fails one order for a
    /// supplier fails the whole batch — a rate limit, a rotated credential, a brief outage — so N
    /// orders failing together were all scheduled at the same offset and came back as one
    /// synchronised burst, three times over. The only ceiling was the Worker's
    /// <c>WorkerCount = 10</c>, which is a concurrency limit, not a rate limit, and it does not
    /// reduce the total the supplier receives — only how many arrive at once. This is the one failure
    /// mode in the retry path that grows with order volume.</para>
    ///
    /// <para><b>Retry-After is a FLOOR, never a ceiling.</b> A supplier can slow us down; they cannot
    /// speed us up. A shorter <c>Retry-After</c> than our own step would otherwise let a rate-limited
    /// endpoint pull the whole batch back in sooner than our backoff intended — the opposite of what
    /// the header is for. (<see cref="RetryAfterHeader"/> has already bounded the value.)</para>
    ///
    /// <para><b>Jitter is upward-only</b>, for the same reason: symmetric jitter could fire before a
    /// wait the supplier explicitly asked for, and before the documented step. The mean shifts up by
    /// half the jitter band, which is the harmless direction.</para>
    /// </summary>
    /// <param name="failedAttempts">Attempts already made — selects the backoff step.</param>
    /// <param name="supplierRetryAfter">
    /// What the counterparty asked for (<c>DeliveryResult.RetryAfter</c>), or null.
    /// </param>
    /// <param name="jitterSample">
    /// A sample in [0,1). Callers pass <c>Random.Shared.NextDouble()</c>; tests pass a fixed value,
    /// which is what keeps this function assertable. Out-of-range values are clamped.
    /// </param>
    public TimeSpan NextRetryDelay(int failedAttempts, TimeSpan? supplierRetryAfter, double jitterSample)
    {
        var step = BackoffFor(failedAttempts);

        // The floor: whichever of "our schedule" and "what they asked for" is longer — and the
        // asked-for value is re-bounded HERE, not only where it was read off the wire. RetryAfterHeader
        // already clamps it, but a bound that lives at one call site is a bound the next caller does
        // not have: this method takes a plain TimeSpan? and cannot see where it came from. An
        // unbounded value would push the retry past the stranded sweep's 3h window, where the sweep
        // re-drives the order anyway — so the wait would not be honoured, merely mis-scheduled.
        if (supplierRetryAfter is { } asked)
        {
            var bounded = asked > RetryAfterHeader.MaxHonoured ? RetryAfterHeader.MaxHonoured : asked;
            if (bounded > step)
                step = bounded;
        }

        var percent = Math.Max(0, RetryJitterPercent);
        if (percent == 0)
            return step;

        var sample = double.IsNaN(jitterSample) ? 0.0 : Math.Clamp(jitterSample, 0.0, 1.0);
        return step + TimeSpan.FromTicks((long)(step.Ticks * (percent / 100.0) * sample));
    }

    /// <summary>The SLA confirmation window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SlaWindow => TimeSpan.FromMinutes(SlaWindowMinutes <= 0 ? 120 : SlaWindowMinutes);
}
