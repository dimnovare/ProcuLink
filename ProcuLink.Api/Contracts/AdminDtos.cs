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

/// <summary>
/// Request body for POST /api/admin/organisations/{id}/limits. All fields are
/// optional; a present field SETS the override (use a non-negative value to set,
/// or a negative value / null per <see cref="ClearOrderLimit"/> etc. to clear).
/// Limits must be non-negative when set. <see cref="ExtendTrialDays"/> is a
/// convenience that pushes the effective trial end out from now by N days.
/// </summary>
public sealed record SetOrgLimitsRequest(
    int? OrderLimitOverride = null,
    int? SupplierLimitOverride = null,
    DateTime? TrialEndsAtOverride = null,
    int? ExtendTrialDays = null,
    bool ClearOrderLimit = false,
    bool ClearSupplierLimit = false,
    bool ClearTrialEnds = false);

/// <summary>
/// Response for POST /api/admin/organisations/{id}/limits — the updated org row
/// plus the now-effective limits/trial-end (override ?? plan/computed default).
/// </summary>
public sealed record OrgLimitsResponse(
    Guid Id,
    string Name,
    string Plan,
    string AccountStatus,
    int? OrderLimitOverride,
    int? SupplierLimitOverride,
    DateTime? TrialEndsAtOverride,
    int EffectiveOrderLimit,
    int EffectiveSupplierLimit,
    DateTime? EffectiveTrialEndsAt);

/// <summary>
/// Request body for POST /api/admin/organisations/{id}/retention (admin-only — org admins
/// cannot self-serve retention yet). Set <see cref="RetentionDays"/> (≥ 1) to enable the
/// blob-retention sweep for the org, or <see cref="Clear"/> = true to disable it (NULL —
/// the safe default). When both are supplied, Clear wins.
/// </summary>
public sealed record SetOrgRetentionRequest(
    int? RetentionDays = null,
    bool Clear = false);

/// <summary>Response for POST /api/admin/organisations/{id}/retention.</summary>
public sealed record OrgRetentionResponse(
    Guid Id,
    string Name,
    int? RetentionDays,
    bool RetentionEnabled);
