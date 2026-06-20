# ProcuLink PO Tool — Brutal Redesign Analysis (2026-06-19)

> Status: **ANALYSIS + PLAN. No product code written. Awaiting founder approval before implementation.**
> Method: live prod QA (83 real orders) + 8-agent parallel codebase analysis (import, canonical model, mapping, validation, output, architecture, frontend-UX, adversarial synthesis).

---

## Brutal Verdict

**PARTIAL REDESIGN — converge and delete. ~70% removal, ~30% targeted new UI. Not a full rebuild. Not keep-and-improve.**

You do not have a *too-little* problem. You have a **two-of-everything** problem.

The output engine you are asking me to build **already exists in the repo and is tested**: a format-parametric `OutputNode` AST + `OutputTemplateEmitter` that does nesting, arrays, attributes, namespaces, conditionals and format presets, reusing the exact same leaf-value resolution as delivery (so values are byte-identical). The problem is that **every good new model was added *next to* the old one instead of *replacing* it.** So today you ship:

- **two** output models (the new AST + the old flat `OutputMappingConfig`), and the runtime silently picks one;
- **two** preview implementations (a 220-line controller hand-copy of the transform);
- **three** review screens shipping at once (`SpineReview` ~2,587 lines + a `mapper/` generation + the `workshop/` generation) — gated by a flag that historically defaulted to the *oldest*;
- **three** format detectors;
- **three** mapping surfaces with three data models;
- an output override that **squats inside `purchase_orders.canonical_json`** next to four other tenants.

The bugs breed at the seams where the new model and the old model must agree and don't. That is why it feels buggy *and* complicated at the same time — they are the same disease.

A full rebuild would be the **third** rewrite of this layer and would throw away the 70% that works. Keep-and-improve cannot work either: as long as two output models are both live, the "+Add field silently dropped," "which editor wins," and 13-field-drop bugs **remain by construction**. The only correct move is structural: make the good model the *only* model, delete the shadows, give the override a real table, make preview *call* the transform, and let the designer bind to *any* incoming field.

---

## Why Current Solution Fails — the root causes (not symptoms)

Five root causes explain ~90% of the bugs and the complexity. The first four came back independently from multiple reviewers; the fifth is the dead button you are staring at.

**RC-A — "Cure added next to the disease, never in its place."** New AST shadows old flat config; a 7th transform precedence branch was prepended to a god-method whose own comment still says "SIX modes"; three review UIs ship together; three format detectors; three mapping surfaces. Every seam where new-must-equal-old is a bug nursery.

**RC-B — `canonical_json` is a god-column with four tenants.** The per-order output override (including the new AST) lives as schema-less JSON merged into the same column as the parsed order, the denormalized `buyerName`, and enrichment provenance. The `SourceCapture.cs` comment *itself* calls the column "already triple-overloaded"; the North Star memo says "stop overloading CanonicalJson"; the code does the opposite. Most historical "collapse-on-wire / wrong-format / accept-all-mismatch" bugs sit at a read/write seam of this column.

**RC-C — Parity-by-discipline instead of parity-by-construction.** Preview re-implements the entire transform precedence chain in a ~220-line controller method whose comments literally say "mirror exactly that." Validation has two contracts (a flat backend DTO vs. a frontend `rule` object rebuilt by `code.split(".")[0]` + a hard-coded `operator:"equals"`). Detection shows "a guess about a guess" because the preview sniffer isn't the parse code. Anything kept in sync by human vigilance **will** drift — and reviewers caught it mid-drift.

**RC-D — Canonical is a narrow 13-field struct that is BOTH the user vocabulary AND the lossy choke-point.** `ParsedOrder` is a positional 5-header + 6-line record. The structured parsers (UBL / X12 / EDIFACT / cXML / CSV) build the bare constructor and **drop parties, ship-to addresses, VAT, alternate SKUs (EAN/MPN)**. Losslessness is delivered entirely by a *parallel* `SourceCapture` token side-channel with **no provenance edge** linking a canonical field back to the token it came from. Then the output designer can only bind to those 13 fields — so "put this arbitrary incoming field into the output" is impossible in the UI even though the engine supports it. **The same 13-field bottleneck starves both the input and the output ends.**

