using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// What a supplier's refusal MEANS for the order — the only distinction that decides where the
/// order lands and whether the automatic queue may try again.
/// </summary>
public enum SupplierResponseKind
{
    /// <summary>
    /// The supplier's system refused the REQUEST, not the ORDER: expired credentials, a moved URL,
    /// a timeout, a rate limit, a network fault, a 5xx. Nothing about the purchase order was
    /// judged. The order goes to <c>delivery_failed</c>, where the automatic backoff queue keeps
    /// trying and — if it never clears — the dead-letter and the operator's Retry / Send again
    /// controls take over. The DEFAULT, deliberately: a refusal we cannot prove is a business
    /// rejection must land somewhere an operator can still act.
    /// </summary>
    Retryable = 0,

    /// <summary>
    /// The supplier READ the order and refused it on its merits — a 422, or a 400 that carries a
    /// reason. Re-sending the same bytes cannot help; the document has to change. The order goes
    /// to <c>rejected_by_supplier</c> and the automatic queue stops.
    /// </summary>
    BusinessRejection = 1,
}

/// <summary>The classification of one failed delivery response.</summary>
/// <param name="Kind">Business rejection, or a refusal the queue may keep trying.</param>
/// <param name="OperatorHint">
/// The sentence an operator reads: what most likely went wrong and what to do about it. Null when
/// the supplier's own message already IS the explanation (a business rejection) or when there is
/// nothing specific to say (5xx / network / no response code) — in which case the raw dispatcher
/// message is passed through untouched.
/// </param>
public readonly record struct SupplierResponseVerdict(SupplierResponseKind Kind, string? OperatorHint)
{
    /// <summary>True when the supplier judged the ORDER and refused it.</summary>
    public bool IsBusinessRejection => Kind == SupplierResponseKind.BusinessRejection;
}

/// <summary>
/// THE table that decides what a failed delivery response means.
///
/// <para><b>Why this exists.</b> Three separate call sites each carried their own hand-written
/// <c>responseCode is &gt;= 400 and &lt;= 499</c> and each concluded "supplier rejection, terminal":
/// <c>DeliveryService.PersistAttemptAsync</c> (which order STATUS to write),
/// <c>DeliverOrderJob</c> (whether to seed the backoff queue after the first failure) and
/// <c>RetryDeliveryJob</c> (whether to schedule the next backoff step). That literal swept in every
/// refusal that is not a rejection at all — an expired API key (401), a moved endpoint (404), a
/// rate limit (429) — and parked the order in <c>rejected_by_supplier</c>, which had NO outgoing
/// transitions and was excluded from Redeliver. The order dead-ended: no automatic retry, no
/// operator control that could move it, and a database edit as the only recourse.</para>
///
/// <para><b>The rule.</b> <c>rejected_by_supplier</c> is reserved for a refusal of the ORDER: a
/// 422, or a 400 that carries a reason from the supplier. Everything else — including a 400 the
/// supplier really did send bare — is <c>delivery_failed</c>, which is retryable, dead-letterable
/// and re-sendable, i.e. somewhere the operator can act.</para>
///
/// <para><b>"Bare" has to be OBSERVED, not assumed.</b> The bare-400 branch asserts something
/// specific: the endpoint answered and offered no reason. That is only knowable on a channel that
/// reads the body. Three did not — <c>erp_erply</c>, <c>erp_directo</c> and <c>email</c> each
/// returned <c>(Success, ErrorMessage, ResponseCode)</c> and never set <c>ResponseBody</c>, though
/// both underlying connectors had the body in hand and folded it into a summary string. Every 400 on
/// the canonical email path and on the two <c>ResendSafety.Unsafe</c> ERP channels therefore
/// classified bare and was re-dispatched to the live endpoint up to the cap. The connectors now
/// carry the body verbatim, AND <see cref="Classify"/> takes a third argument saying whether a blank
/// one is evidence: a dispatcher that cannot see the reason gets the CONSERVATIVE answer (treat the
/// refusal as the supplier's, stop, show a human) rather than the traffic-generating one. See
/// <c>IDeliveryDispatcher.CapturesSupplierResponseBody</c>.</para>
///
/// <para><b>One table, three consumers.</b> The status decision and the two retry gates are
/// derived from the same <see cref="Classify"/> call, so they cannot disagree; and because the
/// two are asserted at their real call sites (<c>DeliveryServiceRejectionTests</c> for the status,
/// <c>RetryDeliveryJobBackoffTests</c> / <c>DeliverOrderJob4xxSplitTests</c> for the queue), a
/// call site that quietly reverts to a literal turns one of them red.</para>
/// </summary>
public static class SupplierResponseClassification
{
    /// <summary>A 400 is a rejection ONLY when the supplier said why. See <see cref="Classify"/>.</summary>
    public const int BadRequest = 400;

    /// <summary>The unambiguous "I read your document and it is not acceptable" code.</summary>
    public const int UnprocessableEntity = 422;

    /// <summary>How much of the supplier's own words to carry into the operator-visible message.</summary>
    private const int MaxSupplierReasonLength = 200;

