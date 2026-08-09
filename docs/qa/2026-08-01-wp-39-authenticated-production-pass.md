# WP-39 — Recorded authenticated production pass

**Date:** 2026-08-01
**Target:** `https://proculink.eu` (Vercel) + `https://api.proculink.eu` (Railway)
**Frontend main:** `1852590` · **Backend main:** `7aa830a` (record written against `8d001bf`, i.e. after PR #133 merged)
**Packet:** WP-39, the master plan's "biggest evidence gap" — every prior UI finding was code- or mock-derived because no session had ever authenticated against production.

Redaction note: both repositories are **public**. Third-party company names, tenant/organisation and order
identifiers, billing identifiers, part numbers and delivery tokens are withheld below, or replaced with
obviously synthetic placeholders. Nothing in this file is a credential.

---

## 1. How the session was obtained

This matters, because the previous attempt failed here and the project docs record a method that no longer works.

- **Not used:** `clerk.sessions.createSession`. Clerk rejects it in production (`request_invalid_for_environment`);
  Backend API session creation is development-only. Already documented in the frontend `CLAUDE.md` §1.5.
- **Not available:** the Clerk **sign-in-token** flow described in `CLAUDE.md` §1.5 (2026-06-03) requires a
  production secret key (`sk_live_…`). It could not be obtained this session:
  - `vercel env pull --environment=production` writes every encrypted value as an **empty string**
    (`CLERK_SECRET_KEY=""`, 19 bytes on that line). It does not write `[SENSITIVE]` as the memory note suggests —
    it writes nothing at all. Same for `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` and `NEXT_PUBLIC_API_BASE_URL`.
  - The backend Railway service holds only `Clerk__Authority` (the JWKS issuer). There is no Clerk secret key
    on the API service, so the backend cannot mint one either.
  - The local `.env.local` holds a **test** instance pair (`pk_test…`/`sk_test…`), not the production instance.
    Production is `pk_live_…` with FAPI host `clerk.proculink.eu`.
- **Used:** an **existing real signed-in browser session** in the operator's own Chrome, driven through the
  browser automation surface. No credentials were entered, no account was created, no password was typed.

**Consequence for future sessions:** the fastest authenticated-production path today is a real browser session,
not a minted token. Record this in `CLAUDE.md` §1.5 alongside the sign-in-token note, which is currently the only
documented method and is unusable without the founder pasting `sk_live_…` into the session.

**Authenticated API evidence technique.** All API probes below were issued *inside the page*, via
`await window.Clerk.session.getToken()` followed by `fetch(...)` in the same expression. The JWT never left the
browser and was never printed. Status codes, timings and response bodies are verbatim.

### Session facts

| Fact | Value |
|---|---|
| Organisation | the founder's own org (name and id withheld) |
| Plan | `growth`, `accountStatus: active` |
| Orders in org at start | 25 (`delivered` 3, `pending_review` 21, `ready` 1) |
| Suppliers | 25 of 30 |

---

## 2. Viewport limitation — WP-39's AC is only partly met

WP-39's acceptance criterion asks for **1440 px and 390 px** captures of every screen.

- **390 px: achieved and genuine.** The app was loaded into a same-origin `<iframe>` sized `390×844`
  (production CSP is `frame-ancestors 'self'`, so this is permitted). Media queries evaluate against the iframe
  viewport, so this exercises the real responsive CSS, not a simulation.
  Measured: `innerWidth 387`, `documentElement.scrollWidth 387` → **no horizontal overflow**.
- **1440 px: NOT achieved.** The automation surface reports `outerWidth: 160` and pins `innerWidth` at **1920**;
  `resize_window(1440, …)` returns success but does not change the rendering viewport. Desktop evidence in this
  record is therefore at **1920 px**, not 1440 px.

**This AC is not fully satisfied and should not be marked complete on my evidence alone.** A Playwright run with
`viewport: { width: 1440 }` would close it — but Playwright cannot reuse the browser session, so it needs either
`sk_live_…` for the sign-in-token flow or a manually signed-in persistent profile.

Screenshots were captured to the automation tool's local store, **not committed**. See §8.

---

## 3. What was exercised end to end

A complete new order was pushed through the full pipeline on production.

| Step | Result | Evidence |
|---|---|---|
| Upload | CSV accepted, routed to "ProcuLink Sample Supplier" | file injected via `DataTransfer` (the file picker is sandboxed) |
| Pre-upload read | `Detected: CSV · 65%`, `PO WP39-QA-001 · 1 lines` — before any upload | client-side parse |
| Upload → order | Redirected to `/inbox/00000000-0000-0000-0000-00000000ffff` (id replaced with a placeholder) — a **real** order id | not the hardcoded `/inbox/008412` that Group I pass 10 fixed |
| Parse | **Completed in < 10 s**; `status: pending_review`, `poNumber: WP39-QA-001`, 1 line | polled `GET /api/orders/{id}` |
| Review | `1 blocker · Needs a supplier code`; `Cannot transform: lines 1 still need review.` | on-screen |
| Mapping | Entered `WP39-SUP-001` inline → blocker cleared, stage advanced to `4 Prepare` | on-screen |
| Transform | Output built; live CSV/JSON/XML/cXML/UBL/X12 preview rendered real values | on-screen |
| Send confirm | Real confirmation dialog (quoted in §4.2) | `[role=alertdialog]` |
| Deliver | **Failed** — `delivery_failed`, `failureCause: supplier_endpoint_not_found`, HTTP **404** | `GET /api/orders/{id}` + passport |
| Failure recovery UI | WP-19 panel rendered correctly (§5) | on-screen |

The delivery target was verified **before** sending to be the project's own test bin
(`https://webhook.site/<bin-token>/<path>`), not a real supplier endpoint.

---

## 4. Findings

Severity: **P1** = damages operator trust or risks a wrong outbound action · **P2** = visible defect · **P3** = polish.

### 4.1 — P1 · The audit trail reports passing checks as "validation issues"

On a **clean, successfully delivered** order, the Audit trail renders a green `✓ Validated` node labelled
**`3 validation issues`**. Confirmed on production, not inferred.

`GET /api/orders/{id}/passport` returns, verbatim:

```json
[
 {"code":"invariant.quantity_positive","lineNumber":1,"message":"Line 1: quantity 2 is valid.","severity":"error"},
 {"code":"invariant.unit_price_valid","lineNumber":1,"message":"Line 1: unit price 376.2 is valid.","severity":"warning"},
 {"code":"invariant.po_number_present","lineNumber":null,"message":"PO number is present.","severity":"error"},
 {"code":"invariant.currency_present","lineNumber":null,"message":"Currency is set (EUR).","severity":"error"}
]
```

Every message states the check **passed**; three carry severity `error`.

Three independent defects compose:

1. `ProcuLink.Api/Services/PassportService.cs:128` projects `LineNumber, Severity, Code, Message` and **drops
   `Status`** (`"pass"|"fail"`) — the only field that distinguishes outcome. The sibling DTO over the same entity
   does carry it (`ProcuLink.Api/Contracts/AcceptanceProfileDto.cs:19`).
2. `ProcuLink.Api/Contracts/PassportDto.cs:96-101` has no slot for `Status`, so the loss is structural.
3. Frontend `src/components/bridge/OrderPassport.tsx:132` falls back to `severity === "error"` because its
   `v.passed === false` guard references a field the backend never sends — frontend type drift at
   `src/types/procurement.ts:653-659`, invisible to the compiler because every field is optional.

Producer behaviour is intentional (`ProcuLink.Api/Services/InvariantValidator.cs:6-17` — emit "checks performed"
so a rule-less order cannot show a vacuous green "Passed"). **Do not fix by suppressing passing rows** — that
resurrects the trust hole those invariants were built to close. Minimal fix is additive: add `Status` to the
passport DTO, project it, and filter on `status === "fail"` in the frontend.

No test covers this. `ProcuLink.Api.Tests/Services/InvariantValidatorTests.cs:35-44` asserts `Status` on the
all-pass case and deliberately skips `Severity`; every `Severity` assertion in the suite is on a *failing* row.
The frontend passport test seeds `validationResults: []`
(`src/components/bridge/OrderPassport.downloadSent.test.tsx:69`), so the classifier never executes under test.

### 4.2 — P2 · An already-delivered order still presents as sendable (UI only — no duplicate-send risk)

Opening `/inbox/{id}` for an order whose API status is exactly `"delivered"` renders, simultaneously:

- a `● Delivered` status pill,
- the page headline **`Review and send this order`**,
- a stage strip sitting at **`4 Prepare`** with `5 Send` greyed — i.e. the strip says the order never shipped,
- a right rail reading **`Ready to send — No open issues — every required field is filled and checked.`**,
- an **enabled** `Send to supplier` button (`button.disabled === false`, no `aria-disabled`), in both the desktop
  and `.plk-mobile-send` variants.

The send gate is driven by **open issues, not by status**. Proof from the same session: on a `pending_review`
order with one blocker the button reads `Send to supplier · 1 to fix` and is `disabled: true`. Clear the blocker
and it enables. Nothing consults "already delivered".

**I first recorded this as a P1 duplicate-send risk. That was wrong, and the code refutes it.** I did not click
through on the delivered order, so I traced what would have happened instead. Nothing would have been sent:

- The click only opens a dialog (`OrderWorkshop.tsx:1093-1096` → `setShowConfirm(true)`), and the dialog's
  confirm button is gated on a **mandatory checkbox** — `Everything checks out. Send to {supplierName}.`
  (`src/components/bridge/review/ConfirmDialog.tsx:128`, required by default via
  `src/components/bridge/review/confirmPolicy.ts:36`).
- The confirm handler then **short-circuits before any API call** —
  `src/components/bridge/review/hooks/useSendFlow.ts:129-135` returns early when `current.status === "delivered"`,
  showing `Delivered to supplier. The audit trail has been updated.` **Zero network requests.**
- Even if that were bypassed, the backend refuses at three further layers:
  `POST /api/orders/{id}/transform` → **409** via an atomic claim whose `TransformableFrom`
  (`ProcuLink.Core/Constants/OrderStatusMachine.cs:640-641`) excludes `Delivered`
  (`ProcuLink.Api/Controllers/OrdersController.cs:1698-1722`, claim precedes job enqueue, so it cannot be raced);
  `POST /api/orders/{id}/redeliver` → **400** via `RedeliverableFrom` (`OrderStatusMachine.cs:470-471`)
  at `OrdersController.cs:2092-2100`; and `DeliveryService.DispatchArtifactAsync`
  (`ProcuLink.Infrastructure/Services/DeliveryService.cs:235-258`) returns a benign `ClaimLost` no-op, writing
  **no `DeliveryAttempt` row** (the insert at `:474-492` is only reached after the claim succeeds).

So the defect is **presentational**: the order reads as un-sent to any operator who did not personally send it in
that browser tab. Root cause is that `canSend` (`OrderWorkshop.tsx:860-862`) and the label builder
(`src/components/bridge/workshop/sendBarLabel.ts:56-103`) **never read `order.status`** — `delivered` is absent
from `PROBLEM_STATUSES` (`src/components/bridge/problem/problemCopy.ts:24-33`), and `crossed` is session-local
state (`useSendFlow.ts:43`), so it is always `false` after a reload.

Coverage caveat worth acting on: the two backend tests that pin this —
`ProcuLink.Api.Tests/Integration/DeliveryConcurrencyPostgresTests.cs:254-286`
(`Redeliver_OnAlreadyDeliveredOrder_IsNoOp`, asserts `dispatcher.Calls == 0` and a single original attempt row) and
`ProcuLink.Api.Tests/Integration/TransformFailedRecoveryPostgresTests.cs:167-185` — are `[DockerRequiredFact]` and
**skip silently where Docker is absent**. Confirm they actually execute in CI rather than being reported green
while skipped. On the frontend there is **no** test pinning the `useSendFlow.ts:131` short-circuit at all.

### 4.3 — P1 · After a delivery failure the Issues rail still says there is nothing wrong

Immediately after the live 404, the same screen showed the failure panel **and**, in the right rail:

```
Nothing to fix
Ready to send
No open issues — every required field is filled and checked.
```

while the header badge read `Couldn't send` and the API read `status: delivery_failed`.
The header also still reads `Review and send this order`.

This is exactly the gap **WP-36** was written to close ("every failure has an obvious action"), and WP-36 has
**no repository trace in either repo** — `git log --all --grep='WP-36'` returns zero commits. WP-36 is unlanded and
this is live evidence that it is needed. *(WP-36 is another chip's scope — reported, not touched.)*

### 4.4 — P2 · Raw HTML from the supplier endpoint is spliced into operator-facing copy

The failure message shown to the operator, verbatim from `GET /api/orders/{id}`:

```
The supplier's endpoint was not found (HTTP 404). The delivery address in this supplier's delivery settings has
most likely moved or contains a typo — confirm the address with the supplier, correct it there, then send the
order again. The supplier's endpoint said: <!-- Tip: Set the Accept header to application/json to get errors in
JSON format. --> <!DOCTYPE html> <html> <head>     <title>Error: Token &quot;<bin-token>&quot; not found -
```

The first sentence is good copy. Everything after `The supplier's endpoint said:` is an unescaped HTML document
truncated mid-tag. The passthrough needs a length cap and a content-type check — echo the body only when it is
text/plain or JSON, otherwise say the endpoint returned an HTML error page.

### 4.5 — P2 · The seeded demo delivery target is dead, so the sample path 404s

`GET /api/suppliers/{sampleSupplierId}/delivery-config` returns `protocol: http`, `autoDeliver: true`, pointing at
`https://webhook.site/<bin-token>/<path>`. That bin has expired — it answers
`Error: Token "<bin-token>" not found`.

Any operator who follows "ProcuLink Sample Supplier" to the end of the loop today gets a 404. This is the concrete,
current form of the audit's journey #1 complaint ("the sample cannot complete the loop its own docstring
advertises"). A demo endpoint that expires is not a stable demo — this needs an owned, permanent echo endpoint.

The same response carries `revisionGoverned: true`, `activeRevisionVersionNo: 6`, and
**`liveMatchesActiveRevisionDelivery: false`** — the live delivery config has drifted from its governed revision.

### 4.6 — P2 · `supplierResponse.outcome` is `"unknown"` after a definitive 404

The passport records `deliveryAttempts[0].status: "failed"`, `responseCode: 404`, and
`finalStatus: "delivery_failed"` — but `supplierResponse.outcome` is `"unknown"`. A 404 is not an unknown outcome.

### 4.7 — P2 · Passport renders a resolved mapping as red "unresolved"

The Audit trail's `MAPPING DECISIONS` row renders `1  —  →  unresolved` with a `DETERMINISTIC 100%` chip, for a
line the API reports as fully resolved:

```json
{"lineNumber":1,"buyerItemCode":"00010","supplierItemCode":"SUP-ITEM-0001","source":"deterministic","confidence":1}
```

Cause is field-name drift, same family as §4.1: `src/types/procurement.ts:663-664` declares `buyerCode` /
`supplierCode`; the API sends `buyerItemCode` / `supplierItemCode`. At
`src/components/bridge/OrderPassport.tsx:244`, `d.supplierCode` is `undefined` → falsy → the literal string
`"unresolved"` renders in red `#B43838`.

Note the *timeline* node is correct (`1 lines mapped`) because it reads `d.source`, which is sent under the name it
expects. Only the detail row is wrong.

### 4.8 — P2 · `/operations/health` contradicts itself

On one screen, at one moment:

- tile `21 Awaiting your review`,
- and, below, **`No orders awaiting operator review.`**

Additionally `29 Open issues` matches no obvious aggregate: orders = 25, `pending_review` = 21, total lines = 45,
sum of `unresolvedCount` = 40.

**Refuted, do not report as a bug:** I initially read `29 Open issues` against `/operations/exceptions`'s
`33 shown` as an inconsistency. It is not — the exceptions page defaults to the **All** tab; switching to **Open**
shows exactly **29**. Scope difference, correctly implemented.

Genuine but minor: 2 open `unrouted_order` exceptions exist while **zero** orders hold `unrouted` status. The page
does explain this (`Fixing the cause clears the issue the next time the order is reprocessed`), so it is stale-by-design
rather than wrong.

### 4.9 — P2 · `/api/billing/status` is slow and uncached on a hot path

Measured on production, same token, same tab:

| Call | Latency |
|---|---|
| `/api/billing/status` #1 | **9,323 ms** |
| `/api/billing/status` #2 | **5,340 ms** |
| `/api/billing/status` #3 | 728 ms |
| `/api/suppliers` (control) | **133 ms** |

Highly variable, not a one-off cold start — but **not** constant either. I initially called it "reproducible, not a
cold start" after two samples; the third sample refutes that. The accurate statement is: p99 on this endpoint is
seconds, on a path the dashboard, upload page and settings all read.

Code path (`ProcuLink.Api/Services/StripeBillingService.cs:130-220`) makes **no** synchronous Stripe or third-party
call — `billingInterval` is config-only (`:236-237`). It issues 4 sequential DB queries, of which one is a redundant
re-read: `MarkPilotExpiredIfNeededAsync` loads the org tracked at `:132`/`:810-817`, then `:134` loads the same row
again `AsNoTracking`. For a paid plan the first load does nothing (`:764` early-returns for non-Pilot).
There is **no caching anywhere in `ProcuLink.Api`** — and `CanProcessOrdersAsync` / `CanAddSupplierAsync` /
`CheckOrderLimitAsync` / `CheckSupplierLimitAsync` (`:251-275`) each re-run the whole of `GetStatusAsync`, so every
billing-gated endpoint pays the same cost.

Two unambiguous code-side fixes regardless of the DB question: drop the duplicate org read, and skip
`MarkPilotExpiredIfNeededAsync` entirely for non-Pilot plans. Root-causing the seconds itself needs
`EXPLAIN ANALYZE` / `pg_stat_statements` against production Postgres — the index the count needs does exist
(`ProcuLink.Infrastructure/ProcuLinkDbContext.cs:514-515`, migration `20260530202549`).

### 4.10 — P2 · Two different stage vocabularies ship at once

- `/inbox` legend: `Pipeline · 1 Parse · 2 Normalize · 3 Validate · 4 Transform · 5 Deliver`
- `/inbox/{id}` stage strip: `Read · Check · Verify · Prepare · Send`

Both render in the same session, for the same five stages. `CLAUDE.md` §9 locks the first set. Either the order
screen is drifting or §9 is stale; they cannot both be right.

### 4.11 — P3 · Field counts disagree within one screen

On the order screen the header badge reads `13/13 mapped` while the left panel reads `11 fields` and its chips read
`All 11` / `Mapped 11`. After adding the supplier code the left panel became `12 fields`; the badge stayed `13/13`.

### 4.12 — P3 · Three pages ship the generic `<title>`

`/operations/health`, `/operations/exceptions` and `/upload` all serve
`<title>ProcuLink — Connecting Procurement</title>`. `/inbox` (`Inbox — ProcuLink`), `/bridge`
(`Dashboard — ProcuLink`) and `/inbox/{id}` (`Order Review — ProcuLink`) are correct, so this is missing per-page
metadata, not a global setting.

### 4.13 — P3 · Dashboard greeting violates the locked spec

`/bridge` renders `Good afternoon, Dim · Saturday, August 1 · 21 blockers need you first`.
`CLAUDE.md` §12 lists under "refuse to build": *"Good morning, Maria" greetings on the dashboard — operators want
the queue*. The blocker count beside it is useful; the greeting is the part the spec forbids.

### 4.14 — P3 · Delivery success rate reads 100% over a window with zero deliveries

`/bridge` shows `Delivery · 100% success rate · 0 failed` (sub-label "last 30 days") beside a Throughput card
reading `0 delivered · last 30 days`. 100% of zero is not a trust signal. All 3 delivered orders are dated
2026-07-02, i.e. at/just outside the 30-day edge.

### 4.15 — P3 · Pre-hydration flash shows a placeholder workspace

On first paint of `/inbox`, the chrome renders `YW / Your workspace` before Clerk resolves the real organisation
name. Brief, but it is an identity element flashing wrong data.

---

## 5. Packet claims CONFIRMED on production

Each was checked against the exact string or HTTP behaviour the packet's diff promises.

| Packet | Claim | Verified |
|---|---|---|
| WP-19 (BE) | `OrderDto` carries `failureCause` + `retryAfterSeconds` | Both keys present. `supplier_endpoint_not_found` / `null` on the live 404 |
| WP-19 (FE) | Cause-specific failure panel, supplier-attributed | `We couldn't reach this supplier` · `The address we have for ProcuLink Sample Supplier doesn't exist any more.` |
| WP-19 (FE) | Primary is the fix, retry demoted | Primary `Check the delivery settings`; secondary `Try sending now` |
| WP-19 (FE) | No invented retry number | `retryAfterSeconds: null` and **no** "wait N seconds" phrase rendered |
| WP-24 | `/operations/health` tiles deep-link to narrow filters | All 10 tiles carry distinct `?status=…`; `Overdue → /inbox?sort=oldest` |
| WP-24 | Overdue helper string | `Sorted oldest first — we can't filter by age yet.` verbatim |
| WP-24 | Unknown filter is not silently ignored | `/inbox?status=notarealstatus` → `We don't recognise that filter, so this is every order.` + `Clear` |
| WP-24 | Attempt history reachable from the failure | `See every attempt` present on the failed order |
| WP-25 | Inbox chips renamed | `All orders · Needs review · Ready to send · Queued to send · Delivered · Failed`; **`Normalized` appears nowhere** |
| WP-26 | Sidebar is four items | `Orders · Suppliers · Activity · Settings` |
| WP-26 | Activity hub tabs | `Overview · Deliveries · Issues` |
| WP-29 | Dashboard/inbox count contract | Dashboard `Ready to send 1` = inbox chip `Ready to send 1` |
| WP-29 | Pipeline legend on desktop | `Pipeline 1 Parse 2 Normalize 3 Validate 4 Transform 5 Deliver` |
| WP-29 | Per-row send action | `Prepare output` button on the `ready` row |
| WP-29 | Accessible stage naming | Mobile cards render `Step 4 of 5 · Transform`, `Step 3 of 5 · Validate` |
| WP-31 | 16 px inputs, 44 px targets at 390 px | Measured in a true 390 px viewport: `fontSize: 16px`, `height: 44px`; no horizontal overflow |
| WP-10 | Upload redirects to the returned order id | Landed on `/inbox/b56ddb85-…`, not a hardcoded id |

Unauthenticated sweep (separate evidence run):

- 64/64 public routes → `200`, zero error bodies. Control probes `/this-route-does-not-exist-wp39` → `404` confirm
  the check is not vacuous.
- `sitemap.xml` → `200`, valid XML, 63 `<url>` entries, zero dead entries. `robots.txt` carries a `Sitemap:` line
  and disallows every protected prefix.
- Every protected API endpoint returns `401` with an empty body and `www-authenticate: Bearer`.
- No Swagger/OpenAPI surface in production (`/swagger`, `/openapi/v1.json`, `/scalar/v1` → `404`).
- Protected routes signed-out → `307` to `/sign-in?redirect_url=…` with `X-Clerk-Auth-Reason: session-token-and-uat-missing`.

---

## 6. Prior claims STRUCK by this pass

| Claim | Source | Evidence it is no longer true |
|---|---|---|
| "Passport shows a file key, not the bytes, and **no SHA-256**" (journey #12) | `AUDIT-2026-07-27-FULL-VERDICT.md:132` | The Audit trail renders `FINGERPRINT OF THE FILE WE GENERATED (SHA-256)` with the real hash `a65920354e0af1fec4657f4cf28fb2f26837b49d61f6042e388f064b33937dd8`. "Not the bytes" **still stands** — `No stored copy of what this attempt sent.` |
| "Production needs a separate `ProcuLink.Worker` Railway service … uploaded orders remain `parsing` for 30+ seconds" | frontend `CLAUDE.md` §1.5 (2026-06-03) | A CSV uploaded this session reached `pending_review` in **under 10 s**. Parse jobs are executing on production. This §1.5 paragraph is stale and should be corrected. |
| `orderLimit: 100000` / `supplierLimit: 30` on a `growth` plan is a billing-ladder defect | **my own earlier reading this session** | **Refuted.** These are designed per-org admin overrides: `StripeBillingService.cs:137-138` → `PlanConstants.GetEffectiveOrderLimit` (`:134-135`), fed by `organisations.order_limit_override` / `supplier_limit_override` (`ProcuLinkDbContext.cs:231-234`), written only by `AdminController.SetOrganisationLimits` (`:367-404`, `[AdminOnly]`, audited). Documented intent at `PlanConstants.cs:128-133`. |
| `isTrialExpired: false` with a past `trialEndsAt` is a bug | **my own earlier reading this session** | **Refuted.** `StripeBillingService.cs:144-145` guards on `plan == PlanConstants.Pilot`, so it is unconditionally false on paid plans by construction. `:206` deliberately returns the historical column (`Organisation.cs:20-21`). |

**Real consequence of the override that is worth a decision** (not a defect): with `orderLimit = 100000 ≥` the top
tier's included volume, `BestPriceOverageOrders` takes the `PlanConstants.cs:248-255` branch, so this tenant accrues
**€0 overage** and `nearLimit`/`atLimit` can never fire. Also, `/upload` displays `Orders 0 / 100000` next to
`Growth Plan`, which reads as a pricing inconsistency to anyone who sees it.

---

## 7. Not covered — do not read this record as covering them

Stated plainly so the ledger is not over-credited.

- **1440 px captures.** Not obtainable through this surface (§2). WP-39's AC is **not** fully met.
- **12 journeys, individually scored.** This pass covered the ingest→deliver spine and the failure path in depth.
  Journeys not exercised: #3 recurring PO / saved knowledge, #6 new supplier setup, #7 custom supplier output,
  #11 replay after config change. #1 sample order was observed only through the dead demo endpoint (§4.5).
- **A live re-send attempt on a delivered order.** I traced the guards rather than clicking (§4.2), so the 409/400
  refusals are verified in code and in Docker-gated tests, not observed on production.
- **WP-19's other failure causes** (401/403/429 supplier rejection). Only `supplier_endpoint_not_found` occurred
  naturally; the rest would need a deliberately misconfigured supplier.
- **WP-27 practice order, WP-30 confidence-chip colour parity, WP-31 dialog Escape/focus-return.** Not reached.
- **WP-38** (SFTP host keys): no repository trace in either repo — `git log --all --grep='WP-38'` finds only the
  plan document itself. Not landed. *(Another chip's scope — reported, not touched.)*

---

## 8. Artifacts and residue

- **One order was created on production and left in a failed state:**
  `00000000-0000-0000-0000-000000000000`, PO `WP39-QA-001`, supplier "ProcuLink Sample Supplier",
  `status: delivery_failed`. It is clearly marked as QA data. Delete it when convenient — it currently counts
  toward the org's inbox and the "22" order badge.
- **One real outbound HTTP POST** was made, to the expired webhook.site test bin. It 404'd. No real supplier was
  contacted at any point.
- **Screenshots were captured but deliberately NOT committed.** Both repositories are public, and the captures
  contain real third-party buyer names, PO numbers, and a buyer's street address and contact name pulled from a
  real PDF. WP-39 asks for them to land in `docs/design-system/current-ui-screenshots-2026-06-26/`; doing that
  publishes third-party commercial data to a public repo. **Founder decision needed** — options are: make the
  captures from a scrubbed demo org, redact before committing, or move the screenshot directory to a private
  location. That directory is also outside this chip's file-disjoint scope.

---

## 9. Suggested follow-ups

Ordered by value, all small.

1. Add `Status` to the passport validation DTO and filter on it in the frontend (§4.1). Additive, no behaviour change
   for the producer.
2. Add a `delivered` arm to `canSend` (`OrderWorkshop.tsx:860-862`) and `sendBarLabel.ts:56-103` so the control and
   headline reflect status, not just open issues (§4.2). Purely presentational — the transmission guards are
   already correct at four layers.
3. Confirm the `[DockerRequiredFact]` integration tests actually run in CI rather than skipping silently (§4.2),
   and add a frontend test pinning the `useSendFlow.ts:131` delivered short-circuit.
4. Rename `buyerCode`/`supplierCode` in `src/types/procurement.ts` to match the API, and delete the dead
   `passed` field (§4.7, §4.1). Both drifts exist because the passport DTO is not validated at the boundary — a
   runtime schema check here would have caught both.
5. Make the Issues rail reflect a delivery failure instead of `Nothing to fix` (§4.3). This is WP-36's job.
6. Cap and content-type-check the supplier error passthrough (§4.4).
7. Replace the expired webhook.site demo target with an owned permanent echo endpoint (§4.5).
8. Drop the duplicate org read and the non-Pilot `MarkPilotExpiredIfNeededAsync` call (§4.9).
9. Correct frontend `CLAUDE.md` §1.5: the worker gap is closed, and add "real browser session" as the working
   production-auth method (§1, §6).
