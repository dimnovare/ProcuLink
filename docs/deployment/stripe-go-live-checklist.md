# Stripe Go-Live Checklist — June 9 (after company registration)

**State today (verified 2026-06-05):** the billing code is complete (checkout +
portal + webhook + all four tiers incl. Distributor, monthly & yearly) and every
`Stripe__*` var is already set in Railway — but in **test mode / a pre-company
account**. Going live = **swap in the live account's values**. No code changes.

Self-serve plans are **Growth / Operations / Integration / Distributor** only.
Pilot is the free trial (no Stripe). Enterprise is manual / contact-sales.

---

## 0. Prereqs
- [ ] Company registered; Stripe account created + verified (business details + IBAN for payouts).
- [ ] You are in **LIVE mode** in the new account (top-right "Test mode" toggle **OFF**).

## 1. Create the 4 subscription products — LIVE mode, currency **EUR**, **recurring/monthly**
Products → **Add product** → recurring, monthly, EUR. Copy each resulting **price ID** (`price_…`).

| Product name | Price | Interval | → env var |
|---|---|---|---|
| ProcuLink Growth | **€149.00** | monthly | `Stripe__GrowthPriceId` |
| ProcuLink Operations | **€399.00** | monthly | `Stripe__OperationsPriceId` |
| ProcuLink Integration | **€999.00** | monthly | `Stripe__IntegrationPriceId` |
| ProcuLink Distributor | **€1,499.00** | monthly | `Stripe__DistributorPriceId` |

- **Optional annual billing:** add a second *yearly* recurring price to each product and set the
  matching `Stripe__{Plan}YearlyPriceId`. The app's checkout passes `billingInterval=yearly` when a
  customer picks annual. If you don't offer annual, leave the `*YearlyPriceId` vars empty — monthly
  still works.
- **Do NOT create Pilot or Enterprise products.**
- **Per-supplier onboarding fee (€500 ×3, then €150):** *not* wired into Checkout (you don't know
  supplier count at subscribe time). Bill it as a **manual one-off Stripe invoice** per supplier for
  now — waived for design partners #1–5.

## 2. Webhook — LIVE mode
- [ ] Developers → **Webhooks** → Add endpoint.
- [ ] **Endpoint URL:** `https://api.proculink.eu/api/billing/webhook`
- [ ] **Events to send** — exactly these 3 (the code ignores everything else):
  - `checkout.session.completed`
  - `customer.subscription.updated`
  - `customer.subscription.deleted`
- [ ] Copy the endpoint's **Signing secret** (`whsec_…`) → `Stripe__WebhookSecret`.

## 3. Customer portal — LIVE mode
- [ ] Settings → Billing → **Customer portal** → activate; allow plan switches + cancellation.
  (The app's "Manage billing" button creates a portal session.)

## 4. (Optional) VAT / Stripe Tax
- [ ] If charging EU VAT/OSS: Settings → **Tax** → enable Stripe Tax + the company's tax registration.

## 5. API secret key
- [ ] Developers → API keys → copy the **LIVE Secret key** (`sk_live_…`) → `Stripe__SecretKey`.

## 6. Set the values in Railway (Claude can run this for you)
Only the **ProcuLink (API)** service needs Stripe vars (the Worker doesn't touch Stripe).
Setting vars triggers an API redeploy (~2–3 min). Replace the placeholders with the live values:

```bash
railway variables --service ProcuLink \
  --set "Stripe__SecretKey=sk_live_xxx" \
  --set "Stripe__WebhookSecret=whsec_xxx" \
  --set "Stripe__GrowthPriceId=price_xxx" \
  --set "Stripe__OperationsPriceId=price_xxx" \
  --set "Stripe__IntegrationPriceId=price_xxx" \
  --set "Stripe__DistributorPriceId=price_xxx"
# optional annual:
#   --set "Stripe__GrowthYearlyPriceId=price_xxx" \
#   --set "Stripe__OperationsYearlyPriceId=price_xxx" \
#   --set "Stripe__IntegrationYearlyPriceId=price_xxx" \
#   --set "Stripe__DistributorYearlyPriceId=price_xxx"
```

## 7. Smoke test — one real checkout
- [ ] Signed in as a test org → upgrade → **Growth** → complete Stripe Checkout with a real card (refund yourself after).
- [ ] Back in the app: org **plan = growth**, **account status = active**; `GET /api/billing/status` reflects it.
- [ ] Stripe → Webhooks → recent deliveries show **200** for `checkout.session.completed`.
- [ ] "Manage billing" opens the portal; cancel there → org reverts to read-only Pilot
      (`customer.subscription.deleted` handled).

## 8. Final sanity
- [ ] `proculink.eu/pricing` tiers match: Growth €149 · Operations €399 · Integration €999 ·
      Distributor €1,499 · Enterprise "from €2,500".
- [ ] No leftover **test-mode** price IDs / `sk_test_` / `whsec_` (test) values remain in Railway.

Once the live price IDs + `sk_live_` secret + live `whsec_` webhook secret are in Railway and the
smoke test passes, **you can take real money.**

---

### Reference (verified against code 2026-06-05)
- Checkout: `POST /api/billing/checkout` body `{ "Plan": "growth|operations|integration|distributor", "BillingInterval": "monthly|yearly" }` (`BillingController.cs:75-103`).
- Plan→priceId map + webhook handlers: `BillingController.cs:205-378`, `StripeBillingService.cs:187-213`.
- Pricing rationale: `docs/strategy/2026-05-30-pricing-proposal.md`.
