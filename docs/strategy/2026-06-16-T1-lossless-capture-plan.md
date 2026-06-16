# T1 — Lossless source capture: every order has a complete, draggable source-field set

> Phase-1, first task of the approved redesign. Goal: kill "the canonical limits what gets parsed"
> and "nothing to drag" at the root, with the **smallest, lowest-risk** change. **Plan only — await go.**

## Why this is the right first cut (revised after grounding)

The original plan proposed a big `ParsedDocument` entity + migration. Grounding the actual code changed
the diagnosis — the cheaper, higher-confidence fix is different and the entity refactor is **not** needed
for the visible win:

- **`SourceTokenizer` is already excellent** (`ProcuLink.Transform/Tokenizing/SourceTokenizer.cs`): it
  fully tokenizes CSV / XLSX / XML / cXML / EDIFACT / X12 — every cell, every leaf element, every
  attribute — with stable ids (`cell:r2c3`, `/Order/Lines/Line[2]/Qty`, `seg:PO1[1].el7`) + human labels.
  It never throws (empty on malformed). It is **not** the problem.
- **The problem is delivery of those tokens to the mapper.** `GET /api/orders/{id}/source-tokens`
  (`OrdersController.cs:1126-1171`) **re-tokenizes the source file live from R2** and **ignores the
  persisted `SourceCapture`**. So it returns an **empty list** whenever:
  - the order has **no stored source file** (`SourceFileKey` null) → **API-ingress / pushed payloads**
    (e.g. the video's structured order) have **zero draggable source fields**;
  - the source blob was **purged** by retention (`SourceFilePurgedAt`);
  - the R2 download fails.
- **The FE then frames canonical as the source.** `IncomingPane` shows Header/Parties/Lines (canonical
  values) as primary and the real source tokens as a collapsed **"Raw extras"** drawer that it dedupes
  against canonical — so a clean structured order reads *"no extra raw fields to remap."* The supplier's
  **actual document fields are demoted to an afterthought.**
- The `OutputFieldRule`/`SourceMap` engine **already** lets any source **token** bind to any output field
  (`OrderMappingOverride.SourceMap`) — so once the tokens are reliably present + surfaced, the user can
  map **any** field to output **without** touching the canonical model. The canonical 12-field limit is a
  separate, later concern (output-side richness), not what blocks "design any field" today.

**So T1 = make the complete source-field set reliably present for EVERY order, and make the FE treat it
as the primary mapping surface.** No new entity, no migration. (The `canonicalJson` → `ParsedDocument`
refactor + the triple-overload split move to T2/T3, where they belong.)

## Scope

**T1a — Tokenize-at-ingest for every order + persist (backend).**
- At ingest (both the sync LLM path `OrderIngestionService.CreateFromFileAsync` and the async file path
  `ParseStoredFileAsync`, and the API/pushed-payload ingress path), run `SourceTokenizer.TokenizeAsync`
  on the **raw bytes we already hold** and persist the result via the existing
  `UpsertSourceCaptureAsync` → `SourceCapture.TokensJson`. (Today persistence happens on some paths but
  the API/pushed path may not hold bytes/ext — make it explicit and uniform.)
- For API/pushed JSON/XML payloads with no file: tokenize the **payload bytes** with the payload's format
  (JSON gets a JSON tokenizer — see T1d) and persist. The order keeps a draggable field set even with no
  stored file.

**T1b — Endpoint prefers persisted SourceCapture, falls back to live (backend).**
- `GetSourceTokens` (`OrdersController.cs:1126`): **first** return `SourceCapture.TokensJson` when present
  (the lossless persisted bag); **only** re-tokenize from R2 when no persisted capture exists; keep the
  graceful empty for genuinely-source-less orders. This removes the retention/purge/no-file fragility.

**T1c — FE: the complete source document is the primary surface (frontend).**
- `IncomingPane` / `incomingPaneModel`: show **all** source tokens grouped by the document's own
  structure (Header / Parties / Line items / Other), each with **label + real value + source pointer**.
  Stop demoting them to a collapsed "Raw extras" drawer and stop the canonical-dedup that hides them.
- Keep the canonical spine fields available, but the user maps from **the supplier's actual fields**.
- Replace the *"no extra raw fields to remap"* copy with an honest state (only shown when the order truly
  has no source — e.g. a hand-keyed order).

**T1d — JSON source tokenizer (backend, small).**
- Add a `.json` arm to `SourceTokenizer` (today JSON falls to the empty default): emit every JSON
  leaf as a token with a JSON-pointer id (`/lines/0/sku`) + label. This closes the gap for the most
  common pushed/structured format (the video's order is JSON-shaped).

**T1e — Golden "no field dropped" tests.**
- Per format (CSV/XLSX/XML/cXML/EDIFACT/X12/JSON), a real sample → assert the token set contains **every**
  source field (count + key fields by id) and that the endpoint returns them (persisted path + R2-fallback
  path). One API-push test (no file) → tokens still present from persisted capture.

## Files affected

| Area | File | Change |
|---|---|---|
| Tokenizer | `ProcuLink.Transform/Tokenizing/SourceTokenizer.cs` | add `.json` arm (T1d) |
| Ingest | `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` | tokenize+persist on every ingest path incl. API/pushed (T1a) |
| Endpoint | `ProcuLink.Api/Controllers/OrdersController.cs:1126` | prefer `SourceCapture.TokensJson`, fall back to live (T1b) |
| FE model | `project-proculink/src/components/bridge/mapper/incomingPaneModel.ts` | all tokens primary, drop canonical-dedup/“extras” demotion (T1c) |
| FE view | `project-proculink/src/components/bridge/mapper/IncomingPane.tsx` | honest empty-state copy (T1c) |
| Tests | `ProcuLink.Transform.Tests` + `ProcuLink.Api.Tests` | golden per-format + API-push (T1e) |

## Backend / Frontend / Data / Tests

- **Backend:** T1a (ingest tokenize+persist), T1b (endpoint precedence), T1d (JSON arm). No DB schema
  change — `SourceCapture.TokensJson` already exists.
- **Frontend:** T1c (pane model + view). No new endpoint (reuse `/source-tokens`).
- **Data:** none (additive use of an existing column). Optional one-off backfill: tokenize existing
  orders' stored files into `SourceCapture` so old orders also gain fields (idempotent, can run lazily).
- **Tests:** T1e golden per-format + API-push; FE vitest for the pane model (all-fields, no dedup-drop).

## Acceptance criteria

1. Upload **any** of CSV/XLSX/XML/cXML/EDIFACT/X12/JSON → the mapper's left pane shows **every** field
   in the document, with its real value + source pointer, all draggable.
2. An **API-pushed JSON/XML** order (no stored file) shows the same complete, draggable field set.
3. A **purged-source** order still shows its fields (from persisted `SourceCapture`).
4. The phrase "no extra raw fields to remap" only appears for a genuinely source-less (hand-keyed) order.
5. Any source field can be dragged to any output field and persists (via `SourceMap`) — no canonical
   gatekeeping.
6. Backend suite green; new golden tests assert no field is dropped per format.

## Risk + sequencing

- **Low risk:** additive backend (no schema change), reuses the existing tokenizer + capture + SourceMap;
  the FE change is presentational + a dedup removal. No delivery-path behavior change.
- **Independently shippable.** Visible to the founder immediately (drag any field).
- **Not in T1 (deferred, correctly):** the `canonicalJson` → `ParsedDocument` entity refactor + the
  triple-overload split (T2/T3 — output-side richness + storage hygiene), the one-mapper consolidation
  (T5), validation/output/UX work (T4–T10). T1 is the unblock; the rest builds on it.

## Execution (per the approved "workflows per phase")

When you say go: a per-phase workflow — **ground** (confirm the exact ingest/persist/endpoint/FE lines) →
**build** (T1a–e, TDD) → **adversarially review** (losslessness, the JSON tokenizer, the FE surfacing,
no delivery regression) → verify suites → deploy → live-verify (upload each format on prod, confirm every
field is draggable). Same rigor as the B12 pass that caught a real bug today.
