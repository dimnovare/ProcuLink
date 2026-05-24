# Stripe Billing — Design Spec
**Date:** 2026-05-24  
**Phase:** 4 Group C  
**Status:** Approved

---

## 1. Overview

Add Stripe-powered subscription billing to ProcuLink. Five tiers — Pilot (time/volume-limited evaluation), Growth, Operations, Integration, Enterprise — designed around Integration/Enterprise as the primary revenue engine. Pilot and Growth tiers serve as qualified lead generation; the real ARR target (€3M within 3 years) comes from 30–100 Integration/Enterprise accounts.

All payment collection and subscription management for self-serve tiers uses Stripe-hosted Checkout + Customer Portal. Enterprise is a manual Stripe invoice flow triggered by a "Contact sales" path (no Checkout redirect).

---

## 2. Plans

| Plan | Orders | Suppliers | Price | Pilot window |
|---|---|---|---|---|
| `pilot` | 20 total | 1 | €0 | Ends at 14 days OR 20 orders — whichever comes first |
| `growth` | 150/mo | 5 | €149/mo | 14-day Stripe trial |
| `operations` | 500/mo | 10 | €399/mo | 14-day Stripe trial |
| `integration` | 1,000/mo | 20 | €999/mo | 14-day Stripe trial |
| `enterprise` | Custom | Custom | from €2,500/mo | — (manual) |

### Pilot behaviour (important)

The Pilot plan is a **one-time evaluation** with two independent exit conditions — whichever triggers first ends the Pilot:

1. **Time:** `DateTime.UtcNow > effective_pilot_end` where `effective_pilot_end = pilot_extended_until ?? (trial_started_at + 14 days)`
2. **Volume:** cumulative orders processed since `trial_started_at` ≥ 20

**Order counting for Pilot is cumulative, not monthly.** Unlike paid plans (which use a rolling monthly window), Pilot counts all `purchase_orders` where `org_id = @orgId AND created_at >= trial_started_at`. There is no monthly replenishment.

**After Pilot ends:**
- All uploads and supplier additions return `429` with `"error": "pilot_expired"`
- The account is fully frozen until upgraded to a paid plan
- "One-time" is structural: the Pilot is bound to the `Organisation` row, not the user. Starting a new Pilot requires a new Clerk org and a new Organisation row — deliberate friction.

**Pilot extension (manual safety valve):**  
A serious lead whose team was unavailable during the initial window should not be lost. The settings page shows a "Need more time? Request a Pilot extension" link when Pilot is active or has just expired. Clicking it calls `POST /api/billing/pilot/request-extension`, which sets `pilot_extension_requested_at` and notifies the sales team (logged + email for now). An admin then manually sets `pilot_extended_until` in the DB to grant extra days. No automation needed in this iteration — the request is the sales signal.

---

## 3. Feature gates per plan

| Feature | Pilot | Growth | Operations | Integration | Enterprise |
|---|---|---|---|---|---|
| CSV / XLSX | ✓ | ✓ | ✓ | ✓ | ✓ |
| XML | — | ✓ | ✓ | ✓ | ✓ |
| PDF | — | ✓ | ✓ | ✓ | ✓ |
| Mapping library | — | ✓ | ✓ | ✓ | ✓ |
| Validation rules | — | ✓ | ✓ | ✓ | ✓ |
| Bulk mapping import/export | — | — | ✓ | ✓ | ✓ |
| cXML support | — | — | ✓ | ✓ | ✓ |
| Delivery history | — | — | ✓ | ✓ | ✓ |
| Advanced audit | — | — | ✓ | ✓ | ✓ |
| Webhook / API delivery | — | — | — | ✓ | ✓ |
| Email ingestion | — | — | — | ✓ | ✓ |
| Custom output templates | — | — | — | ✓ | ✓ |
| ERP connectors | — | — | — | — | ✓ |
| Custom supplier rules | — | — | — | — | ✓ |
| SLA + dedicated onboarding | — | — | — | — | ✓ |

Feature gates are enforced server-side via `IBillingService.HasFeatureAsync(orgId, BillingFeature)`. The frontend reads a `features[]` array from `GET /api/billing/status` to show/hide UI affordances without extra round-trips.

---

## 4. Data Model

### `organisations` table — five new columns

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `trial_started_at` | `timestamptz` | no | Set to `NOW()` at org creation; never updated |
| `pilot_extended_until` | `timestamptz` | yes | Admin-set override for the 14-day deadline |
| `pilot_extension_requested_at` | `timestamptz` | yes | Set by the "Request extension" button; sales signal |
| `stripe_customer_id` | `text` | yes | Set on first Checkout session creation |
| `stripe_subscription_id` | `text` | yes | Set by `checkout.session.completed` webhook |

