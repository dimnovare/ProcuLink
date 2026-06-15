# ProcuLink — Output-Layer Restructuring MASTERPLAN

**Part 1 of 3** · Source: brutally-honest design/UX/integration review + 4-critic code-grounded audit (panel `wgbc7vs8f`)
**Date:** 2026-06-15 · **Status:** DRAFT — to be combined with (Part 2) the founder's next prompt and (Part 3) the ChatGPT analysis into one combined master, then built.
**Baselines audited:** Backend `main @ b2c3dce` · Frontend `main @ 7c622c1` (origin/main = deployed prod code).

> **Founder directive (2026-06-15):** "We do everything mentioned here. We will not keep anything out, defer, or gate it — everything will be done." This plan therefore has **no `LATER` / `nice-to-have` / `gated` tier.** Every item is in scope. Sequencing exists only to keep the product shippable between steps, not to drop work.

---

## 0. How to read this document

This is the analysis-side masterplan. It states **the disease (code-grounded), the cure (target architecture), and the full work breakdown** to get there. It is deliberately exhaustive so that when Parts 2 and 3 arrive, merging is additive (reconcile overlaps, absorb new items) rather than a rewrite. Line/file references are accurate as of the audited baselines; the build phase reconfirms them before editing.

---

## 1. Thesis — the one structural cut

> **Make the visual mapper compile to ONE editable output template that is the single source of output truth for every format. Make preview render that exact template through the exact delivery transform path. Collapse the four "map" surfaces and four sidebar entries into one supplier-scoped output designer. Demote the canonical model to invisible plumbing.**

Four independent critics (output-format engineer, conceptual-model analyst, integration engineer, churn skeptic) converged on this same cut against the same lines of code. It is not a polish list — it is one architectural change that **dissolves the weekly bug crop and the "I can't design the output" complaint together, because they were always the same problem.**

The founder's stated core pain — *"I cannot easily design the final output the way I want"* — is **literally true in the code**, not a UX gap. This plan makes it false.

---

## 2. Non-negotiables (what we are committing to — all of it)

1. **Kill the silent fallback-to-default transformer.** A broken/unusable mapping must FAIL LOUDLY (`delivery_failed` with reason), never deliver the default document while the UI shows success. *(Trust P0.)*
2. **Preview == delivery, by construction.** One transform path. Delete the preview-side twin. The bytes shown are the bytes sent.
3. **One output template = single source of output truth** for CSV, JSON, XML, cXML, UBL, X12 — arbitrary structure (nesting, arrays, attributes, column order, wrappers, footer/total rows, delimiters).
4. **The template is editable inside the mapper** the founder uses daily — two-way **visual ⇄ code**. Wiring a field edits the template; power users hand-tune exact bytes.
5. **"+ Add output field" works for every format** — including XML-family suppliers (today it is a silent no-op for them).
6. **Paste/upload the supplier's required sample → infer the output template.** The one feature that directly solves the complaint. (The dead "clone/import/AI-infer output schema" menu was the right instinct; finish it, backed.)
7. **Collapse 6 transform modes → 2:** visual field-rule mapping (default, one build path, all formats) + raw Scriban (power hatch). Revision-pinned and supplier-promoted become *sources* of the one config, not separate transform branches.
8. **Collapse 4 map surfaces → 1** supplier-scoped output designer; order-review becomes a thin instance of it pre-filled with that order's data.
9. **Collapse 4 sidebar entries (Connections / Suppliers / Mappings / Templates) → one Suppliers hub** with tabs.
10. **Demote canonical to invisible plumbing.** Never render `CANONICAL LINE`, canonical path notation, or a canonical column in any user-facing screen.
11. **Hide versioned-connection machinery** (draft/publish/revision/rollback/archive/replay/bundle) behind plain **Save / Save & make live**. Keep the machinery; demote it from the first screen to operator-only affordances.
12. **Vocabulary purge** — rename every term a procurement buyer does not say.
13. **One order screen** — no silent swap between Triage and Full-document based on invisible data state.

---

## 3. Current architecture — the disease (code-grounded)

### 3.1 The transform engine is six parallel paths with a lying fallback
`ProcuLink.Api/Services/Orders/OrderTransformService.cs:143` — comment: *"SIX transform modes, in precedence order."* Lines `314-339` + `476-529`: on any malformed/unusable pinned-revision OR supplier-promoted mapping, the code **silently falls back to the fixed transformer and logs** — i.e. delivers the DEFAULT document while the pipeline reports success. This is the single most trust-destroying outcome for a procurement user: *the tool lies about what it sent.*

### 3.2 Native reshape exists only for CSV + JSON, and only flat
`ProcuLink.Transform/Output/MappedTransformService.cs` — `BuildJson` hardcodes a `{header, lines, generatedAt}` envelope, flat keys, no nesting, no array root, no wrapper rename, no column-section control. `BuildCsv` repeats header columns per line. A supplier wanting `{"orderNumber":…,"items":[…]}` **cannot be produced** in the visual mapper.

### 3.3 XML-family ignores authored fields — "+Add output field" is a no-op
`ProcuLink.Transform/Output/EffectiveEntityResolver.cs:44-51` — overrides only remap **recognized canonical columns**; anything else returns null and is **silently dropped**. `XmlTransformService.cs:37-60`, `CxmlTransformService.cs`, `UblOrderTransformService.cs`, `X12TransformService.cs` each emit a **hardcoded tree** with baked-in identifiers (e.g. `PROCULINK`/`SUPPLIER` in the ISA segment). For these formats the visual mapper changes leaf VALUES, never SHAPE.

### 3.4 The only arbitrary-shape path is unreachable from the daily mapper
`ProcuLink.Transform/Output/ScribanTemplateTransformService.cs` is the one mode that can emit any structure. Its editor (`OutputMappingEditor.tsx`, ~line 324, a raw textarea) is a **separate component, not mounted in `MapperWorkbench`.** So the honest answer to "make this exact file" is always "go write Scriban in a screen you can't find."

### 3.5 Preview ≠ delivery — two pipelines that drift
`OrderTransformService.TransformAsync` (the real path) vs a preview-side twin in `OrdersController`. They have drifted enough that the codebase carries parity-test suites and `honorFormat` / `connectionFormatRef` reconciliation flags just to paper over it. "Preview shows wrong format" is structural, not a typo.

### 3.6 The canonical middle layer is both the bug factory and the flexibility blocker
This is the deepest finding. INCOMING → **CANONICAL** → OUTGOING forces a second mapping step, leaks `CANONICAL LINE` into the UI, and every bug this week (collapse-on-wire, wrong-format preview, accept-all count mismatch, resolution-failed) **lives at a canonical seam.** The "outgoing document collapses on wire" bug was *fixed* by forcing the canonical spine back into the output (`targetLaneModel.ts` `deriveTargetFields(..., mergeCanonical=true)`) — which is the **exact leak** that prevents removing canonical columns in the review screen. **The fix for bug A is the cause of complaint B.** You cannot fix them separately because they are the same problem.

### 3.7 Four "map" surfaces, four sidebar entries, one mental object
Surfaces: `MapperWorkbench` (order Full-document), `FixQueueTriage` (order Triage), `OutputMappingEditor` (Scriban), `PoMappingEditor` (supplier PO Mapping tab). Nav: `/connections`, `/library/suppliers`, `/library/mappings`, `/library/templates`. A user cannot answer *"where do I change what the supplier receives?"* — there are four valid answers. (Same class as the prior `[[project-delivery-routes-via-pinned-revision]]` "edited endpoint but delivery used OLD url" incident.)

