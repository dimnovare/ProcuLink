namespace ProcuLink.Api.Contracts;

/// <summary>
/// Platform-wide revenue + health snapshot for the owner/admin overview.
/// MRR is the DB estimate (active paid orgs × published monthly list price).
/// <see cref="StripeMrr"/> is the Stripe-sourced figure (sum of active
/// subscription monthly amounts); it is null + <see cref="Reconciled"/> false
/// when Stripe is not configured. ARR = MRR × 12 (DB figure).
/// </summary>
public sealed record AdminOverviewDto(
    decimal Mrr,
    decimal Arr,
    decimal? StripeMrr,
    bool Reconciled,
    IReadOnlyDictionary<string, int> CountsByAccountStatus,
    int NewOrgsThisMonth,
    double TrialToPaidConversion);

/// <summary>One organisation row for the admin customers table. Cross-tenant.</summary>
public sealed record AdminOrganisationDto(
    Guid Id,
    string Name,
    string Slug,
    string Plan,
    string AccountStatus,
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    decimal MrrContribution,
    DateTime CreatedAt,
    DateTime? LastOrderActivity,
    int OrderVolume30d,
    int SupplierCount);

/// <summary>Request body for POST /api/admin/invoices.</summary>
public sealed record CreateInvoiceRequest(
    Guid OrganisationId,
    IReadOnlyList<CreateInvoiceLineItem> LineItems,
    string? Currency = null);

public sealed record CreateInvoiceLineItem(
    string Description,
    long AmountCents,
    int Quantity = 1);

/// <summary>Response for POST /api/admin/invoices.</summary>
public sealed record CreateInvoiceResponse(
    string InvoiceId,
    string? HostedInvoiceUrl,
    string Status);
