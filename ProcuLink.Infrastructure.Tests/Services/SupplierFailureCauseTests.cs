using FluentAssertions;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-19 recovery — the MACHINE-READABLE half of the classification.
///
/// <para><b>The defect.</b> <see cref="SupplierResponseClassification"/> already knew the difference
/// between an expired API key, a moved endpoint and a rate limit, and already said so in excellent
/// prose. But the prose was the ONLY thing that crossed the API boundary: the DTOs carried
/// <c>ErrorMessage</c> and nothing else, so a client that wanted to offer the cause-specific control
/// ("update the credentials" vs "correct the address" vs "wait it out") had no option but to regex
/// <c>"(HTTP 401)"</c> out of an English sentence. That is a mirror of the table written in another
/// language, in another repository, and it drifts the first time the copy is reworded.</para>
///
/// <para><b>The rule these tests pin.</b> The slug is a THIRD output of the same branch that already
/// picks the kind and the hint — never a parallel switch. A parallel switch is how the three
/// hand-written <c>responseCode is &gt;= 400 and &lt;= 499</c> literals drifted in the first
/// place.</para>
/// </summary>
public class SupplierFailureCauseTests
{
    /// <summary>A channel that reads the counterparty's body — http, erp_*, email.</summary>
    private const bool Observable = true;

    /// <summary>A channel that cannot: a blank reason there is absence of capture, not evidence.</summary>
    private const bool NotObservable = false;

    /// <summary>
    /// One row per refusal shape the product can be in. Read by BOTH the exact-slug theory and the
    /// coverage guard below, so a named prose row that nobody gave a slug is a FAILING case rather
    /// than a silently uncovered one.
    /// </summary>
    public static TheoryData<int?, string?, bool, string> EveryRefusalShape() => new()
    {
        // The named rows — each cause is separately actionable, which is the whole point of a slug:
        // "update the credentials", "ask to be allow-listed", "fix the address", "wait".
        { 401,  null,                       Observable,    SupplierFailureCause.AuthRejected },
        { 403,  null,                       Observable,    SupplierFailureCause.PermissionDenied },
        { 404,  null,                       Observable,    SupplierFailureCause.EndpointNotFound },
        { 408,  null,                       Observable,    SupplierFailureCause.Timeout },
        { 429,  null,                       Observable,    SupplierFailureCause.RateLimited },

        // The supplier read the document and refused it — all three routes to that same verdict.
        { 422,  null,                       Observable,    SupplierFailureCause.BusinessRejection },
        { 400,  "unknown buyer code BC-9",  Observable,    SupplierFailureCause.BusinessRejection },
        { 400,  null,                       NotObservable, SupplierFailureCause.BusinessRejection },

        // …and the one that is NOT: a 400 a channel that CAN read the body really did receive bare.
        // It gets its own slug because the recovery is different — nothing in the document is known
        // to be wrong, so the control to offer is "check the endpoint", not "correct the order".
        { 400,  null,                       Observable,    SupplierFailureCause.RefusedWithoutReason },

        // Any other 4xx: refused, cause unnamed.
        { 451,  null,                       Observable,    SupplierFailureCause.RefusedOther },

        // The supplier's system, not the supplier: a 5xx, and a failure that never got a code at all
        // (network fault, or a channel with no status codes — SFTP/FTPS).
        { 500,  null,                       Observable,    SupplierFailureCause.Unreachable },
        { null, null,                       Observable,    SupplierFailureCause.Unreachable },
    };

    [Theory]
    [MemberData(nameof(EveryRefusalShape))]
    public void EveryRefusalShape_ClassifiesToItsExactSlug(
        int? code, string? body, bool observable, string expected)
    {
        SupplierResponseClassification.Classify(code, body, observable).Cause
            .Should().Be(expected,
                $"HTTP {code?.ToString() ?? "(none)"} (body={body ?? "(null)"}, observable={observable}) " +
                "is what a client keys its recovery control off — a slug that shifts is a control " +
                "pointed at the wrong fix");
    }

    /// <summary>
    /// The coverage guard. Adding a row to the prose table without adding a case above leaves a
    /// named cause whose slug nothing asserts — which is exactly how a shipped mirror goes stale.
    /// Enumerated from <see cref="SupplierResponseClassification.NamedCauseRows"/> rather than a
    /// hand-copied list, so the table itself is what has to be satisfied.
    /// </summary>
    [Fact]
    public void EveryNamedProseRow_HasACaseInTheSlugTable()
    {
        var covered = EveryRefusalShape()
            .Select(row => (int?)row[0])
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToHashSet();

        SupplierResponseClassification.NamedCauseRows.Keys.Should().BeSubsetOf(covered,
            "a status code the table names in prose but the slug theory never exercises is a cause " +
            "the API may be emitting with no test standing behind it");
    }

    /// <summary>
    /// Every named prose row maps to a DISTINCT, non-null slug.
    ///
    /// <para>Distinctness is the load-bearing half. The prose already distinguishes 401 from 403
    /// from 404; if two of them collapsed onto one slug, a client could no longer tell "your key
    /// expired" from "you are not allow-listed" — and it would look correct in every test that only
    /// asserted the slug is non-empty.</para>
    /// </summary>
    [Fact]
    public void EveryNamedProseRow_MapsToADistinctNonNullCause()
    {
        var causes = SupplierResponseClassification.NamedCauseRows.Keys
            .ToDictionary(code => code, code => SupplierResponseClassification.Classify(
                code, supplierReason: null, supplierReasonObservable: Observable).Cause);

        causes.Values.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c),
            "a prose row with no slug is a cause the API cannot name to a client");

        causes.Values.Should().OnlyHaveUniqueItems(
            "each named row is a SEPARATE recovery — collapsing two onto one slug points the " +
            "operator at the wrong fix while every 'is it non-empty' assertion stays green");

        causes.Values.Should().NotContain(SupplierFailureCause.RefusedOther,
            "'we cannot name this one' is the fallback for codes the table has NO row for — a named " +
            "row landing there means the branch that picks the hint and the branch that picks the " +
            "slug have come apart");
    }

    /// <summary>
    /// The read side. A stored attempt row carries <c>ResponseCode</c>, <c>RejectionReason</c> and
    /// <c>ResponseBody</c> but NOT the dispatcher's observability flag — so a projection cannot call
    /// <see cref="SupplierResponseClassification.Classify(int?, string?, bool)"/> directly. The
    /// helper recovers the flag from what WAS persisted, and must land on the same slug the live
    /// classification did, or the history screen and the live screen disagree about the same attempt.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRefusalShape))]
    public void TheStoredColumns_DeriveTheSameSlugTheLiveClassificationDid(
        int? code, string? body, bool observable, string expected)
    {
        // What DeliveryService.PersistAttemptAsync writes for this shape: RejectionReason is stamped
        // only for a business rejection (and is the dispatcher's message, not the body).
        var rejectionReason = SupplierResponseClassification.Classify(code, body, observable).IsBusinessRejection
            ? "the dispatcher's message"
            : null;

        SupplierResponseClassification.CauseFor(code, rejectionReason, body)
            .Should().Be(expected,
                "the attempt-history projection and the live verdict are the same statement about " +
                "the same response — deriving them separately is the mirror this packet exists to " +
                "remove");
    }
}
