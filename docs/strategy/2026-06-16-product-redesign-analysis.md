# ProcuLink — Product Re-Analysis & Redesign (2026-06-16)

> Triggered by a founder screen-capture + a brutal-honesty brief. Grounded in: the video (10
> distinct UI states), a 7-agent code-grounding workflow (import/mapping/validation/output/UX/
> data-model), and direct codebase knowledge. **Analysis only — no code until approved.**

---

# Brutal Verdict

**Partial redesign. Keep the engine. Rip out the shell, the data funnel, and the mapping/output UX.**

The transformation *engine* (parsers, transforms, delivery, AES-GCM creds, audit, validation rules) is
real, tested (~1,800 backend tests), and valuable — **do not throw it away.** But three decisions have
quietly poisoned the product:

1. **A lossy canonical funnel.** The parser extracts 30+ fields; only 12 reach the canonical document;
   the raw source fields are scattered across 3 non-authoritative stores and, for structured formats,
   frequently captured as *nothing*. This is the literal cause of "canonical limits parsing,"
   "mappings not draggable," and "wires broken." (`OrderIngestionService.cs:407-423`, `:858-883`.)
2. **An enterprise EDI control-plane bolted onto a procurement coordinator.** Revision versioning
   (draft→test→publish→archive→rollback, immutability, "create draft from live"), 7 sub-editors per
   connection, 4-tab order screens, 5-stage rails, a "Spine," a "Passport," a "Conformance" profile —
   the wrong user is being shown a systems-integrator's cockpit.
3. **Concept duplication everywhere.** Two mappers in one screen with no switcher; five output modes
   with unclear precedence; the same mapping authored in two places; three different names for the
   same pipeline stage. The user can't predict what anything does.

You don't need a from-scratch rebuild. You need to (a) make the parsed model **lossless and
single-source**, (b) collapse the shell to **one linear flow**, and (c) ship **one mapper + one
working output designer**. That is achievable on top of the existing engine.

---

# Why Current Solution Fails

**Root cause #1 — the canonical funnel is lossy and the truth is fragmented (causes bugs 3, 5, 6, 7).**
- `ParsedOrder` has 30+ properties (`ParsedOrder.cs:7-33`, `ParsedOrderLine.cs:8-39`) — incl. lossless
  fields ContactName, Incoterms, ManufacturerPartNumber, Unspsc, NetAmount, Parties.
- `canonicalJson` is built as a **12-field anonymous type** (`OrderIngestionService.cs:407-423`) — the
  rest is dropped from the canonical document.
- Parsed truth is split across **four** stores with **no authority**: typed columns, `OrderParties`
  nav, `SourceCapture.TokensJson`, and `canonicalJson` — and async file-parse writes typed columns
  *only*, deliberately skipping `canonicalJson` (`:786-808`, two-tier truth `:716-731`).
- `canonicalJson` is **triple-overloaded**: parsed data + the mapping override (5 modes under
  `"mappingOverride"`) + provenance, one blob, three masters (`OrderMappingOverride.cs:7`,
  `SourceCapture.cs:11`).
- **The draggable source universe is often empty.** `SourceTokenizer` failures are swallowed
  (`:872-876`, tokens=null); structured parsers never populate `RawFields` (only the LLM PDF/email
  extractors do) → `SourceCapture` ends up empty → the mapper has **no source fields to drag**. The
  connection mapper even says so out loud: *"This order arrived already structured — there are no
  extra raw fields to remap."*

**Root cause #2 — wrong control plane for the user (causes bug 8 and most "this is confusing").**
- A connection exposes 7 sub-editors (Input mapping / Output template / Output format / Delivery
  channel / Item mappings / Acceptance rules / Catalog) **plus** a full version-control lifecycle
  (v1–v4, draft/test/publish/archive/rollback, immutable live, "create draft from live").
- An order exposes 4 tabs (Review / Passport / Conformance / Supplier-response) **plus** 2 sub-tabs
  (Triage / Full document) **plus** a 5-stage rail **plus** Design-structure/Enrich/Validate/Save
  **plus** 6 format buttons — on one screen.
