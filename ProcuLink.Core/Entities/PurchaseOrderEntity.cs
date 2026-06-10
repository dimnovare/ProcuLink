using System.Text.Json;

namespace ProcuLink.Core.Entities;

/// <summary>
/// EF entity for purchase_orders. The "Entity" suffix follows the persistence
/// naming convention used across the data model.
/// </summary>
public class PurchaseOrderEntity
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }
    public string PoNumber { get; set; } = string.Empty;

    /// <summary>
    /// Buyer name extracted from CanonicalJson at parse time.
    /// Null while the order is still parsing or if no buyer name was found.
    /// Denormalised from CanonicalJson for SQL-filterable search.
    /// </summary>
    public string? BuyerName { get; set; }

    public DateOnly OrderDate { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// pending_parse | parsing | pending_review | ready | transforming |
    /// ready_to_deliver | delivering | delivered | delivery_failed | failed
    /// </summary>
    public string Status { get; set; } = "pending_parse";

    /// <summary>R2 object key for the uploaded source file.</summary>
    public string? SourceFileKey { get; set; }

    /// <summary>jsonb: the canonical parsed order structure.</summary>
    public JsonDocument? CanonicalJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>True when this order was created via the sample-order onboarding path. Excluded from billing quota.</summary>
    public bool IsSample { get; set; }

    /// <summary>
    /// How many times the stuck-order sweep has re-enqueued this order after it stalled
    /// in a transient pipeline status ('parsing' / 'transforming'). A transient Worker
    /// restart mid-job leaves an order parked; the sweep requeues it up to a bounded cap
    /// (see <c>StuckOrderDetectionService</c>) before dead-lettering it as genuinely failed.
    /// Default 0; additive column <c>requeue_count</c> (migration <c>AddOrderRequeueCount</c>).
    /// </summary>
    public int RequeueCount { get; set; }

    /// <summary>
    /// Column-layout hash recorded once this order's source file has been fingerprinted
    /// (see <c>ISchemaFingerprintService</c>). Non-null means the fingerprint upsert already
    /// counted this order — the guard that makes parse-time fingerprinting idempotent across
    /// Hangfire retries. Null for orders parsed before fingerprinting or with no column headers.
    /// </summary>
    public string? SchemaFingerprintHash { get; set; }

    /// <summary>
    /// SLA timer: UTC deadline by which this order must be confirmed delivered. Set when delivery
    /// first starts (status → <c>delivering</c>), using the configured SLA window. Cleared (null)
    /// once the order is confirmed delivered. Null for orders that have not begun delivery.
    /// </summary>
    public DateTime? DeliveryDueAt { get; set; }

    /// <summary>
    /// SLA timer: set true by the SLA sweep when <see cref="DeliveryDueAt"/> elapses without a
    /// confirmed delivery. Surfaced to operators as a breached-SLA signal. Reset to false whenever
    /// a fresh delivery attempt starts (a new SLA window opens).
    /// </summary>
    public bool SlaBreached { get; set; }

    // ── Phase 4 enrichment (nullable; populated by the LLM PDF extractor) ──────
    /// <summary>Supplier/vendor name as printed on the document (distinct from the resolved Supplier).</summary>
    public string? SupplierName { get; set; }
    public decimal? SubTotal { get; set; }
    public decimal? TaxTotal { get; set; }
    public decimal? GrandTotal { get; set; }
    public string? PaymentTerms { get; set; }
    /// <summary>
    /// "purchase_order" | "invoice" | "other" — the LLM's document-type classification.
    /// An "invoice" classification flags the order for review (it arrived on the PO path
    /// with no invoice routing), so it isn't silently transformed and delivered as a PO.
    /// </summary>
    public string? DocumentType { get; set; }

    // ── V5 deepen-canonical (nullable; real persisted column) ───────────────────
    /// <summary>
    /// Requested delivery date at the header level (Peppol BIS 3.0 mandatory).
    /// UBL cbc:RequestedDeliveryDate; EDIFACT DTM+2; X12 DTM*002; IDoc E1EDK03 IDDAT=012.
    /// Real persisted column <c>requested_delivery_date</c> (migration <c>AddRequestedDeliveryDate</c>),
    /// mirroring the per-line <c>delivery_date</c> column — survives the async ingest save/reload so
    /// transform/override/Scriban paths read the parsed value back. Set at ingest from the parsed order.
    /// Null for formats that do not carry a header-level delivery date.
    /// </summary>
    public DateOnly? RequestedDeliveryDate { get; set; }

    /// <summary>
    /// Group V1: the exact <see cref="SupplierConnectionRevision"/> this order was processed with,
    /// pinned ONCE at ingest (never re-pinned). This is the reproducibility invariant — the order
    /// records which immutable connection revision drove its parse/map/transform/deliver.
    /// Null for legacy orders created before V1, or for suppliers with no active published revision
    /// (zero-config); NULL means "fall back to live config", preserving prior behaviour.
    /// Additive column <c>connection_revision_id</c> (migration <c>AddSupplierConnections</c>).
    /// </summary>
    public Guid? ConnectionRevisionId { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
    public List<PurchaseOrderLineEntity> Lines { get; set; } = new();
    public List<OutboundArtifact> OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = new();
}
