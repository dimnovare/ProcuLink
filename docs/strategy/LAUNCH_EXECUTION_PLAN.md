# ProcuLink — Launch Execution Plan (final push to 2026-06-09)

**Author:** Claude (Opus) · **Created:** 2026-06-07 · **Source of truth:** `docs/strategy/PROD_LAUNCH_AUDIT.md` (read in full) + live code, **not** stale status docs.
**Mandate (founder, 2026-06-07):** build the tool for the **whole world** — stable, reliable, fast; UI/UX simple yet professional/enterprise-grade; every link works; tackle **everything** in the audit; **one full run-through with a full production test**, as many agents as possible; **do not introduce new bugs**; if unsure, ask.

> This plan is the operating contract for the push. Every item in PROD_LAUNCH_AUDIT.md is accounted for here — either scheduled into a wave, marked **already-done**, or placed in the **explicitly-deferred** bucket (with the audit's own reasoning). Nothing is dropped silently.

---

## ✅ Completion tracker (live — last updated 2026-06-07)

Legend: `[x]` done + verified · `[~]` partial · `[ ]` in progress / not done · `[—]` deliberately deferred or flagged (reason).

**Launch-blocker waves**
- [x] **Wave 0** — reconcile (read-only; ~7 items already-fixed, no redo)
- [x] **Wave 1** — honesty / dead-CTA / capability over-claims (frontend `432c4ea`)
- [x] **Wave 2** — correctness: Npgsql pool ceiling · ingress idempotency · AI 50-line chunking · stuck-order requeue + `requeue_count` migration (`0da39cf`)
- [x] **Wave 3** — security P1 batch: SSRF connect-time revalidation (incl. ERP) · global exception handler/ProblemDetails · `azp` required · CORS wildcard removed · tenant-resolution unified · billing on `IBillingService` · rate-limit policies applied · path-traversal containment (`3c789b6`)
- [x] **Wave 4** — reliability: Worker Sentry + `WorkerHealthAlertJob` · deep `/health` vs `/health/ready` · DataRetention sweep (dormant) · migrate-fail-loud (`5013fd8`/`961d5af`)
- [x] **Wave 5** — billing: Stripe `AppInfo` + test-mode QA + `stripe-go-live-runbook.md` (`bbe1dd5`)
- [x] **Wave 6** — UX: pricing 6→3 + ROI recommender + Distributor upsell · wizard a11y · `next` pinned + engines · explicit `ExtractionModel` (`4af5dd5`)
- [x] **Wave 7** — full prod test: all 7 upload formats parse live · HTTP/webhook delivery → `delivered` · API-key ingress · SFTP/FTPS/SMTP + SFTP/IMAP/S3 + inbound-email proven
- [x] **Wave 8** — clean-env regression gate (**990 green**) · SPF+DKIM+**DMARC** complete (verified resolving) · consolidated ops runbook `docs/deployment/launch-operations-runbook.md` (`03b24fa`)

**Frontend (design + launch polish)**
- [x] Design-primitive migration — 12 in-app pages → `PageShell/PageHeader/Card/MobileListRow/UnifiedStatusBadge` + green-primary (`004c7d0`)
- [x] Per-page SEO metadata + self-canonical (/how-it-works,/formats,/pricing) · CSP `frame-ancestors 'self'` · og-image 1.99 MB→119 KB · honest hero 9/6/6 (`2d7ab38`)

**Wave D — backend (this push)**
- [x] **W4 / P2-3** Redis-ready HMAC nonce (config flag) + API **HSTS/nosniff** headers (`0b34ff7`)
- [x] **§1.1.F / §2.3.2 / §2.3.3** EmailPolling indexed flag (+migration+backfill) + AI-candidates / SFTP / S3 partial indexes (`eb24aa6`)
- [x] **A1.3 / D3** DESADV upload → 501 (was misleading 202) (`d6c44ac`)
- [x] **R1 / R2 / R3** (Waves 0-6) · **P1-1..P1-5, P2-1/2/4** · **P1-3** azp + Clerk prod cutover · **B1-B8** drift — verified done
- [ ] **W2** order-status transition table — building now
- [x] **W3** R2/DB GDPR per-order erase — `IDataErasureService` + admin `DELETE /organisations/{org}/orders/{id}`; FK-safe confirmation erase + R2 blobs; adversarially reviewed (caught+fixed a RESTRICT-FK abort + a confirmation data-leak). 993 tests green.
- [ ] **W1** OrderService decompose behind the `IOrderService` facade — building now
- [ ] **§2.5** Postgres RLS (defence-in-depth) — building now
- [—] **W6** split `api-client.ts` — collides with the active frontend chips; DX-only, zero customer value → post-launch
- [—] **W5** consolidate dual retry schedulers — refactors currently-correct code; pure risk → post-launch
- [—] **§1.4** denormalize `line_count`/`total_value` + partition audit/passport — audit "redesign-later"; drift risk; marginal at pilot
- [—] Neon **pooled endpoint** + enable **DataRetention** sweep — founder env/Railway (connection string + `DataRetention:Enabled=true`)
- [—] **P1-6** Postmark signature (needs the CF inbound Worker to sign) · SchemaFingerprints rename · phantom-migration cleanup — dangerous/cosmetic pre-launch
- [—] **EDIFACT** INVOIC/DESADV (EdiFabric licence) · cross-org mapping library / i18n / PEPPOL AP — post-launch roadmap

Per-item plans + risk + tests for the four in-progress items: **`docs/strategy/WAVE_D_BACKEND_REMAINING.md`**.

---

## 0. Already fixed since the audit (DO NOT redo — verify only)

Wave 0 re-verifies each before any agent touches the area, to avoid re-introducing bugs:

| Audit item | Status now | Evidence |
|---|---|---|
| UX#3 / P0#6 — `.x12` rejected though advertised | **FIXED + live-verified** | `OrdersController` allowlist + frontend accept; live upload parsed `LIVE-X12-2026` |
| A1#4 — Connectors "all Connected" | **FIXED** | live shows honest "available"/Connect |
| Stale `UnsupportedFileFormatException` ".csv,.xlsx" | **FIXED** | now lists real set |
| UX terminology — `ready` reads as "Ready to send" | **PARTIAL** | `ready`→"Normalized" in InboxView + UnifiedStatusBadge; copy sweep still owed for `DeliveryConfigEditor` snake_case + "supplier flows" |
| Design-system unification | **FOUNDATION shipped** | `layout/` primitives + `UnifiedStatusBadge` + `11-unified-page-rules.md`; page migrations still owed |
| Distributor Stripe "doesn't exist" blocker | **STALE — product exists, self-serve** | `price_1Tcq7Y…` €1,499/mo active (test mode) |
| All 7 import formats parse | **PROVEN live** | CSV/XLSX/PDF/cXML/UBL/EDIFACT/X12 in prod inbox |

---

## 1. How we run it (orchestration model — "as many agents as possible", zero new bugs)

**Engine:** one `Workflow` per wave. Inside each wave:
1. **Fan-out (parallel, worktree-isolated agents)** — one agent per independent fix. Worktree isolation is mandatory (prior chip-collisions corrupted shared `.next`/EF state).
2. **Verify-then-fix** — every agent first re-confirms the audit claim is *still true in current code* (cites file:line), then fixes, then **adds/updates tests**. If already fixed → no-op + report.
3. **Adversarial verify** — security/correctness changes get a second, independent agent that tries to *refute* the fix (≥2 reviewers for P0/P1/security).
4. **Integration gate (serial, me)** — merge green branches one at a time; after each wave: `dotnet test ProcuLink.slnx` (887+ tests) **and** `bun run build` **must be green**. Never push red.
5. **Deploy + live QA** — push → Railway/Vercel deploy → I drive the live site (browser + API) to confirm. Evidence captured per item.
6. **No-new-bugs discipline** — the full backend suite + Playwright e2e (mock path) stay green wave-over-wave; any regression blocks the wave.

**Cut rule for June 9:** Waves 0–5 + 7 (prod test) + the honesty/UX-trust slice of 1 & 6 are the launch gate. The **DEFER** bucket (§4) is *not* forced into 2 days — doing risky hot-path refactors right before launch is exactly what "no new bugs" forbids (and what the audit explicitly says not to do). See decision Q1.

---

## 2. The waves (every audit item mapped)

### Wave 0 — Reconcile current state (read-only, ~6 parallel readers)
Verify the §0 "already fixed" list + produce the *true* open-item set (the audit is dated 2026-06-06; several items moved). Output drives the exact scope of Waves 1–8. **No code changes.**

### Wave 1 — Honesty & trust (Prompt 1 + dead-CTAs A1 + UX #3/#6/#7/#8/#10 + terminology) — *frontend-heavy, low risk, highest trust ROI*
- **Hide the Inbound (Invoice/ASN) nav group** behind `NEXT_PUBLIC_INBOUND_ENABLED` (default off) — kills A1#1 (invoice download binary-vs-JSON), A1#2 (invoice accept mismatch), A1#3 (ASN DESADV `NotImplementedException` no-op) in one move. `BridgeSidebar.tsx:60-64`.
- **`/formats` + landing reconciled to true capability** (`formats/page.tsx:41-51`, `app/page.tsx:128,484`): X12 import → **keep "Supported"** (now true via `.x12`); JSON file import → "via REST API"; FTPS/SMTP/Erply/Directo delivery → **"Configurable" UNTIL proven in Wave 7**, then promote any we actually prove to "Supported".
- **Admin nav** gated on the real admin signal, not render-then-refuse (UX#6, `BridgeSidebar.tsx:68`).
- **`LimitBanner`** interpolates `PLAN_BY_ID.pilot.orderLimit` instead of "20" literal (UX#8, `BillingSection.tsx:81`).
- **Document Anatomy confidences** → real per-section or qualitative high/med/low (UX#7, `SpineReview.tsx:809-852`).
- **Terminology sweep** — `ready_to_deliver`/snake-case leak in `DeliveryConfigEditor.tsx`; "supplier flows" vs "suppliers" (`BillingSection.tsx:322`). Pick one noun.
- **Optional InvoiceController/DesadvController → 501** with clear message (defensive; they're now unreachable).

### Wave 2 — Correctness & stability (Prompt 2 + 3 + scale §1.1) — *backend, config + tested*
- **Npgsql pool ceiling** (`Maximum Pool Size` API≈30 / Worker≈20) + **Neon pooled endpoint** (P0#1, config-only).
- **Ingress idempotency** — `IngressController.ReceiveOrder` honors `Idempotency-Key` (or body+slug hash) via existing `idempotency_keys` table (P0#2). Replay ⇒ one PO.
- **Route inbox off `ListAsync`** → `ListPagedAsync`; hard-cap/retire `ListAsync` (`.Take(200)`) (§1.1.B/§2.4); stop per-row `canonical_json` JSON parse.
- **AI batch chunking** ~50 lines/call in `SuggestSupplierItemCodesAsync` (§1.1.D) so a 500–1000-line PO can't truncate or blow the 100k token cap.
- **Stuck-order requeue** — `StuckOrderDetectionJob` re-enqueues parse/transform up to a bounded count before dead-lettering (P0#3, additive `requeue_count` column).

### Wave 3 — Security P1 batch (Prompt 6 + R1/R3 + P2s) — *backend, security-critical, ≥2 adversarial reviewers each*
- **SSRF DNS-rebinding TOCTOU (P1-1)** — resolve once, **connect to the validated IP** (or re-validate at connect) across `Http`/`Smtp`/`Sftp`/`Ftps` dispatchers + `FireIntegrationTriggerJob`.
- **Global exception handler (P1-2)** — `UseExceptionHandler` + `AddProblemDetails`, RFC-7807, **no stack traces in prod**, Sentry capture confirmed.
- **Clerk `azp` (P1-3)** — reject a missing `azp` when `ValidateAudience=false`; confirm Railway runs the **prod Clerk instance** (not dev `golden-alpaca-43`).
- **CORS (P1-4)** — exact origin allowlist; no `*.vercel.app` + `AllowCredentials` in prod.
- **Auto-provision throttle (P1-5)** — rate-limit org creation per IP/email-domain.
- **Postmark inbound auth (P1-6)** — proper signature/basic-auth, rotate the token, treat as a rotated secret.
- **Unify tenant resolution (R1/P1#7)** — API-key path flows through one resolver returning the internal org UUID into `HttpContext.Items`, same as JWT.
- **Billing emit/overage onto `IBillingService` (R3)** — delete the `BillingController.cs:40` runtime cast.
- **P2s:** await `ApiKeyAuthHandler` `LastUsedAt` save (P2-2); dev path-traversal containment `fullPath.StartsWith(BasePath)` (P2-1); broaden rate limiting to transform/AI/signed-URL/webhook (P2-4).

### Wave 4 — Reliability & operations (Prompt 4 + 10 + migration + retention) — *backend, tested; flag the bigger items*
- **Worker heartbeat + alert** (Sentry/Slack/email) when no heartbeat in M min or dead-letter spikes (P0#4 — 2 prior silent-Worker incidents).
- **Prod-safe read-only queue/health view** (the dev-only Hangfire dashboard isn't enough).
- **Deep `/health`** (DB + R2 + Hangfire reachable) + a synthetic "upload→parsed" canary.
- **Alerts:** Worker-down, Neon connection-count, dead-letter spike, OpenAI spend/`MonthlyTokenLimitPerOrg`.
- **EF migrate → deliberate release step** + **fail-loud** on failure (P1#9; today fire-and-forget `Task.Run` runs on stale schema silently). *(Bigger change — see risk note.)*
- **Retention sweep** (recurring Hangfire): audit/passport >180d, idempotency >48h, delivery_attempts policy (§1.1.E).
- **R2 + DB delete path** for GDPR erasure (d-5/§1.1.E). *(Bigger — may split to immediately-post-launch; see Q1.)*

### Wave 5 — Billing live-readiness (Prompt 5) — *backend + a little frontend; test-mode now, live-swap = Q2*
- Pin `StripeConfiguration.ApiVersion` + `AppInfo` (deterministic webhook parsing).
- `plans.ts` ≡ `PlanConstants` (Integration 1,500; Distributor 1,499); `CHECKOUT_PLAN_IDS` includes Distributor.
- **Full TEST-mode simulation:** Checkout session create, Portal, webhook `ConstructEvent`, `invoice.created` **overage** (€0.50/order, period-keyed idempotency).
- **Live-swap runbook** prepared (create 4 live products+prices, swap `sk_live`/`whsec_`, repoint webhook, verify) — execution per Q2.

### Wave 6 — UX / conversion / onboarding + design unification (Prompt 8 + 9 + UX top-10 remainder + tech pins) — *frontend-heavy + a few config*
- **Pricing 6→3 visible decisions** (Pilot / Operations-anchor / Contact-sales) led by the ROI calculator's `recommendPlanByOrders`; all tiers reachable via "see all tiers" (UX#1).
- **In-app Distributor upgrade path** — `Integration.next:"distributor"` + Distributor in `BillingSection` upsell (UX#2).
- **Onboarding wizard:** `T.blue=#1E66C9` (kill banned lime `#28C55E`, UX#4); real `aria-checked` + filling dot (UX#5).
- **Delivery failure/retry UX** — latest `delivery_attempts` error + one-click retry/requeue from dead-letter; expose last attempt error on `GET /api/orders/{id}` (Prompt 8).
- **Mobile order-review lineage cue** — per-field "maps from → to" + `StandardsFieldPopover` on tap for md-and-below (UX#9).
- **Social proof** — one consented logo or a real data-sourced "N POs processed" counter on the landing path.
- **Continue page-migrations to the unified primitives** (settings, ops/health, ops/webhooks, admin, library/templates) — the prior session's follow-up; per-page build+live verify.
- **AI model story** — decide gpt-4o-mini vs gpt-5-mini; pin `Ai:OpenAI:ExtractionModel` explicitly; make code default ≡ prod config (§15/B1).
- **Reproducible builds** — pin `next` to exact `15.x.y`; add Node `engines` (audit #4/§1/§13).

### Wave 7 — FULL PRODUCTION TEST (the headline) — *I drive it; mimic every external counterpart*
See §3 for the matrix. Output: a pass/fail evidence table per capability; promote proven delivery channels to honest "Supported" on `/formats` (feeds back into Wave 1 copy).

### Wave 8 — Final hardening + regression gate + docs
- Full backend suite + Playwright e2e green; live smoke of the golden path on prod.
- Reconcile stale docs (CLAUDE.md/STATUS test counts 211→887; "frozen" vs nav; remove dead mock residue comments) (B5).
- Runbook (Worker restart, stuck-order requeue, R2 secret rotation, Stripe live-swap, incident alerts).
- Tag launch-ready.

---

## 3. Full production-test matrix (Wave 7) — how each is exercised / mimicked

| Capability | How I test in prod | Counterpart I create/mimic | Pass criterion |
|---|---|---|---|
| Browser upload (7 formats) | re-run via live UI/API | — (have it) | each → parsed order, correct lines |
| AI mapping / extraction / schema-infer / email-NLP | real OpenAI (key set) | — | suggestions w/ confidence; PDF→structured |
| Manual resolve → transform | live UI | — | transform artifact generated |
| **HTTP delivery** + test-fire | configure supplier → deliver | **webhook.site / requestbin** capture URL | 2xx delivered; audit attempt row |
| **Outbound webhook (HMAC)** | integration subscription → fire | capture URL | signed `X-ProcuLink-Signature` received |
| **SFTP delivery** | configure → deliver | **a real SFTP server I stand up** (public-reachable) | file lands on server; audit row |
| **FTPS delivery** | configure → deliver | real FTPS server I stand up | file lands; audit row |
| **SMTP / email delivery** | configure → deliver | real mailbox/receiver I control | email w/ attachment received |
| **S3 delivery** | configure → deliver | throwaway S3/R2 bucket | object lands |
| **ERP Erply / Directo** | configure → deliver | **mock REST endpoint** (default) OR real sandbox (Q3) | dispatch + correct payload shape + audit |
| API-key ingress (`plk_`) | create key (UI) → POST JSON | — | one PO; **idempotent replay = 1** |
| Inbound email (Postmark) | simulate signed webhook (token held) + optional real email | optional real address (Q) | attachment → parsed order |
| SFTP / S3 **pull** ingress | configure → drop file | the SFTP server / S3 bucket above | file imported, deduped |
| Billing (TEST mode) | API + simulated signed webhooks | Stripe test mode | checkout/portal/webhook/overage all succeed |
| Admin area | live browser (founder admin) | — | MRR (DB≡Stripe), invoicing, limits |
| Observability — **Sentry** | trigger handled+unhandled errors | Sentry (testing token held) | events captured (verify via Sentry API) |
| Observability — **PostHog** | exercise tracked events | PostHog (token held) | events ingested (note: `phc_` is ingest key; read needs a personal key) |
| Health / canary / alerts | drill: stop Worker, exhaust pool, dead-letter | — | each alert fires |
| Tenant isolation | 2nd org (or code+admin proof) | optional 2nd test user (Q) | no cross-org read |

**Constraints I cannot bypass (you perform these):** entering card/payment details or completing a real checkout payment; creating accounts / entering passwords (sign-up, ERP sandbox sign-up); accepting ToS/consent/OAuth grants. For these I prepare everything and hand you the exact step.

---

## 4. Explicitly deferred (recommend immediately-AFTER June 9 — the audit's own "can wait / do NOT refactor before launch")

Doing these in the 2-day window directly conflicts with "no new bugs" and the audit's guidance. **Recommend scheduling for the week after launch** (decision Q1):
- **OrderService / OrdersController God-object split** (B1) — "pure risk, zero customer value" pre-launch (audit C).
- **Typed-DTO / codegen contract layer** (B3) — multi-day cross-cutting; instead Wave 1 fixes the *concrete* contract bugs.
- **Postgres RLS** defence-in-depth (§2.5).
- **audit/passport time-partitioning**, denormalize `line_count`/`total_value` (§1.4).
- **Redis** for HMAC nonce + Hangfire queue (P2-3) — only needed at >1 API replica / >10–20k jobs/day.
- **Consolidate dual delivery-retry schedulers** (W5/d-7) — currently correct, just fragile.
- **Split `api-client.ts`** monolith (W6/f-5) — DX, not correctness.
- **EDIFACT INVOIC / DESADV** — keep hidden (founder said no to EdiFabric licence).
- **Cross-org mapping library / network-effect moat** — the one real moat; build after ~10 customers.
- **i18n / PEPPOL AP / broad standards breadth** — post-launch roadmap.

> Global-grade note: "build for the whole world" is reflected in reliability, security, observability, honest capability claims, and enterprise-grade UI — **not** by forcing the deep refactors above into launch week. The moat (cross-org library) is the deliberate next chapter, not a 2-day item.

---

## 5. Timeline (honest — today is 2026-06-07; launch 2026-06-09)

- **Day 1 (Jun 7):** Wave 0 (reconcile) → Wave 1 (honesty/trust) → Wave 2 (correctness/stability). Deploy + live QA each. *(These alone remove every "amateur"/trust signal + the two correctness P0s.)*
- **Day 2 (Jun 8):** Wave 3 (security P1 batch, adversarial) → Wave 4 (reliability/ops) → Wave 5 (billing test-mode + runbook) → Wave 6 (UX/conversion/design). Deploy + live QA.
- **Day 3 (Jun 9):** Wave 7 (**full production test**, all channels) → Wave 8 (regression gate + docs + runbook) → **Stripe live-swap** (per Q2) → launch-ready tag.

This is aggressive but achievable for the **launch-blocker set** with heavy parallelism. The **DEFER** bucket is the week-after. I will flag immediately if any wave can't land safely rather than ship a regression.

---

## 6. Decisions I need from you (plan-shaping) + key/secret status

**Have (configured on prod, usable):** every backend secret (OpenAI, Stripe test, Clerk, R2, Neon, Postmark token, encryption keys, SMTP, PostHog, Sentry, admin allowlist) + your logged-in browser + Railway/Vercel/gh CLIs. **Now also:** Sentry testing token + PostHog token (held in memory, never committed).

**Need your call on:**
1. **Risky deep refactors (DEFER list §4)** — schedule immediately-post-launch (recommended), or force into this push (higher regression risk before June 9)?
2. **Stripe go-live** — I do all billing fixes + full test in **test mode** now and prepare the live-swap runbook; do you want me to **create the live products/prices via the Stripe API** (needs the `sk_live` key) and you flip the webhook, or will you do the live swap yourself at the registration gate?
3. **ERP Erply/Directo test** — mimic with a **mock REST endpoint** (proves our dispatch path, default), or you provide **real sandbox creds**, or **demote** them to "Configurable" on `/formats` until tested?
4. **DNS for email deliverability** (SPF/DKIM/DMARC on `proculink.eu`, Cloudflare) — do you add the records, do I (if you grant Cloudflare access), or do we move the supplier email-delivery channel to a transactional sender?

**Standing permission requested for the prod test:** deliver test payloads to capture endpoints; create/revoke API keys; submit the support form (sends email); POST simulated Stripe/Postmark webhooks; leave/clean test orders.