Existing `plan` string column stays. Updated by webhook events. Defaults to `"pilot"`.

### `Organisation` entity additions

```csharp
public DateTime   TrialStartedAt                { get; set; }   // set at creation, never updated
public DateTime?  PilotExtendedUntil            { get; set; }   // admin override
public DateTime?  PilotExtensionRequestedAt     { get; set; }   // set by request-extension endpoint
public string?    StripeCustomerId              { get; set; }
public string?    StripeSubscriptionId          { get; set; }
```

EF migration: `AddStripeFieldsToOrganisations`.

### `PlanConstants` — `ProcuLink.Core.Constants`

```csharp
public static class PlanConstants
{
    public const string Pilot       = "pilot";
    public const string Growth      = "growth";
    public const string Operations  = "operations";
    public const string Integration = "integration";
    public const string Enterprise  = "enterprise";

    public static readonly TimeSpan PilotDuration    = TimeSpan.FromDays(14);
    public const int                PilotOrderLimit  = 20;
    public const int                PilotSupplierLimit = 1;
}
```

### `PlanLimits` — `ProcuLink.Core.Constants`

```csharp
public record PlanLimits(int OrdersPerMonth, int SupplierCount);

// Note: Pilot uses PlanConstants.PilotOrderLimit (cumulative), not OrdersPerMonth (monthly).
// OrdersPerMonth for Pilot is set to PilotOrderLimit for uniform lookup; enforcement logic
// applies the correct counting window per plan type.
public static readonly Dictionary<string, PlanLimits> Limits = new()
{
    [PlanConstants.Pilot]       = new(20,    1),
    [PlanConstants.Growth]      = new(150,   5),
    [PlanConstants.Operations]  = new(500,   10),
    [PlanConstants.Integration] = new(1_000, 20),
    [PlanConstants.Enterprise]  = new(int.MaxValue, int.MaxValue),
};
```

### `BillingFeature` enum — `ProcuLink.Core.Constants`

```csharp
public enum BillingFeature
{
    Xml, Pdf, MappingLibrary, ValidationRules,
    BulkMapping, Cxml, DeliveryHistory, AdvancedAudit,
    WebhookDelivery, EmailIngestion, CustomTemplates,
    ErpConnectors, CustomSupplierRules, SlaOnboarding
}
```

Feature-to-minimum-plan resolution is a static lookup in `PlanConstants`. No DB column needed — derived from `plan` string at runtime.

---

## 5. Backend API

### 5.1 New package

`Stripe.net` added to `ProcuLink.Api.csproj`.

### 5.2 Configuration

```json
// appsettings.Development.json
"Stripe": {
  "SecretKey":          "sk_test_...",
  "WebhookSecret":      "whsec_...",
  "GrowthPriceId":      "price_...",
  "OperationsPriceId":  "price_...",
  "IntegrationPriceId": "price_..."
}
```

`StripeConfiguration.ApiKey` set at startup. Static Stripe SDK pattern (no DI registration).

### 5.3 Service layer

**`IBillingService`** — `ProcuLink.Core.Services`:

```csharp
Task<BillingStatus>    GetStatusAsync(Guid orgId);
Task<LimitCheckResult> CheckOrderLimitAsync(Guid orgId);
Task<LimitCheckResult> CheckSupplierLimitAsync(Guid orgId);
Task<bool>             HasFeatureAsync(Guid orgId, BillingFeature feature);
Task<string>           CreateCheckoutSessionAsync(Guid orgId, string plan, string returnUrl);
Task<string>           CreatePortalSessionAsync(Guid orgId, string returnUrl);
Task                   RequestPilotExtensionAsync(Guid orgId);
```

**`StripeBillingService`** — `ProcuLink.Api.Services` (implements `IBillingService`):
- `CreateCheckoutSessionAsync`: resolves price ID from plan string, sets `trial_period_days = 14`, `mode = subscription`, `allow_promotion_codes = true`, stores `metadata["plan"] = plan`
- `RequestPilotExtensionAsync`: sets `PilotExtensionRequestedAt = DateTime.UtcNow`, saves, logs + sends notification email to configured sales address

