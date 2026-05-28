namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// A single order line as extracted from an email body.
/// Mirrors the essential fields of <c>ParsedOrderLine</c> in ProcuLink.Transform
/// without creating an assembly dependency between Core and Transform.
/// </summary>
public sealed record ExtractedOrderLine(
    int LineNumber,
    string BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal? UnitPrice
);

/// <summary>
/// A purchase order as extracted from an email body.
/// Mirrors the essential fields of <c>ParsedOrder</c> in ProcuLink.Transform
/// without creating an assembly dependency between Core and Transform.
/// </summary>
public sealed record ExtractedOrder(
    string? PoNumber,
    DateTime? OrderDate,
    string? BuyerName,
    string? Currency,
    IReadOnlyList<ExtractedOrderLine> Lines
);

/// <summary>
/// Result of attempting to extract a purchase order from a plain-text email body.
/// </summary>
/// <param name="Success">
/// True when a purchase order was extracted with sufficient confidence
/// (Confidence ≥ 0.6). False on all failure paths.
/// </param>
/// <param name="Confidence">
/// Model-reported extraction confidence in the range 0.0–1.0.
/// Always 0.0 when Success is false.
/// </param>
/// <param name="Order">
/// The extracted purchase order. Null when Success is false.
/// </param>
/// <param name="FailureReason">
/// Human-readable explanation of why extraction failed.
/// Null when Success is true.
/// </param>
public record EmailBodyExtractionResult(
    bool Success,
    double Confidence,
    ExtractedOrder? Order,
    string? FailureReason
);

/// <summary>
/// Attempts to extract a purchase order from a plain-text email body using
/// AI NLP. Callers should treat the result as a draft — the user must review
/// and confirm the extracted lines before they enter the normal PO workflow.
///
/// Implementations are scoped per-request — they read the current tenant from
/// <see cref="ICurrentTenantService"/> (or equivalent test double) to enforce
/// the per-org monthly token cap via <see cref="IAiUsageTracker"/>.
///
/// All failure paths (no API key, wrong provider, over cap, low confidence)
/// return <c>Success = false</c> without throwing.
/// </summary>
public interface IEmailBodyOrderExtractor
{
    /// <summary>
    /// Attempts to extract a purchase order from a plain-text email body.
    /// Returns Success=false when confidence &lt; 0.6 or when no API key is configured.
    /// </summary>
    Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct);
}
