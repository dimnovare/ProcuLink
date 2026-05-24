# Stripe Billing — Design Spec
**Date:** 2026-05-24  
**Phase:** 4 Group C  
**Status:** Approved (updated with 5-tier model)

---

## 1. Overview

Add Stripe-powered subscription billing to ProcuLink. Five tiers — Starter (free), Growth, Operations, Integration, Enterprise — designed around Integration/Enterprise as the primary revenue engine. The Starter and Growth tiers serve as qualified lead generation; the real ARR target (€3M within 3 years) comes from 30–100 Integration/Enterprise accounts.

All payment collection and subscription management for self-serve tiers uses Stripe-hosted Checkout + Customer Portal. Enterprise is a manual Stripe invoice flow triggered by a "Contact sales" path (no Checkout redirect).

---

## 2. Plans

| Plan | Orders/mo | Suppliers | Price | Trial |
|---|---|---|---|---|
| `starter` | 20 | 1 | €0 | 14 days, one-time, then frozen |
| `growth` | 150 | 5 | €149/mo | 14 days |
| `operations` | 500 | 10 | €399/mo | 14 days |
| `integration` | 1,000 | 20 | €999/mo | 14 days |
| `enterprise` | Custom | Custom | from €2,500/mo | — (manual) |

### Starter trial behaviour (important)

The Starter plan is a **one-time 14-day evaluation**, not a permanent free tier.

- `trial_started_at` is set to `DateTime.UtcNow` when the `Organisation` row is created.
- While `DateTime.UtcNow - trial_started_at ≤ 14 days`: normal Starter limits apply (20 orders, 1 supplier).
- After 14 days: the account is **frozen** — order and supplier counts do **not** replenish on the monthly rollover. All uploads and supplier additions return `429` with `"error": "trial_expired"`.
- The only exit from frozen state is upgrading to a paid plan (Growth or above).
- This is enforced server-side via `IBillingService.CheckOrderLimitAsync` / `CheckSupplierLimitAsync`. No Stripe involvement for the Starter tier — the trial is tracked entirely by `trial_started_at`.
- "One-time" is structural: the trial is bound to the `Organisation` row, not the user. A new trial requires a new organisation (new Clerk org), which is a deliberate friction point.

### Feature gates per plan

| Feature | Starter | Growth | Operations | Integration | Enterprise |
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

Feature gates are enforced server-side via `IBillingService.HasFeatureAsync(orgId, BillingFeature)`. The frontend reads a `features[]` array from `GET /api/billing/status` to show/hide UI affordances.

---

## 3. Data Model

### `organisations` table — three new columns

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `trial_started_at` | `timestamptz` | no | Set to `NOW()` at org creation; drives Starter freeze logic |
| `stripe_customer_id` | `text` | yes | Set on first Checkout session creation |
| `stripe_subscription_id` | `text` | yes | Set by `checkout.session.completed` webhook |

Existing `plan` string column stays. Updated by webhook events. Defaults to `"starter"`.

### `Organisation` entity additions

```csharp
public DateTime  TrialStartedAt       { get; set; }   // set at creation, never updated
public string?   StripeCustomerId     { get; set; }
public string?   StripeSubscriptionId { get; set; }
```

EF migration: `AddStripeFieldsToOrganisations`.

### `PlanConstants` — `ProcuLink.Core.Constants`

```csharp
public static class PlanConstants
{
    public const string Starter     = "starter";
    public const string Growth      = "growth";
    public const string Operations  = "operations";
    public const string Integration = "integration";
    public const string Enterprise  = "enterprise";

    public static readonly TimeSpan StarterTrialDuration = TimeSpan.FromDays(14);
}
```

### `PlanLimits` — `ProcuLink.Core.Constants`

```csharp
public record PlanLimits(int OrdersPerMonth, int SupplierCount);

public static readonly Dictionary<string, PlanLimits> Limits = new()
{
    [PlanConstants.Starter]     = new(20,    1),
    [PlanConstants.Growth]      = new(150,   5),
    [PlanConstants.Operations]  = new(500,   10),
    [PlanConstants.Integration] = new(1_000, 20),
    [PlanConstants.Enterprise]  = new(int.MaxValue, int.MaxValue),
};
```

### `BillingFeature` — `ProcuLink.Core.Constants`

```csharp
public enum BillingFeature
{
    Xml, Pdf, MappingLibrary, ValidationRules,
    BulkMapping, Cxml, DeliveryHistory, AdvancedAudit,
    WebhookDelivery, EmailIngestion, CustomTemplates,
    ErpConnectors, CustomSupplierRules, SlaOnboarding
}
```