**Pilot order counting logic in `CheckOrderLimitAsync`:**
```
if plan == "pilot":
    count = SELECT COUNT(*) FROM purchase_orders
            WHERE org_id = @orgId AND created_at >= trial_started_at
    pilot_expired = (UtcNow > effective_pilot_end) OR (count >= 20)
    return LimitCheckResult(Allowed: !pilot_expired, PilotExpired: pilot_expired, ...)
else:
    count = SELECT COUNT(*) FROM purchase_orders
            WHERE org_id = @orgId AND created_at >= start_of_current_month
    return LimitCheckResult(Allowed: count < plan_limit, ...)
```

**`BillingStatus`** record — `ProcuLink.Core.Contracts`:

```csharp
record BillingStatus(
    string           Plan,
    int              OrdersUsed,          // cumulative for Pilot, month-to-date for paid
    int              OrderLimit,
    int              SuppliersActive,
    int              SupplierLimit,
    DateTime?        PilotEndsAt,         // effective_pilot_end for Pilot accounts
    bool             PilotExpired,
    bool             ExtensionRequested,  // true if request-extension was called
    DateTime?        TrialEndsAt,         // Stripe trial end for paid-plan trials
    BillingFeature[] Features
);
```

**`LimitCheckResult`** record — `ProcuLink.Core.Contracts`:

```csharp
record LimitCheckResult(
    bool    Allowed,
    bool    PilotExpired,   // false for paid plans
    string  Plan,
    int     Limit
);
```

### 5.4 BillingController

Route prefix: `/api/billing`.

| Method | Route | Auth | Body / Response |
|---|---|---|---|
| `GET` | `/status` | JWT | `BillingStatus` JSON |
| `POST` | `/checkout` | JWT | `{ plan }` → `{ url }` |
| `POST` | `/portal` | JWT | — → `{ url }` |
| `POST` | `/pilot/request-extension` | JWT | — → `{ message }` |
| `POST` | `/webhook` | Stripe sig | raw body → `200` |

**`POST /checkout` body:**
```json
{ "plan": "growth" | "operations" | "integration" }
```
Enterprise → "Contact sales" UI, no Checkout session.

**`POST /pilot/request-extension`:**
- If `PilotExtensionRequestedAt` already set → return `200` (idempotent, no re-notification)
- Otherwise set field, save, notify sales, return `{ "message": "Extension request received. Our team will be in touch within 1 business day." }`

**Webhook events handled:**

| Event | Action |
|---|---|
| `checkout.session.completed` | Write `StripeCustomerId`, `StripeSubscriptionId`, set `Plan` from `metadata["plan"]` |
| `customer.subscription.updated` | `trialing`/`active` → ensure `Plan` matches metadata; `past_due`/`unpaid` → log only |
| `customer.subscription.deleted` | `Plan = "pilot"`, clear `StripeSubscriptionId` (account reverts to frozen Pilot) |

**Idempotency:** Read DB state before each write, skip if already matches.  
**Error handling:** Catch-all → log + Sentry capture → return `500` for Stripe retry.  
**Raw body:** Webhook route uses `[FromBody]` with raw bytes before signature verification.

### 5.5 Limit enforcement

**Order limit** — `OrdersController.Upload`:

```csharp
var check = await _billing.CheckOrderLimitAsync(orgId);
if (!check.Allowed)
    return StatusCode(429, new {
        error      = check.PilotExpired ? "pilot_expired" : "order_limit_reached",
        plan       = check.Plan,
        limit      = check.Limit,
        upgradeUrl = "/settings"
    });
```

**Supplier limit** — `SuppliersController.Create`:

```csharp
var check = await _billing.CheckSupplierLimitAsync(orgId);
if (!check.Allowed)
    return StatusCode(429, new {
        error      = check.PilotExpired ? "pilot_expired" : "supplier_limit_reached",
        plan       = check.Plan,
        limit      = check.Limit,
        upgradeUrl = "/settings"
    });
```

**Feature gate** — any endpoint serving a gated feature:

```csharp
if (!await _billing.HasFeatureAsync(orgId, BillingFeature.WebhookDelivery))
    return StatusCode(403, new { error = "upgrade_required", feature = "webhook_delivery" });
```

---

## 6. Frontend

### 6.1 Settings page — Billing section

File: `src/app/(app)/settings/page.tsx`

Fetches `GET /api/billing/status` via TanStack Query (`queryKey: ["billing-status"]`).

**Pilot — active** (`plan == "pilot"` AND `pilotExpired == false`):
- Badge: "Pilot · N days left · M orders remaining"
- Usage bars: orders (`ordersUsed / 20`) + suppliers (`active / 1`)
- CTA row: "Upgrade to Growth · €149/mo", "Upgrade to Operations · €399/mo", "Upgrade to Integration · €999/mo"
- Secondary link: "Need more time? [Request a Pilot extension →]" — calls `POST /api/billing/pilot/request-extension`
- "Need Enterprise? [Contact us →]" below

