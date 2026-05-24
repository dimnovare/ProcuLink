# Stripe Billing — Design Spec
**Date:** 2026-05-24  
**Phase:** 4 Group C  
**Status:** Approved

---

## 1. Overview

Add Stripe-powered subscription billing to ProcuLink. Operators on the free Starter plan are limited to 20 orders/month. Upgrading to Growth (€49/mo) raises the limit to 200 orders/month and includes a 14-day free trial. All payment collection and subscription management is handled by Stripe-hosted pages (Checkout + Customer Portal) — no custom payment UI.

---

## 2. Plans

| Plan | Monthly order limit | Price | Trial |
|---|---|---|---|
| `starter` | 20 | Free | — |
| `growth` | 200 | €49/mo | 14 days |
| `enterprise` | Unlimited | Custom | — |

Plan values are string constants defined in `ProcuLink.Core.Constants.PlanConstants`. Order counting is a live `COUNT` query on `purchase_orders` where `org_id = @orgId AND created_at >= start-of-current-month`. No separate counter column.

---

## 3. Data Model

### `organisations` table — two new columns

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `stripe_customer_id` | `text` | yes | Set on first Checkout session |
| `stripe_subscription_id` | `text` | yes | Set by `checkout.session.completed` |

Existing `plan` column (already present, defaults to `"starter"`) is updated by webhook events.

### `Organisation` entity additions

```csharp
public string? StripeCustomerId       { get; set; }
public string? StripeSubscriptionId   { get; set; }
```

EF migration: `AddStripeFieldsToOrganisations`.

---

## 4. Backend API

### 4.1 New package

`Stripe.net` added to `ProcuLink.Api.csproj`.

### 4.2 Configuration

```json
// appsettings.Development.json — already stubbed
"Stripe": {
  "SecretKey":      "sk_test_...",
  "WebhookSecret":  "whsec_...",
  "GrowthPriceId":  "price_..."
}
```

`StripeClient` registered as a singleton in DI via `StripeConfiguration.ApiKey`.

### 4.3 Service layer

**`IBillingService`** — `ProcuLink.Core.Services`:
```csharp
Task<BillingStatus> GetStatusAsync(Guid orgId);
Task<bool> CheckOrderLimitAsync(Guid orgId);           // true = under limit
Task<string> CreateCheckoutSessionAsync(Guid orgId, string returnUrl);
Task<string> CreatePortalSessionAsync(Guid orgId, string returnUrl);
```

**`StripeBillingService`** — `ProcuLink.Api.Services` (implements `IBillingService`):
- Uses `SessionService`, `BillingPortalSessionService` from `Stripe.net`
- Checkout: creates session with `trial_period_days = 14`, `mode = subscription`, `line_items` from `GrowthPriceId`
- Portal: creates session with `return_url` pointing back to `/settings`

**`BillingStatus`** record — `ProcuLink.Core.Contracts`:
```csharp
record BillingStatus(
    string Plan,
    int    OrdersThisMonth,
    int    OrderLimit,
    DateTime? TrialEndsAt
);
```

### 4.4 BillingController endpoints

All in `ProcuLink.Api.Controllers.BillingController`. Route prefix: `/api/billing`.

#### `GET /api/billing/status` — `[Authorize]`
Returns `BillingStatus` JSON. Used by the settings page via TanStack Query.

#### `POST /api/billing/checkout` — `[Authorize]`
Creates a Stripe Checkout Session. Returns `{ url: "https://checkout.stripe.com/..." }`.  
Frontend redirects `window.location.href = url`.

#### `POST /api/billing/portal` — `[Authorize]`
Creates a Stripe Customer Portal Session. Returns `{ url: "..." }`.  
Frontend redirects to the returned URL.