Feature-to-minimum-plan resolution lives in a static lookup in `PlanConstants`. No DB column needed — derived from `plan` string at runtime.

---

## 4. Backend API

### 4.1 New package

`Stripe.net` added to `ProcuLink.Api.csproj`.

### 4.2 Configuration

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

`StripeConfiguration.ApiKey` set at startup. `StripeClient` not registered in DI — static Stripe SDK pattern used throughout (matches Stripe.net conventions).

### 4.3 Service layer

**`IBillingService`** — `ProcuLink.Core.Services`:

```csharp
Task<BillingStatus> GetStatusAsync(Guid orgId);
Task<bool> CheckOrderLimitAsync(Guid orgId);
Task<bool> CheckSupplierLimitAsync(Guid orgId);
Task<bool> HasFeatureAsync(Guid orgId, BillingFeature feature);
Task<string> CreateCheckoutSessionAsync(Guid orgId, string plan, string returnUrl);
Task<string> CreatePortalSessionAsync(Guid orgId, string returnUrl);
```

**`StripeBillingService`** — `ProcuLink.Api.Services`:
- `CreateCheckoutSessionAsync` accepts `plan` parameter, resolves correct price ID from config
- Checkout options: `trial_period_days = 14`, `mode = subscription`, `allow_promotion_codes = true`
- Portal: `return_url` → `/settings`

**`BillingStatus`** record — `ProcuLink.Core.Contracts`:

```csharp
record BillingStatus(
    string           Plan,
    int              OrdersThisMonth,
    int              OrderLimit,
    int              SuppliersActive,
    int              SupplierLimit,
    DateTime?        TrialEndsAt,        // set for Starter and paid-plan trials
    bool             TrialExpired,       // true when Starter trial > 14 days and not upgraded
    BillingFeature[] Features            // resolved from plan
);
```

### 4.4 BillingController

Route prefix: `/api/billing`.

| Method | Route | Auth | Body / Response |
|---|---|---|---|
| `GET` | `/status` | JWT | `BillingStatus` JSON |
| `POST` | `/checkout` | JWT | `{ plan }` → `{ url }` |
| `POST` | `/portal` | JWT | — → `{ url }` |
| `POST` | `/webhook` | Stripe sig | raw body |

**`POST /checkout` body:**
```json
{ "plan": "growth" | "operations" | "integration" }
```
Enterprise omitted — that path shows "Contact sales" UI, no Checkout session.

**Webhook events handled:**

| Event | Action |
|---|---|
| `checkout.session.completed` | Write `StripeCustomerId`, `StripeSubscriptionId`, set `Plan` from metadata |
| `customer.subscription.updated` | `trialing`/`active` → ensure correct plan; `past_due`/`unpaid` → log only |
| `customer.subscription.deleted` | `Plan = "sandbox"`, clear `StripeSubscriptionId` |

Plan is stored in Stripe session metadata (`metadata["plan"] = "growth"`) at Checkout creation so the webhook knows which tier to activate.

**Idempotency:** Read DB state before each write, skip if already matches.  
**Error handling:** Catch-all logs + Sentry, returns `500` for Stripe retry.  
**Raw body:** Webhook route must use `[FromBody]` with raw bytes, not JSON model binding.

### 4.5 Limit enforcement

**Order limit** — `OrdersController.Upload`:

```csharp
var check = await _billing.CheckOrderLimitAsync(orgId);
if (!check.Allowed)
    return StatusCode(429, new {
        error      = check.TrialExpired ? "trial_expired" : "order_limit_reached",
        plan       = check.Plan,
        limit      = check.Limit,
        upgradeUrl = "/settings"
    });
```

`CheckOrderLimitAsync` returns a `LimitCheckResult` (not a plain bool) so the caller can distinguish between `trial_expired` and `order_limit_reached` for the correct frontend message.

**Supplier limit** — `SuppliersController.Create`:

```csharp
if (!await _billing.CheckSupplierLimitAsync(orgId))
    return StatusCode(429, new {
        error      = "Supplier limit reached",
        plan       = status.Plan,
        limit      = status.SupplierLimit,
        upgradeUrl = "/settings"
    });
```

**Feature gate** — any endpoint that serves a gated feature:

```csharp
if (!await _billing.HasFeatureAsync(orgId, BillingFeature.WebhookDelivery))
    return StatusCode(403, new { error = "Upgrade required", feature = "webhook_delivery" });
```

---

## 5. Frontend

### 5.1 Settings page — Billing section

File: `src/app/(app)/settings/page.tsx`

Fetches `GET /api/billing/status` via TanStack Query.

