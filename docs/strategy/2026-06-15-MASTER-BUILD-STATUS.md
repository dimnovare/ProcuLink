# ProcuLink — MASTER BUILD STATUS & HANDOFF

**Read this FIRST.** Any AI/engineer picking up this work starts here, then reads the plan.

- **THE PLAN (source of truth):** [`docs/strategy/2026-06-15-output-layer-restructuring-masterplan.md`](2026-06-15-output-layer-restructuring-masterplan.md) — Parts 1+2+3+4 reconciled + verified, then a FINAL MASTER. Build from its WS-0…WS-14 + phases A→E.
- **This doc:** live BUILD progress + how to resume. Update it as you go.
- **Date opened:** 2026-06-15. **Founder directive:** do everything in the master, nothing deferred/gated; build with full tooling (Railway/Vercel/Wrangler CLIs, OpenAI, PostHog, live prod testing, browser). Total best.

---

## What ProcuLink IS (so you build the right thing)

A **B2B procurement order-conversion bridge.** A buyer/procurement team receives or holds a purchase order in *some* shape (CSV/XLSX/PDF/XML/cXML/UBL/X12/IDoc, via upload/email/API/SFTP), and must send it to a **supplier in that supplier's exact required format and channel**. ProcuLink: **import → normalize → resolve item-code mappings → validate/fix → transform to the supplier's required output → deliver → learn.**

The engine is real and tested (7 input formats parse live; delivery proven on prod; AES-GCM creds; audit; versioned connections). **The problem is the OUTPUT layer + the shell**, not the engine.

**The governing principle (north star for every screen):**
> The user must always see exactly **what arrived, what ProcuLink changed, why it changed, and exactly what will be sent.**

**The core user:** a procurement coordinator, NOT an integration engineer. They think *"Supplier X wants a file with THESE fields in THIS shape"* — not canonical paths, not Scriban, not version-control.

---

## The two things the whole restructure fixes

1. **Trust:** today the product can lie — a broken mapping silently delivers the default doc while showing success; validation shows green "Passed — meets all acceptance rules" on an order with quantity `-3` / no rules. **(Phase A — in progress, see below.)**
2. **Output design:** today you cannot produce a supplier's required *structure* for XML/cXML/UBL/X12 (authored fields are silently dropped); CSV/JSON are flat-only; the only arbitrary-shape path (Scriban) is in an editor unreachable from the daily mapper. **(Phases B–E.)** The cut: **one `OutputNode` AST + `EnvelopeConfig` → format-aware emitters → one preview==delivery path → one supplier-scoped designer (visual⇄AST round-trips; raw Scriban one-way) → canonical invisible → paste-sample→infer.**

---

## BUILD STATUS

### Branches
- Backend: `feat/trust-layer-ws0` (off `main @ b2c3dce`).
- Frontend: `feat/trust-layer-ws0` (off `main @ 7c622c1`, == origin/main = deployed prod).

### Phase A — TRUST (WS-0 + WS-13 quick-wins) — **IN PROGRESS**
P0. Must land before any output-layer feature. Status legend: ☐ todo · ◐ in progress · ☑ done · ✅ verified-live.

| Item | What | Status |
|---|---|---|
| WS-0c | Mandatory `InvariantValidator` (qty>0, unit price present&>0, currency present, PO id present) — ALWAYS runs regardless of supplier profile; produces rows so empty-result can't show green | ☑ `InvariantValidator.cs` + integrated in `ValidateOrderAsync` |
| WS-0d | Validation fails closed — unknown/unsupported acceptance-rule operator errors (or rejected at rule-create), never silently passes (`SupplierAcceptanceService.cs:474 default:return true`) | ☑ default→`false` + `KnownOperators` allowlist + create-time 400 |
| WS-0e | Frontend: zero-rules renders neutral "Not checked", never green "meets all acceptance rules" (`FixQueueTriage.tsx:560`); guard `[].every` vacuous-true (`api-client.ts:2421`) | ☑ `acceptanceSummary` invariant/supplier split + strip + readiness card |
| WS-0f | Negative/zero quantity flagged at parse (`CsvOrderParser.cs:119 ?? 0m`, no sign check) + covered by invariant | ☑ covered by `invariant.quantity_positive` (all formats, parser-agnostic) |
| WS-0a | Kill silent output fallback in `OrderTransformService.cs:314-339,476-529` → fail loud (`delivery_failed`+reason) for a configured-but-broken override; legit no-override default stays | ◐ NEXT (own commit — risky delivery path; grounded, needs migration + 2 test files) |
| WS-0g | `OutputMappingFellBack`(+reason) provenance on artifact + order DTO + UI | ◐ NEXT (with WS-0a) |
| WS-0h | cXML preview credential parity — preview resolves From/To/Sender via same resolver as delivery (`OrdersController.PreviewMappingOverride`) | ◐ NEXT |
| WS-13a | Sample SUPPLIER excluded from quota + normal lists (`StripeBillingService.cs:763` add `&& !s.IsSample`; filter list) | ☑ quota fix done (list-filter follow-up) |
| WS-13b | Live PO-loop E2E heading fixed + made a CI gate (`live-po-loop.spec.ts:48`) | ◐ NEXT |
| WS-13c | Retry disabled when delivery config missing (`FailedPanels.tsx`) | ◐ NEXT |
| WS-13d | 5-vs-6 stages copy reconciled | ◐ NEXT |

