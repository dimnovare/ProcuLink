# Changelog

All notable changes to ProcuLink are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2026-06-04]

Post-delivery hardening wave (multi-agent). Backend 715 tests green; frontend
builds clean; CI restored to green.

### Added

- **Operations health view** — `/operations/health` page plus backend
  `OpsController` (`GET /api/ops/health`, `GET /api/ops/dead-letter`,
  `POST /api/ops/orders/{id}/requeue-delivery`). Surfaces stuck/failed/dead-letter
  orders and lets an operator requeue dead-lettered deliveries (the existing
  `retry-delivery` endpoint deliberately rejects those).
- **Apply starter template (UI)** — one-click "Apply Erply/Directo starter
  template" control in the PO mapping editor, calling the apply-template endpoint.
- **Onboarding docs + sample CSV** in `docs/integrations/ORDER_APIS.md`; root
  `CHANGELOG.md`.

### Fixed

- **Trust/honesty sweep:** Settings member count + "Save changes" now use real
  Clerk data/`organization.update()`; template "Export" downloads a real file;
  connector-edit no longer fakes a "saved" state (routes to the supplier Delivery
  tab); removed a dead "Manage" button.
- **Keyless build** — `<ClerkProvider>` is always mounted, so a build without
  `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` no longer crashes static prerender.
- **CI mock-mode e2e** — queries gated on `clerkReady` now use
  `isApiMockMode || clerkReady`, so mock mode (no Clerk session) no longer starves
  SpineReview / UploadWorkbench. Widened a next-dev nav timeout in the sample-order
  spec.

### Infrastructure

- **One worker.** Deleted the duplicate `ProcuLink-Worker` Railway service; the
  single `aware-amazement` worker (GitHub-auto-deploy, correct R2 secret) runs the
  queue. Re-verified `delivered` end-to-end after consolidation.
- `railway.toml` `watchPatterns` so doc-only commits don't trigger backend/worker
  redeploys.

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
