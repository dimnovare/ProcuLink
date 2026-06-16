using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Shared, format-aware required-field validation for the outbound transforms.
///
/// <para>
/// A pipeline analysis predicted that a line with an empty buyer item code or a
/// missing / zero unit price can produce a <em>structurally invalid</em> document
/// (e.g. an EDIFACT <c>LIN</c> whose mandatory item-number component is empty) or
/// a financially-corrupt <c>€0</c> document that delivers blind. Rather than let
/// the serializers emit those silently, every transform runs the canonical order
/// through one of the two entry points below <em>before</em> writing a byte:
/// </para>
/// <list type="bullet">
///   <item><see cref="ValidateParsedOrder"/> for the <see cref="IParsedOrderTransform"/>
///   family (canonical <see cref="ParsedOrder"/>, buyer-side item code, no review state).</item>
///   <item><see cref="ValidateEntity"/> for the <see cref="ITransformService"/> family
///   (a resolved <see cref="PurchaseOrderEntity"/> with a <c>SupplierItemCode</c> and
///   <c>NeedsReview</c> flag).</item>
/// </list>
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
    /// Validates a canonical <see cref="ParsedOrder"/> against the required-field
    /// rules of <paramref name="format"/>. Throws
    /// <see cref="TransformValidationException"/> if any line is invalid for that
    /// format. No-op when every line satisfies the format's mandatory fields.
    /// </summary>
    public static void ValidateParsedOrder(ParsedOrder order, OutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(order);

        var requiresItemCode = FormatRequiresLineItemCode(format);
        var problems = new List<LineProblem>();

        foreach (var line in order.Lines)
        {
            // Required-by-format line item code (EDIFACT LIN03 / X12 PO1 BP qualifier).
            // An empty value here makes the document STRUCTURALLY invalid for those
            // formats — a hard, mandatory-segment failure.
            if (requiresItemCode && string.IsNullOrWhiteSpace(line.BuyerItemCode))
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingItemCode,
                    $"Line {line.LineNumber}: a buyer item code is mandatory for {format} " +
                    "(empty produces a structurally-invalid document)."));

            // Missing / non-positive unit price. Not structurally fatal, but a 0
            // line delivers a financially-wrong document — flag it for review.
            if (line.UnitPrice is null or <= 0m)
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingOrZeroPrice,
                    $"Line {line.LineNumber}: unit price is missing or not positive " +
                    $"({Describe(line.UnitPrice)}); held for review to avoid a zero-value document."));
        }

        Throw(problems);
    }

    /// <summary>
    /// Validates a resolved <see cref="PurchaseOrderEntity"/>. Always enforces the
    /// existing review guard (<c>NeedsReview</c> / null <c>SupplierItemCode</c>),
    /// then layers the format-specific required-field checks
    /// (empty buyer item code where the format mandates it, missing / zero unit price).
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

            // Missing / non-positive unit price → financially-wrong zero-value document.
            if (line.UnitPrice <= 0m)
                problems.Add(new LineProblem(line.LineNumber, LineProblemKind.MissingOrZeroPrice,
                    $"Line {line.LineNumber}: unit price is not positive ({line.UnitPrice}); " +
                    "held for review to avoid a zero-value document."));
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

    private static string Describe(decimal? price) =>
        price is null ? "null" : price.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The category of a per-line output-validation problem.</summary>
public enum LineProblemKind
{
    NeedsReview,
    MissingSupplierItemCode,
    MissingItemCode,
    MissingOrZeroPrice,
    Sanitized,
}

/// <summary>A single per-line validation / sanitization finding.</summary>
public sealed record LineProblem(int LineNumber, LineProblemKind Kind, string Message);