**Pilot — expired** (`plan == "pilot"` AND `pilotExpired == true`):
- Amber banner: "Your Pilot has ended. Upgrade to continue using ProcuLink."
- Usage bars locked/greyed
- Same CTA row as above (more prominent)
- If `extensionRequested == false`: "Need more time? [Request a Pilot extension →]"
- If `extensionRequested == true`: "Extension request sent — our team will be in touch." (no button)

**Growth / Operations / Integration — Stripe trial active:**
- Badge: plan name + "Trial · N days left"
- Usage bars: monthly window
- CTA: "Manage billing →" → Customer Portal

**Growth / Operations / Integration — active:**
- Badge: plan name + price (e.g. "Operations · €399/mo")
- Usage bars
- CTA: "Manage billing →" → portal
- "Upgrade to [next tier] →" if not on Integration

**Enterprise:**
- Badge: "Enterprise · Custom"
- "Contact your account manager" — no usage bars

### 6.2 Upload 429 handling — `UploadWorkbench.tsx`

Inline banner replaces pipeline animation:

| `error` value | Message |
|---|---|
| `pilot_expired` | "Your Pilot has ended. [Upgrade to continue →]" |
| `order_limit_reached` | "You've reached your [N]-order monthly limit. [Upgrade your plan →]" |

### 6.3 Supplier add 429 handling

Supplier creation form if POST returns `429`:

> "You've reached your [N]-supplier limit on the [Plan] plan. [Upgrade →]"

---

## 7. Files changed / created

| File | Change |
|---|---|
| `ProcuLink.Api/ProcuLink.Api.csproj` | Add `Stripe.net` |
| `ProcuLink.Core/Constants/PlanConstants.cs` | New — plan strings, limits dict, pilot constants, feature map |
| `ProcuLink.Core/Constants/BillingFeature.cs` | New enum |
| `ProcuLink.Core/Entities/Organisation.cs` | Add 5 new fields |
| `ProcuLink.Core/Contracts/BillingStatus.cs` | New record |
| `ProcuLink.Core/Contracts/LimitCheckResult.cs` | New record |
| `ProcuLink.Core/Services/IBillingService.cs` | New interface |
| `ProcuLink.Infrastructure/Migrations/…AddStripeFields…` | New EF migration |
| `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` | Map new columns (snake_case) |
| `ProcuLink.Api/Services/StripeBillingService.cs` | New — implements IBillingService |
| `ProcuLink.Api/Controllers/BillingController.cs` | New — 5 endpoints |
| `ProcuLink.Api/Controllers/OrdersController.cs` | Add order limit check in Upload |
| `ProcuLink.Api/Controllers/SuppliersController.cs` | Add supplier limit check in Create |
| `ProcuLink.Api/Program.cs` | Register StripeBillingService, set StripeConfiguration.ApiKey |
| `appsettings.Development.json` | Stripe section with 3 price IDs |
| `src/app/(app)/settings/page.tsx` | Full billing section with 5-tier UI + extension request |
| `src/components/bridge/UploadWorkbench.tsx` | 429 inline banner with pilot_expired / order_limit_reached |

---

## 8. Webhook registration

Stripe dashboard endpoint: `https://<api-host>/api/billing/webhook`  
Events: `checkout.session.completed`, `customer.subscription.updated`, `customer.subscription.deleted`

Local dev: `stripe listen --forward-to localhost:5223/api/billing/webhook`

---

## 9. Revenue model context

Target: €3M ARR within 3 years.

| Tier | Target accounts | Monthly contribution |
|---|---|---|
| Integration (€999) | 50 | €49,950/mo |
| Enterprise (avg €3,000) | 30 | €90,000/mo |
| Operations (€399) | 100 | €39,900/mo |
| Growth (€149) | 200 | €29,800/mo |
| **Total** | | **~€2.5M ARR** |

Pilot → Growth → Operations → Integration is the conversion funnel. The Pilot extension request is a deliberate sales touch point: a lead who runs out of Pilot capacity in 3 days is a hot prospect who needs a call, not an automated email.

---

## 10. Out of scope (this iteration)

- Proration on mid-cycle plan changes
- Dunning emails (Stripe Smart Retries)
- Invoice PDF download (Customer Portal)
- Usage-based metering
- Multi-seat / per-user pricing
- Admin UI for managing Pilot extensions (manual DB edit for now)
- Self-serve Enterprise signup (manual contract path only)
