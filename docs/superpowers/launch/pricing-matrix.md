# ProcuLink Pricing Matrix

Source of truth for plan limits, Stripe price IDs, and checkout eligibility.
Update this file whenever plan limits or pricing changes.

Last verified against code: 2026-06-03
Code files: `ProcuLink.Core/Constants/PlanConstants.cs`, `ProcuLink.Api/Services/StripeBillingService.cs`

---

## Plan Ladder

| Plan | Price | Orders | Suppliers | Stripe Checkout | Notes |
|---|---|---|---|---|---|
| Pilot | €0 / 14-day trial | 20 total (cumulative, not monthly) | 1 | No | Read-only after expiry. No Stripe record created. |
| Growth | €149/mo | 150/mo | 5 | Yes | `Stripe:GrowthPriceId` env var |
| Operations | €399/mo | 500/mo | 10 | Yes | `Stripe:OperationsPriceId` env var |
| Integration | €999/mo | 1,000/mo | 20 | Yes | `Stripe:IntegrationPriceId` env var |
| Enterprise | Custom, from €2,500/mo | Unlimited | Unlimited | No (contact-sales) | Manual provisioning by founder |
| Distributor | — | 2,500/mo | 30 | Hidden | Not sold at launch. `Stripe:DistributorPriceId` is optional at startup. |

**Code constants match this matrix exactly** — no discrepancies found. See `PlanConstants.Limits` in `ProcuLink.Core/Constants/PlanConstants.cs`.

---

## Feature gates (minimum plan required)

| Feature | Minimum plan |
|---|---|
| XML transform | Growth |
| PDF ingestion | Growth |
| Mapping library | Growth |
| Validation rules | Growth |
| Bulk mapping | Operations |
| cXML transform | Operations |
| Delivery history | Operations |
| Advanced audit | Operations |
| Webhook delivery | Integration |
| Email ingestion (IMAP) | Integration |
| Custom templates | Integration |
| SFTP ingestion | Integration |
| S3 ingestion | Integration |
| ERP connectors (Erply/Directo) | Enterprise |
| Custom supplier rules | Enterprise |
| SLA onboarding | Enterprise |

---

## Stripe env vars

### Required at production startup (API)

These keys block the API from starting in Production if missing:

| Env var (Railway `__` notation) | Config key | Purpose |
|---|---|---|
| `STRIPE__SECRETKEY` | `Stripe:SecretKey` | Stripe secret API key |
| `STRIPE__WEBHOOKSECRET` | `Stripe:WebhookSecret` | Stripe webhook signature secret |
| `STRIPE__GROWTHPRICEID` | `Stripe:GrowthPriceId` | Monthly price ID for Growth plan |
| `STRIPE__OPERATIONSPRICEID` | `Stripe:OperationsPriceId` | Monthly price ID for Operations plan |
| `STRIPE__INTEGRATIONPRICEID` | `Stripe:IntegrationPriceId` | Monthly price ID for Integration plan |

### Optional at startup (warn-only if missing)

| Env var (Railway `__` notation) | Config key | Purpose |
|---|---|---|
| `STRIPE__DISTRIBUTORPRICEID` | `Stripe:DistributorPriceId` | Distributor monthly (not sold at launch) |
| `STRIPE__DISTRIBUTORYEARLYPRICEID` | `Stripe:DistributorYearlyPriceId` | Distributor yearly (not sold at launch) |
| `STRIPE__GROWTHYEARLYPRICEID` | `Stripe:GrowthYearlyPriceId` | Growth annual billing (not yet enabled) |
| `STRIPE__OPERATIONSYEARLYPRICEID` | `Stripe:OperationsYearlyPriceId` | Operations annual billing (not yet enabled) |
| `STRIPE__INTEGRATIONYEARLYPRICEID` | `Stripe:IntegrationYearlyPriceId` | Integration annual billing (not yet enabled) |

---

## Code vs. spec discrepancies

None. The limits in `PlanConstants.Limits` match this matrix and the CLAUDE.md spec exactly:

- Growth: 150 orders/mo, 5 suppliers ✓
- Operations: 500 orders/mo, 10 suppliers ✓
- Integration: 1,000 orders/mo, 20 suppliers ✓
- Distributor: 2,500 orders/mo, 30 suppliers ✓ (hidden, not sold)
- Pilot: 20 orders cumulative, 1 supplier, 14-day trial ✓

---

## Startup validator fix (2026-06-03)

`Stripe:DistributorPriceId` was previously in `ApiRequiredKeys`, which caused the API to
fail-fast at startup in Production if the key was absent. Since Distributor is not being sold
at launch, the Stripe product and price ID do not exist yet. The key has been moved to
`OptionalKeys` alongside the yearly-billing variants. Missing optional keys emit a warning
log but do not block startup.

---

## Launch checklist

- [ ] Growth Stripe product created (monthly price ID in hand)
- [ ] Operations Stripe product created (monthly price ID in hand)
- [ ] Integration Stripe product created (monthly price ID in hand)
- [ ] Stripe webhook endpoint configured for:
  - `checkout.session.completed`
  - `customer.subscription.updated`
  - `customer.subscription.deleted`
- [ ] Webhook signing secret captured and set in Railway
- [ ] `STRIPE__SECRETKEY`, `STRIPE__WEBHOOKSECRET`, `STRIPE__GROWTHPRICEID`, `STRIPE__OPERATIONSPRICEID`, `STRIPE__INTEGRATIONPRICEID` all set in Railway API service env vars
- [ ] Test mode verified end-to-end (Stripe test clock + Stripe CLI `stripe listen`) before switching to live keys
- [ ] Stripe customer portal configured (branding, return URL = `https://proculink.eu/settings`)
- [ ] Create Distributor Stripe product + set `STRIPE__DISTRIBUTORPRICEID` before enabling Distributor plan for customers
