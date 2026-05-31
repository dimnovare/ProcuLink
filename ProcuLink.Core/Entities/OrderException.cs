namespace ProcuLink.Core.Entities;

/// <summary>
/// A durable, operator-workable exception raised against a purchase order.
/// Lean v1: state is open | resolved | ignored. No assignee or SLA yet.
/// Generated idempotently by OrderExceptionService.ReconcileAsync from order state.
/// </summary>
public class OrderException
{
    public Guid     Id         { get; set; }
    public Guid     OrgId      { get; set; }
    public Guid     OrderId    { get; set; }
    public Guid?    LineId     { get; set; }
    /// <summary>Parse | Validate | Map | Transform | Deliver</summary>
    public string   Stage      { get; set; } = string.Empty;
    /// <summary>unresolved_mapping | transform_failed | delivery_failed | supplier_rejected | dead_letter | validation_error</summary>
    public string   Code       { get; set; } = string.Empty;
    /// <summary>info | warning | error | critical</summary>
    public string   Severity   { get; set; } = "warning";
    /// <summary>open | resolved | ignored</summary>
    public string   State      { get; set; } = "open";
    public string   Message    { get; set; } = string.Empty;
    public DateTime CreatedAt  { get; set; }
    public DateTime? ResolvedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
