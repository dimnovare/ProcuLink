# PROCULINK — FABLE-5 PRODUCTION PUSH — MASTER PROMPT

> **How to use:** paste this entire document as the opening prompt of a fresh Claude Code
> (Fable 5) session in `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`. Everything below is
> the instruction set. It was generated 2026-07-02 from a live scan of both repos — the
> inventories in Appendix A are verified ground truth as of that date, not aspiration.

---

## MISSION

You are running the final production-hardening push for ProcuLink so the founder can start
selling and advertising it as a stable, professional, enterprise-grade procurement tool.

The goal, in priority order:

1. **Prove every advertised capability works live** — every inbound document format, every
   inbound transport, every outbound format, every outbound delivery channel, tested end-to-end
   on the production environment (proculink.eu / Railway API / Worker), starting from a clean slate.
2. **Click-audit the entire UI** — every route, every button, every empty state, every error
   state, desktop and mobile. No dead controls, no silent no-ops, no stale/staged data, no jargon.
3. **Consolidate to one design language** — fix the known duplicate-primitive drift (Appendix C),
   polish UI/UX using the design skills, without inventing a new visual direction.
4. **Make marketing pages truthful and complete** — fix the specific gaps in Appendix D;
   every claim must equal a tested capability ("offer ⇔ works" is a hard founder rule).
5. **Fix everything found** — small fixes inline; larger fixes as planned, reviewed, tested
   PRs. Ship to production and re-verify live.

Work autonomously. Do not ask permission for reversible, in-scope work. Stop and ask only for:
destructive actions beyond the sanctioned purge, anything touching Stripe LIVE mode, secrets
rotation, or genuine scope changes.

---

## OPERATING RULES (NON-NEGOTIABLE)

1. **Read first:** `STATUS.md` (repo root) and `CLAUDE.md`. They are the source of truth for
   phase status. This prompt supplements, never overrides, explicit founder instructions there.
2. **Skills are mandatory, not optional:**
   - `/superpowers:brainstorm` before any feature/change touching ≥3 files; `/superpowers:write-plan`
     then `/superpowers:execute-plan` for medium+ tasks; `/superpowers:debug` for any bug surviving
     one fix attempt; `superpowers:verification-before-completion` before every "done" claim.
   - `ui-ux-pro-max` + `frontend-design` + `web-design-guidelines` + `design-review` for all UI work.
   - `/code-review` (or the code-review skill) at the end of every task group, before merging.
   - `caveman` mode for chat responses (token efficiency); write code/commits/PRs/docs normally.
3. **Frontend repo:** `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` — Next.js 15 App
   Router, **bun only** (never npm/yarn), `@clerk/nextjs`, TanStack Query in client components,
   no react-router, no Vite. Backend: .NET 8, EF Core (no raw SQL), every query org-scoped
   (`.Where(x => x.OrganisationId == organisationId)`), Hangfire jobs idempotent.
4. **The 3-column Order Workshop stays.** `/inbox/[orderId]` renders `OrderWorkshop`
   (`src/components/bridge/workshop/OrderWorkshop.tsx`) — the 3-column review view is locked.
   Polish it, fix bugs in it, improve details — do NOT restructure it or replace the layout.
   Same for the overall visual direction (navy/violet/blue→green Bridge Layer): keep it; only
   propose a major change if you can demonstrate it is clearly better, and get founder sign-off first.
5. **Offer ⇔ works.** Never let UI/marketing offer a channel/format that isn't a live, tested
   capability. When in doubt, soften the copy rather than over-claim. `/library/standards`
   and `src/lib/standards/catalog.ts` are the conservative source of truth.
6. **Stripe stays in TEST mode.** The live-mode swap is a founder-only gate. Never create/edit
   live-mode Stripe objects. Billing code changes require extra review (billing/tenancy/security
   are the high-care areas).
