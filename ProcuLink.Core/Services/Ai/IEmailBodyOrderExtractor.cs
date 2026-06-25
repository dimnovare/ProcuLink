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
    decimal? UnitPrice,
    // Phase 4 enrichment (optional — additive, defaulted so existing callers are unaffected).
    decimal? LineAmount = null,
    decimal? TaxRate = null,
    // Per-line VAT amount as printed (e.g. 25% VAT on an ex-VAT unit price). Additive, defaulted.
    // Captured so the VAT-aware reconcile can tell a correctly-VAT'd line from a genuine mismatch.
    decimal? TaxAmount = null,
    DateOnly? DeliveryDate = null,
    string? ManufacturerPartNumber = null,
    string? CustomerPartNumber = null,
    decimal? DiscountPercent = null,
    string? Unspsc = null,
    string? Recipient = null,
    string? ContractNumber = null,
    decimal? NetAmount = null
);

/// <summary>Phase 1: a named party (address/role) on an LLM-extracted order. Mirrors
/// ParsedParty in the Transform layer (Core cannot reference Transform).</summary>
public sealed record ExtractedParty(
    string Role,
    string? Name = null,
    string? Street = null,
    string? City = null,
    string? PostalCode = null,
    string? Country = null,
    string? Vat = null,
    string? RegNr = null,
    string? EdiCode = null,
    string? Reference = null,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null);

/// <summary>Phase 1: a labelled value with no canonical slot (raw bag entry).</summary>
public sealed record ExtractedRawField(string Label, string Value);

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
    IReadOnlyList<ExtractedOrderLine> Lines,
    // Phase 4 enrichment + doc-type classification (additive, defaulted).
    string? SupplierName = null,
    // The BUYER's VAT / org number (e.g. a Norwegian org no or an EU VAT). Additive, defaulted.
    // Used as the cXML From/Identity so a different buyer never inherits the configured From's VatNr.
    string? BuyerTaxId = null,
    decimal? SubTotal = null,
    decimal? TaxTotal = null,
    decimal? GrandTotal = null,
    string? PaymentTerms = null,
    string? DocumentType = null,
    // V5 deepen-canonical: requested delivery date (header-level).
    // Mirrors ParsedOrder.RequestedDeliveryDate — null when absent.
    DateOnly? RequestedDeliveryDate = null,
    // Phase 1 lossless capture (additive, defaulted).
    IReadOnlyList<ExtractedParty>? Parties = null,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Incoterms = null,
    string? ShippingMethod = null,
    string? BuyerOrderRef = null,
    IReadOnlyList<ExtractedRawField>? RawFields = null
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