**RC-E — UI controls address a granularity the model doesn't have (your dead-button bug).** The blocker chips pass a bare **line-GUID** to a mapper keyed only by **output-path**. No map contains a line GUID, so `scrollIntoView` is a guaranteed no-op (confirmed live: the GUID matches zero DOM elements, nothing flashes). The "resilient" resolver was written against an imagined `lines[{guid}].itemCode` key the model never emits — which is why Task #123 was marked "fixed" twice and the button is still dead. A fuzzy matcher cannot bridge a GUID to an output path; there's nothing to match on.

### Live prod evidence (83 real orders, today)
- **78 / 83 (94%) stuck in `pending_review`** — real-world POs almost never auto-complete; nearly all need manual supplier-code resolution.
- **Blocker chips inert** — root-caused above (RC-E), confirmed in the live DOM.
- **Output preview won't switch format** — requesting JSON/XML (even with `honorFormat`) returned CSV every time. Your "I can't design the output the way I want," reproduced at runtime.
- **Duplicate review surfaces** — the old "Fix these to send" heading renders alongside the new send-readiness strip on the same order.
- **"Ready" but 8 validation issues** — validation is an invariant checklist (`{key,state,blocking}`); fine as a model, but labeling a `ready` order "8 issues" is confusing.

---

## What Should Be Kept (do not re-litigate)

| Keep | Why |
|---|---|
| `OutputNode` AST + `OutputTemplateEmitter` (nesting/arrays/attributes/namespaces/`IncludeWhen`, reuses leaf resolution → byte-identical values) | The expensive, correct core. Five of seven reviewers said "do not rebuild this." |
| Paste-a-sample → infer structure (`OutputNodeTemplateInferrer`) — deterministic, no-egress, handles JSON/CSV/XML incl. UBL prefixes | This is the on-ramp that makes the designer usable. |
| Date / number / currency **format presets** (no Scriban needed) | Non-technical formatting control. |
| `InvariantValidator` (fail-closed input trust) + fail-closed unknown-operator | Real safety; keep. |
| Loud-fail: a configured override that throws → revert to `ready` → **never deliver the wrong default** | Trust invariant. |
| Honest refusal of cXML/UBL/X12 from the generic tree (offer⇔works) | Keeps marketing honest. |
| AI SKU suggestion: strict JSON schema, **catalog allow-list guard**, per-org token cap, no-egress, never auto-applied | Correct guardrails. |
| `CsvOrderParser` locale-safe decimal parser that refuses to guess and flags for review | Prevents silent 10×/100× price corruption. |
| `SourceCapture` verbatim token side-channel (survives blob purge) | Keep the data; fix the missing provenance edge. |
| Pinned `ConnectionRevisionId` reproducibility | Replay/rollback model is sound. |

## What Should Be Removed / Collapsed

| Remove | Replace with |
|---|---|
| Flat `OutputMappingConfig` as a persisted output mode + `MappedTransformService.BuildJson/BuildCsv` hard-coded `{header,lines}` + `EffectiveEntityResolver` 13-field allow-list | The AST, with read-time backfill of flat→AST behind a byte-parity gate |
| `OutputTemplate` raw-Scriban as a parallel **whole-document** mode | Raw Scriban as a **per-node terminal escape only** |
| 5 of 7 transform precedence branches; the "SIX modes" god-method | "Which AST wins (order→supplier→revision)" → one emitter call |
| `OrdersController.PreviewMappingOverride` 220-line twin | Preview **calls** the extracted pure render function |
| Two of three review UIs (`SpineReview` + one dormant generation); the "Triage \| Full document" toggle | One screen (the Workshop) with per-line inline fixes |
| Triplicated format detection | One `ISourceFormatDetector` consumed by routing, preview, tokenizer |
| Three per-source upload allowlists (drifted) | One `SUPPORTED_FORMATS` registry |
| One of two order-review mapping surfaces (`MagicMappingPreview` vs picker) | One surface; line-code resolution as an inline cell |
| Override living in `canonical_json` | First-class `order_output_overrides` table (typed `output_tree_json` + FK + org scope) |
| Hard-coded `PO-DEMO-001` demo rows leaking into real inboxes | Real data only |