    // ── The rows. One per refusal whose CAUSE we can actually name. ───────────────────────────
    //
    // Copy rules (CLAUDE.md, plain-language): name the likely cause, then the fix, then where to
    // fix it. No status-code jargon without the plain meaning next to it, and never a claim we
    // cannot stand behind ("most likely", not "has").
    private static readonly IReadOnlyDictionary<int, string> NamedCauses = new Dictionary<int, string>
    {
        [401] =
            "The supplier's endpoint refused our credentials (HTTP 401). The API key or password in " +
            "this supplier's delivery settings has most likely expired or been rotated — update it " +
            "there, then send the order again.",

        [403] =
            "The supplier's endpoint recognised our credentials but refused the request (HTTP 403). " +
            "The account most likely lacks permission to post orders, or the supplier has not " +
            "allow-listed us yet — confirm both with the supplier, check this supplier's delivery " +
            "settings, then send the order again.",

        [404] =
            "The supplier's endpoint was not found (HTTP 404). The delivery address in this " +
            "supplier's delivery settings has most likely moved or contains a typo — confirm the " +
            "address with the supplier, correct it there, then send the order again.",

        [408] =
            "The supplier's endpoint did not answer in time (HTTP 408). Nothing is wrong with the " +
            "order or its settings — ProcuLink keeps trying on its own for a while; if it never " +
            "answers, ask the supplier whether their endpoint is up.",

        [429] =
            "The supplier's endpoint asked us to slow down (HTTP 429) — too many deliveries in a " +
            "short window, not a problem with this order. ProcuLink keeps trying on its own after a " +
            "delay; if it never clears, ask the supplier about their rate limit.",
    };

    /// <summary>Every named row — the surface the table test enumerates against.</summary>
    public static IReadOnlyDictionary<int, string> NamedCauseRows => NamedCauses;

    /// <summary>
    /// Classify a FAILED delivery response.
    /// </summary>
    /// <param name="responseCode">
    /// The HTTP status the supplier returned, or null for channels that have none (SFTP/FTPS/ERP)
    /// and for failures that never reached a response (network, timeout).
    /// </param>
    /// <param name="supplierReason">
    /// The supplier's own response body (<c>DeliveryResult.ResponseBody</c>). Blank means one of two
    /// different things, and <paramref name="supplierReasonObservable"/> is what tells them apart —
    /// read its docs before assuming a blank body says anything. This is what turns an ambiguous 400
    /// into a business rejection: a 400 with a reason is the supplier telling us what is wrong with
    /// the document; a 400 they genuinely sent bare is an unexplained refusal that could equally be a
    /// bad URL or a malformed header, so it must stay somewhere the operator can retry from.
    /// </param>
    /// <param name="supplierReasonObservable">
    /// Whether this channel can SEE the counterparty's reason at all — i.e. whether a blank
    /// <paramref name="supplierReason"/> is evidence that they sent none, or merely the absence of a
    /// capture. Sourced from <c>DeliveryResult.SupplierReasonObservable</c>, which
    /// <c>DeliveryService</c> stamps from the dispatcher's
    /// <c>IDeliveryDispatcher.CapturesSupplierResponseBody</c>.
    ///
    /// <para>Deliberately has NO default. A default here is precisely how the original defect
    /// survived: the doc said blank meant "the endpoint returned nothing" while three dispatchers
    /// made it mean "nobody looked", and nothing at the call sites had to state which. Every caller
    /// now says so out loud.</para>
    /// </param>
    public static SupplierResponseVerdict Classify(
        int? responseCode, string? supplierReason, bool supplierReasonObservable)
    {
        // No code at all: network failure, timeout, or a channel that has no status codes.
        // Nothing to name, nothing to reserve — retryable, message passed through.
        if (responseCode is not int code)
            return new SupplierResponseVerdict(SupplierResponseKind.Retryable, null);

        // 422 — the supplier read the document and refused it. Their own words are the
        // explanation, so we add none of ours.
        if (code == UnprocessableEntity)
            return new SupplierResponseVerdict(SupplierResponseKind.BusinessRejection, null);

        // 400 — ambiguous by itself. WITH a reason it is a business rejection; WITHOUT one it is
        // an unexplained refusal and falls through to the generic retryable hint below.
        if (code == BadRequest && !string.IsNullOrWhiteSpace(supplierReason))
            return new SupplierResponseVerdict(SupplierResponseKind.BusinessRejection, null);

        // …and the case the rule above quietly assumed away: a 400 from a channel that cannot read
        // the counterparty's reason. "No reason" and "we never looked" are indistinguishable in the
        // data, so the retryable branch below would be asserting something we do not know — and it
        // is the expensive direction to be wrong in, because it re-POSTs to a live endpoint up to the
        // cap, on channels that are either the production email path or declare ResendSafety.Unsafe.
        // Treat it as the supplier's own refusal: the queue stops, and rejected_by_supplier is no
        // longer a dead end (WP-19 gave it resolve + re-transform), so the order lands in front of a
        // human who can ask them. No production dispatcher takes this branch today — all three that
        // return a response code now capture the body — it exists so the NEXT one cannot inherit the
        // wrong answer by saying nothing.
        if (code == BadRequest && !supplierReasonObservable)
            return new SupplierResponseVerdict(SupplierResponseKind.BusinessRejection, null);

        // A refusal whose cause we can name.
        if (NamedCauses.TryGetValue(code, out var hint))
            return new SupplierResponseVerdict(SupplierResponseKind.Retryable, hint);

        // Any other 4xx — including the bare 400 — is a refusal we cannot attribute to the order.
        if (code is >= 400 and <= 499)
            return new SupplierResponseVerdict(
                SupplierResponseKind.Retryable,
                $"The supplier's endpoint refused the delivery (HTTP {code}) without saying why, so " +
                "this is not something the supplier asked us to change in the order. Check the " +
                "delivery address, credentials and output format in this supplier's delivery " +
                "settings, then send the order again.");

        // 5xx and anything else: the supplier's system is having trouble. The dispatcher's own
        // message already says so; adding copy here would only change existing behaviour.
        return new SupplierResponseVerdict(SupplierResponseKind.Retryable, null);
    }

