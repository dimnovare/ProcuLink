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
    IReadOnlyList<ParsedRawField>? RawFields = null
);
