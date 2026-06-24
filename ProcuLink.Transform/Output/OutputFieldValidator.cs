using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Shared, format-aware required-field validation for the outbound transforms.
///
/// <para>
/// A pipeline analysis predicted that a line with an empty buyer item code or a
/// missing / zero unit price can produce a <em>structurally invalid</em> document
/// (e.g. an EDIFACT <c>LIN</c> whose mandatory item-number component is empty) or
/// a financially-corrupt <c>€0</c> document that delivers blind. Rather than let
/// the serializers emit those silently, every transform runs the resolved order
/// through <see cref="ValidateEntity"/> (the <see cref="ITransformService"/> family —
/// a resolved <see cref="PurchaseOrderEntity"/> with a <c>SupplierItemCode</c> and
/// <c>NeedsReview</c> flag) <em>before</em> writing a byte.
/// </para>
///
/// <para>
/// Problems are surfaced by throwing <see cref="TransformValidationException"/>,
/// whose <see cref="TransformValidationException.UnresolvedLineNumbers"/> the
/// controller already maps to a review warning / 422 — so flagged lines land in
/// <c>/operations/exceptions</c> instead of being delivered. The exception also
/// carries the per-line <see cref="TransformValidationException.Problems"/> so the
/// operator sees <em>why</em> a line was held.
/// </para>
///
/// <para>
/// This is additive: a fully-resolved, well-formed order (non-empty codes,
/// positive prices, no delimiter contamination) produces no problems and the
/// emitted bytes are byte-for-byte identical to before this guard existed.
/// </para>
/// </summary>
public static class OutputFieldValidator
{
    /// <summary>
    /// Validates a resolved <see cref="PurchaseOrderEntity"/>. Always enforces the
    /// existing review guard (<c>NeedsReview</c> / null <c>SupplierItemCode</c>),
    /// then layers the format-specific required-field checks
    /// (empty buyer item code where the format mandates it, missing / zero unit price,
    /// zero / negative quantity).
    /// Throws <see cref="TransformValidationException"/> when anything is invalid.
    /// </summary>
    public static void ValidateEntity(PurchaseOrderEntity order, OutputFormat format) =>
        Throw(CollectEntityProblems(order, format));

    /// <summary>
    /// The SAME per-line output checks as <see cref="ValidateEntity"/>, but RETURNS the problems
    /// instead of throwing — so the validation surface (POST /orders/{id}/validate) can show output
    /// errors (zero/negative price, format-mandatory buyer code) in the plain-language issue list
    /// BEFORE a transform is attempted, instead of only as a transform-time exception. Pure: no I/O,
    /// no throw, no transform.
    /// </summary>
    public static IReadOnlyList<LineProblem> CollectEntityProblems(PurchaseOrderEntity order, OutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(order);

        var requiresItemCode = FormatRequiresLineItemCode(format);
        var problems = new List<LineProblem>();

        foreach (var line in order.Lines)
        {
            // Existing contract: an unresolved line must never serialize.
            if (line.NeedsReview)
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.NeedsReview,
                    $"Line {line.LineNumber}: still needs review."));

            if (string.IsNullOrWhiteSpace(line.SupplierItemCode))
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingSupplierItemCode,
                    $"Line {line.LineNumber}: supplier item code is unresolved."));

            // Format-mandatory buyer item code (X12 emits it as the BP qualifier).
            if (requiresItemCode && string.IsNullOrWhiteSpace(line.BuyerItemCode))
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingItemCode,
                    $"Line {line.LineNumber}: a buyer item code is mandatory for {format} " +
                    "(empty produces a structurally-invalid document)."));

            // Negative unit price → a financially-impossible line; still a hard hold.
            // A €0 price is NOT held here (founder-approved): legitimately-free lines must
            // deliver. The non-blocking €0 warning is surfaced by InvariantValidator
            // ("invariant.unit_price_valid", severity "warning") on the validation surface,
            // so the coordinator still SEES the zero without it blocking transform/delivery.
            if (line.UnitPrice < 0m)
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingOrZeroPrice,
                    $"Line {line.LineNumber}: unit price is negative ({line.UnitPrice}); " +
                    "held for review to avoid a negative-value document."));

            // Zero / negative quantity → a delivered line ordering nothing (or a negative
            // amount) is almost always a parse/mapping error, not an intentional order. Flag it
            // for review with the same severity as the price check rather than emitting it blind.
            if (line.Quantity <= 0m)
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingOrZeroQuantity,
                    $"Line {line.LineNumber}: quantity is not positive ({line.Quantity}); " +
                    "held for review to avoid ordering a zero / negative quantity."));
        }

        return problems;
    }

    /// <summary>
    /// Formats whose line item code is a mandatory data element such that an empty
    /// value yields a structurally-invalid document. UBL / cXML carry the code in an
    /// optional identification element, so a missing code is not a hard structural
    /// failure for them (the review / supplier-item-code guard still covers it).
    /// </summary>
    private static bool FormatRequiresLineItemCode(OutputFormat format) => format switch
    {
        OutputFormat.EdifactOrders => true, // LIN C212/7140 item number — mandatory
        OutputFormat.X12_850       => true, // PO1 BP qualifier + value
        OutputFormat.X12           => true, // entity-based X12 PO1 BP qualifier + value
        _ => false,
    };

    private static void Throw(IReadOnlyList<LineProblem> problems)
    {
        if (problems.Count == 0) return;

        var lineNumbers = problems
            .Select(p => p.LineNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        throw new TransformValidationException(lineNumbers, problems);
    }
}

/// <summary>The category of a per-line output-validation problem.</summary>
public enum LineProblemKind
{
    NeedsReview,
    MissingSupplierItemCode,
    MissingItemCode,
    MissingOrZeroPrice,
    MissingOrZeroQuantity,
    Sanitized,
}

/// <summary>A single per-line validation / sanitization finding.</summary>
public sealed record LineProblem(int LineNumber, LineProblemKind Kind, string Message);
