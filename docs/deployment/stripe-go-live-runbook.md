# Stripe go-live runbook (TEST → LIVE)

**Status today:** prod runs Stripe in **TEST mode** (`sk_test…`, prices `livemode:false`). Test-mode QA is green: all 4 self-serve prices active (Growth €149 / Operations €399 / Integration €999 / Distributor €1,499), a Distributor checkout session creates successfully, and the backend wiring (checkout→price, overage, `EmitBilling*`, webhook signature) is verified. This runbook is the **founder-executed** switch to live money. Claude cannot enter card/payment details or do the live financial swap — you do the steps below; Claude prepared everything else.

## What's already correct (no action)
- `Stripe:DistributorPriceId` is a **required** boot key and is set (test). It must also be set for **live** or the API won't boot.
- `CreateCheckoutSessionAsync` maps `growth/operations/integration/distributor` (+ yearly) → the right price IDs.
- Overage billing is period-keyed idempotent (€0.50/order via `invoice.created`).
- `EmitBilling*` events are wired to the webhook handlers.
- Stripe.net 51.1.0 pins the outgoing API version automatically (no code pin needed); `AppInfo` tags ProcuLink in Stripe logs.

## Steps

1. **Create the 4 LIVE products + prices** (Stripe Dashboard → switch to **Live mode**):
   - Growth €149/mo, Operations €399/mo, Integration €999/mo, Distributor €1,499/mo (EUR).
   - (Optional) the 4 yearly prices if you want annual billing live (the `*YearlyPriceId` keys are optional).
   - Copy each **live** price ID (`price_…`).

2. **Set Railway env on the `ProcuLink` (API) service** (Variables → edit):
   - `Stripe__SecretKey` = `sk_live_…`
   - `Stripe__WebhookSecret` = the **live** `whsec_…` from step 3
   - `Stripe__GrowthPriceId`, `Stripe__OperationsPriceId`, `Stripe__IntegrationPriceId`, **`Stripe__DistributorPriceId`** = the live IDs (+ the 4 `*YearlyPriceId` if created)
   - (Worker doesn't need Stripe keys.)

3. **Create the LIVE webhook endpoint** (Stripe Dashboard → Developers → Webhooks → Add endpoint, Live mode):
   - URL: `https://api.proculink.eu/api/billing/webhook`
   - Events: `checkout.session.completed`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.created`
   - Copy the endpoint's **signing secret** (`whsec_…`) → that's `Stripe__WebhookSecret` in step 2.
   - Endpoint API version: leave at the account default (Stripe.net 51.1.0 parses current shapes; if Stripe later prompts an account version upgrade, re-test the webhook after).

4. **Redeploy the API** (Railway redeploys on the env change, or trigger manually). Confirm it **boots** — `https://api.proculink.eu/health` → 200 and `/health/ready` → Healthy. (If `DistributorPriceId` is missing for live, the API fail-fasts at boot — that's the guard working.)

5. **Verify live (small real test, then refund):**
   - In the app, start a checkout for one tier with a **real card** (or a low tier) → complete → confirm the plan upgrades in Settings (the `checkout.session.completed` handler fired) and the Stripe webhook shows `200`.
   - Open the Billing Portal from the app → confirm it loads.
   - (Overage) is exercised by `invoice.created` at the next billing cycle; you can also trigger a test event from the Dashboard ("Send test webhook" → `invoice.created`) and confirm `200`.
   - Refund/cancel the test subscription.

6. **Rotate secrets exposed during this session:** the Sentry + PostHog testing tokens pasted in chat were test/observability tokens — rotate them after launch as good hygiene. (No Stripe live secret was ever shared with Claude.)

## Rollback
If anything misbehaves, set `Stripe__SecretKey` back to `sk_test…` + the test `Stripe__WebhookSecret` + test price IDs and redeploy — you're back to test mode, no money moves. Keep the test values noted before switching.
