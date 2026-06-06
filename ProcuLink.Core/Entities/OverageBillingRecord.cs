namespace ProcuLink.Core.Entities;

/// <summary>
/// Idempotency ledger for per-order overage charges. One row is written the
/// first time a given <see cref="BillingKey"/> is billed for an organisation;
/// the unique (OrgId, BillingKey) constraint guarantees a Stripe webhook /
/// Hangfire retry can never add a second overage invoice item for the same
/// billing period.
///
/// <para>
/// <see cref="BillingKey"/> is the natural key of the thing being billed — in
/// practice the Stripe invoice id the overage line was attached to (e.g.
/// <c>in_123</c>). Two different invoices ⇒ two legitimate charges; the same
/// invoice replayed ⇒ a duplicate-key violation that the service swallows.
/// </para>
/// </summary>
public class OverageBillingRecord
{
    public Guid Id { get; set; }

    /// <summary>Organisation that was charged. Scopes the idempotency key.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// Natural key for the period/invoice this overage was billed against
    /// (typically the Stripe invoice id). Unique per org.
    /// </summary>
    public string BillingKey { get; set; } = string.Empty;

    /// <summary>Number of orders billed as overage (orders above the cap).</summary>
    public int OverageOrders { get; set; }

    /// <summary>Total amount charged, in EUR cents (OverageOrders × 50).</summary>
    public long AmountCents { get; set; }

    /// <summary>The Stripe invoice-item id created, when Stripe was configured.</summary>
    public string? StripeInvoiceItemId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
