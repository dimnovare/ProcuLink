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
- **V4 unified validation** — catalog → reusable rule defs; connections → versioned rule
  bindings. BIND to SupplierAcceptanceService, don't rebuild.
- **V5 deepen canonical** — staged; Scriban template is the flexible escape hatch.
- **V6 exception-first UI** — progressive disclosure (what's wrong/why/fix/remember/
  notified+accepted); topology as overview. Model delivered≠accepted (receipts/ACK).
- **V7 connector SDK** — manifest-driven config UI.
- **V8 conformance reports** — validate vs named profiles; downloadable cXML/UBL/X12/
  EDIFACT/IDoc reports (seed = the 8×7 matrix).
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

## BATCH 2 IN FLIGHT (launched 2026-06-09 after V1 shipped) — MERGE when they land
- `auto/be-v2-replay` — Group V2 replay/impact testing (run historical orders through a
  DRAFT revision, non-mutating, never delivers; returns canonical/validation/output diff).
  NO migration. Backend worktree.
- `auto/be-v10-catalog` — Group V10 indexed Postgres catalog retrieval (pg_trgm + GIN
  index; exact-match → trigram ranking; small catalogs unchanged). **OWNS the only
  migration this batch.** ⚠️ trigram needs LIVE-POSTGRES verification (InMemory can't test
  pg_trgm — see project-inmemory-masks-postgres-fk); verify the indexed path live after deploy.
- `auto/fe-v1-connections-ui` (project-proculink) — the Connections list + detail +
  draft→test→publish→archive/rollback lifecycle UI against the live /api/connections API.
  Replay UI is a placeholder until V2 lands.
  MERGE: backend two (V10 owns the migration, V2 disjoint) → one `dotnet test` → push →
  Railway (verify the pg_trgm migration applied + catalog path live). FE → bun build → push.

## Resume instructions
1. Merge BATCH 2 (above) + any earlier in-flight branches (recipe in the merge section),
   verify combined green, deploy, verify the V10 migration + trigram path on live Postgres,
   and confirm the V1 Connections UI renders the backfilled connections.
2. Build the **V1 Connection UI** page (the per-supplier editors become "edit draft
   revision"; publish/rollback controls) against the V1 API.
3. Execute **V2 (replay)**, then continue V4/V6/V8/V10 per priority.
4. Keep a real order flowing end-to-end at every step.
