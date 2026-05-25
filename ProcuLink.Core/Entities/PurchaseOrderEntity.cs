using System.Text.Json;

namespace ProcuLink.Core.Entities;

/// <summary>
/// EF entity for purchase_orders. Named PurchaseOrderEntity to avoid
/// collision with ProcuLink.Core.Canonical.PurchaseOrder used by Phase-0 code.
/// </summary>
public class PurchaseOrderEntity
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
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

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
    public List<PurchaseOrderLineEntity> Lines { get; set; } = new();
    public List<OutboundArtifact> OutboundArtifacts { get; set; } = new();
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = new();
}
