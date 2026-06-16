# 2026-06-16 — Live prod audit + purge + method-test session handoff

Durable handoff so this work resumes cold. Caveman-terse on purpose.

## What shipped this session (all on `main`, both repos, deployed)

| Commit | Repo | What |
|---|---|---|
| `8a9615b` | FE | QueryClient `networkMode:"always"` (necessary defense vs RQ offline-pause) |
| `48cea6e` | FE | **P0 fix** — `authHeader()` waits for `window.Clerk.loaded` before reading the token. Cold-mount race: (app) queries fired before Clerk loaded → 401 → query parked → **landing dashboard + every cold-loaded screen rendered empty** while the API had data. Verified live (206→0→2 orders correct across reloads). |
| `9191d50` | FE | **Pass-1 audit: 11 verified broken-interaction fixes.** Incl. P1 `delivery.ts` had its OWN local authHeader the global fix missed (Delivery tab 401 on cold load — re-verified live); mapper stale-draft on revision switch; email-form clobbered typed password; + 7 P2/P3. |
| `3586be9` | FE | **Pass-2 audit: 5 FE fixes.** P1 Pilot checkout silent-on-error; P2 notif badge hid dead-letter/rejected, no mobile search, template body discarded, dashboard "30 days" mislabel + export 100-cap; P3 dead mapper palette commands. |
| `d778f85` | BE | **Pass-2 P1: invoice list blank.** `GET /api/invoices` emitted issueDate/grandTotal, no supplierName/lineCount, while FE read supplierName/invoiceDate/totalAmount/lineCount → 4 cols always "—". New `InvoiceListItem` projection (supplier left-join + Lines.Count). **Verified live: Supplier+Date now populate, no blanks.** |

Detail in memory file `project-react-query-paused-offline.md`, `project-prod-purge-and-method-tests-2026-06-16.md`, `project-four-track-push-2026-06-16.md`.

## Prod state (org `00000000-0000-0000-0000-000000000000`, slug `personal-workspace-d3be`)

- **Full-wiped** then re-seeded: 0 orders → 2 delivered + 1 invoice (parsing).
- Note: 10 orgs all named "Personal workspace" — **match by SLUG** before any destructive admin call.

### Live test fixtures (clean these up when done)
- **Receiver supplier** `9a87bcbb-8eaf-4b5d-ba32-236c4890332a` "Receiver JSON (test)" — HTTP/JSON auto-deliver → webhook.site.
- **webhook.site receiver** `https://webhook.site/200c097e-170a-4fe8-a63a-6228ea7e9bd5` (read deliveries via its `/token/{uuid}/requests` API from a webhook.site tab — CORS-blocks from the app origin).
- **Ingress API key** `plk_S9Oh…` (created for the REST-ingress test — **REVOKE** via Settings → API keys when done).
- 5 orphaned connections from the purge (supplier soft-deleted, no connection-delete API) — UI hides them.

## Ingestion methods — status
- ✅ **Browser upload** (CSV) — delivered + verified at webhook.site. `POST /api/orders/upload` multipart (file+supplierId).
- ✅ **REST API ingress** — delivered + verified. `X-ProcuLink-Key` + `POST /api/ingress/{slug}/orders`.
- ⏸ **Inbound email** — wired + token-gated (401 w/o token). `POST /api/inbound-email/postmark` (`X-Postmark-Server-Token`). Address `orders@{slug}.proculink.eu`. Needs a real email sent OR the webhook token.
- ⏸ **SFTP / S3 poll** — config endpoints live (`/api/settings/sftp`, `/api/settings/s3`). Needs creds + a dropped file + poll cycle.

Downstream recipe (any method): resolve `POST /api/orders/{id}/resolve {saveMappings, lineResolutions:[{lineNumber,supplierItemCode}]}` → transform `POST /api/orders/{id}/transform {}` (202, auto-delivers when AutoDeliver=true) → poll `GET /api/orders/{id}` for `delivered`.

## Pending (task IDs in the session task list)
- **#107 / #108** — Pass-3 audit (FE mobile/a11y/order-review/settings + BE correctness/security/multitenancy/validation/invoice-self-review) re-running as workflow, then fix confirmed findings the usual way (parallel surgical agents → tsc+BE build → commit per-repo → push → re-verify).
- **#109** — Invoice stuck "parsing": seeded UBL `b2c1aaf1` never left "parsing", no Hangfire failure. Prime suspect: ParseInvoiceJob not enqueued on upload OR Worker doesn't process invoice parse OR parse exception swallowed without status=failed. Investigation agent running.
- **#110** — Methods 3 & 4 end-to-end: BLOCKED on a user trigger (test email / dropped file — a prod secret I won't extract).
- **#111** — this doc.

## How to resume
1. Read this doc + the 3 memory files above + STATUS.md.
2. `git log --oneline -8` both repos to confirm the commits landed.
3. Check pass-3 + invoice-agent results (task notifications / their `.output` files).
4. Fix pass-3 findings; resolve the invoice-parse root cause; ask the founder for an email/SFTP trigger to close methods 3 & 4.
5. Revoke the `plk_` test key; optionally re-wipe the test fixtures.
