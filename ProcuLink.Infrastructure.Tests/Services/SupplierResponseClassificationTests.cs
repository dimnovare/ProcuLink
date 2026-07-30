using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Delivery;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-19 — the table that ended the 4xx dead end.
///
/// <para>Before this table, three call sites each carried their own
/// <c>responseCode is &gt;= 400 and &lt;= 499</c> and each concluded "supplier rejection, terminal".
/// An expired API key, a moved endpoint and a rate limit all landed in
/// <c>rejected_by_supplier</c> — a status with no outgoing transitions, excluded from Redeliver,
/// abandoned by the retry queue. These tests pin the split, and the last one pins the thing that
/// makes it safe: the STATUS decision and the RETRY decision are the same decision.</para>
///
/// <para>Every call below states whether the channel could OBSERVE a supplier reason. That argument
/// is not decoration: the bare-400 branch asserts "the endpoint answered and offered no reason",
/// which three dispatchers could not actually know — see
/// <c>BareBadRequestDiscriminatorTests</c>.</para>
/// </summary>
public class SupplierResponseClassificationTests
{
    /// <summary>A channel that reads the counterparty's body — http, erp_*, email.</summary>
    private const bool Observable = true;

    /// <summary>A channel that cannot: a blank reason there is absence of capture, not evidence.</summary>
    private const bool NotObservable = false;

    // ── One test per status code (each InlineData is its own reported test case) ───────────────

    [Theory]
    [InlineData(401)] // expired / rotated credentials
    [InlineData(403)] // credentials fine, this account may not post orders here
    [InlineData(404)] // the endpoint moved
    [InlineData(408)] // the endpoint did not answer in time
    [InlineData(429)] // rate limited
    public void TransportRefusal_IsRetryable_AndNamesItsLikelyCause(int code)
    {
        var verdict = SupplierResponseClassification.Classify(code, supplierReason: null, Observable);

        verdict.IsBusinessRejection.Should().BeFalse(
            $"HTTP {code} is the supplier's SYSTEM refusing the request — it says nothing about the " +
            "order, so routing it to rejected_by_supplier strands a deliverable PO in a status " +
            "nothing can move");

        SupplierResponseClassification.FailedOrderStatusFor(code, null, Observable)
            .Should().Be(OrderStatusConstants.DeliveryFailed);

        SupplierResponseClassification.SuppressesAutomaticRetry(code, null, Observable).Should().BeFalse(
            $"HTTP {code} can clear on its own or after an operator fixes the delivery settings, so " +
            "the backoff queue must keep the order moving toward either a delivery or a dead-letter");

        verdict.OperatorHint.Should().NotBeNullOrWhiteSpace(
            "an operator staring at a failed order needs the likely cause named, not a status code");
        verdict.OperatorHint.Should().Contain(code.ToString(),
            "the copy must name the actual response so it can be matched against the supplier's own logs");
    }

    [Fact]
    public void UnprocessableEntity422_IsABusinessRejection()
    {
        SupplierResponseClassification.Classify(422, null, Observable).IsBusinessRejection.Should().BeTrue(
            "422 is the unambiguous 'I read your document and it is not acceptable'");

        SupplierResponseClassification.FailedOrderStatusFor(422, null, Observable)
            .Should().Be(OrderStatusConstants.RejectedBySupplier);

        SupplierResponseClassification.SuppressesAutomaticRetry(422, null, Observable).Should().BeTrue(
            "re-sending bytes the supplier read and refused cannot help");
    }

    [Fact]
    public void BadRequest400_WithASupplierReason_IsABusinessRejection()
    {
        const string reason = "{\"error\":\"unknown buyer code BC-9\"}";

        SupplierResponseClassification.Classify(400, reason, Observable).IsBusinessRejection.Should().BeTrue(
            "a 400 that carries a reason IS the supplier telling us what is wrong with the document");

        SupplierResponseClassification.FailedOrderStatusFor(400, reason, Observable)
            .Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Fact]
    public void BadRequest400_WithNoSupplierReason_IsRetryable()
    {
        // The asymmetry that makes 400 the interesting code: bare, it is indistinguishable from a
        // bad URL, a malformed header, or a proxy in front of the supplier. Calling that a business
        // rejection is the dead end in miniature — so an unexplained refusal stays somewhere the
        // operator can retry from.
        SupplierResponseClassification.Classify(400, supplierReason: "   ", Observable).IsBusinessRejection
            .Should().BeFalse();

        SupplierResponseClassification.FailedOrderStatusFor(400, null, Observable)
            .Should().Be(OrderStatusConstants.DeliveryFailed);

        SupplierResponseClassification.Classify(400, null, Observable).OperatorHint
            .Should().NotBeNullOrWhiteSpace("an unexplained refusal still owes the operator a next step");
    }