7. **No commercial EDI licences** (no EdiFabric, ~€1,500/yr rejected). EDIFACT INVOIC / DESADV
   remain stubs — verify the UI presents them as "coming soon", never as errors.
8. **Isolation:** use git worktrees for parallel work chips; never run parallel agents in the
   shared checkout (EF snapshot / .next collisions are a known failure mode). Two worktrees are
   already in flight (`routing-phase0-nullable-supplier` @ 056aff6, `routing-phase1-hold-assign`
   @ 2fed48e) — leave them alone unless integrating them becomes an explicit task.
9. **Windows dev, Linux CI/prod.** After pushing, check `gh run list` — local green ≠ CI green.
10. **Verify infra before trusting a doc's gap claim** (e.g. `railway variables`, Stripe dashboard).
    Docs have historically lagged reality in this project.

**Docs purge context (resolved 2026-07-02):** the founder intentionally deleted ~143 stale docs
(archives, superseded plans, audits, gtm, runbooks); the deletion is committed. Consequence:
**`CLAUDE.md` and `STATUS.md` now contain dead references** to deleted files (the "ACTIVE PLAN"
banner → MASTER-BUILD-STATUS + masterplan, the model-routing policy → docs/CLAUDE_MODEL_ROUTING.md,
standards-visibility rule → docs/standards-matrix.md, north-star/pricing/investor memos, many
superpowers plan links). **Phase 0 housekeeping task:** rewrite CLAUDE.md (and prune STATUS.md)
to reflect current reality — drop dead links and stale phase banners, keep durable rules
(tenancy, coding conventions, offer⇔works, bun-only, design direction). Never try to read the
deleted docs; this prompt's appendices replace them as capability ground truth.

---

## ENVIRONMENT & ACCESS

