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

    /// <summary>
    /// The resolved supplier this order is for. NULLABLE since the supplier-routing track (Phase 0):
    /// an order can be ingested on a shared channel BEFORE its supplier is known, in which case it is
    /// parked <see cref="OrderStatusConstants.Unrouted"/> with a null supplier until one is assigned.
    /// Non-null for every order on the existing upload / channel-bound paths (unchanged). The FK is
    /// <c>ON DELETE SET NULL</c> (migration <c>MakeOrderSupplierNullable</c>).
    /// </summary>
    public Guid? SupplierId { get; set; }
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
    /// DELIVERY-phase re-drive counter, SEPARATE from the parse/transform <see cref="RequeueCount"/>.
    /// <c>StuckDeliveryDetectionService</c> increments this (not the shared counter) each time it
    /// re-drives an order stranded in <c>delivering</c>, so the delivery phase gets its OWN bounded
    /// budget: an order that already spent its <see cref="RequeueCount"/> recovering a stuck
    /// parse/transform is NOT prematurely dead-lettered on its first delivery stall. Never reset (it
    /// accumulates for the order's delivery lifetime, mirroring <see cref="RequeueCount"/>).
    /// Default 0; additive column <c>delivery_requeue_count</c> (migration <c>AddDeliveryRequeueCount</c>).
    /// </summary>
    public int DeliveryRequeueCount { get; set; }

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

    /// <summary>
    /// Set (UTC) when the blob-retention sweep deleted this order's SOURCE FILE blob from
    /// R2 per the org's retention policy. The DB row, <see cref="SourceFileKey"/>, hashes,
    /// provenance and audit trail are KEPT — only the blob is gone. Null = blob still
    /// present (or there never was one). Download/preview paths must check this and return
    /// a clear "purged per retention policy" answer instead of a 500.
    /// Additive column <c>source_file_purged_at</c> (migration <c>AddBlobRetentionSweep</c>).
    /// </summary>
    public DateTime? SourceFilePurgedAt { get; set; }

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

    // ── Phase 1 lossless capture (nullable; copy the RequestedDeliveryDate column pattern) ──
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Incoterms { get; set; }
    public string? ShippingMethod { get; set; }
    public string? BuyerOrderRef { get; set; }

    /// <summary>
    /// The BUYER's tax / org identifier as printed on the document (e.g. a Norwegian org number
    /// or an EU VAT number). Nullable; populated by the LLM PDF/email extractor when present.
    /// Drives the cXML <c>From/Credential/Identity</c> so a PO from a different buyer never inherits
    /// the supplier connection's configured From VatNr (the Gjensidige/REDACTED-PARTY bug). Null → the
    /// configured / legacy From identity is used, so unconfigured orders stay byte-identical.
    /// Migration <c>AddBuyerTaxIdAndLineTax</c>.
    /// </summary>
    public string? BuyerTaxId { get; set; }

    // ── cXML address blocks (nullable; denormalised from the shipTo/billTo OrderParty rows) ──
    // These mirror the Contact*/BuyerName precedent: the address data is also persisted as
    // OrderParty child rows, but the cXML writer reads the PurchaseOrderEntity directly, so the
    // ship-to / bill-to are denormalised onto these flat columns at ingest. All nullable — a
    // document may carry only some, and an order with none stays byte-identical in the cXML output.
    // Migration AddCxmlAddressBlocks.
    public string? ShipToName { get; set; }
    public string? ShipToDeliverTo { get; set; }
    public string? ShipToStreet { get; set; }
    public string? ShipToCity { get; set; }
    public string? ShipToPostalCode { get; set; }
    public string? ShipToCountry { get; set; }
    public string? ShipToEmail { get; set; }
    public string? ShipToPhone { get; set; }
    public string? BillToName { get; set; }
    public string? BillToDeliverTo { get; set; }
    public string? BillToStreet { get; set; }
    public string? BillToCity { get; set; }
    public string? BillToPostalCode { get; set; }
    public string? BillToCountry { get; set; }
    public string? BillToEmail { get; set; }
    public string? BillToPhone { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
    public List<PurchaseOrderLineEntity> Lines { get; set; } = new();
    public List<OutboundArtifact> OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = new();
    public List<OrderParty> Parties { get; set; } = new();
    public SourceCapture? SourceCapture { get; set; }
}
