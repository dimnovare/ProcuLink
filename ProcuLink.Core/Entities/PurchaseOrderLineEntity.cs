namespace ProcuLink.Core.Entities;

/// <summary>
/// EF entity for purchase_order_lines.
/// </summary>
public class PurchaseOrderLineEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int LineNumber { get; set; }
    public string BuyerItemCode { get; set; } = string.Empty;
    public string? SupplierItemCode { get; set; }
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>
    /// The model score behind this line's supplier code, 0..1 — or <c>null</c> when no model
    /// scored it, which is the case for every deterministically resolved and every hand-resolved
    /// line.
    /// </summary>
    /// <remarks>
    /// This was a non-nullable float carrying a three-valued <b>state flag</b>:
    /// <c>resolved ? (parserFlagged ? 0.5f : 1.0f) : 0.0f</c> at ingestion, and a flat
    /// <c>1.0f</c> on human resolve. The order passport rendered it straight onto the confidence
    /// ramp, so a resolved line printed a green <b>100%</b>, a parser-flagged one a red
    /// <b>50%</b>, and an unresolved one a red <b>0%</b> — none of which any model produced.
    /// <para>Worse, the bulk-accept path wrote the model's REAL <see cref="AiSuggestionConfidence"/>
    /// into this same column, so one field held both a state flag and a measurement with nothing
    /// to tell them apart — and it could not be untangled at the read boundary, because accepting
    /// a suggestion also clears <see cref="AiSuggestedSupplierItemCode"/>, leaving a promoted
    /// 1.0 measurement byte-identical to a 1.0 flag.</para>
    /// <para>Resolution state lives in <see cref="NeedsReview"/> and <see cref="ReviewReason"/>,
    /// where it always did. This column now holds a measurement or nothing.</para>
    /// </remarks>
    public float? Confidence { get; set; }
    public bool NeedsReview { get; set; }

    /// <summary>
    /// Short human-readable explanation of WHY this line was flagged for review,
    /// written at parse time where the flag originates (unresolved supplier code,
    /// parser numeric ambiguity, AI anti-hallucination mismatch, scanned-PDF vision
    /// extraction). Null for never-flagged lines and for rows created before the
    /// column existed. Cleared when a human resolves the line.
    /// </summary>
    public string? ReviewReason { get; set; }
    public string? AiSuggestedSupplierItemCode { get; set; }
    public float? AiSuggestionConfidence { get; set; }
    public string? AiSuggestionReason { get; set; }
    public string? AiSuggestionProvenance { get; set; }

    // ── Phase 4 enrichment (nullable; populated by the LLM PDF extractor) ──
    /// <summary>Printed extended line total (quantity × unit price), when stated.</summary>
    public decimal? LineAmount { get; set; }
    public decimal? TaxRate { get; set; }

    /// <summary>
    /// Per-line VAT amount as printed (distinct from <see cref="TaxRate"/>). Nullable; populated by
    /// the LLM PDF/email extractor. Emitted in the cXML <c>ItemDetail/Tax/Money</c> when present.
    /// Migration <c>AddBuyerTaxIdAndLineTax</c>.
    /// </summary>
    public decimal? TaxAmount { get; set; }
    public DateOnly? DeliveryDate { get; set; }

    // ── Phase 1 lossless capture (nullable). ManufacturerPartNumber = the catalog key (Phase 2). ──
    public string? ManufacturerPartNumber { get; set; }

    /// <summary>
    /// The manufacturer / brand as the source document states it (cXML
    /// <c>&lt;ManufacturerName&gt;</c>, e.g. "Litware"). Captured so the review UI can show
    /// "Litware LTQ2500-BK-BTK1" rather than a bare part number, and so a manufacturer-part
    /// match can be sanity-checked at a glance. Advisory only — never part of the match predicate.
    /// </summary>
    public string? ManufacturerName { get; set; }

    public string? CustomerPartNumber { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string? Unspsc { get; set; }
    public string? Recipient { get; set; }
    public string? ContractNumber { get; set; }
    public decimal? NetAmount { get; set; }

    // Navigation
    public PurchaseOrderEntity Order { get; set; } = null!;
}
