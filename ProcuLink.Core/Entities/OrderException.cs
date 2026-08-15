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
    /// <summary>
    /// Route | Map | Parse | Validate | Transform | Deliver — the stages actually emitted by
    /// <c>OrderExceptionService.ProblemFor</c> and the independent detectors beside it.
    /// </summary>
    public string   Stage      { get; set; } = string.Empty;
    /// <summary>
    /// Every code this build produces, all from <c>OrderExceptionService</c>:
    /// unrouted_order | unresolved_mapping | duplicate_po_number | parse_failed | transform_failed |
    /// delivery_failed | delivery_unconfirmed | supplier_rejected | dead_letter.
    /// <para>This list was stale before 2026-08-15: it advertised <c>validation_error</c>, which
    /// nothing has ever emitted, and omitted <c>unrouted_order</c>, <c>parse_failed</c> and
    /// <c>delivery_unconfirmed</c>, which are emitted. There is no enum and no DB CHECK constraint
    /// behind these strings, so this comment is the only inventory — keep it honest.</para>
    /// </summary>
    public string   Code       { get; set; } = string.Empty;
    /// <summary>warning | error | critical (the documented <c>info</c> level is never emitted).</summary>
    public string   Severity   { get; set; } = "warning";
    /// <summary>open | resolved | ignored</summary>
    public string   State      { get; set; } = "open";
    public string   Message    { get; set; } = string.Empty;
    public DateTime CreatedAt  { get; set; }
    public DateTime? ResolvedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