| Thing | Value |
|---|---|
| Backend repo | `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink` (.NET 8, `ProcuLink.slnx`) |
| Frontend repo | `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (Next.js 15, bun) |
| Prod frontend | https://proculink.eu (Vercel, auto-deploy from FE `main`) |
| Prod API | Railway service `ProcuLink` (auto-deploy from BE `main`); Worker = `aware-amazement` (MANDATORY for parse/deliver — nothing moves without it) |
| Prod DB | Neon Postgres · Storage: Cloudflare R2 `proculink` (private, pre-signed only) |
| Local API | `http://localhost:5223` · Scalar: `/scalar` · Hangfire: `/hangfire` · local PG on :5435 |
| Local live QA | `PROCULINK_QA_BYPASS_AUTH=true` + `ASPNETCORE_ENVIRONMENT=Development` + valid `Delivery__EncryptionKey` (32-byte base64); Worker must run |
| Railway CLI | linked, project `lucid-generosity` |
| Auth | Clerk production instance (clerk.proculink.eu); founder provides login for browser testing |
| Inbound email | `{slug}@orders.proculink.eu` (CF MX → Postmark webhook) — proven live |
| Cloudflare DNS | edit ONLY via scoped API token (dashboard SPA won't render in the browser tool) |
| Test org | "Dim's Organization" = test org with admin override; personal-workspace-d3be also used in past tests |

**Browser testing:** use the Claude-in-Chrome MCP tools (`mcp__claude-in-chrome__*`) against
production. To upload a local file through the prod browser, use the proven recipe: inject the
file as SHA-verified base64 chunks via `javascript_tool` (PNA blocks localhost fetches; the
`file_upload` allowlist rejects non-share paths). For local UI verification during fixes, use
the preview tools / Playwright, not screenshots-by-hand.

**Free external receivers to test deliveries against (ask founder if a paid/persistent one is preferred):**
- HTTP delivery + outbound webhooks: **webhook.site** (proven in earlier QA) — inspect payload, headers, HMAC signature.
- SFTP delivery: a free ephemeral SFTP (e.g. sftpcloud.io free instance) or a local `atmoz/sftp` Docker container exposed via a tunnel; verify the file lands byte-identical (SHA-256).
- FTPS delivery: free test FTPS instance or local Docker (pure-ftpd with TLS) + tunnel.
- Email delivery: send to a founder-controlled mailbox AND a disposable inbox; verify subject/body templates and attachment integrity. Postmark is in Test/pending-approval mode — confirm current approval status first; if still test-mode, verify via Postmark activity log.
- ERP connectors (erp_erply / erp_directo): no sandbox creds available → test-fire against a webhook.site endpoint standing in for the ERP URL where the connector shape allows; otherwise verify request-shape via unit/integration tests and mark the channel "config-verified, no live ERP sandbox" honestly in the report.
- S3/R2 ingress: use a dedicated prefix in the founder's R2 (S3 creds recipe: token id + SHA256(token value)).
- Test documents: generate realistic fixtures per format from the parser specs (Appendix A);
  a 12-PO real-world corpus exists in `~/Downloads` (see memory) and the founder can supply
  real Markit POs/catalogues on request — ask early, don't block on it.

---

## PHASE PLAN

Work through phases in order. Each phase ends with: a written findings/results log under
`docs/qa/2026-07-fable5-push/` (create it), fixes applied or ticketed, `/code-review` run on
any code changed, and a short caveman status to the founder.

### PHASE 0 — Baseline & clean slate
1. CLAUDE.md / STATUS.md dead-reference cleanup (see "Docs purge context" above).
2. Baseline builds: `dotnet test ProcuLink.slnx` (expect ~2,844 green) and `bun run build`
   + `bun run test:e2e` (mock mode, expect ~509) — record exact numbers as the regression floor.
   Note: 3 wire tests were failing on FE main earlier (pre-existing); verify current state and fix or document.
3. Health check prod: `GET /health`, `/health/ready`, `/api/ops/health`, Sentry for unresolved
   prod errors, Hangfire dead-letter queue, PostHog ingestion.
4. **Purge live data — founder pre-approved for ALL orgs (2026-07-02: "all orgs can be redone
   if needed").** Clean slate for testing. Use the admin surface — `GET /api/admin/organisations`
   to enumerate orgs (match by SLUG, not name), then per org
   `POST /api/admin/organisations/{orgId}/orders/bulk-erase` (filter required — use `olderThan: <now>`;
   endpoint refuses an empty filter). Also clear suppliers/connections/catalogs where a true zero
   state helps testing. Two cautions even with blanket approval: post a short list of org slugs
   you are about to purge BEFORE executing (cheap safety net), and leave Stripe subscription
   objects/billing state untouched (erase order data, not billing records).
5. Create the fresh test fixtures set (one valid + one deliberately-broken sample per input format).

### PHASE 1 — Full capability matrix, live on production
This is the heart of the push. Test **from a real browser session where the surface is the UI**,
and via API where the channel is machine-to-machine. Every run must reach a terminal, audited
state (`delivered`, or an HONEST failure state) — HTTP 200 from a supplier endpoint is not
business acceptance; check the delivery attempt rows and Order Passport.

**Coverage contract (minimum):**
- Every INPUT format variant (10): CSV (comma + semicolon/EU-locale), XLSX, PDF-text, PDF-scanned
  (vision path — expect all lines review-flagged, that is correct behaviour), UBL 2.1 Order,
  Peppol BIS Order 3, cXML 1.2 (with DOCTYPE), EDIFACT ORDERS D96A, X12 850, SAP IDoc ORDERS05
  → each parsed to a fully-populated canonical order: **all lines present, all header fields,
  line numbers, quantities, unit prices, currencies, buyer codes, descriptions** — diff parsed
  values against the fixture's known values, not just "it parsed".
- Every INBOUND transport (8): browser upload · REST ingress (`POST /api/ingress/{slug}/orders`,
  API-key + idempotency) · inbound email to `{slug}@orders.proculink.eu` with CSV/XLSX/PDF
  attachments · IMAP polling (needs a mailbox — ask founder; else mark honestly untested) ·
  SFTP polling · S3/R2 polling · webhook ingress ack/status (HMAC) · catalog push API.
- Every OUTPUT format (6): XML, CSV, JSON, cXML 1.2, UBL/Peppol Order, X12 850 — validate each
  artifact structurally (parse the XML/JSON back; check X12/ISA envelope widths; cXML DTD shape).
- Every DELIVERY channel (6 live): HTTP(S) (all auth modes worth one pass: none/api-key/bearer/basic;
  OAuth2 if a free token endpoint can be stood up), SFTP, FTPS, Email, erp_erply, erp_directo
  (see receiver notes above). Verify test-fire AND real order delivery per channel, plus the
  retry/backoff path (30/60/120min — trigger a failure, then force/requeue via ops) and dead-letter + requeue.
- Invoices: UBL 2.1 invoice upload → parse → approve → each invoice output (CSV/XML/JSON/Peppol BIS
  Billing). ASN/DESADV: verify honest "coming soon" (never a 501/error page).
- Full pairwise is not required: run every input → one output; every output → one delivery;
  every delivery → one output; PLUS the founder-priority combos (Erply/Directo templates,
  cXML round-trip cXML-in→cXML-out, EDIFACT-in→X12-out cross-standard) and a 3-random-combo spot check.
- Feature systems, each exercised live: versioned Connection lifecycle (draft → test-evidence →
  publish → order pins revision → edit live config → verify pinned order does NOT re-route →
  rollback) · replay/impact testing · catalog import via ALL channels (push API, SFTP pull, HTTP pull
  with each auth method) + AI catalog matching · AI line-SKU suggestions + magic auto-map +
  confidence calibration (check `GET /api/billing/ai-usage` FIRST if AI "stops working" — the
  per-org monthly token cap latch is a known incident pattern) · validation rules + acceptance
  profiles (block-on-fail actually blocks; plain-language messages render) · exceptions dashboard +
  resolve/ignore · order passport completeness for every tested order · supplier routing/`unrouted`
  hold+assign · sample order onboarding flow · billing plan gates (upload past Pilot cap → honest
  429; feature gates per plan; overage ledger on a paid test org) · API keys create/reveal-once/revoke ·
  outbound webhook subscriptions firing with valid HMAC (order.created/delivered/failed) ·
  GDPR erase (single + bulk) verified to actually remove rows + R2 blobs.
- Negative paths per channel: wrong file type, oversized (>10MB), malformed content per format,
  supplier 4xx rejection, unreachable endpoint, wrong credentials, expired API key, bad HMAC,
  rate limits (upload 20/min). Every failure must surface a human-readable, honest error in the
  UI — no raw stack traces, no infinite spinners.

Record every run in a matrix log: input → transport → parse result (field-level diff) → transform →
delivery → terminal state → evidence link (passport / attempt id / receiver screenshot).

### PHASE 2 — Click-through UI audit (every route, every button)
Route inventory is in Appendix B (44 app+marketing routes + 19 help pages). For EACH route,
desktop (1280+) and mobile (390px — render it, don't static-analyze; static audits false-positive):
1. Every interactive element gets clicked: buttons, menus, tabs, toggles, drawers, dialogs,
   command palette (Cmd+K), keyboard nav, focus states. A control either performs a real action
   with visible feedback, or it must not exist. (Precedent: "Accept suggestion" was a silent
   no-op — that class of bug is the target.)
2. States: loading (BridgeLoader only — no stray spinners), empty (helpful, not blank), error
   (API down → the bounded-timeout "unavailable" cards), permission/plan-gated, read-only
   (expired Pilot org must be view-only everywhere).
3. Data honesty: zero hardcoded/demo/staged literals reaching real users (grep + visual check;
   this leaked before). All numbers wired to real APIs.
4. Copy: plain language, no leftover bridge/dock/crossing/revision jargon in user-facing text
   (component names may keep the metaphor), consistent capitalization, no dev-speak in errors.
5. A11y: focus-visible everywhere, aria-labels, contrast, 44px mobile hit targets,
   prefers-reduced-motion respected. Run `web-design-guidelines` skill per major screen.
6. Auth/tenancy edges: cold-mount (queries must wait for Clerk), org switch, sign-out/in,
   deep-link to an order from another org → 404 not leak.
Log every finding with severity (P0 broken / P1 wrong / P2 rough / P3 polish); fix P0/P1 in
place during the phase, batch P2/P3 into the Phase 3 polish wave.

### PHASE 3 — Design consolidation & UI/UX polish
Known drift is catalogued in Appendix C — start there, then apply judgement with the design skills:
1. Consolidate the duplicate `UnifiedStatusBadge` (2 implementations, 8-vs-2 import split → picks
   one canonical, migrate all 10 sites, delete the other).
2. Migrate `SettingsPrimitives.tsx` (inline-style Card/buttons) onto PageShell/Card/Tailwind tokens.
3. Rationalize the two Button stacks (bridge `DSPrimitives` 14-variant vs shadcn `ui/button`) —
   document which is canonical where; kill redundant variants (blue/green duplicates).
4. Sweep for inline `style={{display:...}}` defeating responsive classes (known bug class).
5. One design language check across all 44 routes: PageShell/PageHeader/Card adoption, spacing,
   typography scale, badge/status colors, table patterns (shared OrderTable), loaders, dialogs
   (useConfirm everywhere — native confirm is banned).
6. Mobile pass at 390px on all routes (previous audit got 24 routes clean — re-verify + cover the rest).
7. Order Workshop (3-column): keep layout; polish micro-interactions, issue-tab flow, hover
   highlight (received↔send↔preview), sticky send bar, confidence display.
Add Playwright viewport presets (375px/768px) so mobile stays covered in CI.

### PHASE 4 — Marketing truth & sales readiness
Fix the specific gaps in Appendix D. Additionally:
1. Verify the numbers on the landing page against the standards catalog after Phase 1 results
   ("10 inbound formats / 6 outbound / 6 delivery channels" must match what you just proved live).
2. Pricing consistency: FE pricing page ↔ backend plan constants ↔ Stripe products must agree
   (scan found possible drift, e.g. Growth price and Integration/Distributor limits — reconcile
   to the founder-approved ladder; ask if ambiguous).
3. Every CTA works: sign-up flow end-to-end (fresh account → org creation → onboarding checklist →
   sample order → first real upload), book-demo (needs `NEXT_PUBLIC_BOOK_DEMO_URL` — ask founder for
   a Cal.com/Calendly link), watch page video, status page link, support form (test a real submission).
4. SEO/meta: `generateMetadata` on all marketing pages, OG images, sitemap, robots (app routes
   disallowed), favicon, 404 page quality.
5. Add the security-header pass if still missing (HSTS/nosniff/CSP — Railway/edge-side; known open item).

### PHASE 5 — Fix waves, regression, ship
1. All P0/P1 from every phase fixed, each with tests where the bug class is testable
   (TDD for bugfixes; use `superpowers:test-driven-development`).
2. Full regression: `dotnet test ProcuLink.slnx` + FE build + full Playwright (mock AND live
   recipes) ≥ the Phase 0 baseline, 0 new failures. CI green on GitHub (`gh run list`).
3. Deploy (merge to main → Railway/Vercel auto-deploy), then RE-RUN a compressed live smoke of
   the Phase 1 matrix (one order per input format to delivered, one per delivery channel).
4. Final report: `docs/qa/2026-07-fable5-push/FINAL-REPORT.md` — capability matrix with
   evidence links, all findings + resolutions, honest list of anything still not sellable
   (e.g. IMAP untested, ERP no sandbox, Postmark approval pending, Stripe live swap pending),
   and the go/no-go recommendation with the founder-action list (the launch gates that are
   founder-only: Stripe live swap, secrets rotation, Postmark approval, demo-booking URL,
   real-supplier pilot).

---

## APPENDIX A — VERIFIED CAPABILITY INVENTORY (scanned 2026-07-02)

**Input PO parsers** (`ProcuLink.Transform/Parsing/`, routed by `OrderParserFactory` — extension
dispatch + content sniffing for .xml/.edi/.txt ambiguity): `CsvOrderParser` (comma/semicolon/tab;
semicolon ⇒ EU comma-decimal), `XlsxOrderParser` (raw cell types), `PdfOrderParser` (PdfPig text →
LLM primary, regex fallback, vision for scanned, OCR seam), `UblOrderParser` (UBL 2.1 Order +
Peppol BIS 3 detection), `CxmlOrderParser` (cXML 1.2, DOCTYPE-tolerant, MPN/UNSPSC), `EdifactOrderParser`
(ORDERS D96A/D01B, hand-rolled, UNA-aware), `X12OrderParser` (850 004010/005010, hand-rolled),
`IDocOrders05Parser` (SAP ORDERS05). Invoices: `UblInvoiceParser` (live), `EdifactInvoiceParser`
(STUB). ASN: `EdifactDesadvParser` (STUB). All numeric parsing via `NumberParsing.TryParseFlexibleDecimal`
(locale-safe; refuses silent corruption).

**Output transforms** (registered in Program.cs): `XmlTransformService`, `CsvTransformService`,
`CxmlTransformService`, `JsonTransformService`, `UblOrderTransformService` (Peppol BIS Order 3
profile), `X12TransformService` (850/004010, EnvelopeConfig). Plus `OutputNode` AST +
`OutputTemplateEmitter` (custom structures, IncludeWhen conditionals, namespaces), Scriban escape
hatch, `MappedTransformService`. Invoice outputs: XML/CSV/JSON + `PeppolBisInvoiceTransformService`
(+ lightweight validator). Transforms throw `TransformValidationException` on NeedsReview lines or
missing supplier item codes — deliberate safety, test it.

**Inbound transports** (all FULLY WIRED in prod): `POST /api/orders/upload` (Clerk JWT, 10MB,
20/min) · `POST /api/ingress/{slug}/orders` + `/ping` + `/catalog/{supplierId}` (X-ProcuLink-Key,
Idempotency-Key 24h) · `POST /api/webhook-ingress/{slug}/acknowledge|status|ping` (HMAC-SHA256,
timestamp+nonce) · `POST /api/inbound-email/postmark` (server token) · `EmailPollingJob` (IMAP,
5min, CSV/XLSX/PDF) · `SftpPollingJob` + `S3PollingJob` (5min, CSV/XLSX/PDF/XML/EDI, AES-GCM creds,
DefaultSupplierId) · `CatalogSyncDispatcherJob` (SFTP/FTP/FTPS + HTTP pull, 5 auth methods,
SHA-256 dedupe).

**Outbound delivery** (dispatcher registry, AES-GCM creds, SSRF guard, test-fire on all):
`http` · `sftp` (password or SSH key) · `ftps` (cert-validated, opt-out flag) · `email` (Postmark
HTTPS — canonical email path; SMTP is retired/legacy-dormant, `ftp` dormant) · `erp_erply` ·
`erp_directo`. State machine: pending_parse → parsing → pending_review/ready → transforming →
ready_to_deliver → delivering → delivered | delivery_failed → delivery_dead_letter; also
rejected_by_supplier, unrouted, failed. Retry 3 attempts, backoff 30/60/120min, SLA 120min +
sla_breached flag, per-order mutex, provenance (revision id + config digest + artifact SHA-256)
on every attempt.

**Purge endpoints (admin-gated):** `GET /api/admin/access` (probe) · `GET /api/admin/organisations` ·
`DELETE /api/admin/organisations/{orgId}/orders/{orderId}` · `POST /api/admin/organisations/{orgId}/orders/bulk-erase`
(body: poNumberPrefix/status/ids/olderThan — ≥1 required) · retention policy endpoint (Retention:DryRun
defaults true on Worker).

**Key feature systems:** versioned Supplier Connection (draft→test→publish→archive, revision
pinning, replay/impact diff, rollback-as-new-revision) · acceptance profiles (versioned validators,
block-on-fail) · AI mapping (line-SKU + magic auto-map + org-level confidence calibration + shared
monthly token budget per plan) · schema fingerprinting · catalog + AI matching · billing (Pilot
20/1 free 14d → Growth/Operations/Integration/Distributor/Enterprise; soft cap + €0.50/order
overage, never hard-blocks paid plans; Pilot expiry → read-only) · admin overrides (limits, trial
extension) · order passport evidence trail · outbound webhooks with HMAC + auto-deactivate after
3 failures.

## APPENDIX B — ROUTE INVENTORY (frontend)

Marketing: `/`, `/pricing`, `/how-it-works`, `/customers`, `/watch`, `/support`, `/security`,
`/formats`, `/privacy`, `/terms`, `/aup`, `/dpa`, `/subprocessors`, `/changelog`, `/one-pager`,
`/welcome`, `/help` + 19 help sub-pages.
App: `/bridge` (dashboard), `/inbox`, `/inbox/[orderId]` (★ 3-column OrderWorkshop — layout locked),
`/drafts`, `/upload`, `/upload/preview/[orderId]`, `/library/mappings` (MappingEditor),
`/library/standards`, `/library/templates`, `/library/rules`, `/library/rule-definitions`,
`/library/suppliers`, `/library/suppliers/[id]`, `/library/buyers`, `/connections`,
`/connections/[connectionId]`, `/inbound/invoices`, `/inbound/asns`, `/operations/connectors`,
`/operations/exceptions`, `/operations/health`, `/operations/log`, `/operations/webhooks`,
`/settings`, `/admin`.
Auth/onboarding: `/sign-in`, `/sign-up`, `/onboarding/select-organization`.
Shell surfaces to audit too: BridgeSidebar, BridgeTopbar (auto-breadcrumb), CommandPalette,
HelpSlideover, OnboardingChecklist, SectionGuide (23-route registry), toasts, ConfirmDialog.

## APPENDIX C — KNOWN DESIGN-SYSTEM DRIFT (fix in Phase 3)

1. `UnifiedStatusBadge` duplicated: `src/components/bridge/UnifiedStatusBadge.tsx` (8 importers)
   vs `src/components/ui/UnifiedStatusBadge.tsx` (2 importers, icon variant) — conflicting renders.
2. Two Button stacks: `bridge/DSPrimitives.tsx` (14 variants incl. redundant blue/green, mobile
   44px, loading state) vs `ui/button.tsx` (shadcn CVA). Needs a documented canonical split.
3. `src/components/settings/SettingsPrimitives.tsx` — inline-style cards + `primaryGreenButton`
   CSSProperties object; not on Tailwind/tokens; used by settings pages.
4. No mobile viewports in Playwright config (desktop chromium only).
5. Tokens are otherwise solid: `tailwind.config.ts` ("Bridge Layer v1.0") + `globals.css` vars +
   `docs/design-system/11-unified-page-rules.md`. PageShell/PageHeader/Card adoption is broad but
   not audited page-by-page — Phase 2 confirms.

## APPENDIX D — MARKETING GAPS (fix in Phase 4)

1. `NEXT_PUBLIC_STATUS_URL` empty → footer status link absent (stand up a status page or drop the promise).
2. `NEXT_PUBLIC_BOOK_DEMO_URL` empty → /watch promises a 15-min demo with no link (blocks sales conversion — get URL from founder).
3. `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` empty; primary `WALKTHROUGH_VIDEO_URL` points to R2 mp4 — verify it plays; v13 master video was DRAFT-staged, check if final.
4. Landing testimonial section is an unattributed value statement (correctly honest) — leave honest; replace only with a real consented quote.
5. `/customers` = 2 anonymized "coming soon" pilots — fine, but verify copy stays accurate.
6. Annual billing toggle gated off because yearly price IDs aren't wired into checkout — either wire yearly prices end-to-end (they exist in Stripe test) or keep hidden; do not show a toggle that fails.
7. Compliance: GDPR/DPA verifiable; SOC2/ISO27001 correctly framed "roadmap" — keep framing.
8. Pricing-figure drift between FE page, backend constants and docs (e.g. Growth €149 vs €349 seen in different sources) — reconcile with founder-approved ladder.
9. Security headers (HSTS/nosniff/CSP) — open item, edge/Railway-side.

## APPENDIX E — HARD-WON GOTCHAS (respect these; each cost real debugging time)

- **Worker is mandatory** — parse/transform/delivery all run through Hangfire on `aware-amazement`; "nothing happens" usually = worker not running/deployed.
- **AI "broken everywhere"** = per-org monthly AI token cap latched → check `GET /api/billing/ai-usage` BEFORE debugging code.
- **R2 GET needs pre-signed URL + HttpClient** — SDK chunked GET signing fails on R2; diagnose via hangfire.job rows in Neon.
- **EF traps:** `GetByIdAsync` is AsNoTracking → mutate+SaveChanges is a silent NO-OP (re-query tracked or ExecuteUpdate). ExecuteUpdate/Delete commit immediately outside SaveChanges — wrap mixed persistence in one explicit transaction. InMemory provider masks Postgres FK/insert-order issues — verify on real Postgres.
- **Never fire-and-forget** on a scoped DbContext (`_ = ...Async()` races); always await.
- **FE cold-mount:** queries must gate on Clerk readiness (`clerkReady` / `isApiMockMode`), networkMode "always" — else landing shows empty data after login.
- **BuyerName lives in BOTH** `purchase_orders.buyer_name` (updated async) and CanonicalJson — read the column first.
- **Parallel patch-gen agents** HTML-escape JSX (`&lt;` etc.) and emit LF vs repo CRLF — unescape + normalize + apply atomically + build before commit; never bulk-ship unreviewed agent UI.
- **Preview server contention:** the FE dev server races on :8082/.next — QA via HTTP/DOM eval against the running server, don't spawn duplicates; worktree FE QA needs its own `next dev -p <port>` + throwaway Playwright.
- **429s have 3 meanings:** pilot_expired / order-limit / rate-limit — read the body before concluding.
- **Delivery routes via the pinned revision snapshot** — editing legacy `PUT /delivery-config` is inert for pinned orders; live-edit republish goes through `RepublishLiveDeliveryAsync`.
- **XLSX exotic compression** (Deflate64) needs the SharpCompress repack path; ClosedXML reports it as "corrupted".
- **Locale numbers:** comma-decimal CSV/XLSX caused silent 10×/100× corruption before the fix — include EU-locale fixtures in every parse test.

## DEFINITION OF DONE

- [ ] Every live-advertised input format parsed field-perfect on prod; every output format emitted valid; every live delivery channel delivered + audited; negative paths honest.
- [ ] Every route × every control clicked on desktop + 390px mobile; zero dead controls, zero staged data, zero raw errors.
- [ ] One design language: duplicates consolidated, settings migrated, Workshop 3-column intact and polished.
- [ ] Marketing = truth; all CTAs functional; pricing consistent everywhere; sign-up→first-delivery journey smooth for a stranger.
- [ ] Test suites ≥ baseline, CI green, deployed, post-deploy live smoke green.
- [ ] FINAL-REPORT.md with evidence, honest residual-risk list, and the founder-only launch-gate checklist (Stripe live swap · secrets rotation · Postmark approval · demo URL · real-supplier pilot).
