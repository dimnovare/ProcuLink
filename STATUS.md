# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

> **Pruned 2026-07-02.** The founder purged ~143 stale planning docs (commit `9456a08`), and
> this file was cut from ~1,290 lines of session-by-session narrative to the current state.
> Implementation history (Phases 0–6, Groups A–L, Waves 1–4, UI passes 1–15, the June launch
> waves) lives in `git log` — do not re-execute old checklists. The active plan + verified
> capability ground truth is
> [`docs/prompts/2026-07-02-fable5-production-push-master-prompt.md`](docs/prompts/2026-07-02-fable5-production-push-master-prompt.md).

---

## Snapshot (2026-07-02)

- **Production is LIVE** at `proculink.eu` + `api.proculink.eu` (launched 2026-06-09 window).
  Live QA 2026-06-29/30 verdict: **CONDITIONAL GO** — 7 inbound formats, 6 outbound formats,
  and HTTP delivery proven live on prod (locale-safe).
- **Active work:** the Fable-5 production-hardening push (master prompt above) — prove every
  advertised capability live from a clean slate, click-audit the entire UI, consolidate
  design drift, make marketing truthful, fix everything found.
- **Billing:** Stripe is **LIVE** (verified 2026-07-02 via API: `sk_live` key in Railway;
  Growth €149 / Operations €399 / Integration €999 / Distributor €1,499 monthly + all 4
  yearly prices, all active in live mode). Real-money infrastructure — no test checkouts
  against prod. Frontend annual-billing toggle is still gated off; yearly price IDs exist
  and are live (wire-up is a pending task).

## Durable identity rule (2026-06-09)

ProcuLink is the product and customer-facing brand. The operating legal entity is
**Diip Solutions OÜ**, registry code **17527757**, registered at Uus-Sadama tn 15-2,
10120 Tallinn, Estonia. Frontend source of truth: `project-proculink/src/lib/legal-entity.ts`
(legal pages, footers, one-pager, JSON-LD consume it). **Never restore the fabricated
"ProcuLink OÜ" / 17477775 / Katusepapi identity.** Do not publish the founder's personal
registry email or invent a VAT number.

## Deployment topology (verified live)

