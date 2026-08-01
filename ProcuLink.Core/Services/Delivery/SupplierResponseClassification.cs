using System.Text.Json;
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

/// <summary>
/// The stable, machine-readable name for WHY a delivery was refused — the slug a client keys its
/// recovery control off.
///
/// <para><b>Why this exists.</b> <see cref="SupplierResponseClassification"/> already knew the
/// difference between an expired API key, a moved endpoint and a rate limit, and already said so in
/// plain language. But the prose was the only thing that crossed the API boundary, so a client that
/// wanted to offer the cause-specific control — "update the credentials" vs "correct the address"
/// vs "wait it out" — had to regex <c>"(HTTP 401)"</c> out of an English sentence. That is a mirror
/// of this table written in another language in another repository, and it goes stale the first time
/// the copy is reworded. These slugs are the contract instead; the prose stays free to improve.</para>
///
/// <para><b>Not the status code.</b> A code is transport trivia; a cause is a decision. Several
/// codes collapse onto <see cref="BusinessRejection"/> because the recovery is the same one
/// (correct the document and re-transform), and one code — 400 — splits across two causes because
/// the recovery is NOT the same depending on whether the supplier said why.</para>
///
/// <para>Values are wire contract. Change a string here and every stored client mapping breaks.</para>
/// </summary>
public static class SupplierFailureCause
{
    /// <summary>401 — our credentials were refused. Cure: re-enter the key/password.</summary>
    public const string AuthRejected = "supplier_auth_rejected";

    /// <summary>403 — credentials recognised, this account may not post orders. Cure: ask the supplier.</summary>
    public const string PermissionDenied = "supplier_permission_denied";

    /// <summary>404 — the delivery address is wrong or has moved. Cure: correct the address.</summary>
    public const string EndpointNotFound = "supplier_endpoint_not_found";

    /// <summary>408 — the endpoint did not answer in time. Cure: none; the queue keeps trying.</summary>
    public const string Timeout = "supplier_timeout";

    /// <summary>
    /// 429 — too many deliveries in a short window. Cure: none; the queue keeps trying, and
    /// <c>DeliveryAttempt.RetryAfterSeconds</c> carries how long the supplier asked us to wait.
    /// </summary>
    public const string RateLimited = "supplier_rate_limited";

    /// <summary>
    /// The supplier READ the order and refused it — a 422, a 400 carrying their reason, or a 400 on
    /// a channel that cannot see the reason (where the conservative reading is that the refusal was
    /// theirs). Cure: correct the document and re-transform. The one cause where the supplier's own
    /// words, not ours, are the explanation.
    /// </summary>
    public const string BusinessRejection = "supplier_business_rejection";

    /// <summary>
    /// A 400 a channel that CAN read the body genuinely received bare. Deliberately NOT
    /// <see cref="BusinessRejection"/>: nothing in the document is known to be wrong, so the control
    /// to offer is "check the address, credentials and output format", not "correct the order".
    /// </summary>
    public const string RefusedWithoutReason = "supplier_refused_without_reason";

    /// <summary>Any other 4xx — refused, cause unnamed. The honest "we cannot tell you which".</summary>
    public const string RefusedOther = "supplier_refused_other";

    /// <summary>
    /// A 5xx, or no response code at all (network fault, or a channel that has none — SFTP/FTPS/ERP).
    /// The supplier's SYSTEM, not the supplier: nothing about the order was judged.
    /// </summary>
    public const string Unreachable = "supplier_unreachable";
}

