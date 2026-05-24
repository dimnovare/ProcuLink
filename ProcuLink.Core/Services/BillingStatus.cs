using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services;

/// <summary>
/// Snapshot of a tenant's billing state. Returned by GET /api/billing/status.
/// OrdersUsed is cumulative since trial_started_at for Pilot; month-to-date for paid plans.
/// </summary>
public record BillingStatus(
    string           Plan,
    int              OrdersUsed,
    int              OrderLimit,
    int              SuppliersActive,
    int              SupplierLimit,
    DateTime?        PilotEndsAt,         // effective pilot end for Pilot accounts; null for paid
    bool             PilotExpired,
    bool             ExtensionRequested,  // true if request-extension was called
    string[]         Features             // feature names: BillingFeature enum member names, lowercase
);