---

## Recommended Product Concept

Your A–G model (Source → Detected Structure → Internal Model → Mapping → Validation → Output Template → Preview) is **correct**, with two amendments forced by the root causes:

1. **The internal model must stop being a 13-field bottleneck.** Keep the canonical PO model as the *stable vocabulary*, but every field carries a **provenance edge** to the source token it came from, and the designer can bind to **any captured source field or custom field**, not just the canonical 13. Detected Structure = the token capture; Internal Model = canonical + provenance; they are linked, not parallel.
2. **The Output Template is the AST tree — one artifact, two zoom levels, one escape hatch.** Not three parallel editors. Preview is not a sibling of delivery; preview **is** delivery rendered early.

---

## Recommended User Flow

```
Upload (file first → suggest supplier)
  → Review (ONE screen: incoming values + auto-run issues, each fixable in place)
  → Output Designer (tree picker, bind any field, format presets, paste-a-sample, live = delivery)
  → Send (gated only on real errors; warnings counted separately)
  → Library (one "Output design" area; reusable templates; history)
```

One happy path. Power features by progressive disclosure (Scriban escape, standards popover, calculated fields), never as a parallel mode.

## Recommended Screen Structure

1. **Upload** — file first, supplier second; auto-suggest supplier from parsed buyer; a fresh org can upload immediately (today it hard-gates on supplier → a new org literally cannot upload). Kill demo-row leakage.
2. **Review (the Workshop, the only review screen)** — incoming values from the canonical order; issues = invariants + acceptance + output checks, **run automatically on mount + after each commit** (not a button); each issue **fixed in place**: inline supplier-code entry for line issues, inline edit / currency picker / date-disambiguation chip for header issues. No "Triage | Full document" toggle.
3. **Output Designer (the money screen)** — tree-backed picker; bind any source field or calculated value; format presets; paste-a-sample to scaffold; **one live preview that is byte-for-byte the delivery**, with validation inline. Format source of truth = the supplier's delivery format (kill the designer's independent format pills).
4. **Send** — gate on `severity === "error" && status === "fail"` only; warnings separate; tooltip explains the block.
5. **Library → "Output design"** — collapse Mappings / Rules / Rule definitions / Output templates / Standards into one area; Standards becomes a reference popover, not a nav item.

---

## Mapping Editor Design

**Default = the inline picker, not free-form drag-wires.** The SVG-wire engine (`MapperWorkbench`) has failed in production **four times** — its own header comment is the changelog of those failures. The pure logic under it (`mapperModel.ts`) is clean; the *interaction metaphor* is the fragile part. The picker (`mappingMode="picker"`) already exists, is reliable, and works on mobile.

- **Automatic:** field suggestions with confidence + provenance; deterministic alias matches auto-applied; AI SKU suggestions surfaced (never auto-applied), catalog-grounded.
- **Manual:** one click per unresolved line to pick/enter the supplier code, **inline in the row** (this is also the fix action for the "Needs a supplier code" blocker — so the dead chip becomes a working inline cell).
- **Avoid overwhelm:** auto-mapped fields collapse to a summary ("12 fields mapped automatically"); only unresolved items demand attention. Power affordances (transforms, defaults, custom fields, standards mapping) via per-row disclosure + command palette, never a global mode toggle.
- **Reusable:** mapping templates per supplier; remembered resolutions; schema-fingerprint auto-apply once confidence is proven.
- Input mapping must be **format-agnostic and additive** (overlay, never erase unmapped fields). Until then, disable the magic-mapper for non-CSV (today it silently routes only CSV through the template — the empty-line trap).

---

## Output Designer Design (your #1 priority)

**HYBRID: one tree-backed model, two zoom levels of one visual editor, raw template as a per-node escape.** Not "visual vs tree vs template" — that framing is the trap that produced the current three-parallel-editors mess.

