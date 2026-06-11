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
    public float Confidence { get; set; }
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
    public DateOnly? DeliveryDate { get; set; }

    // Navigation
    public PurchaseOrderEntity Order { get; set; } = null!;
}