- Pipeline stage labels are inconsistent across screens: the same step is "Normalized" / "Validate" /
  "Validated" (`InboxView.tsx:79`, `BridgeDashboard.tsx:84`, `OrderPassport.tsx:63`).

**Root cause #3 — duplication and dead/half-wired controls (causes bugs 1, 3, 4, 5, 7).**
- Order review mounts **both** the legacy triptych (SpineConnectors/WireDragLayer) **and** the new
  `MapperWorkbench` with no clean mode switch (`SpineReview.tsx`). Hence "Full document" *and* "Map
  fields by dragging" — and inconsistent wires/persistence.
- Three+ mapper implementations (`PoMappingEditor` static, `MappingEditor`, `MapperWorkbench`
  interactive) with different drag semantics → "draggable here, not there."
- "Select all" gates on `isRedeliverable()` (`InboxView.tsx:338,346`); any backend status not in that
  check makes **zero** rows selectable, silently (bug 4).
- Five output modes with unclear precedence (`OrderMappingOverride.cs:30-95`): Output / OutputTemplate
  / OutputTree / CustomFields / SourceMap. The output designer's preview shows **"UPDATING…/-"
  forever** with a blank Format box (bug 1/3, seen in the video).

**Root cause #4 — errors written for developers (bug 2).**
- Acceptance failures are templated as `"unitPrice ('100') failed rule max 50000"`
  (`SupplierAcceptanceService.cs:308-310`) and rendered verbatim (`ContextStage.tsx:180`), ignoring
  the human titles already curated in `RuleCatalog.cs:46-163`.
- Replay/test errors leak jargon: *"output format 'json' has no named conformance profile,"* *"1 of 5
  replayed orders failed to render,"* *"Rollback target must be a previously published (now archived)
  revision."*
- Output structural errors (missing item code, zero price) throw into `/operations/exceptions`, **not**
  into the validation list the user is looking at (`OutputFieldValidator.cs:62-112`).

---

# What Should Be Kept

- **The parsing engine** — 8 format parsers (CSV/XLSX/UBL/cXML/X12/EDIFACT/IDoc/PDF) + LLM PDF/email
  extraction. Real, tested, hard-won.
- **The transform + delivery engine** — `OutputTemplateEmitter` (the OutputNode AST, just hardened in
  B12), the per-format transforms, HTTP/SFTP/webhook delivery, AES-GCM credentials, audit/attempts.
- **`SourceTokenizer`** — the right idea (addressable raw values: `cell:r1c2`, XPath, `seg:TAG.el3`).
  It just needs to *always run and be the single lossless path*.
- **`InvariantValidator` + `RuleCatalog`** — solid validation foundation with human titles + standards
  refs (just not surfaced).
- **The OutputNode visual designer** (B12) — the right primitive for "design the output," once its
  preview works and it stops offering formats it can't emit.
- **The catalog/SKU mapping + AI suggestions + confidence calibration** — genuinely differentiating.

# What Should Be Removed

- **The lossy 12-field `canonicalJson` payload.** Replace with a lossless parsed model (below).
- **`canonicalJson` as a triple-overloaded blob.** Split parsed-data vs mapping-override vs provenance.
- **The legacy triptych mapper** (SpineConnectors / WireDragLayer / SourceTokenPanel) and the static
  `PoMappingEditor` drag pretense. **One** mapper: `MapperWorkbench`.
