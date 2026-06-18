# Unified Order Workshop — design spec

> The money-maker screen. Replaces today's two-mode order-review (`SpineReview`: Triage rail *or*
> Classic mapper — they never coexist) with ONE collapsible 3-zone screen. Brainstormed +
> founder-approved 2026-06-16 (mockups: `order_workshop_unified_3zone`, `order_workshop_ai_mapping_focus`).

## North star

> The user always sees **what arrived, what ProcuLink changed, why, and exactly what will be sent** —
> and only ever touches the few things that are genuinely uncertain.

A procurement coordinator opens an order → AI has already mapped ~90% → they fix only the flagged few
(Accept / pick a source) → watch the exact supplier output update live → Send. One screen. No
Triage-vs-Full-document swap, no canonical wall, preview == delivery, green only when actually valid.

## Locked decisions (from the brainstorm)

1. **Center = issues over the mapper.** A plain-language issue list sits on top; the drag mapper is
   directly below, always visible. Click/keyboard an issue → the mapper scrolls + highlights that
   field; preview updates. At 0 issues the list collapses to a green "ready to send" bar.
2. **Left = lossless source-first.** Every received field (not just the 12 canonical leaves), grouped
   (Header / Parties / Lines / Other), each with its value + source pointer (`cell B2`, `cbc:ID`), all
   draggable. This folds in **T1 (lossless capture)** on the backend.
3. **Mapping is AI-first; drag is the escape.** On load the AI auto-maps using catalog + the order's
   past history + calibrated confidence. ~90% lands pre-mapped and **collapsed** behind an "N mapped by
   AI · review" toggle. Only **attention** rows show (unmapped OR confidence below the calibrated
   trust threshold). Each attention row: `received → output`, confidence, provenance, **[Accept]** /
   **change-source dropdown** / a drag handle. Drag-to-wire is the optional manual override, **not** the
   required mechanic. Nothing low-confidence auto-applies silently.
4. **Every zone collapses; the layout adapts.** A `Focus: All / Mapping / Output` control + per-zone
   chevrons. Collapse the sides → the mapper goes full-width; collapse left+center → the output
   designer/preview goes full-width. Collapsed zones become a thin rail with a chevron + vertical label.
5. **Output is flexible.** `+ Add output field` on the mapping side; the right zone reshapes the output
   structure (the existing `OutputStructureDesigner` — visual node tree, paste-a-supplier-sample →
   infer, per-format), switches format (re-renders the preview), all preview == delivery.
6. **Reduced mobile.** Phone/tablet shows issues (inline quick-fixes) + the live output preview +
   Send/retry; the full drag-map workstation is desktop-only with an honest "Open on desktop to map
   fields." The orphaned triptych mobile/tablet fallbacks are deleted.

## Approach

**Build a new `OrderWorkshop` component that composes the proven pieces; freeze invariants with
characterization tests; ship flag-gated; prove on prod; then delete the old two-mode code.** Not an
in-place hack of the 2,400-line `SpineReview` (that tangle bred 3 rebuilds), not a from-scratch rewrite
(throws away the working mapper / preview / AI / validator). Converge by composition + tests.

## Components (each a focused, independently-testable unit)

| Unit | Responsibility | Composes / depends on |
|---|---|---|
| `OrderWorkshop` (shell) | 3-zone responsive grid; mounts the zones; owns nothing but layout | `useWorkshopLayout` |
| `useWorkshopLayout` | collapse/focus state `{ focus: all\|mapping\|output, leftCollapsed, rightCollapsed }` → derived grid columns; session-persisted (layout state, NOT a persona flag) | — |
| `ReceivedZone` (left) | lossless source fields, grouped, draggable, source pointers, collapse rail | the order's `rawFields` (T1) + existing drag source |
| `IssuesPanel` (center-top) | the one ordered issue list; click → focus field; green ready bar at 0; gates Send | the unified validator (already built, Phase 4) |
| `MappingPanel` (center) | AI-first: collapsed "N mapped by AI" + attention rows (Accept / change-source / drag); `+ Add output field` | existing `MapperWorkbench` wire engine + `IAiMappingService` suggestions + calibration |
| `OutputZone` (right) | preview == delivery + format switch + reshape (designer) + Send | existing `OutputPreview` + `OutputStructureDesigner` + send flow |

`MapperWorkbench` (the wire engine), `OutputPreview`, `OutputStructureDesigner`, the validator, and the
AI suggestion + calibration services are **kept and reused** — `OrderWorkshop` is the shell that unifies
them under one layout and the AI-first interaction.

## Data flow

