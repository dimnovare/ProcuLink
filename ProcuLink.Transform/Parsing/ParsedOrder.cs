namespace ProcuLink.Transform.Parsing;

/// <summary>
/// The full output of parsing a purchase order file.
/// Header fields may be null when the file format does not include them.
/// </summary>
public record ParsedOrder(
    string? PoNumber,
    DateTime? OrderDate,
    string? BuyerName,
    string? Currency,
    IReadOnlyList<ParsedOrderLine> Lines,
    // Phase 4 enrichment + doc-type classification (additive, defaulted).
    string? SupplierName = null,
    // The BUYER's VAT / org number. Additive, defaulted. Feeds the cXML From/Identity so a
    // different buyer never emits the configured From credential's VatNr.
    string? BuyerTaxId = null,
    decimal? SubTotal = null,
    decimal? TaxTotal = null,
    decimal? GrandTotal = null,
    string? PaymentTerms = null,
    string? DocumentType = null,
    // V5 deepen-canonical: requested delivery date (header-level).
    // Peppol BIS 3.0 mandatory; UBL cbc:RequestedDeliveryDate; EDIFACT DTM+2;
    // X12 DTM*002; IDoc E1EDK03 IDDAT=012. Null when the format does not carry it.
    DateOnly? RequestedDeliveryDate = null,
    // Phase 1 lossless capture (additive, defaulted).
    IReadOnlyList<ParsedParty>? Parties = null,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Incoterms = null,
    string? ShippingMethod = null,
    string? BuyerOrderRef = null,
    IReadOnlyList<ParsedRawField>? RawFields = null,
    // Order-level parser review flag: true when a HEADER field could not be read
    // unambiguously and the parser resolved it by policy rather than from the data
    // (today: a numeric date whose day/month ordering was a genuine ≤12/≤12 collision,
    // e.g. "03/04/2026"). The value IS emitted — day-first is the documented product
    // default — but it is emitted WITH this flag so a human confirms it instead of the
    // guess shipping silently.
    //
    // Header fields have no per-line home, and the ingestion layer's order-level review
    // gate is `Lines.Any(l => l.NeedsReview)` — which is FALSE over an empty line set
    // (OrderStatusMachine.cs:223). So this needs its own term in the status decision;
    // it is not redundant with the line flags.
    //
    // Appended LAST + defaulted so every existing positional construction of this record
    // keeps its current meaning.
    bool NeedsReview = false,
    // Short human-readable "why was this flagged" for the review UI; set alongside
    // NeedsReview = true, null otherwise.
    string? ReviewReason = null
);
