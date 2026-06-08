namespace ProcuLink.Transform.Parsing;

/// <summary>
/// A single line from a parsed purchase order file, before mapping or validation.
/// SupplierItemCode is intentionally absent — it is resolved from the mappings table,
/// not trusted from the upload file.
/// </summary>
public record ParsedOrderLine(
    int LineNumber,
    string BuyerItemCode,
    string? Description,
    decimal Quantity,
    string? Unit,
    decimal? UnitPrice,
    // Phase 4 enrichment (optional — additive, defaulted so existing parsers are unaffected).
    decimal? LineAmount = null,
    decimal? TaxRate = null,
    DateOnly? DeliveryDate = null,
    // Parser-level review flag: true when a numeric token (quantity / unit price) was
    // ambiguous or unparseable and the parser refused to emit a silently-wrong value
    // (e.g. scientific notation "1.5e2"). The ingestion layer ORs this into the line
    // entity's NeedsReview so the line surfaces in /operations/exceptions instead of
    // being delivered with a corrupted number. Additive + defaulted: other parsers
    // that always produce clean numbers leave it false.
    bool NeedsReview = false
);
