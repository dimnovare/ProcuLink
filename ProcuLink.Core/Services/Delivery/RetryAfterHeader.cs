using System.Net.Http.Headers;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Reads the counterparty's <c>Retry-After</c> — the one piece of back-pressure a rate-limited
/// endpoint actually sends us, and which this codebase ignored entirely before WP-19.
///
/// <para>RFC 9110 allows two forms and suppliers use both: <c>Retry-After: 120</c> (delta-seconds)
/// and <c>Retry-After: Wed, 30 Jul 2026 09:00:00 GMT</c> (an HTTP-date). The date form is resolved
/// against the caller's clock, so a date already in the past reads as "no wait", not as a negative
/// one.</para>
///
/// <para><b>Bounded on purpose.</b> The value is tenant-facing input from a third party, so it is
/// clamped to <see cref="MaxHonoured"/>. Beyond that the wait stops being honoured in any meaningful
/// sense anyway: <c>StrandedFailedDeliveryDetectionJob</c> re-drives an aged <c>delivery_failed</c>
/// order after 3h, so a 24h <c>Retry-After</c> would be overridden by the sweep rather than
/// respected — better to come back once at a bounded delay, fail, and let the attempt cap
/// dead-letter the order in front of an operator.</para>
/// </summary>
public static class RetryAfterHeader
{
    /// <summary>
    /// The longest wait we will take from a counterparty. Deliberately equal to the last backoff
    /// step (<c>BackoffMinutes</c> {30,60,120}), so honouring a supplier can never push a retry past
    /// the stranded-delivery sweep's 3h window and turn a respected pause into a sweep re-drive.
    /// </summary>
    public static readonly TimeSpan MaxHonoured = TimeSpan.FromMinutes(120);

    /// <summary>
    /// The wait the counterparty asked for, clamped to <see cref="MaxHonoured"/>, or null when they
    /// asked for nothing (or asked for a time that has already passed).
    /// </summary>
    /// <param name="headers">The response headers; null-safe for channels that have none.</param>
    /// <param name="now">
    /// The instant the HTTP-date form is measured against. Passed in rather than read from the clock
    /// so the parse is a pure function and can be tested without freezing time.
    /// </param>
    public static TimeSpan? Read(HttpResponseHeaders? headers, DateTimeOffset now)
    {
        if (headers?.RetryAfter is not { } retryAfter)
            return null;

        var requested =
            retryAfter.Delta is { } delta ? delta
            : retryAfter.Date is { } date ? date - now
            : (TimeSpan?)null;

        if (requested is not { } wait || wait <= TimeSpan.Zero)
            return null;

        return wait > MaxHonoured ? MaxHonoured : wait;
    }
}
