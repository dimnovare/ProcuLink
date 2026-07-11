using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;

namespace ProcuLink.Api.Services;

/// <summary>
/// Single source of truth for Stripe → ProcuLink plan/status mapping, shared by the billing
/// webhook (<see cref="Controllers.BillingController"/>) and the reconciliation service
/// (<see cref="StripeSubscriptionReconciliationService"/>) so the two can never drift. Pure
/// functions — config + Stripe string in, ProcuLink constant out.
///
/// <para>Deliberately scoped to the TWO pieces both callers need identically: the price-id →
/// plan resolver and the subscription-status → account-status switch. The checkout-completed
/// <c>trialing?Trialing:Active</c> default and the subscription-deleted hard-set to
/// Pilot/ReadOnly stay INLINE in the controller — their semantics differ (checkout never keeps
/// the current status; deletion is an unconditional business action, not a status mapping).</para>
/// </summary>
public static class StripeBillingMapping
{
    /// <summary>
    /// Maps a Stripe price id to a ProcuLink plan constant using the configured price ids, or
    /// null when the price is blank/unrecognised (the caller keeps the org's existing plan —
    /// e.g. a manually-provisioned Enterprise subscription whose price is not in config).
    /// </summary>
    public static string? MapPriceIdToPlan(IConfiguration config, string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return null;
        if (priceId == config["Stripe:GrowthPriceId"]) return PlanConstants.Growth;
        if (priceId == config["Stripe:GrowthYearlyPriceId"]) return PlanConstants.Growth;
        if (priceId == config["Stripe:OperationsPriceId"]) return PlanConstants.Operations;
        if (priceId == config["Stripe:OperationsYearlyPriceId"]) return PlanConstants.Operations;
        if (priceId == config["Stripe:IntegrationPriceId"]) return PlanConstants.Integration;
        if (priceId == config["Stripe:IntegrationYearlyPriceId"]) return PlanConstants.Integration;
        if (priceId == config["Stripe:DistributorPriceId"]) return PlanConstants.Distributor;
        if (priceId == config["Stripe:DistributorYearlyPriceId"]) return PlanConstants.Distributor;
        return null;
    }

    /// <summary>
    /// Maps a Stripe subscription status to an <see cref="AccountStatusConstants"/> value.
    /// An unknown/null status keeps the caller's <paramref name="currentStatus"/>.
    ///
    /// <para>Used by BOTH the webhook (<c>BillingController.HandleSubscriptionUpdatedAsync</c>) and
    /// the reconciliation service, so they stay in lock-step. <c>paused</c> → read_only (a paused
    /// subscription blocks new processing until it resumes) is a founder-approved 2026-07-11
    /// addition applied uniformly to both paths.</para>
    /// </summary>
    public static string MapStatusToAccountStatus(string? stripeStatus, string currentStatus) => stripeStatus switch
    {
        "trialing"             => AccountStatusConstants.Trialing,
        "active"               => AccountStatusConstants.Active,
        "past_due" or "unpaid" => AccountStatusConstants.PastDue,
        "paused"               => AccountStatusConstants.ReadOnly,
        "canceled"             => AccountStatusConstants.ReadOnly,
        _                      => currentStatus,
    };
}