    // ── The discriminator the bare-400 rule needs ─────────────────────────────────────────────

    /// <summary>
    /// The claim "the supplier said nothing" requires a channel that could have heard them.
    ///
    /// <para><b>The defect this closes.</b> The rule above reads <c>DeliveryResult.ResponseBody</c>,
    /// and on <c>erp_erply</c>, <c>erp_directo</c> and <c>email</c> nothing ever set it — so a
    /// genuine business 400 was ALWAYS classified bare, landed in <c>delivery_failed</c>, and was
    /// re-dispatched to the live endpoint up to the attempt cap. Email is the canonical outbound
    /// path and is live on production; both ERP channels declare <c>ResendSafety.Unsafe</c>.</para>
    ///
    /// <para><b>Which way to be wrong.</b> With no capture the two cases are identical in the data,
    /// so one of them must be chosen blind. Retryable spends real requests against a live endpoint
    /// and tells the operator to go check credentials that may be perfectly fine. Business rejection
    /// spends none, and lands the order in a status that (since WP-19) has two exits and a human
    /// looking at it. The conservative direction is the second — and it is only reachable at all
    /// because this packet gave <c>rejected_by_supplier</c> a way out.</para>
    ///
    /// <para>No production dispatcher takes this branch today; all three that return a response code
    /// now capture the body. It exists so the FOURTH one cannot inherit the wrong answer in silence
    /// — the same role <c>ResendSafety.Unsafe</c> plays as the interface default.</para>
    /// </summary>
    [Fact]
    public void BadRequest400_FromAChannelThatCannotSeeTheReason_IsNotCalledUnexplained()
    {
        SupplierResponseClassification.Classify(400, supplierReason: null, NotObservable)
            .IsBusinessRejection.Should().BeTrue(
                "a blank body from a dispatcher that never captures one is the ABSENCE of evidence, " +
                "not evidence of absence — treating it as 'the supplier said nothing' re-sends a " +
                "refusal that may well have named a fault in the document");

        SupplierResponseClassification.FailedOrderStatusFor(400, null, NotObservable)
            .Should().Be(OrderStatusConstants.RejectedBySupplier);

        SupplierResponseClassification.SuppressesAutomaticRetry(400, null, NotObservable).Should().BeTrue(
            "the expensive half of the mistake is the traffic: three real POSTs to a live endpoint, " +
            "on channels that are either the production email path or cannot de-duplicate at all");
    }

    /// <summary>
    /// …and the flag changes NOTHING else. It is a discriminator for one ambiguous code, not a
    /// second classification axis — every other response means the same thing on every channel.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(422)]
    [InlineData(451)]
    [InlineData(500)]
    [InlineData(503)]
    public void Observability_ChangesOnlyTheBareFourHundred(int code)
    {
        SupplierResponseClassification.Classify(code, null, NotObservable).IsBusinessRejection
            .Should().Be(
                SupplierResponseClassification.Classify(code, null, Observable).IsBusinessRejection,
                $"HTTP {code} carries its own meaning; only 400 needs the supplier's words to be " +
                "read, so only 400 may depend on whether we could read them");
    }

