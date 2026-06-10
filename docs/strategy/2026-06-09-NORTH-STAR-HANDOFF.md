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

## In flight (launched 2026-06-09, will land on branches — MERGE these next)
1. **Group V1 backend core** → branch `auto/be-v1-connection`. SupplierConnection +
   SupplierConnectionRevision tables, draft→test→publish→archive, ConnectionRevisionId
   pinned to orders, backfill existing config → "rev 1 (published)", service + API,
   tests. **Owns the only migration this batch.**
2. **Source-token labels** → branch `auto/be-source-token-labels`. "row 9" → meaningful
   labels (column header + row/value) so a user can identify + map the exact source
   field. No migration.
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

## Resume instructions
1. Merge the 3 in-flight branches (recipe above), verify combined green, deploy both.
2. Build the **V1 Connection UI** page (the per-supplier editors become "edit draft
   revision"; publish/rollback controls) against the V1 API.
3. Execute **V2 (replay)**, then continue V4/V6/V8/V10 per priority.
4. Keep a real order flowing end-to-end at every step.