**Starter — trial active** (`plan == "starter"` AND `trialExpired == false`):
- Badge: "Starter · Trial · N days left"
- Usage bars: orders (`ordersThisMonth / 20`) + suppliers (`active / 1`)
- CTA row: "Upgrade to Growth", "Upgrade to Operations", "Upgrade to Integration" — three buttons
- Subtle "Need Enterprise? [Contact us →]" link below

**Starter — trial expired** (`plan == "starter"` AND `trialExpired == true`):
- Banner at top of page: "Your 14-day trial has ended. Upgrade to continue using ProcuLink."
- All usage bars show as locked/greyed
- Same CTA row as above, but the primary button is more prominent
- No "manage billing" option (no Stripe subscription exists yet)

**Growth / Operations / Integration (trial):**
- Badge: plan name + "Trial · N days left"
- Usage bars: orders + suppliers for that plan's limits
- CTA: "Manage billing →" → portal

**Growth / Operations / Integration (active):**
- Badge: plan name + price (e.g. "Operations · €399/mo")
- Usage bars
- CTA: "Manage billing →" → portal
- "Upgrade →" link to the next tier if not already on Integration

**Enterprise:**
- Badge: "Enterprise · Custom"
- No usage bars (unlimited)
- "Contact your account manager" static text

### 5.2 Upload 429 handling

`UploadWorkbench.tsx`: if upload POST returns `429`, inline banner replaces the pipeline animation:

- `error == "trial_expired"` → "Your 14-day trial has ended. [Upgrade to continue →]"
- `error == "order_limit_reached"` → "You've reached your [N]-order monthly limit. [Upgrade your plan →]"

### 5.3 Supplier add 429 handling

Supplier creation form: if POST to suppliers returns `429`:

> "You've reached your [N]-supplier limit on the [Plan] plan. [Upgrade →]"

---

## 6. Files changed / created

| File | Change |
|---|---|
| `ProcuLink.Api/ProcuLink.Api.csproj` | Add `Stripe.net` |
| `ProcuLink.Core/Constants/PlanConstants.cs` | New — plan strings, limits dict, feature-to-plan map |
| `ProcuLink.Core/Constants/BillingFeature.cs` | New enum |
| `ProcuLink.Core/Entities/Organisation.cs` | Add `StripeCustomerId`, `StripeSubscriptionId` |
| `ProcuLink.Core/Contracts/BillingStatus.cs` | New record (includes `TrialExpired` bool) |
| `ProcuLink.Core/Contracts/LimitCheckResult.cs` | New record — `Allowed`, `TrialExpired`, `Plan`, `Limit` |
| `ProcuLink.Core/Services/IBillingService.cs` | New interface |
| `ProcuLink.Infrastructure/Migrations/…` | `AddStripeFieldsToOrganisations` |
| `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` | Map new columns (snake_case config) |
| `ProcuLink.Api/Services/StripeBillingService.cs` | New — Stripe SDK calls |
| `ProcuLink.Api/Controllers/BillingController.cs` | New — 4 endpoints |
| `ProcuLink.Api/Controllers/OrdersController.cs` | Add order limit check in Upload |
| `ProcuLink.Api/Controllers/SuppliersController.cs` | Add supplier limit check in Create |
| `ProcuLink.Api/Program.cs` | Register `StripeBillingService`, set `StripeConfiguration.ApiKey` |
| `appsettings.Development.json` | Stripe section — 3 price IDs + secret key |
| `src/app/(app)/settings/page.tsx` | Full billing section with 5-tier UI |
| `src/components/bridge/UploadWorkbench.tsx` | 429 inline banner |

---

## 7. Webhook registration

Stripe dashboard endpoint: `https://<api-host>/api/billing/webhook`  
Events: `checkout.session.completed`, `customer.subscription.updated`, `customer.subscription.deleted`

Local dev: `stripe listen --forward-to localhost:5223/api/billing/webhook`

---

## 8. Revenue model context

Target: €3M ARR within 3 years.

| Tier | Target accounts | Monthly ARR contribution |
|---|---|---|
| Integration (€999) | 50 | €49,950 |
| Enterprise (avg €3,000) | 30 | €90,000 |
| Operations (€399) | 100 | €39,900 |
| **Total** | | **~€2.16M ARR at these counts** |

Starter and Growth tiers are acquisition funnels, not revenue targets. The product, onboarding, and sales motion should all optimise for converting Starter → Growth → Operations trials into Integration contracts.

---

## 9. Out of scope (this iteration)

- Proration on mid-cycle plan changes
- Dunning emails (Stripe Smart Retries handles this)
- Invoice PDF download (Customer Portal)
- Usage-based metering (per-order pricing)
- Multi-seat / per-user pricing
- Self-serve Enterprise signup (manual contract path only)