- **The user-facing revision lifecycle** (draft/test/publish/archive/rollback/immutability/"create
  draft from live"). Keep versioning *under the hood* for reproducibility; surface only "Save" and
  "Make live" (auto-versioned).
- **Most order-screen tabs** (Passport/Conformance/Supplier-response as top-level tabs) and the
  Triage-vs-Full-document split. One review surface.
- **Decorative dashboard topology + contradictory metrics** ("100% auto-processed" next to "170 need
  attention").
- **The five-mode output ambiguity.** One output contract (OutputNode tree) + one raw escape (Scriban),
  visible precedence.

---

# Recommended Product Concept

**One sentence:** *Drop in a purchase order in any shape → ProcuLink shows you every field it found →
you map the ones the supplier needs (most are pre-mapped) → it flags what's wrong → you design the
exact output → preview the real bytes → send. Reuse the mapping next time automatically.*

**The corrected mental model (7 concepts, named for a procurement user, not an engineer):**

| Concept (internal) | User-facing name | What it is |
|---|---|---|
| A. Source/Input | **The order you received** | The raw file/paste/payload, untouched |
| B. Detected Structure | **What we found** | Every field detected, with where it came from (lossless) |
| C. Internal PO Model | **Standard order** | A spine of known fields + a complete bag of everything else |
| D. Mapping Template | **Supplier recipe — inputs** | Which received field feeds each output field (reusable) |
| E. Validation Rules | **Checks** | What's missing/wrong/suspicious, in plain words + one-click fixes |
| F. Output Template | **Supplier recipe — output** | The exact shape the supplier wants (visual designer) |
| G. Preview | **What the supplier gets** | The real output bytes, before sending |

**The model is correct — with two mandatory corrections:**
1. **B and C must be lossless.** "Standard order" = a *spine of ~25 well-known fields* **+** a *complete
   raw-field bag* where **every** source field is a first-class, addressable, draggable citizen. Nothing
   is ever dropped because it "doesn't fit canonical." This is the single most important change.
2. **D and F are two halves of one "Supplier recipe."** Don't split "input mapping" and "output
   template" into separate editors/screens. The user thinks *"Supplier X wants THIS file built from
   THESE of my fields."* One recipe, one place.

---

# Recommended User Flow

A single linear path, same for every order, with the advanced surface tucked behind disclosure:

```
Import  →  Review & Map  →  Validate & Fix  →  Design Output  →  Preview  →  Send
 (1)         (2)               (3)               (4)            (5)        (6)
```

- The **stage rail is the flow** (these 6, named consistently *everywhere*). No separate Passport /
  Conformance / Triage / Full-document concepts — those become *panels within* a stage, not parallel
  universes.
- **90% of orders auto-advance** to "Validate & Fix" or "Preview" because the supplier recipe already
  exists. The user only stops where there's a decision (unmapped field, a validation flag, a new
  supplier).
- The first time you map a supplier, steps 2 + 4 *create the reusable recipe*. Every order after that
  *reuses* it — you mostly just glance at Preview and hit Send.

---

# Recommended Screen Structure

| Screen | Purpose | User sees | User does | System does | Errors → fix | Saved as template |
|---|---|---|---|---|---|---|
| **Dashboard** | "what needs me" | Orders needing attention; recent sends; per-supplier health | Click an order; jump to exceptions | Surfaces only actionable counts (no decorative sankey; no contradictory metrics) | — | — |
| **Import** | get a PO in | One drop zone: paste / upload / pick a sample; detected format badge | Drop or paste | Detects format; parses losslessly; routes to Review | "Couldn't read this" → show raw + manual field entry | — |
| **Review & Map** | confirm fields + map to output | 2 panes: **Received fields** (every detected field + value + source) ↔ **Output fields**; wires; live output preview docked | Drag/confirm mappings; accept AI suggestions; add a field | Pre-maps via supplier recipe + AI; shows confidence | Unmapped required output field → highlighted, AI suggests source | **Supplier recipe (inputs)** |
| **Validate & Fix** | catch problems | Plain-language checklist of issues, grouped by severity, each anchored to a field | One-click fix or edit inline | Runs invariants + supplier rules + output checks in one list | Each issue: what/why/where/fix-button | Acceptance rules (per supplier) |
| **Design Output** | shape the exact file | Visual output tree (the supplier's structure) + **working** live preview with real PO data; "paste a supplier sample" to auto-build | Edit structure; bind fields; format values | Renders the same bytes that will be delivered (preview == delivery) | Invalid structure for a format → fail loud, plain message | **Supplier recipe (output)** |
| **Preview** | last look | The real output bytes + a "what we changed & why" diff | Approve, or jump back to fix | Renders final artifact; runs final validation gate | Validation fail → blocks send, links to the field | — |
| **Send / History** | deliver + audit | Delivery channel, result, retry; past sends per supplier | Send / retry / download | Delivers; records attempt + artifact (auto-versioned recipe pinned) | Delivery fail → plain reason + retry | — |
| **Suppliers** (Library) | reusable recipes | One card per supplier: recipe, channel, catalog, last send | Edit recipe (= edit-in-place, auto-saves a new version) | Auto-versions on edit; "make live" is implicit on save | — | The recipe IS the template |
| **Templates / History** | reuse + replay | Saved recipes; test runs; golden samples | Clone, test, replay | Runs a recipe against sample orders | Test diff shown plainly | — |

**Net:** Dashboard + 6 flow stages + Suppliers + Templates. **No** Passport/Conformance/Spine/Triage
tabs, **no** visible revision lifecycle. ~9 surfaces instead of ~20.

---

# Mapping Editor Design

**One mapper (`MapperWorkbench`), two panes, drag-first, preview-always-visible.**

- **Left = Received fields.** *Every* detected field (spine + raw bag), grouped (Header / Parties /
  Lines / Other), each showing **its value** and **where it came from** (e.g. `cell B2`, `cbc:ID`,
  `PO1.el07`). This is the fix for "canonical limits parsing" — the user sees the supplier's *actual*
  fields, not 13 canonical slots.
- **Right = Output fields** for the chosen supplier recipe. Each row: bound source (or AUTO / fixed
  value / ƒx expression), confidence, required-marker.
- **Wires** connect left→right, drawn from real measured anchors (no bunching). Drag a left grip to a
  right zone to connect; dropdown fallback for non-drag users; keyboard-connect for a11y.
- **AI suggestions** appear as dashed ghost wires with confidence; Accept (✓) / Dismiss (✗); nothing
  auto-applies silently. Calibrated confidence already exists — surface it.
- **Line-item mapping** maps once (the line template), applies to all lines.
- **Add output field**, **rename**, **default value**, **transform (ƒx)** are inline on the row, behind
  a single "⋯" — not scattered toolbar buttons.

**Automatic:** format detection, spine pre-fill, recipe reuse, AI suggestions for unmapped, line-template
inference. **Manual:** confirming low-confidence mappings, custom transforms, new output fields.
**Avoid overwhelm:** collapse mapped fields by default ("13 mapped ✓" chip, expand on demand); show only
*unmapped / low-confidence* by default; advanced (ƒx, fixed, source-swap) behind the row "⋯".

---

# Output Designer Design

**Recommendation: Hybrid, visual-first.** Visual output tree (the OutputNode AST — already built in
B12) as the default, **paste-a-supplier-sample → auto-infer the tree** as the fast start, and a **raw
template (Scriban) one-way escape hatch** for the rare power case. This beats every single-mode option:

| Option | Verdict |
|---|---|
| Visual builder only | Good default, can't express every exotic shape |
| JSON/XML tree editor | This *is* the visual builder (format-aware) — keep |
| Template editor only | Powerful but excludes non-technical users — escape hatch only |
| AI-assisted only | Great accelerant, can't be the only path (trust) |
| **Hybrid (visual + paste-sample + Scriban escape)** | **MVP choice** — simple default, powerful ceiling |

**Non-negotiable fixes before it ships as "design the output":**
1. **The live preview must work.** Today it's stuck on "UPDATING…/-" with a blank Format box. Preview ==
   delivery, always rendered, never silent.
2. **Only offer formats it can validly emit** — JSON / XML / CSV (done in B12). cXML/UBL/X12 keep their
   dedicated valid transforms; the designer must not pretend to build them.
3. **Must support** (the brief's list): nested objects, arrays/line-items, static values, renamed
   fields, formatted dates, currency formatting, calculated fields (ƒx), required fields, conditional
   fields *(new — add a per-node "include when" predicate)*, supplier/customer-specific templates,
   preview with real PO data, validation before export. Most exist; **conditional fields** and **format
   helpers** (date/currency presets, not raw Scriban) are the gaps.

---

# Validation and Fix Design

**One list, plain language, anchored, fixable.** Merge the three validators that currently live in
three places (invariants, supplier acceptance, output-field) into one ordered issue list shown in
"Validate & Fix" *and* gating "Send."

Each issue is a structured object (not a string), so the UI can render affordances:
```
{ code, severity, field/lineRef, title (from RuleCatalog), why, suggestedFix, fixAction? }
```
- **What's wrong** — human title from `RuleCatalog` (kill the `"unitPrice ('100') failed rule max
  50000"` template at `SupplierAcceptanceService.cs:308`).
- **Why it matters** — one line ("the supplier rejects orders over €50,000 without approval").
- **Where** — click jumps to the field/line in Review.
- **Suggested fix + one-click** where deterministic ("Set currency to EUR", "Use catalog SKU
  SC-PMX94EG", "Clarify delivery date"); manual edit otherwise.

**Detect (all in one pass):** missing required fields, invalid/again-in-past dates, non-positive qty,
non-positive/zero price, missing SKU, unknown currency, broken/empty addresses, empty line items,
duplicate lines, wrong types, **and output-template render errors** (route
`OutputFieldValidator` failures into this list, not `/operations/exceptions`).

---

# Recommended Data Model

**The core change: a lossless parsed model + one authoritative store + a separated recipe.**

```
ParsedDocument (the lossless truth — replaces the 12-field canonicalJson)
  ├── spine: { poNumber, orderDate, currency, buyer{…}, supplier{…}, contact{…},
  │            incoterms, paymentTerms, totals{…}, requestedDeliveryDate, … ~25 known fields }
  ├── lines[]: { lineNo, buyerCode, supplierCode, mpn, ean, description, qty, unit,
  │              unitPrice, netAmount, discount, recipient, … }
  └── rawFields[]: { id, label, value, group, sourcePointer }   ← EVERY detected field, always,
                                                                  for EVERY format (the draggable bag)

SupplierRecipe (versioned, reusable — replaces "connection" jargon)
  ├── inputMapping:  outputPath → { sourcePointer | spineField | fixed | ƒx | manipulators }
  ├── outputTemplate: OutputNode tree (+ Scriban escape)
  ├── acceptanceRules[]
  ├── deliveryChannel
  └── catalog ref
  (auto-versioned on edit; "live" pointer; pinned per order for replay — versioning hidden from UI)

Order
  ├── parsedDocumentId  (lossless)
  ├── recipeVersionId   (pinned)
  ├── overrides         (per-order tweaks, SEPARATE table — not inside canonicalJson)
  ├── validationResults[] (structured)
  └── deliveryAttempts[]
```

**Why this fixes the bugs:**
- `rawFields[]` populated for **every** format (structured parsers call `SourceTokenizer` and **must**
  emit; no silent null) → the mapper always has draggable source fields (kills bugs 3, 5, 6).
- One authoritative parsed store → "Full document" and "Map by dragging" read the **same** data → one
  view (kills bug 7).
- Overrides leave `canonicalJson` → predictable parsed payload; recipe independently queryable/versioned
  (kills the triple-overload).

**Endpoints (consolidate):** `POST /orders/import`, `GET /orders/{id}` (returns parsed spine + rawFields
+ mapping state + validation + preview pointer), `PUT /orders/{id}/mapping`, `GET /orders/{id}/preview?format`,
`POST /orders/{id}/send`, `GET/PUT /suppliers/{id}/recipe` (auto-versions). AI = suggestions only
(deterministic everywhere else). Templates/history = recipe versions + test runs.

---

# Recommended Architecture

- **Backend (keep ASP.NET Core + the engine):** make `ParsedDocument` the single parsed entity (lossless
  spine + rawFields), one ingest path (kill the sync/async two-tier truth — both write the same model),
  `SupplierRecipe` as a first-class versioned entity (input mapping + output tree + rules + delivery),
  overrides in their own table. Validation = one service returning structured results. Transform = the
  one `OutputTemplateEmitter` path + Scriban escape; retire the 5-mode ambiguity into one resolver with
  a visible precedence (override > recipe > default).
- **Frontend (Next.js, keep):** one `MapperWorkbench`, one output `Designer`, one `IssueList`, one
  consistent stage rail. Delete the legacy triptych + `PoMappingEditor` drag pretense. State: TanStack
  Query per order; the mapper reads `{spine, rawFields, recipe, validation, preview}` from one order
  endpoint.
- **AI:** suggestions + paste-sample inference + calibration only. Everything that touches delivered
  bytes is deterministic.
- **Versioning/history:** automatic snapshot of `SupplierRecipe` on every save; order pins its version;
  "replay" runs a recipe vs a saved sample. The user never sees "draft/publish/archive" — they see
  "Save" and "Make live."

---

# MVP Scope

**Must exist (the spine of value):**
1. Lossless parse → `ParsedDocument` (spine + rawFields for **every** format).
2. One linear flow: Import → Review&Map → Validate&Fix → Design Output → Preview → Send.
3. One mapper with draggable real source fields + AI suggestions + reuse.
4. One issue list with plain language + one-click fixes.
5. The visual output designer with a **working** preview + paste-sample (JSON/XML/CSV).
6. Reusable `SupplierRecipe` (auto-versioned, lifecycle hidden).

**Wait:** EDI envelope design (X12/cXML/UBL stay on dedicated transforms), multi-buyer/customer
templates beyond per-supplier, advanced conditional logic, connector marketplace, the topology viz.

**Remove now:** legacy triptych mapper, `PoMappingEditor` drag, visible revision lifecycle, the 4 order
tabs + Triage/Full-document split, decorative dashboard, contradictory metrics, the 5-mode output
ambiguity, developer-jargon error strings.

**Simplify:** connection → "supplier recipe"; 7 sub-editors → one recipe page with inline sections;
stage labels → one consistent set.

**Can be hardcoded temporarily:** the ~25 spine fields; the default per-format output templates; the
catalog match threshold. **Must be flexible from day one:** `rawFields` (arbitrary source fields), the
output tree, the input mapping.

**Fastest path to useful:** Phase 1 (lossless model) + Phase 3 (one mapper reading rawFields) + Phase 5
(designer preview fix) — that alone makes "import → map any field → design output → preview → send"
actually work end-to-end without the funnel and without the duplication.

---

# Implementation Plan

> Each phase ships independently, behind the existing engine, with the suite green. Numbered to match
> the brief.

**Phase 1 — Lossless parsed model + single source of truth.**
- Goal: every detected field survives, one authoritative parsed store, overrides moved out of
  `canonicalJson`.
- Backend: introduce `ParsedDocument` (spine + `rawFields[]`); make **all** structured parsers emit
  `SourceTokenizer` output (no silent null at `OrderIngestionService.cs:872`); one ingest path; move
  mapping override to its own table.
- Frontend: none yet.
- Data: migration (additive: new tables; backfill rawFields on read where possible).
- Tests: golden parse → assert *no field dropped* per format; rawFields non-empty for every structured
  format; override round-trip from the new table.
- Acceptance: upload any of the 8 formats → every field appears in `rawFields`; `canonicalJson` no
  longer carries overrides.

**Phase 2 — Import + the spine model surfaced.**
- Goal: one Import screen; order endpoint returns `{spine, rawFields, recipe, validation}`.
- Backend: consolidate `GET /orders/{id}` to the unified shape.
- Frontend: Import screen (paste/upload/sample); Review reads the unified shape.
- Acceptance: import → Review shows every received field + its source pointer.

**Phase 3 — One mapper.**
- Goal: `MapperWorkbench` everywhere; delete the legacy triptych + `PoMappingEditor` drag; draggable
  real source fields.
- Frontend: retire `SpineReview` dual-mount; `MapperWorkbench` reads `rawFields`; fix wire anchors;
  fix "select all" (`InboxView.tsx:338` — read a shared, tested `isSelectable`).
- Acceptance: drag any received field to any output field; "select all" works; one mapper in both
  order + recipe contexts.

**Phase 4 — Validation & fixes as one list.**
- Goal: structured issues, plain language, one-click fixes, output errors included.
- Backend: `OrderValidationResult` carries `{code, severity, ref, title, why, fixAction}`; lookup
  `RuleCatalog` titles (`SupplierAcceptanceService.cs:308`); route `OutputFieldValidator` into the list.
- Frontend: one `IssueList`; fix buttons.
- Acceptance: every issue type renders a title + why + fix; output render errors appear here, not only
  in exceptions.

**Phase 5 — Output designer (working).**
- Goal: visual tree + paste-sample + **live preview that renders** + format helpers + conditional
  fields.
- Backend: preview == delivery (done in B12); add date/currency formatters + per-node "include when".
- Frontend: fix the stuck "UPDATING…/-" preview + blank Format; collapse the 5 modes to one resolver.
- Acceptance: design a structure → preview shows real bytes instantly; conditional + formatted fields
  emit correctly.

**Phase 6 — Preview + export/send.**
- Goal: final preview + "what changed" diff + send/retry; recipe auto-versioned + pinned.
- Acceptance: preview == delivered bytes; send records attempt + pinned recipe version.

**Phase 7 — Supplier recipes + history (lifecycle hidden).**
- Goal: "connection" → "supplier recipe"; 7 sub-editors → one page; draft/publish/archive replaced by
  Save / Make live (auto-versioned underneath); test/replay kept but plain-language.
- Acceptance: edit a recipe → auto-saves a version → "Make live" in one click; no draft/archive jargon.

**Phase 8 — AI suggestions polish.**
- Goal: surface calibrated confidence; paste-sample inference for input mapping too; "explain this
  suggestion."
- Acceptance: suggestions show confidence + provenance; nothing auto-applies silently.

---

# Concrete Tasks for Claude Code

(Ordered; each is a shippable, test-gated unit. Await approval before starting.)

1. **T1** Add `ParsedDocument` (spine + `rawFields[]`) + migration; make CSV/XLSX/UBL/cXML/X12/EDIFACT/
   IDoc parsers emit `SourceTokenizer` rawFields (no silent null). Golden "no-field-dropped" tests.
2. **T2** Move `OrderMappingOverride` out of `canonicalJson` into its own table; round-trip tests.
3. **T3** Unify ingest (sync + async write the same `ParsedDocument`); kill the two-tier truth + guard
   test; reload tests.
4. **T4** Unified `GET /orders/{id}` returning `{spine, rawFields, recipe, validation, previewPtr}`.
5. **T5** Frontend: retire legacy triptych + `PoMappingEditor` drag; one `MapperWorkbench` reading
   `rawFields`; fix wire anchors; fix "select all" via shared tested `isSelectable`.
6. **T6** Structured `OrderValidationResult` + `RuleCatalog` title lookup + route output errors into the
   list; one `IssueList` UI with fix buttons.
7. **T7** Output designer: fix the stuck preview + blank Format; one output resolver (visible
   precedence); add date/currency formatters + conditional ("include when").
8. **T8** Rename "connection" → "supplier recipe"; collapse 7 sub-editors to one page; replace
   draft/publish/archive UI with Save / Make live (auto-version under the hood).
9. **T9** Dashboard: remove decorative topology + contradictory metrics; show actionable counts only.
10. **T10** Stage-label single source of truth; consistent rail everywhere; jargon pass (Spine/Passport/
    Conformance/Triage → plain words or removed).

# Acceptance Criteria

- **No field is ever dropped:** any of the 8 formats → every detected field is a draggable received
  field. (Kills bug 6.)
- **One mapper, drag works in every context;** wires anchor to real fields; "select all" selects every
  selectable row. (Kills bugs 3, 4, 5.)
- **Output designer preview renders real bytes instantly;** preview == delivery; only emittable formats
  offered. (Kills bug 1 in the designer.)
- **Every error is one human sentence + a fix.** No `"failed rule max 50000"`, no "named conformance
  profile." (Kills bug 2.)
- **One linear flow, one set of stage names, no revision jargon;** a new user reaches "Send" on a
  pre-mapped supplier without help. (Kills bugs 7, 8.)
- Engine tests stay green throughout; new golden + snapshot tests per phase.

# Bug & Quality Strategy

- **Golden sample POs** per format (the `~/Downloads/PO` corpus) → assert parse losslessness + output
  snapshot per supplier recipe.
- **Output snapshot tests** (byte-level, line-ending-normalized — the B12 pattern) so designer changes
  can't silently alter delivered bytes.
- **Schema validation** of output where the format has one (Peppol UBL, cXML DTD) on the dedicated
  transforms.
- **Per-supplier test runs** (replay N saved orders through a recipe; assert renders + validates).
- **One structured error type** end-to-end; user-friendly rendering; nothing raw.
- **Live in×out matrix** smoke (the existing practice) before each deploy.

---

# Final Recommendation

**Stop adding surface. Subtract.** The engine is good enough to win on; the product is losing because
of a lossy data funnel and an integrator's cockpit shown to a procurement coordinator. Do three things,
in order:

1. **Make the parsed model lossless** (Phase 1) — this single change unblocks the mapper, the output
   designer, and "design it the way I want," and erases four of the eight reported bugs at the root.
2. **Collapse to one linear flow with one mapper and one working output designer** (Phases 2–5) —
   delete the duplicates, the tabs, and the revision jargon.
3. **Hide the version-control machinery** behind Save / Make-live (Phase 7) — keep the reproducibility,
   lose the cognitive load.

This is a **partial redesign**, not a rebuild: ~70% of the value (the engine) stays; the funnel and the
shell change. Estimate: Phases 1–5 are the MVP of the *good* product; 6–8 make it polished. Await
approval, then I start with T1 (the lossless model) since everything else depends on it.

---

## Build progress log

### 2026-06-16 — Shell wave A: hide the lifecycle (Phase 7 of the plan, pulled forward) — SHIPPED + LIVE-VERIFIED

The founder greenlit "Shell wave — hide the lifecycle" before the deeper structural phases.
Shipped to prod (frontend `project-proculink` main) and verified end-to-end via Claude-in-Chrome
on the real **Acme HTTP (JSON)** connection (created + discarded a real draft to exercise the full loop):

- `77adc6f` — **Click-to-edit overlay** on the read-only (published) connection mapper. The user
  clicks the mapper → a draft is transparently created (`createDraftMutation`) → the mapper becomes
  editable. The header "Edit mapping" button steps aside while a draft exists (no second-draft footgun).
- `149d849` — **Plain-language sweep** over the revision lifecycle (machinery unchanged, display only):
  badge Published→**Live** / Archived→**Previous** / Test→**Tested** (central in `RevisionStatusBadge`,
  propagates to list + replay); buttons Run tests→**Test**, Publish→**Make live**, Roll back→**Restore
  this version**, Archive→**Discard**, Create draft→**Edit mapping**; confirm dialogs + success notices +
  empty states + the `/connections` first-visit guides reworded; dropped the "immutable" lecture.
- `88da172` — **Order-review two views unified**: "Triage | Full document" pills + a separate "Map fields
  by dragging" CTA (three names for two views) → **Fix issues (N) | Map fields**, CTA word now matches the
  pill. subView ids + `?view=` params unchanged (pure relabel). Touched `SpineReview`, `FixQueueTriage`,
  `OutputPreview`, `section-guides`.
- `2b9e07a` — dropped the duplicate "Live" pill (badge already says "Live" → was "Live Live"); live row
  date now "Live since <date>".
- `718ea0d` — honest "Discarded" label for archived-never-live versions (was misleadingly "Published —";
  fixed a pre-existing abandoned-draft row too).

All `bunx tsc --noEmit` clean; `section-guides.test.ts` (21) green. All five deploys Ready/Production.

**Next (per plan):** the deeper structural phases still stand — T1 lossless model (largely done — see
`2026-06-16-T1-lossless-capture-plan.md`), then collapse duplicate surfaces (Phases 2–5). The shell wave
above is the low-risk, high-visibility slice of Phase 7 done first.
