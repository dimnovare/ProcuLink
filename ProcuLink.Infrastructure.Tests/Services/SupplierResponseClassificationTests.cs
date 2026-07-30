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
/// </summary>
public class SupplierResponseClassificationTests
{
    // ── One test per status code (each InlineData is its own reported test case) ───────────────

    [Theory]
    [InlineData(401)] // expired / rotated credentials
    [InlineData(403)] // credentials fine, this account may not post orders here
    [InlineData(404)] // the endpoint moved
    [InlineData(408)] // the endpoint did not answer in time
    [InlineData(429)] // rate limited
    public void TransportRefusal_IsRetryable_AndNamesItsLikelyCause(int code)
    {
        var verdict = SupplierResponseClassification.Classify(code, supplierReason: null);

        verdict.IsBusinessRejection.Should().BeFalse(
            $"HTTP {code} is the supplier's SYSTEM refusing the request — it says nothing about the " +
            "order, so routing it to rejected_by_supplier strands a deliverable PO in a status " +
            "nothing can move");

        SupplierResponseClassification.FailedOrderStatusFor(code, null)
            .Should().Be(OrderStatusConstants.DeliveryFailed);

        SupplierResponseClassification.SuppressesAutomaticRetry(code, null).Should().BeFalse(
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
        SupplierResponseClassification.Classify(422, null).IsBusinessRejection.Should().BeTrue(
            "422 is the unambiguous 'I read your document and it is not acceptable'");

        SupplierResponseClassification.FailedOrderStatusFor(422, null)
            .Should().Be(OrderStatusConstants.RejectedBySupplier);

        SupplierResponseClassification.SuppressesAutomaticRetry(422, null).Should().BeTrue(
            "re-sending bytes the supplier read and refused cannot help");
    }

    [Fact]
    public void BadRequest400_WithASupplierReason_IsABusinessRejection()
    {
        const string reason = "{\"error\":\"unknown buyer code BC-9\"}";

        SupplierResponseClassification.Classify(400, reason).IsBusinessRejection.Should().BeTrue(
            "a 400 that carries a reason IS the supplier telling us what is wrong with the document");

        SupplierResponseClassification.FailedOrderStatusFor(400, reason)
            .Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Fact]
    public void BadRequest400_WithNoSupplierReason_IsRetryable()
    {
        // The asymmetry that makes 400 the interesting code: bare, it is indistinguishable from a
        // bad URL, a malformed header, or a proxy in front of the supplier. Calling that a business
        // rejection is the dead end in miniature — so an unexplained refusal stays somewhere the
        // operator can retry from.
        SupplierResponseClassification.Classify(400, supplierReason: "   ").IsBusinessRejection
            .Should().BeFalse();

        SupplierResponseClassification.FailedOrderStatusFor(400, null)
            .Should().Be(OrderStatusConstants.DeliveryFailed);

        SupplierResponseClassification.Classify(400, null).OperatorHint
            .Should().NotBeNullOrWhiteSpace("an unexplained refusal still owes the operator a next step");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void ServerError_IsRetryable_AndKeepsTheDispatcherMessageVerbatim(int code)
    {
        SupplierResponseClassification.Classify(code, null).IsBusinessRejection.Should().BeFalse();
        SupplierResponseClassification.Classify(code, null).OperatorHint.Should().BeNull();
        SupplierResponseClassification.DescribeFailure(code, null, "Gateway timeout")
            .Should().Be("Gateway timeout", "5xx behaviour is unchanged by this packet");
    }

    [Fact]
    public void NoResponseCode_IsRetryable_AndKeepsTheDispatcherMessageVerbatim()
    {
        // SFTP/FTPS/ERP have no status codes, and a network failure never got one.
        SupplierResponseClassification.Classify(null, null).IsBusinessRejection.Should().BeFalse();
        SupplierResponseClassification.DescribeFailure(null, null, "Connection refused")
            .Should().Be("Connection refused");
    }

    // ── The table, whole ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryFourHundredCode_IsClassified_AndOnlyTheBusinessOnesAreRejections()
    {
        for (var code = 400; code <= 499; code++)
        {
            var withoutReason = SupplierResponseClassification.Classify(code, null);
            var withReason    = SupplierResponseClassification.Classify(code, "supplier says: line 3 is not orderable");

            withoutReason.OperatorHint.Should().NotBeNullOrWhiteSpace(
                $"HTTP {code} lands in front of an operator, so it must carry a next step — " +
                "including the codes this table has no named row for");

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
            401, "token expired at 2026-07-30T09:00Z", "HTTP 401: supplier endpoint returned an error.");

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
    /// </summary>
    [Fact]
    public void RetrySuppression_AndRejectedBySupplier_AreTheSameDecision()
    {
        var codes = Enumerable.Range(400, 200).Select(c => (int?)c).Append(null).ToList();

        foreach (var code in codes)
        foreach (var reason in new[] { null, "", "   ", "the supplier explained itself" })
        {
            var status     = SupplierResponseClassification.FailedOrderStatusFor(code, reason);
            var suppressed = SupplierResponseClassification.SuppressesAutomaticRetry(code, reason);

            suppressed.Should().Be(
                status == OrderStatusConstants.RejectedBySupplier,
                $"HTTP {code?.ToString() ?? "(none)"} with reason '{reason ?? "(null)"}' must not " +
                "land in one half of the split and be treated as the other half by the retry queue");
        }
    }
}
