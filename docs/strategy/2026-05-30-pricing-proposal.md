# ProcuLink — Pricing Repricing (2026-05-30)

Derived from the §3 customer-economics analysis in `2026-05-30-investor-analysis.md`.
Approved by the founder on 2026-05-30 (scope: "fix calculator now + draft repricing", top-tier =
"add Distributor €1,499"). **The subscription tier is implemented in code; the per-supplier setup
fee is a manual process until the automated charge flow is built (see "Not yet automated").**

---

## Why

The unit economics showed the ladder was mispriced at both ends:
- **Small suppliers** (~100 orders/mo) have a labor-savings WTP of ~€12/mo — 12× below the €149
  entry. They are a **viral/mandated channel** (a large buyer mandates delivery, the buyer pays),
  not a paid target. No lower tier added.
- **Large distributors** (10k orders/mo) save ~€5,880/mo, so Integration €999 was ~2× underpriced.
  A new **Distributor €1,499** tier (2,500 orders / 30 suppliers) captures that value; Enterprise
  floor made explicit at **from €2,500/mo**.
- **Mapping config is the real cost driver**, so a **per-supplier onboarding fee** (€500 for the
  first 3 suppliers, €150 after) is added on Operations and above — waived for design partners #1–5.

## The ladder (as implemented)

| Plan | €/mo | Per-supplier setup | Orders/mo | Suppliers |
|---|---|---|---|---|
| Pilot | €0 / 14d | — | 20 | 1 |
| Growth | €149 | — (self-serve) | 150 | 5 |
| Operations | €399 | €500 ×3, then €150 | 500 | 10 |
| Integration | €999 | €500 ×3, then €150 | 1,000 | 20 |
| **Distributor** ⭐ | **€1,499** | €500 ×3, then €150 | **2,500** | **30** |
| Enterprise | from €2,500 | custom | custom | custom |

## What was implemented in code (2026-05-30)

**Backend (`ProcuLink`):**
- `PlanConstants.cs` — `Distributor` constant; added to `All`, `Limits` (2,500 / 30), `IsPaidPlan`,
  `PlanOrder` (ranked between Integration and Enterprise). Limit enforcement flows automatically.
- `StripeBillingService.CreateCheckoutSessionAsync` — `distributor → Stripe:DistributorPriceId`.
- `BillingController` — `PlanRank` (distributor=4, enterprise=5), checkout `validPlans`,
  `MapPriceIdToPlan` (recognizes the Distributor subscription from its price ID).
- `appsettings.Development.json` + `appsettings.Production.json` — `Stripe:DistributorPriceId`.

**Frontend (`project-proculink`):**
- `types/procurement.ts` — `"distributor"` added to `BillingPlan`.
- `BillingSection.tsx` — `PLAN_META.distributor`, `integration.next = "distributor"`, added to
  `CHECKOUT_PLANS` (in-app upgrade now chains …→ Integration → Distributor).
- `pricing/page.tsx` — Distributor tier card; Enterprise "from €2,500/mo"; setup-fee footnote.
- `ROICalculator.tsx` — Distributor band (1,000–2,500 orders → €1,499); subscription-only payback.

The Distributor color is teal `#0E7490` (distinct from Integration's violet) across both UIs.

## Founder action required before it can bill (Stripe + env)

1. In Stripe, create a **Distributor** product with a **€1,499/mo recurring price**.
2. Put the new price ID in `Stripe:DistributorPriceId` (Railway env for prod; user-secrets/dev
   appsettings for local). Until set, a Distributor checkout throws "price ID not configured"
   (same safe pattern as the other tiers).
3. Nothing else is needed for the subscription tier to work end-to-end.

## Not yet automated (deliberate) — the per-supplier setup fee

The €500/€150 per-supplier onboarding fee is **not** wired into Checkout, because at subscription
time you don't yet know how many suppliers will be configured — folding it into the recurring
checkout would be fragile. For now:
- It is **advertised** on the pricing page and is a **manual Stripe invoice** the founder raises
  per supplier as they're onboarded (fine at pilot scale; matches the §9 concierge model).
- **Follow-up feature (when there's demand):** charge it automatically via a Stripe one-time
  invoice item triggered when a new `SupplierDeliveryConfig` is first activated — gated behind a
  `Stripe:SupplierSetupPriceId` config and an org-level "first 3 then €150" counter. Estimated
  ~½ day once the founder wants it.

## Open follow-ups
- Add a `distributor` case to any billing unit test that enumerates tiers (verify after `dotnet test`).
- Optional: surface "Distributor" as a direct upgrade button for Pilot accounts with very high
  detected volume (currently reachable via the Integration→Distributor upgrade chain or sales).