One order endpoint returns the unified shape the workshop reads in one query (TanStack):
`{ spine, rawFields (lossless), mapping (suggestions + confidence + provenance + accepted state),
issues (structured), previewPtr }`. Accept/edit a mapping → `PUT mapping` → preview + issues refetch.
Preview and delivery render through the **same** transform path (already true). Send gates on
`issues.length === 0 && invariants pass`.

## Backend — T1 lossless capture (Phase P0)

- `SourceTokenizer` runs + persists `SourceCapture.TokensJson` on **every** ingest path, incl. the
  API/pushed-payload path (today some paths skip it → "nothing to drag" for structured orders).
- Add a `.json` arm to `SourceTokenizer` (today JSON → empty).
- `GET /api/orders/{id}/source-tokens` prefers the persisted capture, falls back to live re-tokenize
  (removes the no-file / purged-blob / R2-fail fragility).
- No schema change (`SourceCapture.TokensJson` exists). Optional lazy backfill for old orders.

## Mapping contract (AI-first) — precise behavior

- **Auto-map on load:** for each output field, resolve the best source via catalog + past-order
  fingerprint + AI, with calibrated confidence. `confidence ≥ trusted-threshold` → state `auto`
  (counted in "N mapped by AI", collapsed, reviewable). Below → state `attention`.
- **Attention row actions:** `Accept` (commit the suggestion), `change source` (dropdown of received
  fields + fixed value + ƒx), or **drag** (manual wire via the kept engine). Provenance + confidence
  always shown; AI violet only.
- **Accept-all** uses the already-fixed calibrated/raw boundary alignment (count == acted).
- **Never silent:** low-confidence never auto-commits; high-confidence is committed but labeled
  "mapped by AI" and is one click to review/override.

## What gets deleted (only after the new path is proven on prod)

- `SpineReview` two-mode `subView` split (Triage vs Classic branches) + the `?view=` swap.
- `FixQueueTriage` as a separate screen (issue logic → `IssuesPanel`; its single-line ContextStage
  retired).
- Orphaned old triptych: `SpineConnectors`, `WireDragLayer`, `SourceTokenPanel`, and the
  `TabletSpineLayout` / `MobileSpineAccordion` fallbacks (replaced by the reduced-mobile view).

## Invariants — frozen by characterization tests (end the 55-commit churn)

1. Adding or wiring a field **never shrinks** the visible target list.
2. Preview bytes **==** delivery bytes, every format.
3. The issues list **==** the actual send-gating validator (no green-on-garbage; invariants always run).
4. **Every** source field appears in the received zone for every format (lossless).
5. Collapsing/focusing a zone **never loses** an unsaved mapping edit (the draft-survives class).

## Phasing (each independently shippable + prod-verified)

- **P0 — backend lossless (T1).** Tokenize+persist every path + JSON arm + endpoint precedence +
  golden "no field dropped" tests. Ships invisibly (the existing pane just gets more fields).
- **P1 — `OrderWorkshop` desktop shell.** Compose mapper + `IssuesPanel` + `OutputZone` + collapse/focus
  + AI-first mapping list. Behind a feature flag. Characterization tests. Prove on prod (real PO
  corpus) side-by-side with the old screen.
- **P2 — cutover + mobile.** Reduced-mobile view; flip the flag; delete the old two-mode + orphaned
  code. Re-verify prod (the 3-supplier × 3-format live delivery proof).

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| Heart-piece churn (rebuilt 3×) | Composition + characterization tests + flag-gate + keep-old-until-proven |
| T1 backend coupling delays the visible win | P0 ships independently first; P1 reads its output |
| Collapse vs unsaved edits | Invariant #5 + test |
| Mobile cramming | Reduced-mobile by design; "open on desktop" |
| AI mis-maps silently | Low-confidence never auto-commits; provenance + Accept required |

## Test strategy

- **Backend:** golden no-field-dropped per format (Postgres, not InMemory); preview==delivery parity
  across CSV/JSON/XML/cXML/UBL.
- **Frontend:** vitest for `useWorkshopLayout` + the mapping-list model; characterization tests for the
  5 invariants; e2e — cold-load order → AI-mapped → accept the few → issues clear → send; collapse/focus;
  mobile reduced.
- **Live:** prod with the `~/Downloads/PO` corpus + the in×out matrix smoke before each deploy.

## Definition of done

One screen. A coordinator opens an order, sees every received field, the AI has mapped the easy 90%
(collapsed), fixes only the flagged few (Accept / dropdown), watches the exact supplier output update
live, collapses zones to focus when they want, and sends — with preview bytes identical to delivered
bytes and green status only when the order is actually valid — without a Triage/Full-document swap, a
canonical wall, or a week-old wire bug. Verified on prod with the real PO corpus.