    [Fact]
    public void ADeliveryResultClassifiesItself_SoTheBodyAndTheFlagCannotBeMispaired()
    {
        // The overload exists because pairing a body from one result with a flag from somewhere else
        // is the one mistake that silently reintroduces the defect.
        var fromANonCapturingChannel = new DeliveryResult(false, "HTTP 400", 400);
        var fromACapturingChannel = new DeliveryResult(false, "HTTP 400", 400, SupplierReasonObservable: true);

        SupplierResponseClassification.FailedOrderStatusFor(fromANonCapturingChannel)
            .Should().Be(OrderStatusConstants.RejectedBySupplier);
        SupplierResponseClassification.FailedOrderStatusFor(fromACapturingChannel)
            .Should().Be(OrderStatusConstants.DeliveryFailed);

        new DeliveryResult(false, "x", 400).SupplierReasonObservable.Should().BeFalse(
            "the DEFAULT must be the conservative one: a result nobody stamped has not proven the " +
            "supplier stayed silent");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void ServerError_IsRetryable_AndKeepsTheDispatcherMessageVerbatim(int code)
    {
        SupplierResponseClassification.Classify(code, null, Observable).IsBusinessRejection.Should().BeFalse();
        SupplierResponseClassification.Classify(code, null, Observable).OperatorHint.Should().BeNull();
        SupplierResponseClassification.DescribeFailure(code, null, Observable, "Gateway timeout")
            .Should().Be("Gateway timeout", "5xx behaviour is unchanged by this packet");
    }

    [Fact]
    public void NoResponseCode_IsRetryable_AndKeepsTheDispatcherMessageVerbatim()
    {
        // SFTP/FTPS have no status codes, and a network failure never got one. Asserted for BOTH
        // observability answers: with no code there is no 400 to disambiguate, so the flag is inert.
        foreach (var observable in new[] { Observable, NotObservable })
        {
            SupplierResponseClassification.Classify(null, null, observable).IsBusinessRejection.Should().BeFalse();
            SupplierResponseClassification.DescribeFailure(null, null, observable, "Connection refused")
                .Should().Be("Connection refused");
        }
    }

    // ── The table, whole ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryFourHundredCode_IsClassified_AndOnlyTheBusinessOnesAreRejections()
    {
        for (var code = 400; code <= 499; code++)
        {
            var withoutReason = SupplierResponseClassification.Classify(code, null, Observable);
            var withReason    = SupplierResponseClassification.Classify(
                code, "supplier says: line 3 is not orderable", Observable);

            // Every refusal that is NOT a business rejection owes the operator a next step —
            // including the codes this table has no named row for. A business rejection carries no
            // hint of ours on purpose: the supplier's own reason IS the explanation, and prefixing
            // it with our guesswork would bury the one sentence that says what to change. Not
            // hindsight — this sweep is what caught the first version of the assertion demanding a
            // hint for 422.
            if (withoutReason.IsBusinessRejection)
                withoutReason.OperatorHint.Should().BeNull(
                    $"HTTP {code} is the supplier explaining themselves — our copy must not " +
                    "displace theirs");
            else
                withoutReason.OperatorHint.Should().NotBeNullOrWhiteSpace(
                    $"HTTP {code} lands in front of an operator with no supplier reason attached, " +
                    "so the product owes it a likely cause and a next step");

            withoutReason.IsBusinessRejection.Should().Be(
                code == SupplierResponseClassification.UnprocessableEntity,
                $"with no reason from the supplier, only 422 is a refusal OF THE ORDER (HTTP {code})");

            withReason.IsBusinessRejection.Should().Be(
                code is SupplierResponseClassification.UnprocessableEntity
                     or SupplierResponseClassification.BadRequest,
                $"with a reason from the supplier, 400 joins 422 and nothing else does (HTTP {code})");
        }
    }

    [Fact]
    public void DescribeFailure_CarriesBothOurHintAndTheSuppliersOwnWords()
    {
        var message = SupplierResponseClassification.DescribeFailure(
            401, "token expired at 2026-07-30T09:00Z", Observable,
            "HTTP 401: supplier endpoint returned an error.");

        message.Should().Contain("401");
        message.Should().Contain("delivery settings", "the copy must point at where the fix is made");
        message.Should().Contain("token expired at 2026-07-30T09:00Z",
            "the supplier's own words are evidence and must not be thrown away by our copy");
    }

    // ── The difference ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The status decision and the retry decision are ONE decision, asserted as an equivalence over
    /// the whole range rather than trusted to two call sites that agreed once.
    ///
    /// <para>Both directions are real failures, and both have happened in this codebase's shape:
    /// a status the queue keeps retrying but the delivery claim refuses burns the backoff budget on
    /// a claim that matches 0 rows (the 52c6431 shape); a status the queue abandons but no operator
    /// control can move is the dead end this packet exists to end.</para>
    ///
    /// <para>Swept over BOTH observability answers, so the argument added for the bare-400
    /// discriminator cannot pull the two halves apart on one kind of channel and not the other.</para>
    /// </summary>
    [Fact]
    public void RetrySuppression_AndRejectedBySupplier_AreTheSameDecision()
    {
        var codes = Enumerable.Range(400, 200).Select(c => (int?)c).Append(null).ToList();

        foreach (var code in codes)
        foreach (var reason in new[] { null, "", "   ", "the supplier explained itself" })
        foreach (var observable in new[] { Observable, NotObservable })
        {
            var status     = SupplierResponseClassification.FailedOrderStatusFor(code, reason, observable);
            var suppressed = SupplierResponseClassification.SuppressesAutomaticRetry(code, reason, observable);

            suppressed.Should().Be(
                status == OrderStatusConstants.RejectedBySupplier,
                $"HTTP {code?.ToString() ?? "(none)"} with reason '{reason ?? "(null)"}' " +
                $"(observable={observable}) must not land in one half of the split and be treated " +
                "as the other half by the retry queue");
        }
    }
}
