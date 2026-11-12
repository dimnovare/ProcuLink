namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// A single order line as extracted from a document body.
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
    // Phase 4 enrichment (optional ā€” additive, defaulted so existing callers are unaffected).
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
/// A purchase order as extracted from a document body.
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
    // Mirrors ParsedOrder.RequestedDeliveryDate ā€” null when absent.
    DateOnly? RequestedDeliveryDate = null,
    // Phase 1 lossless capture (additive, defaulted).
    IReadOnlyList<ExtractedParty>? Parties = null,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Incoterms = null,
    string? ShippingMethod = null,
    string? BuyerOrderRef = null,
    IReadOnlyList<ExtractedRawField>? RawFields = null,
    // Invoice-only (empty on orders): payment due date, seller VAT id, IBAN, payment reference.
    DateOnly? DueDate = null,
    string? SellerTaxId = null,
    string? Iban = null,
    string? PaymentReference = null
);