> **Increment 1 committed** (`feat/trust-layer-ws0`): the **input-trust** layer (WS-0c/d/e/f) + sample-supplier quota (WS-13a). Backend 132 acceptance/invariant/billing tests green; frontend stageModel 24 green + production build clean. **Next:** WS-0a output fail-loud (own commit), then the remaining WS-13 hygiene.

### Phases B–E — NOT STARTED
- **B (output contract):** WS-1 `OutputNode` AST · WS-2 format-aware emitters · WS-12 `EnvelopeConfig`. Backfill live suppliers → AST, byte-parity gate before cutover.
- **C (designer):** WS-3 (3-pane visual⇄AST, inline Expression, `src/lib/mapping` extraction, characterization tests) · WS-6 canonical invisible · WS-7 6-modes→resolver+2 · WS-11 delete dead `IParsedOrderTransform` stack.
- **D (inference):** WS-4 paste-sample→infer AST · WS-14 template test fixtures.
- **E (consolidation):** WS-5 (5 areas: Orders/Supplier flows/Templates/Activity/Settings; named template scopes) · WS-8 hide versioning behind Save · WS-9 vocabulary purge + rename table · WS-13e dashboard funnel + topology→secondary.

---

## How to resume / verify
- **Local golden path** (`[[project-local-golden-path-and-hardening]]`): `PROCULINK_QA_BYPASS_AUTH=true` + local Postgres `:5435` + a 32-byte base64 `Delivery__EncryptionKey`; **Worker is mandatory** (API hosts no Hangfire).
- **Tests:** backend `dotnet test ProcuLink.slnx` (988 green at the baseline). **Postgres, not InMemory**, for FK/override/ExecuteUpdate work (InMemory masks Postgres). Frontend `bun run build` + `bun run test` (vitest) + e2e.
- **Live testing:** prod is real customer data — use disposable/sample data; admin per-org limit override + QA recipes in memory. proculink.eu (Vercel) / api.proculink.eu (Railway `ProcuLink` API + `aware-amazement` Worker, EU) / Neon Postgres / R2.
- **The real-PO benchmark corpus:** `~/Downloads/PO` (24 real POs + their DocParser target output mappings) — Phases B–D must reproduce those target outputs.

## Hard constraints (do not violate)
- No commercial EDI licences (hand-rolled / MIT only).
- Offer ⇔ works (every channel/format the UI offers must be a real tested capability).
- Worktree isolation for parallel chips (shared dir races on EF snapshot / .next).
- EF queries always `org_id`-scoped; Hangfire jobs idempotent; no raw SQL.
- Preview == delivery; fail loud, never deliver a silent default.

---

## Verified during analysis (don't re-litigate)
- ChatGPT's **validation false-positive is REAL** (code-confirmed) — Phase A fixes it.
- ChatGPT's **duplicate-delivery, mock-data-in-prod, silent-input-data-loss, metrics-lie** claims were **REFUTED or overblown** — not carried as work (details in masterplan P3.B / P4.A).
- The mapper was rebuilt ~3× in two weeks = re-litigation, not convergence. **The structural cut is the convergence point — stop re-skinning the mapper; build the AST + one transform path, freeze invariants with characterization tests.**
