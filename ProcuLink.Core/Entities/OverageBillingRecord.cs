namespace ProcuLink.Core.Entities;

/// <summary>
/// Idempotency ledger for per-order overage charges. One row is written the
/// first time a given <see cref="BillingKey"/> is billed for an organisation;
/// the unique (OrgId, BillingKey) constraint guarantees a Stripe webhook /
/// Hangfire retry can never add a second overage invoice item for the same
/// billing period.
///
/// <para>
/// <see cref="BillingKey"/> is the natural key of the BILLING PERIOD being billed
/// — <c>{orgId}:{periodStart:O}</c> (see
/// <c>BillingController.BuildPeriodBillingKey</c>). Keying on the period (not the
/// Stripe invoice id) means a voided + re-issued or recreated draft invoice for the
/// SAME period maps to the same key, so a second charge is blocked even though the
/// invoice id differs. Two different periods ⇒ two legitimate charges; the same
/// period replayed under any invoice id ⇒ a duplicate-key violation the service
/// swallows.
/// </para>
/// </summary>
public class OverageBillingRecord
{
    public Guid Id { get; set; }

    /// <summary>Organisation that was charged. Scopes the idempotency key.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// Natural key for the billing period this overage was billed against —
    /// <c>{orgId}:{periodStart:O}</c>. Unique per org.
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
