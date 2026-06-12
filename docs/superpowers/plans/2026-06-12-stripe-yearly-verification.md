# Stripe yearly-renewal verification (test clock) — 2026-06-12

Closes the "4-bis" open question from the yearly-billing audit: does a REAL Stripe
yearly-renewal invoice carry the full-year period (→ all 12 months metered) or a
narrow period (→ under-charge)?

## Method
Stripe TEST sandbox, Test Clock frozen at 2026-01-15 (mid-month on purpose), customer
subscribed to the existing Growth yearly price (`price_1TdQY4LMyzXaWowfm3aIMCPe`),
clock advanced past the 1-year renewal to 2027-01-16. Renewal invoice inspected raw.

## Observed
**Invoice 1** (`subscription_create`): top-level period_start == period_end ==
2026-01-15 (zero-length); line period = 2026-01-15 → 2027-01-15 (the year charged,
forward).

**Invoice 2** (`subscription_cycle`, THE renewal): **top-level period_start =
2026-01-15, period_end = 2027-01-15 — the ENTIRE just-closed year.** Line period =
2027-01-15 → 2028-01-15 (the upcoming year — advance billing).

## Verdict — CORRECT AS-IS, no fix needed
`HandleInvoiceCreatedAsync` reads the top-level fields → `SplitBillingPeriodIntoMonthlyWindows`
(365 d > 32) → **13 calendar windows (2 partials + 11 full) covering all 12 months, no
gaps/overlaps**, each period-key idempotent, each metered at the as-of plan. The
hypothesized "prefer line-item period" fix would have been a BUG (line period is the
FUTURE year on cycle invoices). First invoice: zero-length window → 0 orders → no
spurious overage. Anchor-month double-allowance quirk re-confirmed (customer-favorable,
runbook §5).

## Founder decision memo — cancellation final-period overage
Today `customer.subscription.deleted` bills nothing; overage is only charged at the next
`invoice.created`, which never comes after a cancellation.

- **Option A (current, customer-favorable):** forgive it. Monthly plans: exposure ≤ the
  final month's overage (Distributor at 2× allowance forgoes 2,500 × €0.50 = **€1,250**;
  Growth at 2× = €75). **Yearly plans: exposure = the ENTIRE final year** — this
  verification proves yearly overage is metered only at the renewal invoice, so a yearly
  customer who cancels at period end pays €0 overage for all 12 months (Distributor at 2×
  all year = **€15,000** worst-case envelope; Growth at 2× = €900).
- **Option B (bill final period):** on `subscription.deleted`, run the same monthly-window
  metering over the unbilled span and issue a one-off final invoice
  (`pending_invoice_items_behavior=include`, auto-advance). Honest, but charges a
  just-churned customer and needs renewal-grade idempotency + dunning care.
- **Recommendation:** keep Option A for monthly (cheap goodwill), but DECIDE explicitly
  for yearly before selling annual contracts — Option A there is a revenue hole that
  scales with the whole year. Middle path: contract language reserving the right to
  invoice accrued overage on termination; implement Option B later if needed.

## Hygiene
Test clock `clock_1ThXvyLMyzXaWowfFqCCo6LG` deleted (customer/sub/invoices cascaded);
`sk_test_` verified before every call; key never written to file/echoed/committed.