/// <summary>The classification of one failed delivery response.</summary>
/// <param name="Kind">Business rejection, or a refusal the queue may keep trying.</param>
/// <param name="OperatorHint">
/// The sentence an operator reads: what most likely went wrong and what to do about it. Null when
/// the supplier's own message already IS the explanation (a business rejection) or when there is
/// nothing specific to say (5xx / network / no response code) — in which case the raw dispatcher
/// message is passed through untouched.
/// </param>
/// <param name="Cause">
/// The same verdict as a stable <see cref="SupplierFailureCause"/> slug — what a client keys a
/// cause-specific recovery control off, so it never has to parse <paramref name="OperatorHint"/>.
/// Never null: every response this table classifies has a cause, even if that cause is "we cannot
/// name this one".
/// </param>
public readonly record struct SupplierResponseVerdict(
    SupplierResponseKind Kind, string? OperatorHint, string Cause)
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

    /// <summary>
    /// One row of the named-cause table: the sentence an operator reads AND the slug a client keys
    /// its recovery control off. Deliberately ONE type rather than two dictionaries — a row that
    /// arrives with prose and no slug is then a compile error rather than a silently unnameable
    /// cause on the wire.
    /// </summary>
    /// <param name="Hint">The operator-facing sentence: likely cause, the fix, where the fix is made.</param>
    /// <param name="Cause">The stable <see cref="SupplierFailureCause"/> slug for this row.</param>
    public readonly record struct NamedCause(string Hint, string Cause);

    // ── The rows. One per refusal whose CAUSE we can actually name. ───────────────────────────
    //
    // Copy rules (CLAUDE.md, plain-language): name the likely cause, then the fix, then where to
    // fix it. No status-code jargon without the plain meaning next to it, and never a claim we
    // cannot stand behind ("most likely", not "has").
    private static readonly IReadOnlyDictionary<int, NamedCause> NamedCauses = new Dictionary<int, NamedCause>
    {
        [401] = new(
            "The supplier's endpoint refused our credentials (HTTP 401). The API key or password in " +
            "this supplier's delivery settings has most likely expired or been rotated — update it " +
            "there, then send the order again.",
            SupplierFailureCause.AuthRejected),

        [403] = new(
            "The supplier's endpoint recognised our credentials but refused the request (HTTP 403). " +
            "The account most likely lacks permission to post orders, or the supplier has not " +
            "allow-listed us yet — confirm both with the supplier, check this supplier's delivery " +
            "settings, then send the order again.",
            SupplierFailureCause.PermissionDenied),

        [404] = new(
            "The supplier's endpoint was not found (HTTP 404). The delivery address in this " +
            "supplier's delivery settings has most likely moved or contains a typo — confirm the " +
            "address with the supplier, correct it there, then send the order again.",
            SupplierFailureCause.EndpointNotFound),

        [408] = new(
            "The supplier's endpoint did not answer in time (HTTP 408). Nothing is wrong with the " +
            "order or its settings — ProcuLink keeps trying on its own for a while; if it never " +
            "answers, ask the supplier whether their endpoint is up.",
            SupplierFailureCause.Timeout),

        [429] = new(
            "The supplier's endpoint asked us to slow down (HTTP 429) — too many deliveries in a " +
            "short window, not a problem with this order. ProcuLink keeps trying on its own after a " +
            "delay; if it never clears, ask the supplier about their rate limit.",
            SupplierFailureCause.RateLimited),
    };

    /// <summary>Every named row — the surface the table test enumerates against.</summary>
    public static IReadOnlyDictionary<int, NamedCause> NamedCauseRows => NamedCauses;

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
            return new SupplierResponseVerdict(
                SupplierResponseKind.Retryable, null, SupplierFailureCause.Unreachable);

        // 422 — the supplier read the document and refused it. Their own words are the
        // explanation, so we add none of ours.
        if (code == UnprocessableEntity)
            return new SupplierResponseVerdict(
                SupplierResponseKind.BusinessRejection, null, SupplierFailureCause.BusinessRejection);

        // 400 — ambiguous by itself. WITH a reason it is a business rejection; WITHOUT one it is
        // an unexplained refusal and falls through to the generic retryable hint below.
        if (code == BadRequest && !string.IsNullOrWhiteSpace(supplierReason))
            return new SupplierResponseVerdict(
                SupplierResponseKind.BusinessRejection, null, SupplierFailureCause.BusinessRejection);

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
            return new SupplierResponseVerdict(
                SupplierResponseKind.BusinessRejection, null, SupplierFailureCause.BusinessRejection);

        // A refusal whose cause we can name. ONE lookup produces both the prose and the slug, so a
        // row added to NamedCauses cannot arrive with a hint and no cause (or vice versa).
        if (NamedCauses.TryGetValue(code, out var named))
            return new SupplierResponseVerdict(SupplierResponseKind.Retryable, named.Hint, named.Cause);

        // Any other 4xx — including the bare 400 — is a refusal we cannot attribute to the order.
        // The two share one hint because the operator's next step is the same; they do NOT share a
        // cause, because a 400 the channel really did receive bare is a specific, recognisable
        // situation a client can offer a specific control for, while "some other 4xx" is not.
        if (code is >= 400 and <= 499)
            return new SupplierResponseVerdict(
                SupplierResponseKind.Retryable,
                $"The supplier's endpoint refused the delivery (HTTP {code}) without saying why, so " +
                "this is not something the supplier asked us to change in the order. Check the " +
                "delivery address, credentials and output format in this supplier's delivery " +
                "settings, then send the order again.",
                code == BadRequest
                    ? SupplierFailureCause.RefusedWithoutReason
                    : SupplierFailureCause.RefusedOther);

        // 5xx and anything else: the supplier's system is having trouble. The dispatcher's own
        // message already says so; adding copy here would only change existing behaviour.
        return new SupplierResponseVerdict(
            SupplierResponseKind.Retryable, null, SupplierFailureCause.Unreachable);
    }

    /// <summary>
    /// The cause of a failure as recorded on a STORED <c>DeliveryAttempt</c> row — the read-side
    /// entry point, for projections that have the persisted columns and not the live result.
    ///
    /// <para><b>Why the flag is recovered rather than stored.</b> A row carries no
    /// <c>SupplierReasonObservable</c> column, but it does not need one: <c>DeliveryService</c>
    /// stamps <c>RejectionReason</c> exactly when the classification came out
    /// <see cref="SupplierResponseKind.BusinessRejection"/>, so its presence on a bare 400 IS the
    /// record that the channel could not see the supplier's words. Feeding that back into
    /// <see cref="Classify(int?, string?, bool)"/> keeps ONE branch deciding — a read-side switch of
    /// its own is precisely the parallel table this packet exists to avoid.</para>
    ///
    /// <para><b>Its one edge, stated rather than hidden.</b> The recovery reads
    /// <c>RejectionReason</c>, which <c>PersistAttemptAsync</c> writes as the DISPATCHER's message —
    /// so a future dispatcher that returned a bare 400 on an unobservable channel with a null
    /// <c>ErrorMessage</c> would store a blank reason, and this would read the row back as the
    /// observable bare-400 case. Only that one shape is affected (every other code means the same
    /// thing on every channel), no dispatcher produces it today — all of them set an error message
    /// on failure — and the ORDER's own status, written live, stays correct regardless. It is a
    /// read-side label, not a routing decision.</para>
    /// </summary>
    /// <param name="responseCode">The stored <c>DeliveryAttempt.ResponseCode</c>.</param>
    /// <param name="rejectionReason">The stored <c>DeliveryAttempt.RejectionReason</c>.</param>
    /// <param name="responseBody">The stored <c>DeliveryAttempt.ResponseBody</c>.</param>
    public static string CauseFor(int? responseCode, string? rejectionReason, string? responseBody) =>
        Classify(
            responseCode,
            responseBody,
            supplierReasonObservable: string.IsNullOrWhiteSpace(rejectionReason)).Cause;

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

        var summary = SummarizeResponseBody(supplierReason);
        if (summary.Text is null) return hint;

        return summary.Quotable
            ? $"{hint} The supplier's endpoint said: {summary.Text}"
            : $"{hint} {summary.Text}";
    }

    /// <summary>
    /// What, if anything, may be said about a remote endpoint's response body.
    /// </summary>
    /// <param name="Quotable">
    /// TRUE when <see cref="Text"/> is the supplier's OWN words and may be quoted after
    /// "said:". FALSE when it is our sentence describing what they sent instead of words.
    /// </param>
    /// <param name="Text">Null when the body was empty and there is nothing to say at all.</param>
    public readonly record struct SupplierReasonSummary(bool Quotable, string? Text);

    /// <summary>
    /// The ONE place that decides how a remote endpoint's response body may appear in copy a
    /// person reads. Both passthroughs go through it — <see cref="DescribeFailure(int?, string?, bool, string?)"/>
    /// and the HTTP dispatcher's own failure sentence — because fixing one and leaving the other
    /// is how a body reaches the operator anyway: DescribeFailure returns the dispatcher's message
    /// UNCHANGED for every response code this table has no hint for.
    ///
    /// <para>WP-39 §4.4 found a webhook.site error page pasted into an operator's failure message
    /// and cut off mid-tag. The 200-character cap had fired correctly; nothing had asked whether
    /// the bytes were a message in the first place.</para>
    ///
    /// <para>The rule is the frontend's, already settled in <c>parseApiErrorBody</c>: extract a
    /// message you can identify, or say what arrived — never fall back to pasting the raw body in
    /// front of a person. The full body is persisted verbatim on
    /// <c>DeliveryAttempt.ResponseBody</c> either way, so nothing is lost for a dispute; this only
    /// governs the sentence.</para>
    /// </summary>
    public static SupplierReasonSummary SummarizeResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new(false, null);

        var oneLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length == 0) return new(false, null);

        // Markup. A tag where a sentence was expected is a machine's answer, not a supplier's.
        if (oneLine[0] == '<')
            return new(false, LooksLikeHtml(oneLine)
                ? "Their endpoint returned an HTML error page, not a message."
                : "Their endpoint returned an XML response with no readable message in it.");

        // JSON. Mine it for the field a human would have written, and say so when there is none —
        // a raw object pasted into a sentence is the same defect wearing different brackets.
        if (oneLine[0] is '{' or '[')
        {
            var mined = MessageFromJson(oneLine);
            return mined is null
                ? new(false, "Their endpoint returned a JSON error with no message in it.")
                : new(true, Cap(mined));
        }

        return new(true, Cap(oneLine));
    }

    private static string Cap(string s) =>
        s.Length > MaxSupplierReasonLength ? s[..MaxSupplierReasonLength] : s;

    private static bool LooksLikeHtml(string oneLine)
    {
        // The production case opened with an HTML comment, so the first tag is not enough.
        var head = oneLine.Length > 512 ? oneLine[..512] : oneLine;
        return head.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first non-empty string under a name a person would have put a sentence in. Nested
    /// objects are searched too — a supplier that answers <c>{"error":{"message":"…"}}</c> has
    /// still told us something.
    /// </summary>
    private static string? MessageFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindMessage(doc.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            // Opened like JSON and is not. Nothing here is identifiable, so nothing is quoted.
            return null;
        }
    }

    private static readonly string[] MessageFieldNames = ["message", "error_description", "detail", "error", "title", "reason"];

    private static string? FindMessage(JsonElement element, int depth)
    {
        if (depth > 3) return null;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindMessage(item, depth + 1) is { } fromItem) return fromItem;
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in MessageFieldNames)
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (FindMessage(value, depth + 1) is { } nested) return nested;
            }
        }

        return null;
    }
}