### 3.8 The design tool and the review tool have opposite rules
`/connections` designer uses `mergeCanonical=false` (REPLACE — you can author a clean target). Order-review uses `mergeCanonical=true` (MERGE — canonical forced in, can't remove columns). Same `MapperWorkbench`, two contradictory behaviours, depending on which screen you opened it from.

### 3.9 Overbuilt enterprise machinery taxing the core
Versioned connections (~1,360 LOC, draft→test→publish→archive→replay, immutable revisions), V9 Beta-distribution calibration, 6 transform modes, 3 transform vocabularies (manipulators / Scriban expressions / whole-doc template), Invoice/ASN/PEPPOL — all built before a customer needs them. Each is an interaction surface; each produced one of this week's bugs. *Churn skeptic's framing to internalize: "the basic thing is buggy, so why would I trust the advanced things?"*

---

## 4. Target architecture — the cure

### 4.1 One output artifact: the OutputTemplate
A single **`OutputTemplate`** is the source of truth for what a supplier receives. It is:
- **Format-parametric** — one template definition renders CSV / JSON / XML / cXML / UBL / X12. Format selection chooses an emitter, not a different mapping.
- **Structure-complete** — expresses nesting, arrays/repeating groups (lines), attributes vs elements (XML), wrapper/root names, column order + headers (CSV), footer/total rows, delimiters/encoding.
- **Field-bound** — each output node binds to a source token (incoming field, catalog value, fixed value, or a transform/manipulator expression).
- **Editable two ways** — the visual mapper edits it; the raw view (Scriban/structured DSL) edits it; both read/write the same artifact.

### 4.2 One transform path
`OrderTransformService` resolves the effective `OutputTemplate` (order override → supplier default → connection revision) and renders it through **format emitters** (`IOutputEmitter` per format). There is exactly one render path. Preview calls it. Delivery calls it. They are the same call. **No fixed-transformer fallback** — an unusable template fails loudly.

### 4.3 One designer surface
A single **Output Designer** component. `/suppliers/{id}` hosts it (the supplier's standing template). Order-review mounts the *same* component pre-filled with that order's parsed data (instance edit, optionally saved back to the supplier). No second editor, no Triage/Full-document split, no PoMappingEditor.

### 4.4 Canonical = invisible plumbing
The canonical model stays internally (it is how the parser fleet normalizes 7 input formats). It is **never rendered**. The designer shows: *incoming field (their words)* → *output node (the file they receive)*. No canonical column, no `CANONICAL LINE`, no path notation.

### 4.5 Versioning demoted
Every Save creates a revision under the hood (keep replay/rollback for operators + audit). The user sees **Save** (draft) and **Save & make live** (publish). Rollback/replay/archive/bundle live in an "Advanced / History" drawer, not the primary flow.

---

## 5. Guiding invariants (apply to every task)

- **Offer ⇔ works.** Every format/option the designer exposes must be backed by a real, tested emitter path. No dead menu items. (Founder rule; `[[feedback-offer-equals-works]]`.)
- **Preview == delivery.** If they can ever differ, the task is not done.
- **Fail loud, never deliver the default silently.** (`[[project-first-prod-delivery]]` proved real delivery; this protects it.)
- **No canonical vocabulary in the UI.** Grep gate on `canonical`, `CANONICAL LINE`, path notation before ship.
- **One screen answers "what does the supplier receive?"**
- **Live-first verification.** Prove on prod with the real PO corpus (`~/Downloads/PO`, 24 real POs + DocParser target mappings), not just InMemory green. `[[project-live-matrix-and-diverse-review]]`, `[[project-inmemory-masks-postgres-fk]]`.
- **No commercial EDI licences.** Hand-rolled / MIT only. `[[feedback-no-commercial-edi-licences]]`.
- **Worktree isolation for every chip.** `[[project-concurrent-chips-shared-dir]]`.

---

## 6. Workstreams

Each workstream lists: **Why · Scope · Key files · Acceptance · Tests.** Ordering within Section 11.

### WS-0 — P0 trust fixes (ship first, independently)
**Why:** the lying fallback + preview drift are live trust bombs; they can ship before the big rebuild and de-risk everything after.
**Scope:**
- 0a. Remove silent fallback-to-fixed-transformer. On unusable pinned/supplier/order mapping → `delivery_failed` with a precise reason surfaced on the order + `/operations/exceptions`. Keep the fixed transformer **only** as the genuine default when no override exists at all (not as a silent error-swallow).
- 0b. Unify preview + delivery on one transform call; delete the `OrdersController` preview-side twin and the parity-test scaffolding that compensated for it.
**Key files:** `OrderTransformService.cs:314-339,476-529`; `OrdersController` preview action(s); `MapperPreviewPane.tsx`, `MapperPreviewPane`/`OutputMappingEditor` preview callers.
**Acceptance:** a deliberately-broken override never delivers a default doc silently; preview bytes byte-match delivered bytes for all 6 formats on the real corpus.
**Tests:** Postgres-backed (not InMemory) broken-mapping → `delivery_failed`; preview==delivery parity across CSV/JSON/XML/cXML/UBL/X12 on `~/Downloads/PO`.

### WS-1 — OutputTemplate as single source of output truth
**Why:** the core cut. Everything else hangs off this.
**Scope:** define the `OutputTemplate` model (format-parametric, structure-complete, field-bound — see §4.1). Extend `OrderMappingOverride.OutputTemplate` from "optional Scriban string" to the always-on canonical artifact. Effective-template resolution order: order override → supplier default → connection revision → (only if none) format default.
**Key files:** `ProcuLink.Core` mapping/override models; `OrderMappingOverride`; `EffectiveEntityResolver.cs` (becomes effective-template resolver); migration for the new template column/shape.
**Acceptance:** every delivery for every format is produced by rendering one resolved `OutputTemplate`; no format has a separate hardcoded structure path.
**Tests:** round-trip template → render → parse-back for each format; resolution-precedence unit + Postgres tests.

### WS-2 — Format emitters (replace hardcoded trees)
**Why:** make structure real for XML-family and rich CSV/JSON.
**Scope:** introduce `IOutputEmitter` per format that renders the resolved `OutputTemplate`. Rewrite `XmlTransformService` / `CxmlTransformService` / `UblOrderTransformService` / `X12TransformService` / `MappedTransformService`(CSV/JSON) to emit from the template (nesting, arrays, attributes, wrapper names, column order, footers, delimiters, ISA/GS identifiers configurable — no baked `PROCULINK`/`SUPPLIER`). Keep Scriban as the raw emitter for fully hand-authored cases.
**Key files:** all `ProcuLink.Transform/Output/*TransformService.cs`; new `IOutputEmitter`; `ParsedOrderTransformFactory.cs`.
**Acceptance:** "+ Add output field", rename, reorder, nest, and array-shape all take effect in XML/cXML/UBL/X12 output (no silent drop); cXML ISA/credential identifiers editable (`[[project-flexible-mapping-redesign]]` random-orgId complaint).
**Tests:** per-format structure tests proving authored shape appears; X12 envelope identifier override; reproduce the 24 DocParser target shapes from `~/Downloads/PO`.

### WS-3 — Visual ⇄ code mapper (surface the template in MapperWorkbench)
**Why:** the template must be editable in the daily screen, both ways.
**Scope:** mount the OutputTemplate editor INSIDE `MapperWorkbench`. Two-way bind: wiring/adding/removing/reordering an output node edits the template; a raw (Scriban/DSL) panel edits the same artifact and re-renders the visual tree. Inline fixed-value + transform editing on each node (founder complaint: "fixed value/transform not inline"). Live preview pane right-sized, renders the one template, honest format label, multi-format toggle actually re-renders (`[[project-flexible-mapping-redesign]]`).
**Key files:** `src/components/bridge/mapper/MapperWorkbench.tsx`, `targetLaneModel.ts`, `mapperModel.ts`, `MapperPreviewPane.tsx`; fold `OutputMappingEditor.tsx` in (or retire it).
**Acceptance:** wiring a field changes both the visual tree and the raw template; "+ Add output field" works for all formats; outgoing document never collapses on wire/add (the `mergeCanonical` regression is gone because there is no canonical merge anymore — see WS-6).
**Tests:** e2e — add field → see it in preview + delivered bytes; toggle format → preview re-renders; visual edit ⇄ raw edit round-trip.

### WS-4 — Paste/upload supplier sample → infer template
**Why:** the single most direct solution to the founder's complaint; the deleted dead menu's correct instinct, backed.
**Scope:** "Paste or upload the file your supplier requires" → infer the target structure into an `OutputTemplate` (shape + node names + repeating groups), then drop the user into the visual ⇄ code designer to bind sources. Mirror the existing input-side `OpenAiSchemaInferencer` for the output side. Support pasted CSV/JSON/XML samples and uploaded files. No-egress orgs: deterministic structural inference (no LLM) per the existing no-egress gating.
**Key files:** new output schema inferencer (mirror `OpenAiSchemaInferencer`); designer entry point; wire into the supplier output designer + order-review.
**Acceptance:** paste a supplier's sample CSV/JSON/XML → designer opens pre-shaped to match; binding sources produces byte-compatible output. Validate against the 24 DocParser target mappings.
**Tests:** infer-from-sample for each format on the real corpus; no-egress deterministic path; round-trip sample → template → render matches sample shape.

### WS-5 — Collapse surfaces (4→1) + nav (4→1)
**Why:** one mental object → one place.
**Scope:** one **Output Designer** component used by both supplier page and order-review. Retire `FixQueueTriage` as a separate screen (its line-code resolution folds into the one designer/review as an inline step). Retire `PoMappingEditor` (its PO field mapping is the same designer). Sidebar: merge `/connections` + `/library/suppliers` + `/library/mappings` + `/library/templates` → one **Suppliers** hub with tabs (Overview · Output · Catalog · Delivery · History). Redirect old routes.
**Key files:** `ConnectionDetail.tsx`, `PoMappingEditor.tsx`, `FixQueueTriage`, supplier library routes, sidebar nav config.
**Acceptance:** exactly one screen answers "what does the supplier receive?"; old routes redirect; no orphaned editor.
**Tests:** route redirects; e2e supplier-setup happy path through the single hub.

### WS-6 — Demote canonical to invisible plumbing
**Why:** kill the leak that is both bug source and rigidity source.
**Scope:** remove canonical from all rendered surfaces: no `CANONICAL LINE` chip, no canonical column, no path notation. The designer shows incoming (their words) → output node. Internally canonical still normalizes parser input, but `deriveTargetFields` no longer merges a canonical spine into the target (the `mergeCanonical=true` order-path branch is deleted — order-review and designer now share ONE rule). The collapse-on-wire bug class disappears with the merge.
**Key files:** `targetLaneModel.ts` (`deriveTargetFields`, drop `mergeCanonical`), `mapperModel.ts`, any UI rendering canonical labels; backend DTOs exposing canonical paths to the UI.
**Acceptance:** grep finds zero canonical vocabulary in rendered components; order-review and supplier-designer behave identically (one rule); columns removable in both.
**Tests:** snapshot/grep gate; e2e remove-column works in order-review (today it can't).

### WS-7 — Collapse 6 transform modes → 2
**Why:** six precedence branches + a lying fallback is the structural source of drift and overbuild.
**Scope:** reduce `OrderTransformService` to two modes — **field-rule template** (default; pinned-revision + supplier-promoted become *sources* of the resolved template, not separate branches) and **raw Scriban** (power hatch, same template artifact, hand-authored). Delete the precedence chain at `:143`. Keep replay/rollback as revision plumbing, not transform branches.
**Key files:** `OrderTransformService.cs:143-529`.
**Acceptance:** two code paths; resolution is "which template wins," not "which of six modes"; no silent fallback (WS-0 holds).
**Tests:** precedence resolution tests; ensure pinned-revision reproducibility preserved (`ConnectionRevisionId` still pins the resolved template).

### WS-8 — Hide versioning behind Save / Save & make live
**Why:** version-control jargon is on the first screen; it belongs to operators.
**Scope:** primary actions become **Save** (draft) and **Save & make live** (publish a revision under the hood). Move Create-draft / Roll back / Run tests / Archive / Replay / Bundle into an "Advanced / History" drawer. Keep all backend machinery + audit; just demote the surface.
**Key files:** `ConnectionDetail.tsx` action bar, `useMapperModel.ts`, connection revision API callers.
**Acceptance:** first-screen shows two buttons; revisions still created on every save; history/rollback reachable but secondary.
**Tests:** save → revision created; publish → live; rollback from drawer still works.

### WS-9 — Vocabulary / naming purge
**Why:** the product is named for its builders, not its buyers.
**Scope:** rename across UI + copy. Proposed mapping (reconcile with `[[project-canonical-design-source]]` 2026-05-30 vocab decision):

| Today | → Target |
|---|---|
| OUTGOING DOCUMENT | What {supplier} receives |
| INCOMING ORDER | Your order / the file you got |
| CANONICAL LINE / canonical paths | *(removed — invisible)* |
| Triage / Full document | *(one screen — "Review & send")* |
| Author the mapping | Set up the output |
| Conformance / Passport / Normalize | plain words (Checks / Status / Standardize) |
| Create draft from live · Publish · Archive · Replay · Bundle | Save · Save & make live · *(Advanced/History)* |
| Connection / revision | Supplier setup / version (in History only) |

**Acceptance:** no builder-jargon term in any first-line user-facing surface.
**Tests:** copy review pass; grep gate for the retired terms.

### WS-10 — Order-review = thin instance of the designer
**Why:** stop maintaining two output editors; the review screen is just the designer pre-filled.
**Scope:** order-review mounts the WS-3 designer with the order's parsed data + per-line code resolution inline. Accept-all uses the same (already-fixed) RAW-confidence alignment (`[[project-flexible-mapping-redesign]]` accept-all-90% fix). Multi-file inbox shows correct per-order data (founder: "after uploading multiple you see unknown data").
**Key files:** order-review route, `MagicMappingPreview.tsx`, `buildFixQueue.ts`, `UploadWorkbench.tsx`.
**Acceptance:** one editor component for supplier + order; multi-file upload shows correct data per order; accept-all matches the accept endpoint.
**Tests:** e2e multi-file upload → each order correct; accept-all count == acceptable suggestions.

---

## 7. The ideal user flow (target)

| Step | User sees | System does | Anti-pattern removed |
|---|---|---|---|
| 1 Import | "Send to {supplier}? Drop file(s)." | Detect format, parse | (already good) |
| 2 Detect | "We found these fields + values" (their words) | Extract header+lines, MPNs | canonical jargon leak |
| 3 Match codes | Only lines needing a code, one confident suggestion | AI/catalog/source-MPN match | two screens, bad suggestions |
| 4 Validate | "2 issues — fix or send anyway" | Run rules inline | validation as separate tab |
| 5 Pick output | "Supplier X format" + **"Paste their sample"** | Load saved template OR infer from sample | no template reuse / no infer |
| 6 Preview & edit | **The literal file X receives**, editable (visual ⇄ code) | Render the ONE template | preview ≠ delivery; can't edit shape |
| 7 Send | "Sent ✓ — here's exactly what we sent" | Deliver, log bytes | silent fallback lies |

---

## 8. Data model changes

- `OrderMappingOverride.OutputTemplate` → promoted to the always-on, structured `OutputTemplate` artifact (not an optional Scriban string). Migration to backfill existing overrides into the new shape (existing flat/leaf overrides → equivalent template).
- Supplier default `OutputTemplate` (the standing template per supplier) — first-class column/table, not overloaded `CanonicalJson` (`[[project-north-star-pivot]]` "stop overloading CanonicalJson").
- Connection revision stores the resolved `OutputTemplate` snapshot so `ConnectionRevisionId` still pins exact output for replay/rollback.
- No new commercial dependencies.

---

## 9. Migration & backward compatibility

- **Existing live connections must keep delivering.** Backfill each supplier's current effective mapping (whatever wins today) into an equivalent `OutputTemplate`; verify byte-identical output for the real corpus before cutover.
- **Pinned revisions stay reproducible:** old `ConnectionRevisionId`s resolve to their stored snapshot; replay produces the same bytes.
- **Prod proof gate:** re-run the 3-supplier × 3-format live delivery proof (`[[project-first-prod-delivery]]`) on the new path before declaring done.
- **Route redirects** for the retired `/library/*` + `/connections` URLs.

---

## 10. Test strategy (live-first)

- **Real corpus is the bench:** `~/Downloads/PO` (24 real POs + their DocParser outgoing mappings). The acceptance bar is: reproduce those DocParser target outputs from ProcuLink's designer.
- **Postgres, not InMemory**, for anything touching FKs/overrides/ExecuteUpdate (`[[project-inmemory-masks-postgres-fk]]`, `[[project-executeupdate-autocommit-window]]`).
- **Preview==delivery parity** suite across all 6 formats.
- **Live in×out matrix** (`[[project-live-matrix-and-diverse-review]]`) — it caught a prod 500 every unit test missed.
- **Diverse-model review lens** on the structural diffs (use a second model as reviewer; it caught a class-identity bug Sonnet missed).
- **No-egress path** tested for WS-4 inference + WS-2 emitters.

---

## 11. Sequencing / phases (shippable between each)

1. **Phase A — Trust (WS-0).** Ship the loud-fail + preview==delivery first. Independently valuable, de-risks the rest.
2. **Phase B — Core artifact (WS-1, WS-2).** OutputTemplate + format emitters. Backend-heavy; output becomes structure-real for all formats. Backfill migration + byte-parity gate.
3. **Phase C — Designer (WS-3, WS-6, WS-7).** Visual ⇄ code mapper, canonical demotion, 6→2 modes. The founder's daily screen becomes the real designer.
4. **Phase D — Inference (WS-4).** Paste-sample → infer template. The complaint-killer feature.
5. **Phase E — Consolidation (WS-5, WS-8, WS-9, WS-10).** Collapse surfaces + nav, demote versioning, vocabulary purge, order-review-as-designer.

(All five phases are committed. Phasing only keeps prod green between steps.)

---

## 12. Risk register

| Risk | Mitigation |
|---|---|
| Backfill produces non-identical bytes for a live supplier | Byte-parity gate on real corpus before cutover; per-supplier diff review |
| OutputTemplate model too rigid to express some EDI shape | Scriban raw emitter is the always-available escape hatch (same artifact) |
| Rebuild #4 churn (we've rebuilt the mapper 3× in a week) | This is a structural cut, not a re-skin; the artifact + single path are the convergence point — stop re-litigating the screen |
| Concurrent chips racing shared dir / EF snapshot | Worktree isolation per chip (`[[project-concurrent-chips-shared-dir]]`) |
| Windows-dev / Linux-CI drift | Verify on CI + prod, not local green (`[[project-windows-dev-linux-ci-gotcha]]`) |
| Hiding versioning breaks operator replay | Keep all machinery; only demote the surface; History drawer retains full control |

---

## 13. Definition of done — the litmus test

> A procurement coordinator opens **Supplier X**, sees and edits **the literal file X receives**, optionally **pastes X's required sample** to shape it, and sends a **PDF → that-exact-file** — **without ever hitting a canonical-node wall, a Scriban detour, a second map screen, a version-control button, or a week-old wire bug** — and the preview bytes are **identical** to what X receives.

When that holds on prod with the real corpus, the restructuring is done.

---

## 14. Open questions to reconcile with Part 2 (next prompt) + Part 3 (ChatGPT)

1. **DSL choice for the editable template** — extend Scriban as the raw form, or introduce a structured JSON template DSL with Scriban only for expressions? (Affects WS-1/WS-3 two-way binding.)
2. **How aggressively to retire** `FixQueueTriage` vs fold it inline (WS-5/WS-10).
3. **Invoice/ASN/PEPPOL** — do they ride the same OutputTemplate, or stay on their current transforms for now? (Plan assumes PO-first; reconcile.)
4. **No-egress inference quality** — deterministic-only acceptable, or allow opt-in LLM per the existing per-org gate? (WS-4.)
5. **Naming** — final reconciliation with the 2026-05-30 vocab decision (`[[project-canonical-design-source]]`).
6. Anything in Parts 2/3 that adds scope beyond §2 — absorb, don't drop.

---

---
---

# PART 2 — Live-QA / Architecture Audit (absorbed)

**Source:** Part-2 review prompt (senior product-engineer + QA critic brief) answered by 3 code-grounded critics (workflow `wtrob8llb`, baselines BE `b2c3dce` / FE `7c622c1`) + this session's live testing record (founder screen-captures 3/4/5, live prod verifications). Every claim below was checked against real files. **No scope dropped vs Part 1 — Part 2 sharpens it and adds 6 net-new items.**

## P2.0 — How Part 2 changes Part 1

Part 1 named the disease and the cure. Part 2 **confirms it against the code** and adds concrete shape + 6 items Part 1 missed. All fold into the existing workstreams (mapping in §P2.H); two new workstreams added (WS-11, WS-12). The §2 non-negotiables gain 5 commitments (§P2.A).

## P2.A — Net-new commitments (added to §2 non-negotiables)
14. **Delete the dead second export stack.** `IParsedOrderTransform` / `ParsedOrderTransformFactory` (`UblParsedOrderTransform`, `X12ParsedOrderTransform`, `EdifactParsedOrderTransform`) are registered (`Program.cs:607-610`) but invoked **nowhere** in the API — duplicate UBL/X12 implementations maintained for nothing. Pick ONE output stack (fold any worth-keeping logic into the unified emitters, delete the rest + their tests). → **WS-11.**
15. **Envelope identity is data, not a constant.** X12 `ISA06/08`/qualifiers/version/usage/delimiters (today baked `SenderId="PROCULINK"`/`ReceiverId="SUPPLIER"` at `X12TransformService.cs:49-50`) and cXML `From/To/Sender` party identity become a per-connection **`EnvelopeConfig`**, pinned into the revision snapshot. Independently shippable; alone unblocks real X12/cXML delivery. → **WS-12.**
16. **Mapping logic leaves the UI.** Extract `buildOverrideDraft` + override wire types from `OutputMappingEditor.tsx:67` into a shared non-UI lib (`src/lib/mapping`) consumed by every surface. → folds into WS-3/WS-5.
17. **Provenance is visible when anything falls back.** Even where a legitimate default applies, surface it: add `OutputMappingFellBack` (+ reason) to the outbound artifact + order DTO + UI. Combined with WS-0's loud-fail, the user always knows whether they got *their* mapping or a default. → folds into WS-0.
18. **Expose the per-field Scriban `Expression` that already exists.** The backend honors `OutputFieldRule.Expression` (`OrderMappingOverride.cs:179-189`, `ResolveExpressionOrField`) but the frontend type/UI never sends it — real per-leaf transform power is built and hidden. Wire it into the inline transform editor. → folds into WS-3.

## P2.B — Architecture separation verdict (Part-2 §7)

| Question | Verdict | Evidence |
|---|---|---|
| Import / mapping / export separated? | **partly-coupled** | Parsers (`IPurchaseOrderParser`+`OrderParserFactory`) and transformers (`ITransformService`) are cleanly modular at package boundaries. BUT the **mapping layer is not its own layer** — it's smeared through `OrderTransformService.TransformAsync` (`:143-451`), one ~370-line method holding 6 interleaved modes + override detection + format resolution + cXML creds + idempotency claim + R2 upload + provenance hash + audit, selected by 7 booleans (`:163-190`). Plus the dead second export stack (§P2.A-14) and override persisted by overloading `purchase_orders.canonical_json` (the exact anti-pattern the North Star memo says to stop). |
| UI tightly coupled to business logic? | **partly-coupled** | Render boundary mostly correct (preview shows server bytes, no JS re-impl of transforms). BUT: save-contract `buildOverrideDraft` lives in a UI component reached across editors (§P2.A-16); canonical internals leak into the UI model (`MapperWorkbench.tsx:168-186`, `onWireConnect` branches on canonical keys); same component, two contradictory rules by host (`deriveTargetFields(mergeCanonical)`); preview parity maintained by a **second transform impl** in the controller, not one shared call. |
| Scales to many formats × many templates? | **with-pain** | Inputs scale (parser-per-format + canonical absorbs variety). **Output does not.** Each format is a hardcoded C# tree; per-supplier template flexibility is a bolted-on second axis (native reshape only CSV+JSON, only flat); the only arbitrary-shape path (Scriban) is a third vocabulary in an unreachable editor. Adding a format that needs arbitrary shape forces a new hardcoded transformer OR a per-supplier Scriban template, and the override engine must be re-taught per format. **This is the literal mechanism behind "I can't design the output" + "it produces bugs" — every recent bug sits at a format×template seam.** |

## P2.C — Data-model verdict (Part-2 §3 flexibility)

**Can a non-developer define an arbitrary output structure? → flat-only.**
- **JSON:** partial — UI controls leaf keys inside a hardcoded `{header,lines,generatedAt}` wrapper (`MappedTransformService.BuildJson:149-184`); no nesting / root-array / wrapper-rename except by hand-authored Scriban.
- **CSV:** partial — column names+order editable, but shape is always header-cols-repeated-per-line, comma-only, no footer/total/grouping.
- **XML / cXML / UBL / X12:** **no** — override changes leaf VALUES only; `EffectiveEntityResolver` applies a **closed allow-list of ~13 canonical fields** (`:44-51`) and **silently drops** any authored non-canonical field; structure + envelope identity are C# literals.

**Rigidity points (exact):** `EffectiveEntityResolver` 13-field allow-list · `BuildJson/BuildCsv` shape is a C# literal · `OutputFieldRule.OutputPath` is a flat string (no `a.b[].c` grammar, no node-type) · `ScribanOrderModel` reads a frozen namespace (fixed globals + fixed 12-key ShippingAddress) · X12/cXML envelope identity is const/config · per-field `Expression` exists backend-only.

**Minimal model change (the concrete shape for WS-1's OutputTemplate):**
> Replace/extend the flat `OutputMappingConfig` with a **recursive `OutputNode` tree** — `{ name, nodeType: object | array | field | attribute, children[], rule? }` where `rule` reuses the existing `OutputFieldRule` (incl. `Expression`) — and add **one tree-walking emitter per serialization family** (one structured emitter for JSON/XML/cXML/UBL, one delimited emitter for CSV; X12 via the envelope+segment walker). Reuse `ManipulatorRegistry`/`ScribanFieldEvaluator` verbatim. Add **`EnvelopeConfig`** (per-connection, pinned) for EDI/cXML identity. Build the Scriban model from the **`SourceCapture` raw bag + canonical** (the flexible-mapping spine, ~70% built per `[[project-flexible-mapping-redesign]]`) so nodes/templates read ANY source field, not just the 13 canonical leaves.

This is **one new model type + one new emitter family**, not six rewrites — it replaces the hardcoded `BuildJson/BuildCsv` shapes and the value-only `EffectiveEntityResolver` path with a real structure-from-data path.

## P2.D — Concrete bug inventory at HEAD (Part-2 §4)

| # | Tested | Happened | Should | Sev | Type | Status@HEAD | Fix → WS |
|---|---|---|---|---|---|---|---|
| 1 | XML/cXML/UBL/X12 supplier: add custom field / fixed value / rename / manipulator in mapper | Silently discarded; only 13 canonical leaves emit | Authored field appears in output, or UI refuses with a reason | **CRITICAL** | product-design | **open** | Interim: gate "+Add field" with disabled-reason for XML-family (offer⇔works); real fix = WS-2 + OutputNode (WS-1) |
| 2 | Per-supplier/revision mapping throws at transform time | Silently falls back to default doc; provenance hashed away; UI shows success | Fail loud (`delivery_failed`+reason) OR deliver-with-visible-`OutputMappingFellBack` | **HIGH** | architecture | **open** | WS-0 + §P2.A-17 |
| 3 | cXML supplier with configured From/To/Sender: live preview | Preview renders legacy GUID `<Credential domain="OrgId">`, not configured creds | Preview resolves creds via same resolver as delivery | **HIGH** | bug | **open** | WS-0 (cXML preview parity) |
| 4 | Mapper change history (`git log` mapper/ = 55 commits/2wk) | Rebuilt ~3× + patched ~20×, same bug classes recurring (wire/collapse/preview) | Stable invariants frozen by characterization tests | **HIGH** | architecture | **partially-fixed** | WS-3 (characterization tests freezing invariants) |
| 5 | Native-override CSV/JSON shape | CSV forced denormalized header-per-line; JSON forced `{header,lines,generatedAt}` | User picks shape (csvHeaderMode, jsonEnvelope/root) | **MEDIUM** | product-design | **open** | subsumed by WS-1 OutputNode |
| 6 | Count distinct mapping surfaces | 4+ overlapping (drag mapper / code-translate / output-form / Scriban) different mental models | One surface, progressive disclosure | **MEDIUM** | product-design | **open** | WS-5 |
| 7 | Accept-all ≥90% count vs accept action | (was) count used calibrated, accept used raw → "(1) then accept 0" | count == accepted at 0.9 boundary | MEDIUM | bug | **fixed** (task #82) | keep regression test |
| 8 | Multi-file upload | (was) "Unknown buyer / 0 lines" stale data | each file → own order, distinct data | MEDIUM | bug | **fixed** (task #83) | keep e2e |
| 9 | Canonical vocabulary in mapper | Canonical names are the default field vocabulary both sides | real source/output names default; canonical only via standards disclosure | LOW | ux | **partially-fixed** | WS-6 |
| 10 | Triage ↔ Full-document swap | PDF opens Triage then auto-advances; can surprise | explicit + signposted | LOW | ux | **fixed** (T6) | residual: one-time toast |
| 11 | cXML creds editable per connection | Editable at HEAD (delivery path); earlier "random orgId" resolved | — | LOW | bug | **fixed** | add preview wiring (#3) + no-To delivery guard |

## P2.E — Top 10 problems (ranked)
1. Output for XML/cXML/UBL/X12 cannot be shaped — authored fields silently dropped (bug #1, CRITICAL).
2. Silent fallback delivers the default doc while showing success (bug #2 — trust killer).
3. Canonical middle layer is simultaneously the bug source and the rigidity source (Part 1 §3.6).
4. `OrderTransformService.TransformAsync` = 370-line, 6-mode, 7-boolean god-method.
5. Preview ≠ delivery (two transform impls; cXML creds diverge — bug #3).
6. 4+ overlapping map surfaces / 4 sidebar entries for one mental object.
7. Data model is flat (no nesting/arrays/attributes/envelope-as-data) — the OutputNode gap.
8. Dead second export stack doubling UBL/X12 maintenance.
9. Mapping save-logic + canonical internals live in the UI (coupling + churn source).
10. Built-but-hidden power (per-field Scriban `Expression`) unreachable; version-control jargon on the first screen.

## P2.F — Top 10 improvements (ranked, = the build order)
1. WS-0: loud-fail + `OutputMappingFellBack` visibility + cXML preview parity (trust, ship first).
2. WS-1: recursive `OutputNode` tree as the single output artifact.
3. WS-2: `IOutputEmitter` per serialization family rendering the tree (kills hardcoded trees + the 13-field cap).
4. WS-12: `EnvelopeConfig` (X12/cXML identity as data) — independently shippable.
5. WS-3: surface the tree in `MapperWorkbench`, two-way visual⇄code, inline `Expression`, characterization tests.
6. WS-4: paste/upload supplier sample → infer the OutputNode tree.
7. WS-6: demote canonical to invisible plumbing (one `deriveTargetFields` rule).
8. WS-7+WS-11: collapse 6 modes → resolver+2 paths; delete the dead stack.
9. WS-5: collapse 4 surfaces → 1 + extract `src/lib/mapping`.
10. WS-8+WS-9+WS-10: hide versioning behind Save, vocabulary purge, order-review = thin designer instance.

## P2.G — Fix-first / can-wait / not-yet + ideal MVP
- **Fix FIRST (before any new feature):** WS-0 (bugs #2, #3) + the bug-#1 honesty gate. These are live trust bombs and ship in days.
- **The ideal MVP (the core loop, done right):** import PO → detect fields (their words) → match codes → validate/fix inline → **pick output: load saved template OR paste supplier sample → infer** → preview the literal file (visual⇄code editable) → send, with delivered bytes shown. One screen for "what the supplier receives." That's WS-0→WS-6.
- **Can wait (still committed, later phase):** WS-8/9/10 cosmetics + consolidation, full template library UX.
- **Do NOT build yet (explicitly out until the core loop lands):** new input channels/formats beyond what exists, Invoice/ASN/PEPPOL output on the new engine, RBAC/SCIM, more EDI dialects. *(These are paused, not cancelled — reconcile final scope with Part 3.)*

## P2.H — New/updated workstreams
- **WS-11 — Delete the dead second export stack.** Remove `IParsedOrderTransform`+`ParsedOrderTransformFactory`+the 3 `*ParsedOrderTransform` + tests (or fold worth-keeping logic into the WS-2 emitters first). Acceptance: one output stack; grep finds no live second-stack path.
- **WS-12 — `EnvelopeConfig` (EDI/cXML identity as data).** Per-connection X12 `ISA/GS` ids/qualifiers/version/usage/delimiters + cXML `From/To/Sender`, pinned into the revision. Acceptance: a real supplier's X12 envelope + cXML party identity are user-set and delivered correctly; no baked `PROCULINK`/`SUPPLIER`.
- **WS-1 sharpened:** the OutputTemplate IS the recursive `OutputNode` tree (§P2.C shape).
- **WS-2 sharpened:** emitters are **per serialization family** (one structured walker + one delimited walker + X12 segment walker), not per-format hand-coding.
- **WS-3 sharpened:** also extract `src/lib/mapping` (move `buildOverrideDraft` out of UI), wire the inline per-field `Expression`, and add **characterization tests** freezing mapper invariants (adding a field/wire never reduces the visible target list; preview==delivery; canonical never auto-injected) to end the 55-commit churn.
- **WS-0 sharpened:** add `OutputMappingFellBack` provenance + the cXML preview-credential parity fix.

## P2.I — Part-2 final recommendation
**Same as Part 1, now code-confirmed: improve the engine, rebuild the output layer, do NOT rewrite the product.** The audit found the cure is *smaller* than feared — the recursive `OutputNode` + one emitter family + `EnvelopeConfig` is **one model type + one emitter family + one config**, reusing the existing manipulator/Scriban machinery, replacing the hardcoded shapes and the 13-field value-only path. The trust fixes (WS-0) ship in days and are unconditionally worth doing regardless of the bigger cut. Continue the direction; execute the one structural cut.

---

---
---

# PART 3 — ChatGPT analysis, adversarially verified (absorbed)

**Source:** ChatGPT's review, then **verified against real code** by 4 skeptic critics (workflow `w2z14dkle`) — NOT rubber-stamped (the repo's own history records prior ChatGPT audits shipping wrong P0s; `[[chatgpt-audit-verified]]`). Each claim below is tagged **CONFIRMED / PARTIAL / REFUTED** with file:line evidence. ChatGPT's biggest contribution is real and was a **blind spot in Parts 1–2**: those were output-myopic; ChatGPT found the **input/validation trust hole** and one **architectural correction**.

## P3.A — CONFIRMED (merge as real work)

### The big one — validation reports "Passed" on garbage (input-trust). CONFIRMED.
- `SupplierAcceptanceService.cs` `EvaluateProfile` (`:273-274`): a supplier with **no acceptance profile → empty result list**. Frontend `api-client.ts:2421-2423` computes `passed = rows.every(r => r.status==='pass')` → **`[].every()` is vacuously true → `passed:true`.** (`useAcceptanceValidation.ts:63`, summary `{0,0,0}` at `stageModel.ts:127-134`.)
- `FixQueueTriage.tsx:560-562` then renders the **misleading green** `"✓ Passed — order meets all acceptance rules"` on zero rules. (`SendReadinessCard.tsx:90-93` is honest — `"Passed — no rules configured"` — but the always-rendered strip in FixQueueTriage over-claims with the word **"all"** over an empty rule set.)
- **No mandatory invariant layer on the common path.** `OutputFieldValidator.cs:61-71,103-112` checks unit price ≤ 0 but **never quantity ≤ 0**, and is wired ONLY into X12/UBL/EDIFACT/cXML transforms — **NOT** Csv/Json/Xml/Mapped/Scriban (the default paths). `CsvOrderParser.cs:119` parses qty `?? 0m` with **no sign check** → **quantity `-3` passes every gate** and shows green. (Non-numeric price `abc` IS review-flagged at parse, `:121/164` — that half was already handled.)
- **Fail-open unknown operator:** `SupplierAcceptanceService.cs:474-475` `default: return true` — a typo'd/misconfigured rule operator passes unconditionally.
> **→ This expands WS-0 from output-trust-only to a full TRUST layer (input + output).** New sub-items WS-0c…0f.

### Sample SUPPLIER counts against quota + shows in list. CONFIRMED (new).
`StripeBillingService.cs:762-763` `CountSuppliersAsync` lacks `&& !s.IsSample` (order-quota was guarded; **supplier-count was not**) → the `__sample__` supplier consumes the Pilot 1-supplier cap and appears in normal lists. One-line fix. → **WS-13.**

### Live PO-loop E2E is not a CI gate + stale assertion. CONFIRMED (new).
`tests/e2e/live-po-loop.spec.ts:20` skips unless `PLAYWRIGHT_LIVE=1`; CI (`ci.yml:80-81`) runs mock-only → the live body **never runs in CI**, and `:48` still asserts an old heading. → **WS-13 + WS-3 test discipline.**

## P3.B — PARTIAL / REFUTED (do NOT carry as P0 — ChatGPT overstated)

- **"Dashboard shows Auto-processed 100% while orders need attention" → PARTIAL/LOW.** `BridgeDashboard.tsx:81,429-431` denominator does exclude problem states, but the metric is **honestly labelled** "Auto-processed / No manual mapping needed" and gated to `—` until 3+ completed orders. Not a lie. Optional copy clarity only.
- **"Topology dashboard decorative / unreadable" → PARTIAL/LOW.** It's the primary surface and **derived from real orders+suppliers**, not decorative. Real concern is only high-volume scaling (cap top-N ports). Minor.
- **"Common input fields disappear silently (data loss)" → PARTIAL/MEDIUM, framing WRONG.** Unmapped CSV columns are **NOT lost** — they persist in `source_captures` and are surfaceable in the mapper's Raw group. The hardcoded alias registry (`CsvOrderParser.cs:253-270`) is real, but the true gap is small: **no "N columns not mapped" notice/badge**, not data loss. → small UX add in WS-3/WS-6, not a crisis.
- **"Metrics come from mock/staged data in prod" → REFUTED.** `core.ts:35-37,40` hard-disables `USE_MOCK` in production. Prod metrics are fully real.
- **"Retry enabled when config missing" → PARTIAL/LOW.** `FailedPanels.tsx:223-226,353-404` already has a dedicated config-missing branch; Retry is only disabled while in-flight. Literal fix = also disable when `configMissing`. Minor (already in P2.D #?). → WS-13.
- **"Cookie banner obstructs first-run" → NEEDS-LIVE-CHECK.** `CookieConsentBanner.tsx` is `fixed; bottom; zIndex:60`, global. Confirm overlap with a screenshot at ~768px before fixing. → WS-13.

## P3.C — The architectural CORRECTION ChatGPT got right (changes the plan)

> **"Use one common template *contract* with format-aware emitters — NOT one literal universal template. Arbitrary Scriban cannot reliably round-trip through a visual editor. Raw code becomes one-way expert mode."**

This is correct and sharpens WS-1/WS-2/WS-3. Parts 1–2 implied two-way visual⇄code binding of the template. **Full round-trip of arbitrary Scriban is intractable.** Resolution:
- The **`OutputNode` AST is the single round-trippable source of truth.** Visual edits ⇄ AST ⇄ format emitters. The AST round-trips cleanly because it is structured, not free-text.
- **Raw Scriban is a one-way *terminal* escape:** dropping a node (or a whole template) to raw Scriban is allowed for power users, but that node/template is then **raw-owned** (clearly flagged, no auto-reverse into the visual tree). You don't lose power; you just can't round-trip arbitrary code back into structured UI.
- "One common template contract" = the `OutputNode` AST + `EnvelopeConfig`; "format-aware emitters" = the per-family emitters (WS-2). This is exactly the P2.C model — ChatGPT independently converged on it and corrected the round-trip overreach.
> **→ Amend WS-1 (AST is the contract), WS-2 (format-aware emitters), WS-3 (visual⇄AST round-trips; raw Scriban one-way).**

## P3.D — New non-negotiables (added to §2)
19. **No-rules means "Not checked," never "Passed."** Zero acceptance rules must render neutral/amber, never green "meets all rules."
20. **Mandatory invariants run on EVERY order, EVERY format, regardless of supplier profile:** quantity > 0, unit price present & > 0, currency present, PO identifier present. Negative quantity flagged at parse + invariant.
21. **Validation fails closed:** unknown/unsupported rule operators error or are rejected at rule-create time — never silently pass.
22. **Samples never pollute production truth:** sample suppliers excluded from quota + normal lists (as sample orders already are); prod test-data purged via the bulk-erase endpoint (`[[project-bulk-erase-endpoint]]`).
23. **The live PO-loop E2E is a real gate** (current assertions, runs in CI on a schedule or pre-deploy).
24. **`OutputNode` AST is the only round-trippable contract; raw Scriban is a one-way terminal escape.**

---
---

# FINAL MASTER — Combined, deduplicated build plan (Parts 1+2+3)

All three analyses converged on the same core; Part 2 gave the model shape; Part 3 added the input-trust layer + the round-trip correction. This is what we build from.

## M.1 — Two trust bombs are P0 (ship before any feature)
1. **Output-trust:** silent fallback delivers the default doc while showing success.
2. **Input-trust:** validation reports green "Passed — meets all acceptance rules" on an order with qty `-3` / no rules.
Both let the product **lie about correctness** to a procurement user. Nothing else ships until these are gone.

## M.2 — The one structural cut (the product fix)
Replace the flat, six-mode, hardcoded-tree output layer with **one `OutputNode` AST + `EnvelopeConfig` (the template contract) rendered by format-aware emitters through one transform path shared by preview and delivery**, surfaced in one supplier-scoped designer (visual⇄AST, raw Scriban one-way), with canonical demoted to invisible plumbing. Paste-a-sample → infer the AST. This dissolves the "can't design output" complaint and the format×template bug seams together.

## M.3 — Unified workstreams (build order)

| WS | Title | Phase | Folds in |
|---|---|---|---|
| **WS-0** | **TRUST (input + output)** — (0a) kill silent output fallback → fail-loud; (0b) preview==delivery, delete the controller twin; (0c) mandatory `InvariantValidator` (qty>0, price>0, currency, PO id) on all formats; (0d) fail-closed unknown operators / validate at create-time; (0e) zero-rules copy neutral (kill "meets all"); (0f) flag negative qty at parse; (0g) `OutputMappingFellBack` provenance; (0h) cXML preview credential parity | **A (days)** | P1 WS-0, P2.A-17, P3.A |
| **WS-1** | `OutputNode` AST (`{name,nodeType:object\|array\|field\|attribute,children[],rule?}`) = the single output contract; resolution order order→supplier→revision→default | B | P2.C, P3.C |
| **WS-2** | Format-aware emitters (`IOutputEmitter`: one structured walker + one delimited walker + X12 segment walker) rendering the AST; kill hardcoded trees + the 13-field cap | B | P1 WS-2, P2.C |
| **WS-12** | `EnvelopeConfig` (X12 ISA/GS + cXML party identity as per-connection data, pinned) — independently shippable | B | P2.A-15 |
| **WS-3** | Surface AST in `MapperWorkbench` (visual⇄AST round-trip; raw Scriban one-way terminal); inline per-field `Expression`; extract `src/lib/mapping`; **characterization tests** freezing invariants (end the 55-commit churn) | C | P1 WS-3, P2.A-16/18, P3.C |
| **WS-6** | Demote canonical to invisible plumbing (one `deriveTargetFields` rule; no `CANONICAL LINE`); add "N columns not mapped" notice | C | P1 WS-6, P3.B |
| **WS-7** | Collapse 6 transform modes → resolver + 2 render paths (field-rule AST + raw Scriban) | C | P1 WS-7 |
| **WS-11** | Delete the dead second export stack (`IParsedOrderTransform`/`ParsedOrderTransformFactory`) | C | P2.A-14 |
| **WS-4** | Paste/upload supplier sample → infer the `OutputNode` AST (mirror `OpenAiSchemaInferencer`; deterministic for no-egress) | D | P1 WS-4 |
| **WS-5** | Collapse 4 map surfaces → 1 designer; 4 sidebar entries → 1 Suppliers hub; order-review = thin instance | E | P1 WS-5/WS-10, P2.D-6 |
| **WS-8** | Hide versioning behind Save / Save & make live (machinery kept, demoted) | E | P1 WS-8 |
| **WS-9** | Vocabulary purge (no builder jargon in first-line UI) | E | P1 WS-9 |
| **WS-13** | Launch hygiene: sample-supplier quota/list exclusion (`StripeBillingService.cs:763`); live PO-loop E2E fix + CI gate; retry-disable-when-config-missing; cookie-banner check; purge prod test data | A/E | P3.A/B |

## M.4 — Sequencing
- **Phase A — Trust (WS-0, WS-13 P0 bits).** Days. Ships independently; unconditionally worth it. **Gate: nothing green that isn't actually validated; nothing delivered that isn't actually the user's mapping.**
- **Phase B — Output contract (WS-1, WS-2, WS-12).** Backend-heavy. Output becomes structure-real for all formats. Backfill existing suppliers → AST, byte-parity gate before cutover.
- **Phase C — Designer (WS-3, WS-6, WS-7, WS-11).** The daily screen becomes the real, stable designer; canonical invisible; modes collapsed; dead stack gone.
- **Phase D — Inference (WS-4).** Paste-sample → AST. The complaint-killer.
- **Phase E — Consolidation (WS-5, WS-8, WS-9, WS-13 rest).** One surface, hidden versioning, clean vocabulary, hygiene.

## M.5 — What we do NOT build until Phase D lands
New input channels/formats beyond what exists, Invoice/ASN/PEPPOL on the new engine, more EDI dialects, RBAC/SCIM, extra AI providers, decorative dashboard work. **Paused, not cancelled.**

## M.6 — Definition of done (the litmus, now two-sided)
A coordinator opens **Supplier X**, sees and edits **the literal file X receives** (paste X's sample to shape it), and sends **PDF → that-exact-file** — with **preview bytes identical to delivered bytes**, **green status only when the order is actually valid** (qty/price/currency/id invariants enforced), and **never a silently-substituted default** — without hitting a canonical wall, a second map screen, a version-control button, or a week-old wire bug. When that holds on prod with the 24-PO corpus, the restructuring is done.

---

---
---

# PART 4 — Second ChatGPT review (absorbed)

**Source:** ChatGPT's structured 9–19 review. ~85% confirms the master; the net-new factual claim was verified. Scores it gave: marketing 7/10, app usability 4/10, mapping 5/10, **output designer 3/10, operational trust 3/10** — consistent with the master's verdict.

## P4.A — Verified
- **"Delivered order still offers 'Send to supplier' → duplicate delivery" → REFUTED.** Both layers protect it: FE relabels the action to "Done"/disabled on `delivered` (`useSendFlow.ts:78-83`, `SpineReview.tsx:2101-2115`); BE rejects redeliver of a delivered order (`OrdersController.cs:1539-1542` + `OrderStatusMachine.cs:35,67-68` — `delivered` ∉ `RedeliverableFrom`). No duplicate-delivery gap. *(Keep the explicit "Resend (confirm)" affordance idea only for the legitimately-redeliverable `delivery_failed`/`ready_to_deliver` states.)*

## P4.B — Confirms the master (no new work)
Output designer opens CSV on a JSON connection · output flexibility overstated for XML-family · validation passes with no rules · preview/download/delivery not guaranteed one renderer · mapping ownership unclear · canonical is implementation language · raw Scriban is dev tooling · missing exact-artifact preview · sample-driven template creation · publish draft↔live separation · "move topology off the primary dashboard." All already in WS-0/1/2/3/5/6/7/8.

## P4.C — Net-new, absorbed (amends the FINAL MASTER)

1. **Elevate the unifying PRINCIPLE (now the master's north star):**
   > **The user must always see exactly what arrived, what ProcuLink changed, why it changed, and exactly what will be sent.**
   → added to M.6. Drives WS-0 (provenance/parity), WS-3 (per-node "what this does" plain-English explanations), and the exact-artifact preview.

2. **Concrete IA + rename spec → sharpens WS-5 + WS-9.** Five primary areas (this **supersedes** Part 1's "4→1 Suppliers hub" — Templates is promoted to its own top-level area because templates are reusable across suppliers):
   **Orders · Supplier flows · Templates · Activity · Settings.**
   Renames: `Connections → Supplier flows` · `PO Mapping → Input fields` · `Canonical order → Normalized order` · `Output mapping → Output template` · `Acceptance rules → Validation checks` · `Delivery → Send method` · `Triage → Issues to fix` · `Conformance → Format validation`. Move Passport / standards paths / raw payloads / Scriban / protocol internals into an **Advanced** bucket.

3. **Explicit template SCOPE → amends WS-1/WS-5.** The override already has three scopes (order / supplier / connection-revision); surface them as named, user-visible choices: **"this order" · "this supplier flow" · "organisation-wide."** No more guessing which mapping wins.

4. **NEW — WS-14: Output-template test fixtures.** Attach a sample input + expected output to a template; assert match on publish (draft→test→publish gains real teeth). Composes with WS-4 (sample import) and WS-3 (characterization tests).

5. **3-pane designer spec → sharpens WS-3.** Left = available order fields · middle = target JSON/XML/CSV **structure tree** (the `OutputNode` AST) · right = **exact live output**. Each node exposes: source · transform · required/default · validation. Constants/conditions/loops/calculations via controls; code mode is the one-way expert escape.

6. **Responsive mapping → WS-3/WS-5.** The mapper tells a 1280px laptop to use a bigger screen; make the designer usable at standard laptop widths, not just `xl`.

7. **Dashboard headline = operational funnel → WS-13.** Replace the auto-% hero + topology-as-primary with headline counts: **received · blocked · ready · delivered · failed**. Keep the (honestly-labelled, gated) auto-% as a secondary stat; demote topology to a secondary "system map" tab. *(Reconciles P3.B: the auto-% isn't a lie, but it shouldn't be the headline.)*

8. **Copy fix:** marketing says "five stages," app shows six (Receive · Parse · Normalize · Validate · Transform · Deliver). Pick one count and make them match. → WS-9.

## P4.D — Updated FINAL MASTER deltas
- **M.3 table gains WS-14** (template test fixtures, Phase D, with WS-4).
- **WS-3** spec sharpened: 3-pane (fields | AST structure tree | exact output), per-node source/transform/required-default/validation/plain-English explanation, responsive at laptop width.
- **WS-5** spec sharpened: 5 primary areas (Orders/Supplier flows/Templates/Activity/Settings) + named template scopes + Advanced bucket; **Templates is top-level, not folded into the supplier hub.**
- **WS-9** gains the concrete rename table + the 5-vs-6-stage copy fix.
- **WS-13** gains: dashboard headline = operational funnel counts; topology → secondary tab.
- **M.6 DoD** gains the principle (see arrived/changed/why/will-be-sent) as the governing test.

---

*End of master. Parts 1+2+3+4 reconciled and verified (overblown/refuted claims excluded). The governing principle: **show what arrived, what changed, why, and exactly what will be sent.** Build starts at Phase A / WS-0 (the two trust bombs).*