#### `POST /api/billing/webhook` — no `[Authorize]`
Raw request body required (do not parse as JSON before signature check).  
Reads `Stripe-Signature` header. Calls `EventUtility.ConstructEventAsync`.  
Returns `400` on invalid signature. Returns `200` on unrecognised event (Stripe's recommendation).

**Events handled:**

| Event | Action |
|---|---|
| `checkout.session.completed` | Write `StripeCustomerId`, `StripeSubscriptionId`, set `Plan = "growth"` |
| `customer.subscription.updated` | If status `trialing` or `active` → ensure `Plan = "growth"`; if `past_due` / `unpaid` → log, no immediate downgrade |
| `customer.subscription.deleted` | Set `Plan = "starter"`, clear `StripeSubscriptionId` |

**Idempotency:** Before each write, check current DB state. Skip write if already matches target state.  
**Error handling:** Unhandled exceptions caught, logged + Sentry capture, return `500` so Stripe retries.

### 4.5 Order limit enforcement

In `OrdersController.Upload`, before accepting the file:

```csharp
if (!await _billing.CheckOrderLimitAsync(orgId))
    return StatusCode(429, new {
        error      = "Order limit reached",
        plan       = status.Plan,
        limit      = status.OrderLimit,
        upgradeUrl = "/settings"
    });
```

Growth trial orgs count as Growth (limit 200) during the trial.

---

## 5. Frontend

### 5.1 Settings page — Billing section

File: `src/app/(app)/settings/page.tsx`

Fetches `GET /api/billing/status` via TanStack Query (`queryKey: ["billing-status"]`).

**Three render states:**

**Starter:**
- Badge: "Starter · Free"
- Usage bar: `ordersThisMonth / 20` — amber above 80%
- CTA: "Upgrade to Growth →" → POST `/api/billing/checkout` → redirect

**Growth (trial):**
- Badge: "Growth · Trial" + days-remaining chip
- Usage bar: `ordersThisMonth / 200`
- CTA: "Manage billing →" → POST `/api/billing/portal` → redirect

**Growth (active):**
- Badge: "Growth · €49/mo"
- Usage bar: `ordersThisMonth / 200`
- CTA: "Manage billing →" → POST `/api/billing/portal` → redirect

### 5.2 Upload 429 handling

`UploadWorkbench.tsx`: if the upload POST returns `429`, show inline banner:
> "You've reached your 20-order limit this month. [Upgrade to Growth →]"

No pipeline animation plays. Banner links to `/settings`.

---

## 6. Webhook registration (Stripe dashboard)

Stripe webhook endpoint: `https://<api-host>/api/billing/webhook`  
Events to subscribe: `checkout.session.completed`, `customer.subscription.updated`, `customer.subscription.deleted`

For local dev: `stripe listen --forward-to localhost:5223/api/billing/webhook`

---

## 7. Files changed / created

| File | Change |
|---|---|
| `ProcuLink.Api/ProcuLink.Api.csproj` | Add `Stripe.net` |
| `ProcuLink.Core/Constants/PlanConstants.cs` | New — plan string constants + limits |
| `ProcuLink.Core/Entities/Organisation.cs` | Add `StripeCustomerId`, `StripeSubscriptionId` |
| `ProcuLink.Core/Contracts/BillingStatus.cs` | New record |
| `ProcuLink.Core/Services/IBillingService.cs` | New interface |
| `ProcuLink.Infrastructure/Migrations/…AddStripeFields…` | New migration |
| `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` | Map new columns |
| `ProcuLink.Api/Services/StripeBillingService.cs` | New — implements IBillingService |
| `ProcuLink.Api/Controllers/BillingController.cs` | New — 4 endpoints |
| `ProcuLink.Api/Controllers/OrdersController.cs` | Add limit check in Upload |
| `ProcuLink.Api/Program.cs` | Register StripeBillingService, configure Stripe |
| `appsettings.Development.json` | Stripe section already stubbed — add real keys in `.gitignore`d user secrets |
| `src/app/(app)/settings/page.tsx` | Billing section UI |
| `src/components/bridge/UploadWorkbench.tsx` | 429 inline banner |

---

## 8. Out of scope

- Proration on mid-cycle plan changes (Enterprise upgrade path is manual)
- Dunning emails (handled by Stripe's built-in Smart Retries)
- Invoice PDF download (available via Customer Portal)
- Usage-based billing metering
- Multi-seat pricing
