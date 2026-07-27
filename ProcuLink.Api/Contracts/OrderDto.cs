namespace ProcuLink.Api.Contracts;

/// <summary>Full order response including lines and artifacts.</summary>
public record OrderDto(
    Guid       Id,
    string     PoNumber,
    Guid       SupplierId,
    string     SupplierName,
    string     OrderDate,   // ISO-8601 date string (yyyy-MM-dd) — DateOnly not well-supported by all JSON clients
    string     Currency,
    string     Status,
    string?    SourceFileKey,
    DateTime   CreatedAt,
    DateTime   UpdatedAt,
    IReadOnlyList<OrderLineDto>    Lines,
    IReadOnlyList<ArtifactDto>     Artifacts,
    /// <summary>Buyer name extracted from CanonicalJson; null until parsing completes.</summary>
    string?    BuyerName = null,
    /// <summary>Human-readable error from the newest *Failed audit event; null for non-failed orders.</summary>
    string?    ErrorMessage = null,
    // ── Phase 4 enrichment (nullable; only the LLM PDF/email paths populate these) ──
    /// <summary>Order subtotal as extracted from the source document; null when not captured.</summary>
    decimal?   SubTotal = null,
    /// <summary>Total tax as extracted from the source document; null when not captured.</summary>
    decimal?   TaxTotal = null,
    /// <summary>Grand total as extracted from the source document; null when not captured.</summary>
    decimal?   GrandTotal = null,
    /// <summary>Payment terms as extracted from the source document; null when not captured.</summary>
    string?    PaymentTerms = null,
    /// <summary>Classified document type: "purchase_order" | "invoice" | "other"; null when not classified.</summary>
    string?    DocumentType = null,
    /// <summary>Supplier name AS PRINTED ON THE DOCUMENT (extracted). Distinct from the resolved
    /// <see cref="SupplierName"/> (Supplier.Name). Null when not captured.</summary>
    string?    DocumentSupplierName = null,
    // ── Buyer tax id + addresses/contact (nullable; additive — for order-review transparency) ──
    /// <summary>The buyer's VAT / tax / org number as extracted; drives the cXML From identity. Null when not captured.</summary>
    string?    BuyerTaxId = null,
    /// <summary>Ship-to address (as extracted). All nullable.</summary>
    string?    ShipToName = null,
    string?    ShipToDeliverTo = null,
    string?    ShipToStreet = null,
    string?    ShipToCity = null,
    string?    ShipToPostalCode = null,
    string?    ShipToCountry = null,
    string?    ShipToEmail = null,
    string?    ShipToPhone = null,
    /// <summary>Bill-to address (as extracted). All nullable.</summary>
    string?    BillToName = null,
    string?    BillToDeliverTo = null,
    string?    BillToStreet = null,
    string?    BillToCity = null,
    string?    BillToPostalCode = null,
    string?    BillToCountry = null,
    string?    BillToEmail = null,
    string?    BillToPhone = null,
    /// <summary>Ordering contact (as extracted). All nullable.</summary>
    string?    ContactName = null,
    string?    ContactEmail = null,
    string?    ContactPhone = null
);

/// <summary>Single purchase order line in the API response.</summary>
public record OrderLineDto(
    Guid     Id,
    int      LineNumber,
    string   BuyerItemCode,
    string?  SupplierItemCode,
    string?  Description,
    decimal  Quantity,
    string?  Unit,
    decimal  UnitPrice,
    float    Confidence,
    bool     NeedsReview,
    AiMappingSuggestionDto? AiSuggestion,
    // ── Phase 4 per-line enrichment (nullable; only the LLM PDF/email paths populate these) ──
    /// <summary>Line amount as extracted from the source document; null when not captured.</summary>
    decimal? LineAmount = null,
    /// <summary>Per-line tax rate (percent) as extracted; null when not captured.</summary>
    decimal? TaxRate = null,
    /// <summary>Per-line tax amount (currency) as extracted; null when not captured.</summary>
    decimal? TaxAmount = null,
    /// <summary>Per-line delivery date as ISO yyyy-MM-dd; null when not captured.</summary>
    string?  DeliveryDate = null,
    /// <summary>Short human-readable explanation of why the line was flagged for review
    /// (unresolved code / numeric ambiguity / AI anti-hallucination / scanned-PDF vision).
    /// Null for never-flagged lines and rows created before the column existed.</summary>
    string?  ReviewReason = null,
    /// <summary>The manufacturer's own part number as the source document states it
    /// (cXML <c>&lt;ManufacturerPartID&gt;</c>). For a punchout order this is the only identifier
    /// that means anything outside the buying network, so the review UI must be able to show it
    /// next to the buyer's item code. Null for sources that carry none.</summary>
    string?  ManufacturerPartNumber = null,
    /// <summary>The manufacturer / brand the source names (e.g. "REDACTED-PARTY"). Advisory context
    /// so the reviewer can check a manufacturer-part match at a glance. Null when not stated.</summary>
    string?  ManufacturerName = null
);

/// <summary>
/// An AI mapping suggestion as surfaced to the resolve UI.
///
/// <para><see cref="Confidence"/> is the RAW model confidence — it is never mutated by the
/// calibration layer (the durable decision history must keep recording the raw value so the
/// empirical curve stays sound). The three <c>Calibrated*</c> fields are a V9 DISPLAY-ONLY
/// overlay derived from this org's past accept/reject history:</para>
/// <list type="bullet">
///   <item><see cref="CalibratedConfidence"/> — the empirically-adjusted confidence. Equals
///   <see cref="Confidence"/> exactly when <see cref="IsCalibrated"/> is false.</item>
///   <item><see cref="IsCalibrated"/> — true only when a sufficiently-sampled empirical rate
///   backed the number; false during cold-start / thin buckets / when the AI layer is off.</item>
///   <item><see cref="CalibrationBasis"/> — a truthful explanation: "calibrated from N of your
///   past decisions" vs "model estimate — not enough history yet".</item>
/// </list>
/// </summary>
public record AiMappingSuggestionDto(
    string SupplierItemCode,
    float Confidence,
    string Reason,
    string Provenance,
    // ── V9 confidence calibration (display-only; never alters the raw Confidence above) ──
    float CalibratedConfidence,
    bool IsCalibrated,
    string CalibrationBasis
);

/// <summary>Outbound artifact reference in the order response.</summary>
public record ArtifactDto(
    Guid     Id,
    string   Format,
    string   FileKey,
    DateTime CreatedAt
);
