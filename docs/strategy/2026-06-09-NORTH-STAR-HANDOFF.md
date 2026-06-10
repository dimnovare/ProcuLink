# North Star execution — session handoff (2026-06-09)

Durable state so work continues per the plan if context resets. **Read this first**,
then `docs/strategy/2026-06-09-supplier-connection-north-star.md` (the plan of record)
and `docs/strategy/2026-06-09-V1-versioned-connection-plan.md` (the V1 blueprint).

## Where we are (all shipped + live + verified)
- **Strategic PIVOT done:** dropped Baltic-bootstrap/sell-first/freeze-features. Active
  goal = the **Versioned Supplier Connection** platform. `CLAUDE.md` "Current direction"
  points to the North Star memo; memory `project-north-star-pivot` records it.
- **Backend `main` = `18af325`** (pushed → Railway, API 200/200, **1765 tests green**,
  `AddAiSuggestionDecisions` migration applied clean on prod). Shipped: per-field +
  whole-document Scriban templates, all-format per-order override + template-aware
  live-preview endpoint, output-format validation, delivery stuck-sweep, best-price
  overage fix, AI decision-history table, real OutputTemplates Config/SuppliersCount,
  editable-resolve (poNumber/supplierName), promote fix.
- **Frontend `main` = `ceca783`** (pushed → Vercel). Shipped: the heart-piece wiring
  revamp (sticky panes + rAF scroll-resync, snap-to-circle, tidy curves, hover-highlight,
  chip-taming 255→5 + default-collapsed doc, editor backdrop), the **Scriban template
  editor** (toggle + proposed-structure panel + starter + live preview), **all-format
  mapping preview**, save-mappings fix.

## Deterministic proof
- 8×7 IN×OUT matrix (every parser × every transform) = **56/56 structurally valid**
  (`ProcuLink.Transform.Tests/FormatMatrix/FullInOutMatrixTests.cs`).

