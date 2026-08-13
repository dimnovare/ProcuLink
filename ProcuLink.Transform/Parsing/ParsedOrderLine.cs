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
    // Per-line VAT amount as printed. Additive, defaulted. Carried so the VAT-aware reconcile
    // and the cXML <ItemDetail>/<Tax> emit have a faithful per-line tax value.
    decimal? TaxAmount = null,
    DateOnly? DeliveryDate = null,
    // Parser-level review flag: true when a numeric token (quantity / unit price) was
    // ambiguous or unparseable and the parser refused to emit a silently-wrong value
    // (e.g. scientific notation "1.5e2"). The ingestion layer ORs this into the line
    // entity's NeedsReview so the line surfaces in /operations/exceptions instead of
    // being delivered with a corrupted number. Additive + defaulted: other parsers
    // that always produce clean numbers leave it false.
    bool NeedsReview = false,
    // P2 hardening: short human-readable explanation of WHY the parser flagged this
    // line (set alongside NeedsReview = true; null otherwise). Carried onto the
    // persisted line entity's review_reason so the review UI can say "why flagged".
    // Additive + defaulted: parsers that never flag leave it null.
    string? ReviewReason = null,
    // Phase 1 lossless capture (additive, defaulted). ManufacturerPartNumber = the catalog key (Phase 2).
    string? ManufacturerPartNumber = null,
    string? CustomerPartNumber = null,
    decimal? DiscountPercent = null,
    string? Unspsc = null,
    string? Recipient = null,
    string? ContractNumber = null,
    decimal? NetAmount = null,
    // The manufacturer / brand the source names alongside the part number (cXML
    // <ManufacturerName>, e.g. "Litware"). Advisory context for the operator and for AI
    // grounding — never a match predicate, because feeds spell one brand many ways
    // ("Litware" / "LITWARE ADC" / "Litware S.p.A."). Appended LAST so every existing
    // positional construction of this record keeps its current meaning.
    string? ManufacturerName = null
);