1. **The model is the tree** (`OutputNode` AST) — already built. Everything renders from it; nothing else persists as an output mode.
2. **Default editor = structured field list / inline picker** for the daily 90% (rename, static value, format, bind).
3. **Structural designer = the same artifact zoomed out** for nesting / arrays / attributes / namespaces. The daily mapper's add/wire/fixed-value handlers mutate `override.outputTree` **directly** — "inline mapper" and "Edit output structure" become one model at two zoom levels.
4. **Binding dropdown exposes the REAL source** — every `SourceCapture` token + every custom field, **with names and sample values** — plus a "Calculated" option writing `OutputFieldRule.Expression` (engine already honors it → zero backend work). This is the DocParser-parity bar and the single change that lets the designer reproduce a real supplier's file.
5. **Raw Scriban survives only as a per-node terminal escape** for power users — never a competing whole-document mode.
6. **One preview = the delivery bytes.** Format driven by the supplier's delivery format; validation surfaced inline before export.

Supports (all already in the engine, just needs the UI converged): nested objects, arrays/line-items, conditional fields (`IncludeWhen`), static values, renamed fields, date/currency/number formatting, calculated fields, required fields, supplier/customer-specific templates, preview with real PO data, validation before export.

---

## Validation and Fix Design

Validation is a **checklist of invariants + acceptance rules + output-render checks**, run automatically.

- **Run on mount + after every commit**, not behind a button (today, flipping the workshop flag ships a screen where validation may never run — a release blocker).
- **Numeric comparisons (`min`/`max`/`greater_than`) compare typed `decimal` columns**, never `ToString(InvariantCulture)` round-trips (silent price corruption is a money bug).
- Each issue shows: **what** is wrong, **why** it matters, **where** it is (links to the exact line/field that actually focuses — fixing RC-E), **suggested fix**, **one-click fix** where possible (apply AI code, set currency, disambiguate date), **manual fix** inline.
- Detects: missing required fields, invalid dates, invalid quantities/prices, missing SKUs, unknown currencies, broken addresses, empty/duplicate line items, wrong types, **and output-template render errors** (route these into the same issue list, no duplicates).
- **One contract, one severity model.** Kill the dual flat-DTO/`rule`-object split. Send gates on real errors only; warnings counted separately and labeled "optional."

---

## Recommended Data Model

- **`order_output_overrides`** (new table): `id`, `org_id`, `order_id` FK, `output_tree_json` (typed), `format`, `created_at`, `revision_id?`. Move the override OUT of `canonical_json`.
- **Canonical PO model**: keep the stable field vocabulary; **add `Parties[]` (buyer/supplier/ship-to/bill-to with addresses) and line `Identifier[]` (SKU/EAN/MPN/buyer-part)**; every field carries `sourceTokenId` provenance.
- **`SourceCapture`**: becomes the parse front-end (tokenize first), not a side-channel; provenance edge canonical-field → token.
- **One canonical field registry** (server-owned) drives both C# and TS — kill the divergent hard-coded lists.
- **Templates**: supplier-level output template (AST) + input mapping template, both versioned via the existing `ConnectionRevisionId`. History/test-runs piggyback on revisions + delivery attempts (already present).

## Recommended Architecture

- **One `ISourceFormatDetector`** consumed by routing + preview + tokenizer (delete the other two).
- **One pure render function** `(order, effectiveConfig, format) → bytes`; transform, preview, and test-export all call it. Delete the controller twin. **Preview == delivery by construction.**
- **One output resolution rule**: which AST wins (order override → supplier template → revision snapshot) → one emitter call. Collapse the 7 precedence branches to 2 (AST-render vs per-node Scriban escape).
- **One review screen** (Workshop); delete `SpineReview` + the dormant generation behind the flag.
- Frontend decoupled from business logic: the mapper/designer mutate a typed override object via a thin API; no business rules in components.
- Deterministic everywhere except SKU suggestion + structure inference fallback (AI), both guarded (catalog allow-list, token cap, no-egress, never auto-applied).

---

## MVP Scope