    /// <summary>
    /// The order status a FAILED delivery attempt lands in. The single decision
    /// <c>DeliveryService.PersistAttemptAsync</c> makes about a non-success result.
    /// </summary>
    public static string FailedOrderStatusFor(
        int? responseCode, string? supplierReason, bool supplierReasonObservable) =>
        Classify(responseCode, supplierReason, supplierReasonObservable).IsBusinessRejection
            ? OrderStatusConstants.RejectedBySupplier
            : OrderStatusConstants.DeliveryFailed;

    /// <summary>
    /// The same three decisions taken straight from a <see cref="DeliveryResult"/>, so a call site
    /// cannot pair the body with the wrong observability flag — the one mistake that reintroduces the
    /// defect this argument exists to close. Prefer these over the loose overloads everywhere a
    /// result is in hand.
    /// </summary>
    public static SupplierResponseVerdict Classify(DeliveryResult result) =>
        Classify(result.ResponseCode, result.ResponseBody, result.SupplierReasonObservable);

    /// <inheritdoc cref="FailedOrderStatusFor(int?, string?, bool)"/>
    public static string FailedOrderStatusFor(DeliveryResult result) =>
        FailedOrderStatusFor(result.ResponseCode, result.ResponseBody, result.SupplierReasonObservable);

    /// <inheritdoc cref="SuppressesAutomaticRetry(int?, string?, bool)"/>
    public static bool SuppressesAutomaticRetry(DeliveryResult result) =>
        SuppressesAutomaticRetry(result.ResponseCode, result.ResponseBody, result.SupplierReasonObservable);

    /// <inheritdoc cref="DescribeFailure(int?, string?, bool, string?)"/>
    public static string? DescribeFailure(DeliveryResult result) =>
        DescribeFailure(result.ResponseCode, result.ResponseBody, result.SupplierReasonObservable, result.ErrorMessage);

    /// <summary>
    /// Whether the automatic retry queue must stop. TRUE only for a business rejection: re-sending
    /// bytes the supplier read and refused cannot help, and <c>rejected_by_supplier</c> is not a
    /// status the delivery claim admits, so a scheduled retry would only bounce.
    ///
    /// <para>This is the SAME predicate as <see cref="FailedOrderStatusFor"/> returning
    /// <c>rejected_by_supplier</c>, and it must stay that way: a status the queue keeps retrying
    /// but the claim refuses burns the backoff budget on nothing, and a status the queue abandons
    /// but the operator has no control over is the dead end this table exists to end.</para>
    /// </summary>
    public static bool SuppressesAutomaticRetry(
        int? responseCode, string? supplierReason, bool supplierReasonObservable) =>
        Classify(responseCode, supplierReason, supplierReasonObservable).IsBusinessRejection;

    /// <summary>
    /// The message an operator reads on the failed attempt. For a refusal we can name, that is our
    /// sentence plus the supplier's own words when they sent any; for everything else it is the
    /// dispatcher's message, unchanged.
    /// </summary>
    public static string? DescribeFailure(
        int? responseCode, string? supplierReason, bool supplierReasonObservable, string? dispatcherMessage)
    {
        var hint = Classify(responseCode, supplierReason, supplierReasonObservable).OperatorHint;
        if (hint is null) return dispatcherMessage;

        var reason = SummarizeSupplierReason(supplierReason);
        return reason is null ? hint : $"{hint} The supplier's endpoint said: {reason}";
    }

    /// <summary>
    /// One line, bounded. The full body is persisted verbatim on
    /// <c>DeliveryAttempt.ResponseBody</c>; this is only the readable tail of a sentence.
    /// </summary>
    private static string? SummarizeSupplierReason(string? supplierReason)
    {
        if (string.IsNullOrWhiteSpace(supplierReason)) return null;

        var oneLine = supplierReason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length == 0) return null;

        return oneLine.Length > MaxSupplierReasonLength
            ? oneLine[..MaxSupplierReasonLength]
            : oneLine;
    }
}
