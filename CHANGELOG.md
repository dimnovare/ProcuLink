# Changelog

All notable changes to ProcuLink are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2026-06-03]

The full live purchase-order loop is proven end-to-end in production on
`proculink.eu` / `api.proculink.eu`: upload → parse → resolve → transform →
delivered (HTTP 200), plus the honest `delivery_failed` "missing config" path.

### Added

- **Exception dashboard** at `/operations/exceptions` — a single view of all
  orders in an exception/failed state.
- **Erply/Directo apply-template endpoint** — one-click copies a starter
  PO field-mapping config into a supplier so first setup is near-zero.

### Fixed

- **BuyerName** is now read from the denormalized column, so the buyer name
  (e.g. "Northwind Trading OÜ") shows correctly on the order/review screens.
- **Settings page** wired to live organisation/plan data instead of placeholder
  values.
- **R2 downloads** use `GetObjectAsync` (was `DownloadAsync`), resolving the
  signing path used by parse/transform jobs.
- Added delivery-phase logging for clearer diagnosis of delivery attempts.

### Infrastructure

- **Background Worker is live.** `ProcuLink.Worker` runs as a single healthy
  Railway container (service `aware-amazement`), auto-deploying from GitHub and
  consuming the Hangfire parse/transform/delivery queue.
- **Fixed intermittent R2 `SignatureDoesNotMatch`** caused by two worker
  services sharing the Hangfire queue with a stale/wrong R2 secret on
  `aware-amazement`. Corrected the secret and deleted the duplicate
  `ProcuLink-Worker` service, so exactly one worker now runs.
- `railway.toml` now uses `watchPatterns` so docs-only commits no longer trigger
  a redeploy.
- Founder configuration completed: Resend email domain verified, Google Search
  Console set up, PostHog set up.
- Backend test suite: 704 tests green.

### Pending

- Stripe activation (after company registration on 2026-06-09).
- Rotate the Clerk and R2 secrets that were pasted into chat.
