using System.Text.Json;

namespace ProcuLink.Core.Entities;

/// <summary>
/// Phase 1 lossless raw bag: every field/token we saw on the inbound document that the
/// canonical model did not promote to a typed column. ONE row per order. For structured
/// formats (CSV/XLSX/XML) <see cref="TokensJson"/> holds the full <c>SourceToken</c> set;
/// for the LLM PDF/email path it holds the extractor's <c>raw_fields</c>. Immutable after
/// insert and revision-pinnable (Phase 4). Table <c>source_captures</c> (migration
/// <c>AddLosslessCanonicalCapture</c>). Kept deliberately OUT of <c>purchase_orders.canonical_json</c>
/// (already triple-overloaded) so the spine row stays lean.
/// </summary>
public class SourceCapture
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrgId { get; set; }
    /// <summary>Detected source format, e.g. "csv" | "xlsx" | "xml" | "pdf" | "email".</summary>
    public string Format { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
    /// <summary>jsonb: full token set or raw_fields, keyed by token id / label.</summary>
    public JsonDocument? TokensJson { get; set; }
    /// <summary>Optional extracted plain text (PDF/email), for audit/replay.</summary>
    public string? RawText { get; set; }
    /// <summary>Optional page/segment references, free-form.</summary>
    public string? PageRefs { get; set; }

    public PurchaseOrderEntity Order { get; set; } = null!;
}