| Piece | State |
|---|---|
| Frontend | Vercel, auto-deploy from FE `main`; `https://proculink.eu` is the single canonical origin (`www` → 308 to apex); `NEXT_PUBLIC_USE_MOCK=false` |
| API | Railway (EU) service `ProcuLink` → `api.proculink.eu`; auto-deploys from BE `main`; EF migrations apply on startup (fail-loud + phantom reconciler) |
| Worker | Railway service `aware-amazement` — the **single** Hangfire worker, GitHub auto-deploy, **mandatory** (nothing parses/delivers without it). Railway CLI linked, project `lucid-generosity` |
| DB | Neon Postgres (also hosts Hangfire) |
| Storage | Cloudflare R2: `proculink` (private order data — pre-signed URL GETs only; SDK chunked GET signing is rejected by R2) + `proculink-public` (marketing assets, `assets.proculink.eu`) |
| Auth | Clerk **production** instance (`clerk.proculink.eu`, `pk_live_…`); org id/slug read from the Clerk v2 `o` claim; force-org-creation (adopt-on-create + softened-resolve) deployed + live-verified 2026-06-30 |
| Inbound email | `{slug}@orders.proculink.eu`: CF Email Routing MX → Postmark → `POST /api/inbound-email/postmark` — proven live with a real email |
| Outbound email | Postmark HTTPS is the **canonical** email delivery path (SMTP is dead on Railway); domain verified (SPF/Return-Path/DKIM via CF API); **Postmark approved — DeliveryType Live** (verified 2026-07-02 via server API) |
| DNS | Cloudflare — edit **only** via scoped API token (the dashboard SPA won't render in the browser tool) |
| Observability | Sentry capturing (API + Worker + frontend); PostHog EU ingesting; `/health` (liveness) + `/health/ready` (DB + storage + migration checks); Worker heartbeat alert |
| Email auth | SPF + DKIM + DMARC (`p=none`) complete on `proculink.eu` |
| Stripe | **LIVE mode** (`sk_live` verified 2026-07-02); all 8 monthly+yearly price IDs set in Railway and active | 

Prod env vars are fully set in Railway (API + Worker) and Vercel; the required-key list is
enforced by `StartupConfigurationValidator` + `appsettings.Production.json` — verify infra
(`railway variables`, Stripe dashboard) before trusting any doc's gap claim.

## Test / build state

- Backend: **1,029 tests green, 0 failures** (224 Transform + 452 Infrastructure + 353 Api)
  — last count recorded here 2026-06-07 at `main` `216b3fa`. Substantial code landed since;
  run `dotnet test ProcuLink.slnx` for the live count before claiming green.
- Frontend: `bun run build` clean (48 routes at last record). Mock e2e suite green;
  live e2e recipe: `PROCULINK_QA_BYPASS_AUTH` + local PG :5435 + `Delivery__EncryptionKey`
  + Worker running.
- Windows dev, Linux CI/prod — after pushing check `gh run list`; local green ≠ CI green.

## What happened 2026-06-09 → 2026-07-02 (summary; detail in git log + memory)

- **North Star pivot (06-09):** versioned Supplier Connection platform (draft → test →
  publish → archive, `ConnectionRevisionId` pinning, replay/impact diff) — V1–V10 shipped,
  plus confidence-calibration (per-org accept-rate buckets).
- **Output-layer restructuring (06-15):** 100% complete — trust P0s, `OutputNode` AST +
  emitters + output designer (IncludeWhen conditionals, format presets, namespaces),
  cXML DTD, plain-language validation messages.
- **Order Workshop (06-18 → 06-21):** unified 3-column order review (`OrderWorkshop`) with
  inline source picker + "bind any source field" flexible mapping — live; layout now locked.
- **Hardening + audits (06-16 → 06-24):** four-track push (idempotency, retry, GDPR erase,
  AI-usage atomic), strand-race fix, full 5-lens audit (0 P0), workbench UX + mobile audits.
- **cXML address blocks (06-25):** ShipTo/BillTo/Contact emission, MapForce byte-identical,
  proven on a real REDACTED-PARTY PDF→cXML round trip.
- **Supplier routing (06-26):** Phase 0 (nullable SupplierId + `unrouted` status) + Phase 1
  (hold + assign-supplier re-parse) shipped; producer dormant; Phase 1b blocked on the
  SFTP/S3 enqueue gap. Two routing worktrees in flight — see master prompt operating rule 8.
- **Design (06-26 → 06-29):** design-system v1 branch `feat/design-system-v1` review-gated;
  a redesign handoff spec + 228 screenshots were produced for Claude Design (the spec was
  removed in the 07-02 docs purge; recover from git history at `4f0855f` if needed).
- **Prod launch QA + fixes (06-29 → 06-30):** pre-launch audit → CONDITIONAL GO; Postmark
  HTTP email channel (PR #15 overage retry, #14 Clerk `o` claim, #11 force-org-creation)
  merged + deployed; inbound email live end-to-end on a real org.
- **07-01:** shared `useConfirm()` dialog primitive replaced all native confirms; mapper
  toolbar/API-key polish. **07-02:** docs purge (`9456a08`) + master prompt (`93a5b10`) +
  this CLAUDE.md/STATUS.md cleanup.

## Known issues / limitations (honest capability edges)

- **EDIFACT INVOIC / DESADV are stubs** (no commercial EDI licence — EdiFabric rejected);
  DESADV upload returns 501. UI must present these as "coming soon", never as errors.
- **ERP connectors (`erp_erply` / `erp_directo`):** no live ERP sandbox creds — verified via
  unit/request-shape tests + mock REST test-fires only; label honestly.
- **Scanned/image-only PDFs:** every extracted line is review-flagged (no text layer to
  verify numbers against); illegible scans fail with an honest message. Assisted, not silent.
- **Postgres RLS not implemented** — final-deferred by design (post-revenue redesign);
  app-level `.Where(OrganisationId == …)` scoping enforces isolation everywhere.
- **Postmark:** approved and Live (verified 2026-07-02); inbound webhook signature
  verification deferred (needs a CF Worker).
- Design-system drift (duplicate primitives, e.g. `UnifiedStatusBadge` ×2) — inventory in
  master prompt Appendix C; fix in the push's Phase 3.

## Open items — founder / ops gates (not code)

1. ~~Stripe live-mode swap~~ **DONE** (verified 2026-07-02: `sk_live` + live `whsec_` + all
   8 live price IDs in Railway). Remaining billing to-do is engineering: verify the live
   webhook end-to-end with a real subscription event and wire the annual toggle.
2. **Rotate chat-exposed secrets** — Clerk, R2, ElevenLabs, Cloudflare API token; delete
   `~/.proculink-cf-creds.env`. Deletable cruft: the `proculink-livetest` delivery Worker + KV.
   **2026-07-02 addition:** supplier catalog feed credentials (Ingram, Also/Actebis, 100MEGA,
   REDACTED-PARTY, Logicom, Jarltech URLs) were pasted in chat — rotate/re-issue with those vendors
   after the push, and store only in encrypted catalog-source config.
3. **A real PO to a real supplier's endpoint** — controlled-endpoint deliveries are proven
   live (code 200, verified at receiver); an actual third-party supplier remains untested.
4. **Monitored support/ops mailbox + alert destinations** (Sentry/ops alerts need a real
   destination the founder watches).
5. **OpenAI compliance for customer data** — EU-residency project + DPA + zero-retention
   before real customer PO text flows through extraction at scale.
6. **Google Search Console** — use the Domain property `proculink.eu`, submit
   `https://proculink.eu/sitemap.xml` (apex, not www).
7. **The actual selling.**

## Open items — engineering (deferred by design / next up)

- Postgres RLS (needs a real-Postgres two-org test harness + Hangfire/migration role
  exemptions before it can land green).
- Invoice-pipeline rerouting (needs a PO↔invoice link migration + relational test; the
  original plan doc was purged — re-plan if picked up).
- Frontend `api-client.ts` split, retry-consolidation, denormalize/partition — audit-flagged
  counterproductive pre-revenue; don't do without a fresh reason.
- Neon pooler + `DataRetentionSweepJob` enablement — env-only flips; both dormant safe-by-default.
- Full app CSP (script/style/connect — needs Clerk/Stripe/PostHog/Sentry testing); per-page
  SEO metadata on the remaining marketing pages; Sentry stale-issue resolve; Postmark webhook
  log level.
- Supplier-routing Phase 1b (SFTP/S3 enqueue gap) + integrating the two in-flight routing
  worktrees (`routing-phase0-nullable-supplier` @ `056aff6`, `routing-phase1-hold-assign`
  @ `2fed48e`).
- "Your inbound address" card in Settings (the `{slug}@orders.proculink.eu` address now
  exists but isn't surfaced in-app).
- Design consolidation per master prompt Appendix C; `feat/design-system-v1` branch is
  review-gated, not merged.

## Open items — founder configuration gaps (feature works, config missing)

| Area | Action | Where | Effect when missing |
|---|---|---|---|
| Clerk post-signup redirect | Set post-sign-up redirect to `/welcome` | Clerk dashboard (production instance) | New sign-ups skip the welcome funnel |
| Status page | Host a status board, set the URL | `NEXT_PUBLIC_STATUS_URL` (Vercel + `.env`) | Footer link hidden |
| Book-a-demo CTA | Create Cal.com/Calendly slot | `NEXT_PUBLIC_BOOK_DEMO_URL` | Pilot book-a-demo cards hidden |
| Support-form delivery | Verify the support form actually delivers email (SMTP config or rewire to Postmark) | Railway `Smtp:*` keys | Form returns 200 but mail goes to console log |
| DPA counter-signature | Staff `legal@proculink.eu`, sign DPAs within 5 business days as committed on `/dpa` | Operational | Trust commitment becomes false |
| Subprocessor notifications | Maintain the subscriber list; 30-day advance notice per `/subprocessors` | Operational | Trust commitment becomes false |
| Cookie banner copy | Review live banner tone (incognito) | Browser smoke test | Cosmetic |

Closed since this table was first written: PostHog (ingesting live), `Frontend:Url` (set to
prod domain), walkthrough video (real R2-hosted video live on `/watch` — the Loom env var is
superseded).

---

## Archive note

Everything before June 2026 — Phase 0–3 build-out, the Next.js migration, commercial Groups
A–L, Waves 1–4 (invoice/ASN + Zapier/Make layer), Group I UI passes 1–15, Group J/K/L, the
2026-06 launch waves (0–8 + Wave D), and the per-session narratives that used to live here —
is implemented history. See `git log` (both repos) and the memory files. Treat all of it as
shipped unless a section above explicitly reopens it.