## V1 + labels — ✅ SHIPPED (backend `main` = `14c2144` → Railway, API 200/200, 1807 tests green)
1. **Group V1 backend core** — SHIPPED. Migration `20260610110331_AddSupplierConnections`
   applied clean on prod; `supplier_connections` + `supplier_connection_revisions` +
   child tables + `purchase_orders.connection_revision_id`; idempotent boot backfill;
   ConnectionRevisionId pinned at ingest; `/api/connections` API (list/get/ensure-not-
   present/create-draft/update/test/publish/archive) live; 21 new tests incl.
   byte-identical backfill + lifecycle + org-scoping + pinning. **`ConnectionResolver`,
   `ConnectionBackfillService`, `SupplierConnectionService` in ProcuLink.Api.**
   ✅ **BACKFILL RESOLVED** (`f6ee1c1`): prod `/api/connections` first returned 0 — root
   cause was a connection↔revision **circular-FK insert in ONE SaveChanges** (Postgres
   can't order two mutually-referencing inserts → threw → backfill aborted). The InMemory
   test provider doesn't enforce FKs so 21 tests passed anyway. Fixed: insert connection
   with `active_revision_id=NULL`, then revision, save, then set the pointer + save again;
   + per-supplier try/catch so one bad row can't abort the batch. **Verified live: 2
   connections backfilled** (both suppliers, published rev-1). NOTE: API has no
   `/api/connections/ensure` (404) — use create-draft; confirm exact routes in
   `ConnectionsController.cs` for the FE.
2. **Source-token labels** — SHIPPED. "row 9" → meaningful labels (column header + row,
   readable XML paths, EDIFACT/X12 segment meanings); +21 Transform tests. token.id
   unchanged.
3. **FE cleanups** → ✅ SHIPPED (FE `main` = `274320b` → Vercel). Editable PO#/supplier
   node (sends poNumber/supplierName via resolve) + full hide-wires-when-editor-open
   (OutputPreview relays mapOpen up; SpineReview feeds the wire layers' `hidden` prop +
   skips SpineConnectors while the editor is open). Verified green + live-mock-QA'd.

   MERGE RECIPE (proven this session): worktree agents commit to `auto/*`; `git checkout
   main` → `git merge --no-ff` each (disjoint files → clean; resolve OrderTransformService
   / OrdersController if they recur) → one full `dotnet test ProcuLink.slnx` → push →
   Railway. FE: `git merge` the FE branch → `bun run build` → push → Vercel. Then
   `git worktree remove --force` each + `git branch -D` + `git worktree prune`.

## Roadmap status (groups from the North Star memo)
- **V1 versioned connection core** — IN FLIGHT (backend), then UI page next.
- **V2 replay/impact testing** — NEXT after V1. Run historical orders through a draft
  revision without delivering; diff canonical/validation/output before publish.
- **V3 output templates runtime** — engine done (Scriban + editor); finish supplier
  assignment + artifact revision pinning + rollback as part of V1's revision bundle.
- **V4 unified validation** — ✅ SHIPPED + LIVE (`10bb6f1`). Org-level RuleDefinition + versioned
  bindings; EvaluateProfile unchanged (bind, didn't rebuild); 12-entry catalog seeded live.
- **V5 deepen canonical** — staged; Scriban template is the flexible escape hatch.
- **V6 exception-first UI** — progressive disclosure (what's wrong/why/fix/remember/
  notified+accepted); topology as overview. Model delivered≠accepted (receipts/ACK). **NEXT (FE).**
- **V7 connector SDK** — ✅ BACKEND SHIPPED + LIVE (`082393b`). Code-defined `ConnectorManifestCatalog`
  (no migration, additive — 6 new files only), built + adversarially reviewed (verdict: safe, additive-only,
  offer⇔works, migration-free, 1942 tests green incl. 571 new). `GET /api/connector-manifests` (6 manifests:
  http/sftp/ftps/smtp/erp_erply/erp_directo — each mirrors a REAL wired dispatcher; bare ftp excluded as it
  has none), `GET /api/connector-manifests/{key}`, `POST /api/connector-manifests/{key}/validate-config`
  ({valid,missing[],unknown[]}). LIVE-verified: 6 manifests returned, validate-config correctly flags
  missing `url` + unknown keys. FE follow-up (manifest-driven connector config UI) still open.
- **V8 conformance reports** — ✅ SHIPPED + LIVE (`10bb6f1`). 5 profile checkers (cXML/UBL/X12/
  EDIFACT/IDoc), `GET /api/orders/{id}/conformance?format=`, downloadable Markdown. FE tab pending.
- **V9 AI decision history** — table SHIPPED; calibration is the follow-up.
- **V10 catalog scale** — indexed Postgres (pg_trgm/full-text) replacing ≤2000 in-memory.
  NEEDS A MIGRATION → run in a batch where V1's migration isn't also pending.
- **Cross-cutting:** pricing-overage SHIPPED; eng hygiene pending (PageShell-in-CI,
  typed OpenAPI client, decompose SpineReview/api-client, `@next/mdx` v16→15 align).
- **Small FE:** editable PO# + hide-wires + labels IN FLIGHT; **live 2000-combo matrix**
  still pending (deterministic 56/56 already proves validity; live = volume proof).

## Hard rules / gotchas (carry forward)
- Worktree-isolate parallel BACKEND agents; **one migration per parallel batch** (EF
  snapshot collides otherwise). FE repo (project-proculink) can't be worktree-isolated by
  the Workflow → run ≤1 FE agent at a time (no concurrent FE editing).
- Scriban = power-user escape hatch, NOT default. Don't build a 2nd rules engine.
- HTTP 200 ≠ supplier business acceptance. STOP overloading `canonical_json` — new
  connection concepts get FIRST-CLASS tables.
- Template-preview endpoint contract: `POST /api/orders/{id}/mapping-override/preview`
  (optional draft override body); template mode → `{ok,output,contentType}` /
  `{ok:false,error}`@200; field mode → `{format,content}`. Safe per keystroke.
- MCP Chrome automation window runs BACKGROUNDED → rAF paused → can't film rAF behaviour;
  verify rAF features by reasoning + the founder's focused screen.
- `dotnet`/long bash need `dangerouslyDisableSandbox:true`. bun never npm.

## BATCH 2 — ✅ ALL SHIPPED + LIVE (backend `main` = `e52ca2d` → Railway, API 200/200, 1824 tests green)
- **V2 replay/impact testing** — SHIPPED + VERIFIED LIVE. `POST /api/connections/{cid}/revisions/{revId}/replay`
  (body `{orderIds?|recentLimit?}`, cap 50) returns per-order output/effective-value/validation
  diffs, non-mutating, never delivers. Reuses the transform engine + a pure extracted
  `SupplierAcceptanceService.EvaluateProfile`. Live smoke: replayed rev-1 over 5 real orders
  → 200, outputChanged/validationChanged=false (correct, same revision), output re-rendered.
  Full contract (ReplayResponse/ReplayOrderDiffDto) in the workflow result — feeds the V2 UI
  that replaces the "Coming soon" placeholder on the connection detail page.
- **V10 indexed catalog retrieval** — SHIPPED. Migration `20260610120109_AddCatalogTrigramIndexes`
  (pg_trgm + GIN trigram indexes on supplier_products.code/name + barcode btree; idempotent
  raw SQL) **applied clean on prod (API healthy)**. `CatalogRetrievalService` exact→trigram;
  threshold-gated so catalogs ≤2000 use the unchanged in-memory path (byte-identical). ⚠️
  the trigram RANKING path itself only triggers for >2000-SKU catalogs and can't be proven by
  InMemory tests — exercise it with a real large catalog when one exists (extension + indexes
  are confirmed live).
- `auto/fe-v1-connections-ui` — ✅ SHIPPED (FE `main` = `78fb893` → Vercel). Connections
  list + detail (active-revision bundle summary + revision history) + full lifecycle
  (create-draft/clone, mark-test, publish-with-confirm, archive, rollback), sidebar nav,
  409→inline-error. Replay = "Coming soon" placeholder (V2). Draft component editing links
  to the existing per-supplier editors. EXACT routes (verbatim from ConnectionsController):
  GET /api/connections; GET /api/connections/{id}; POST /api/connections/ensure/{supplierId};
  GET|POST /api/connections/{id}/revisions[/{revId}]; PUT .../{revId}; POST .../{revId}/{test|publish|archive}.
  MERGE: backend two (V10 owns the migration, V2 disjoint) → one `dotnet test` → push →
  Railway (verify the pg_trgm migration applied + catalog path live). FE → bun build → push.

## BATCH 3 — ✅ ALL SHIPPED + LIVE (backend `main` = `10bb6f1` → Railway, API 200/200, 1872 tests green)
- **V4 unified validation** — SHIPPED + VERIFIED LIVE. Migration `20260610123131_AddRuleDefinitions`
  (purely additive: new `rule_definitions` table with a SINGLE FK→organisations + two nullable
  columns `rule_definition_id`/`rule_code` on `supplier_acceptance_rules` + 2 indexes — **NO
  circular FK by design**, the V1 trap avoided) applied clean on Neon. `RuleDefinition` is an
  org-level rule TEMPLATE; `SupplierAcceptanceRule` gained an optional versioned binding (nullable
  FK + denormalised `rule_code`); **`EvaluateProfile` is byte-for-byte unchanged** — the rule's own
  scalar columns stay the executor's source of truth (a per-binding severity override still flows).
  Idempotent boot backfill seeds the 12-entry `RuleCatalog` per org + links loose rules (never
  mutates rule scalars), wired after the V1 connection backfill. Read API: `GET /api/rule-definitions`,
  `GET /api/rule-definitions/{id}`, `GET /api/suppliers/{id}/rule-bindings` (Definition carries the
  standards refs → satisfies the standards-visibility rule). **LIVE PROOF:** `GET /api/rule-definitions`
  = 200, **12 seeded defs** with real UBL/EDIFACT/X12/cXML refs → migration + seed confirmed ran on
  Postgres (NOT the V1 zero-backfill bug); `rule-bindings` = 200 (count 0 for the sample supplier =
  correct, it has no acceptance rules).
- **V8 conformance reports** — SHIPPED + VERIFIED LIVE. Pure stateless `ConformanceService` in
  ProcuLink.Transform/Conformance validates a generated OUTBOUND doc vs its NAMED profile
  (structural + mandatory-element/segment + key-cardinality; NOT a full XSD/EDI engine). 5 checkers
  (Cxml/Ubl/X12/Edifact/IDoc), each finding a named `ConformanceCheck{code,severity,passed,message,
  profileRef}`, overall pass = no Error-severity failure, deterministic `ToMarkdown()` downloadable.
  `GET /api/orders/{id}/conformance?format=cxml|ubl|x12` (defaults to supplier delivery format;
  honest 400 when no format). NO migration. **LIVE PROOF:** on a real order — cXML→`CXml12OrderRequest`
  (15 checks, pass), UBL→`Ubl21Order` (14, pass), X12→`X12_850` (12, pass), all 0 failing.
- `auto/fe-v2-replay-ui` — ✅ SHIPPED (FE `main` = `040d972` → Vercel). ReplayPanel on
  ConnectionDetail: revision picker + recent-window, per-order rows sorted most-dangerous-
  first, "would start failing" flagged red, expandable output diff / field-change table /
  validation-flip rows, non-destructive. Uses the live replay endpoint.
- **Batch-3 FE + V6 — ✅ SHIPPED + LIVE-VERIFIED (FE `main` = `a5d1854` → Vercel).** Branch
  `auto/fe-v6-exception-first` merged (1243 +/35 −, `bun run build` green, 20/20 unit tests).
  THREE features, all read-only over now-live endpoints, all verified rendering real prod data:
  (1) **Conformance viewer** — `ConformancePanel` as a "Conformance" tab on the order-review screen
  (SpineReview) with `?tab=conformance` deep-link; cXML/UBL/X12 selector + checks table + real
  authenticated Markdown download (`GET /api/orders/{id}/conformance?format=…&download=md`). LIVE:
  tab renders, format selector + checks + download present, no error. (2) **Rule-definitions
  catalog** — new `/library/rule-definitions` route (+ sidebar nav under the Library group, gated by
  `NEXT_PUBLIC_LAUNCH_FULL_NAV` like its siblings) reading `GET /api/rule-definitions`; grouped by
  scope; standards refs (UBL/EDIFACT/X12/cXML) behind a `StandardsRefList` disclosure. LIVE: shows
  "12 definitions", all codes, Order/Line groups. Plus an "Active rule bindings" panel on
  `SupplierDockProfile` reading `GET /api/suppliers/{id}/rule-bindings` (clean empty state at count
  0). (3) **Exception-first elevation** — `/operations/exceptions` rows/cards expand
  (`ExceptionDetail`) to what's-wrong / why / how-to-fix / honest delivery status (delivered≠accepted),
  linking to the conformance tab + supplier bindings. Verified the agent's `ConformanceReport` TS
  interface matches the raw prod JSON exactly (`profile`/`profileName`/`profileVersion`/`overallPass`
  at report level; `profileRef` per-check) — no casing bug.

## Resume instructions — backend `082393b`, FE `a5d1854` (all live)
North-Star groups V1, V2, V3, V4, V6, V7(backend), V8, V9, V10 are all SHIPPED + LIVE-VERIFIED.
**Only V5 remains of the numbered groups**, plus FE follow-ups + eng hygiene. Remaining, by priority:
1. **V5 deepen canonical** — HIGH BLAST RADIUS (touches the core ParsedOrder model + every
   transform; output for existing orders MUST stay byte-identical). Do NOT autonomous-background
   this — design it carefully, add fields additively, prove byte-identical output on the 8×7 matrix
   + a live order before/after. Scriban remains the flexible escape hatch for anything not yet
   first-class.
2. **FE follow-ups (net-new, read/CRUD over live endpoints):** (a) a manifest-driven connector
   CONFIG UI consuming `GET /api/connector-manifests` + `validate-config` (render the right fields
   per transport, secret-mask secret fields, validate before save) — wire into the supplier
   Delivery tab / connections; (b) deeper V6 polish if the founder wants it.
3. **Eng hygiene:** PageShell-in-CI, typed OpenAPI client, decompose SpineReview/api-client,
   `@next/mdx` v16→15 align. **Live 2000-combo matrix** (runner at
   project-proculink/scripts/live-matrix/runner.js) — deterministic 56/56 already proves validity.
4. Keep a real order flowing end-to-end at every step. One FE agent at a time (FE repo can't be
   worktree-isolated); backend agents worktree-isolated, ONE migration per parallel batch.

PROVEN THIS SESSION (recipe): backend agent(s) in worktrees → commit to `auto/be-*` → (adversarial
review for risky ones) → `git merge --no-ff` → ONE `dotnet test ProcuLink.slnx` → push → poll the
new route 404→401 to confirm the Railway deploy → live-verify content via the authenticated browser
tab 1405651030 (`window.Clerk.session.getToken()` → fetch the endpoint). FE: merge → `bun run build`
→ push → poll `proculink.eu/<newroute>` DOM (not curl — Next middleware 200s everything) → verify
real data renders. Then `git worktree remove --force` + `git branch -D` + `git worktree prune`.
