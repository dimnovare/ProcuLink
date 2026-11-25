# Changelog

All notable changes to ProcuLink are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2026-11-25]

### Added

- **ZUGFeRD / Factur-X invoice released** — the `factur-x-en16931` standard
  output is no longer behind the Peppol switch: every workspace can add and send
  it. Its credit note and the other e-invoice outputs stay switched.
- **OneDrive destination** (#351) — `microsoft_onedrive` protocol. A pasted
  folder sharing link is resolved through Graph `/shares` at save and dropped;
  only the drive id, item id and folder name are stored. Uploads by file name
  with `conflictBehavior=replace`, so a re-send overwrites the same file.
- **ZUGFeRD / Factur-X read** (#350) — `EmbeddedInvoice` takes the embedded
  CII invoice (2.x) as the read, with no AI call, only when every header value
  the read keeps (a flow with saved fields keeps only those) and prints as
  written is also on the page; otherwise the page is read as before.
  The attachment is size-capped, DTD-prohibited and resolver-free.

### Fixed

- **Reader** (#349) — the two-word-heading backstop ("UNIT" + "PRICE") folds an
  empty column only into a filled neighbour, so a table whose cells landed in no
  column no longer collapses its whole header line into one column.
- **Outlook pickup** (#348) — the folder list asks for no headers; they are
  fetched only for mail matching the sender / subject filter.
- **XML output** (#348) — a table element still named by its internal key takes
  the flow's name for the table, the same in every file of a flow.

### Docs

- `STATUS.md` rewritten as a short current state (#348).
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