**Must exist:** one output model (AST) end-to-end; designer binds to any source field; preview == delivery; one review screen with working inline fixes; validation auto-runs; one format source of truth.
**Can wait:** full cXML/UBL into the AST (envelope-carrier hack works today), calculated-field UI polish, vocabulary purge, dashboard funnel, multi-sheet XLSX, drag-wire visualization.
**Remove now:** the duplicate output model, the preview twin, two review screens, triplicated detection, demo-row leakage.
**Hardcode temporarily:** the canonical field list may stay hard-coded in Phase 1; make it data-driven in Phase 4.
**Flexible from day one:** the override table + the binding-to-any-source-field (these are the founder's actual ask).

---

## Implementation Plan

**Phase 0 — Stop the bleeding (days; honesty/trust bugs you hit week one):**
- Fix inert blocker chips: line-scoped blockers resolve to a **real focusable key** (or restore the per-line inline editor as the fix). Add a test asserting the chip id focuses a real element (current tests check `buildFixQueue` purity but never that the id is focusable — that's how it shipped "fixed").
- Wire `validation.validate()` into the Workshop (on mount + post-commit) + thread the supplier's real `?format`. Non-negotiable before the workshop flag flip.
- Culture-safe numeric comparison in the validator.
- Unify the per-source format allowlist; fix `.txt` and "no line items"-vs-"unrecognized columns" offer≠works lies.
- Interim guard: if `outputTree` is set, the flat mapper shows "structure designer is active," not fake wires.

**Phase 1 — Converge the output model (the core cut):** backfill flat→AST on read behind a byte-parity gate on the real corpus; delete `BuildJson/BuildCsv` + the 13-field `EffectiveEntityResolver`; collapse 7 modes → 2; move override to `order_output_overrides`; extract the pure render fn; preview calls it; delete the twin.

**Phase 2 — Make the designer actually design (your literal ask):** daily mapper mutates `override.outputTree` directly; converge structural overlay into the same artifact; binding dropdown exposes real source fields + sample values + Calculated; one format source of truth; pick ONE review screen, restore per-line fix, flip `ORDER_WORKSHOP_V2`, delete `SpineReview`.

**Phase 3 — Close the losslessness gap:** tokenize-first parse front-end; every canonical field carries its `SourceToken.Id`; fill `Parties` + line `Identifier[]` from structured parsers; one header home (delete the canonical_json read-order convention).

**Phase 4 — Honesty + reach (defer):** real raw-JSON/text + paste import; format-agnostic additive input mapping; data-driven canonical field set; typed revision columns; cXML/UBL into the AST emitter.

Each phase: goal, affected modules, backend/frontend/data-model work, tests, and acceptance criteria to be detailed per-phase in the execution plan once approved.

---

## Concrete Tasks for Claude Code (Phase 0, on approval)

1. **Blocker-chip identity fix** — give every fix-queue issue a `targetRef` equal to the mapper row key (or restore inline per-line editor); test that every chip id focuses a real element.
2. **Validation auto-run** — call `validate()` on Workshop mount + post-commit; thread supplier format.
3. **Validator numeric safety** — compare typed decimals, not culture strings; golden tests for comma-decimal.
4. **Format allowlist unification** — one registry; honest accept/reject copy.
5. **Output-model interim guard** — when `outputTree` set, flat mapper shows the structure-designer-active state.

## Acceptance Criteria (Phase 0)

- Clicking any "Needs a supplier code" chip scrolls to + flashes the exact line, on desktop and mobile; automated test proves the id is focusable.
- Opening the Workshop runs validation automatically; the issues list is populated without pressing a button.
- A comma-decimal price (`73,22`) never validates as `7322`; a golden test pins it.
- Every format the upload UI offers actually parses or is honestly refused; no "unrecognized columns" shown for a valid file.
- No order ever shows both the old "Fix these to send" card and the new strip.

---

## Final Recommendation

**Approve the PARTIAL REDESIGN (converge + delete).** The best output engine you could ask for is already in the repo and tested; it's being shadowed by older code you never deleted, the override is squatting in a god-column, your preview is a hand-copy of delivery, and three review screens ship at once. The output designer becomes the strongest part of the product the moment (a) it is the only output model, (b) it can bind to any incoming field, and (c) what you see in preview is provably the bytes you send. Phase 0 stops the embarrassing dead-button / validation-never-runs bugs this week; Phases 1–2 deliver the "I can finally control the output" you are asking for — without a third rewrite.

**No product code will be written until you approve.** On approval, I'll start with Phase 0 (smallest, highest-trust) and bring each phase back with per-task acceptance criteria.
