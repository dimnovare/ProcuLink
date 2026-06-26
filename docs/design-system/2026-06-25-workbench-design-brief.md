# ProcuLink Workbench — Design Brief for Claude Design

*Prepared 2026-06-25. Describes the CURRENT behaviour of the "Workbench" screen so a designer can redesign it for clarity, calm, and trust. This is a functional + UX brief, not a visual spec — but it ends with the locked visual canon the redesign must stay inside.*

---

## 1. What ProcuLink is (one paragraph)

ProcuLink is a B2B procurement bridge. A buyer's purchasing team receives or creates purchase orders (POs) in whatever format they have (CSV, Excel, PDF, email, XML/EDI, cXML…), and every supplier they send to wants those POs in a *different* required format and delivered over a *different* channel (HTTP API, SFTP, email, an ERP like Erply/Directo, etc.). ProcuLink imports the order, validates it, lets the user resolve item codes and fix problems, transforms it into exactly the shape the chosen supplier accepts, previews the real output, and delivers it — keeping a full audit trail. **The Workbench is the screen where a single order is reviewed, corrected, shaped, previewed, and sent.** It is the heart of the product.

## 2. Who uses it (design for this person)

A **procurement coordinator**, not a developer. They:
- Are time-pressured and handle many POs a day.
- Are **anxious about correctness** — sending a wrong PO to a supplier has real cost (wrong quantity, wrong price, a rejected order, an angry supplier).
- Are **not technical** — they do not know what cXML, EDIFACT, UBL, "namespaces", "canonical model", or "Scriban" mean, and should never need to.
- Think in plain business terms: "the order we received", "what the supplier will get", "this line has no item code", "is this exactly what they'll receive?", "send it".
- Often work on a **laptop** (13"–15"), sometimes in a split window.

**Their emotional need from this screen: confidence.** "Is what I'm about to send exactly right? Show me, in plain language, anything that's wrong, let me fix it fast, let me see precisely what the supplier receives, and let me send it without fear."

## 3. The Workbench's single job

> Turn one received purchase order into **exactly** what this specific supplier accepts, surface anything wrong in plain language, let the user fix it inline, show them the real output, and send it — with total confidence it's correct.

Everything on the screen should serve that one sentence. Anything that doesn't is noise.

## 4. The end-to-end flow the screen supports

1. **Open** an order (from the inbox list) → land on the Workbench.
2. **Understand what arrived** — see the real values that were parsed from the buyer's file (PO number, buyer, supplier, currency, total, and every line: item code, description, quantity, unit, price).
3. **Fix issues** — a single plain-language list of everything blocking or worth checking (missing item codes, validation failures, AI suggestions to confirm). Resolve each inline; accept good AI suggestions in bulk.
4. **Map fields** (when needed) — decide which received value feeds each field the supplier's output format requires; adjust a value if needed (trim, reformat a date, set a default).
5. **Preview** — see the actual bytes that will be delivered, in the supplier's real format, framed as "this is exactly what they receive".
6. **Send** — one confirmed action, only enabled when nothing is blocking.

The user does NOT always need step 4: for a well-known supplier the mapping is already learned and auto-applied, so most orders are really "check issues → preview → send".

## 5. Screen anatomy (current state — every region)

The route is `/inbox/[orderId]`. Top to bottom, on a desktop/laptop (≥1024px):

### A. Header (always)
- Back arrow → inbox.
- **PO number** as the page title (e.g. "PO-2026-00417").
- **Status badge** in plain words (e.g. *Needs review*, *Ready*, *Delivered*, *Delivery failed*).
- Conditional badges: *"Looks like an invoice"* (held for review), a *dead-letter* warning pill.
- A quiet line: **Buyer name → Supplier name · order total**.
- A small **progress stepper** (Received → Review → Map → Preview → Send-ish) — currently *derived/optimistic*, a known weakness (it can imply progress the server hasn't actually made).
- **"Details"** button (opens a drawer with order metadata + a "supplier rules check").
- **Focus** control with three options: *All / Mapping / Output* (collapses panes to let the user concentrate).
- **Send to supplier** button — the primary action.

### B. Send-readiness strip + Issues panel (stacked, currently two surfaces)
- A **SendReadinessStrip**: a summary bar of how many things block sending.
- The **Issues panel** ("Fix these to send · N issues · M must-fix"):
  - A **bulk-accept** row: *"Accept all AI suggestions (N)"* and *"Accept the high-confidence ones only (M)"*.
  - An ordered list. Each issue = a severity chip (**Must fix** / **Warning** — colour **and** word, never colour alone), a **plain-language title**, a one-sentence **"why"**, and an **inline action** appropriate to the issue (type a missing code, accept/replace an AI suggestion, confirm a flagged line).
  - When zero issues remain → a green **"Ready to send"** bar.
- *Known weakness:* the strip and the panel say overlapping things; the same blockers can appear 2–3 times, pushing the actual work down the page.

### C. The mapper (the core, ≥1024px) — two columns + a live preview
A toolbar above it (Save mappings, Send, "Customize output layout", "Fill from catalog", "Standards check", show/hide connections) — *currently too many verbs in one row.*

- **Left — "What we received"** (the buyer's order):
  - Header + field count + a chip for the source type (CSV / PDF / XML…).
  - A **search** box and **filter chips** (All / Unmapped / Mapped / Has AI suggestion / Has value).
  - Fields grouped in collapsible sections: **Header**, **Parties**, **Line items**, **Other fields**.
  - Each row shows the **field name and its REAL parsed value** (e.g. `Buyer item code → HX-4471`, `Quantity → 12`). Empty values are shown honestly as "(empty)".

- **Right — "What we'll send"** (the supplier's required output fields):
  - Header + **"Add output field"**.
  - **Attention-first ordering**: fields that still need something (required + unmapped) are at the top; already-good fields collapse under a *"N fields ready"* group.
  - Each row **leads with the human field name** (e.g. *Order ID*, *Buyer reference*), with the machine path (e.g. `cbc:ID`, `BEG03`) demoted to a small secondary line.
  - A **status** per row: *mapped from a source* / *fixed value* / *auto* / **needs a value** (amber, only when truly required) / *not set*.
  - Inline controls: pick the source, **"Edit value"** (opens a small popover to trim, find-&-replace, reformat a date, set a default, etc. — all in plain words), and an inline **AI fix** ("Suggested … Apply") with confidence.

- **Between/under them — the live preview** ("MapperPreviewPane"):
  - Header: **"This is exactly what {Supplier} receives"** + the format name.
  - The pane renders the **actual delivered bytes** (the real CSV/XML/cXML/UBL/X12/JSON that will be sent).
  - Format pills (the delivered format is primary; the others are a quiet "preview-as" option, clearly secondary).
  - **Copy** and **Download** of exactly what's shown.

### D. Output structure designer (modal, opened from "Customize output layout")
For shaping a brand-new supplier output. Left = an editable tree of the output (objects, lists, fields); right = the same live "what the supplier receives" preview. First run offers **"paste a supplier sample → build the structure from it"**. Per-node: rename, bind a source, choose a **Date/Number/Currency** format. Advanced controls (a condition like "only include this line when quantity > 0", and XML namespaces) are tucked behind a per-node **"Advanced"** disclosure so a coordinator isn't confronted with them.

### E. Mobile / narrow (<1024px) — MobileTriage
A simpler surface: review the order, resolve item codes, bulk-accept AI suggestions, and send. Field-mapping is not available at this width; the screen now **says so honestly** ("review and send here; to map output fields, open on a wider screen"). 

### F. Failure & special states
- Dedicated panels for **parse failed / transform failed / delivery failed** (with the real error + retry).
- **Scanned/image-only PDFs**: every extracted line is flagged "needs review" (assisted, never silently auto-sent).
- **No supplier / unsupported file / textless PDF**: honest, specific messages.

## 6. The data shown (so the designer knows what's real)

Real, live data for one order: PO number, buyer name, supplier name, order date, currency, total; per line: line number, buyer item code, **supplier item code (the thing often missing → the #1 issue)**, description, quantity, unit, unit price, line amount. Plus, per output field: its status, its bound source, any fixed/adjusted value, and any AI suggestion (with a confidence % and a short reason/provenance). The preview shows the genuine generated output bytes.

## 7. Interaction model (the verbs)

- **Resolve an issue** inline (type a code, accept a suggestion, confirm a flag).
- **Bulk-accept** AI suggestions (all, or only high-confidence).
- **Pick a source** for an output field (a searchable list of received fields, each showing its real value).
- **Edit a value** (trim, reformat date, find-&-replace, default-when-empty, multiply/divide) — plain words, live preview.
- **Customize the output structure** (the designer modal) — only for new/custom suppliers.
- **Preview** updates live as the user changes anything.
- **Send** — single primary action, confirmed, only enabled when nothing blocks.

## 8. States the design must cover

Loading (skeleton), empty (no issues = green ready; no fields; no supplier), error (parse/transform/delivery failed with retry), **dirty vs saved** (the user must always know their edits stuck — there's now a "✓ Saved" confirmation), **blocked vs ready to send**, AI-suggestion-present, scanned-PDF-review, and the read-only/expired-plan case.

## 9. The trust contract (the most important thing to protect)

Three promises make this screen trustworthy. The redesign must keep them loud and obvious:
1. **One validator → one gate.** The exact same checks that populate the issue list also gate the Send button. There is never a hidden reason it won't send. "Fix these N things" is literally what's standing between the user and Send.
2. **Preview == delivery.** What the preview shows is the **real bytes** that get sent — not a mockup. The "This is exactly what {Supplier} receives" line is the single most reassuring statement on the screen; it should feel that way.
3. **Assisted, never silent.** Anything the system guessed (AI mapping, a scanned-PDF line, a low-confidence extraction) is visibly flagged for human confirmation, with its confidence and reason — never auto-sent behind the user's back.

## 10. Language rules (keep ALL jargon hidden)

The user must never see developer/EDI vocabulary. Current plain-language mapping the design must preserve and extend:
- "Canonical model / source token / output tree / transform / override / revision / archive / replay / conformance / passport / spine / raw payload / Scriban" → **never shown**.
- "What we received" / "What we'll send" / "This is exactly what {supplier} receives".
- "Must fix" (not "Blocking"), "Heads-up/Warning", "Supplier rules check" (not "Standards conformance").
- "needs a value", "Edit value", "Add output field", "Build from a sample".
- Output rows lead with the **human field name**, not `cbc:ID` / `BEG03`.

## 11. What's working well today (preserve in any redesign)

- Showing the **real received values** (not placeholders).
- **Honest per-field status** (not "everything is red").
- **One issue list** in plain language with inline fixes + bulk-accept.
- **Preview == delivered bytes** with the "what the supplier receives" framing.
- **Honest disabled-with-reason** controls and honest failure panels.
- The output designer's **paste-a-sample** first run + dirty-guard.

## 12. Known pain points the redesign should solve

1. **Density / too many surfaces.** The header is crowded (back + PO + status + stepper + Details + Focus + Send), and the issues are summarised in 2–3 stacked places before the user even reaches the mapper. **Goal: one calm path. One issue summary, one clear next action, the actual work above the fold.**
2. **Too many toolbar verbs** in the mapper (Save, Send, Customize output layout, Fill from catalog, Standards check, show/hide connections). **Goal: one obvious primary action; everything else subordinate or progressively disclosed.**
3. **Two mapping metaphors** coexist (pick-a-source dropdowns AND draggable "connection wires"). **Goal: pick ONE mental model that a non-technical user grasps instantly.**
4. **Two places to shape output** (inline "add field" + the separate designer modal). **Goal: make it obvious which to use when.**
5. **The optimistic progress stepper** can overstate where the order really is. **Goal: a status indicator that reflects reality.**
6. **The trust line is under-emphasised** relative to its importance. **Goal: make "this is exactly what they receive" the visual anchor of the preview.**
7. **The output designer is still powerful/technical.** **Goal: keep the everyday controls (name, source, date/number/currency) effortless; keep the rest out of sight until needed.**

## 13. Layout schematic (current, desktop ≥1024)

```
┌──────────────────────────────────────────────────────────────────────────┐
│  ‹ Back   PO-2026-00417   [Needs review]      Buyer → Supplier · €4,210    │
│           · · · progress · · ·            [Details] [Focus ▾]  [ Send ▸ ]  │
├──────────────────────────────────────────────────────────────────────────┤
│  ⚑ 3 to fix before you can send                            (readiness)     │
│  ┌── Fix these to send · 3 · 2 must-fix ───────────────────────────────┐  │
│  │  [Accept all AI suggestions (4)]   [Accept high-confidence only (2)] │  │
│  │  • Must fix · Line 2 has no supplier item code   [ type code ____ ]  │  │
│  │  • Must fix · Line 5 price looks off (why…)       [ confirm ] [edit] │  │
│  │  • Warning  · Line 7 — AI suggested ACM-90 (92%)  [ apply ] [manual] │  │
│  └─────────────────────────────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────────────────────────────┤
│  [toolbar: Save · Customize output · Fill from catalog · Standards · Send] │
│  ┌── What we received ───┐        ┌── What we'll send ──────────────────┐  │
│  │ search… [filters]     │        │  [+ Add output field]               │  │
│  │ ▸ Header              │        │  ⚠ Needs a value                    │  │
│  │ ▸ Parties            │   ⇆    │   Supplier item code  ← pick source │  │
│  │ ▾ Line items         │        │  ──────────────────────             │  │
│  │   Item code  HX-4471 │        │  ▸ 14 fields ready (auto-mapped)    │  │
│  │   Quantity   12      │        └─────────────────────────────────────┘  │
│  │   Unit price 38.50   │        ┌── This is exactly what Acme receives ┐ │
│  └───────────────────────┘        │  [XML] csv cxml ubl…   [Copy][⬇]    │ │
│                                    │  <Order><ID>PO-2026-00417</ID>…     │ │
│                                    └─────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```
*Below 1024px this collapses to a single-column "review & send" (MobileTriage); field-mapping needs ≥1024.*

## 14. Visual canon the redesign MUST stay inside (do not reinvent the brand)

ProcuLink already has a **locked visual direction** ("The Bridge Layer"). The new Workbench design should be a better *information design / layout / interaction* within this system, not a new visual identity:
- **Palette:** deep navy + violet accent; calm, professional, trustworthy. Light surfaces, dark ink, restrained colour used semantically (amber = needs attention, green = ready, red = failed).
- **Motion:** subtle; respects `prefers-reduced-motion`.
- **Iconography:** clean line icons (no emoji as icons).
- **Voice:** plain business English, calm, confident.
- **Naming in the app:** Dashboard / Suppliers / Buyers / Deliveries — plain procurement words (no "bridge/dock/crossing" vocabulary in user-facing copy, even though the visual system is "Bridge Layer").
- Reference: the existing design system lives in `docs/design-system/` (start with `00-agent-quick-brief.md`); the canonical render reference is the Claude Design export the team already maintains. Stay consistent with it.

## 15. Success criteria for the new design

A redesign is better if a non-technical coordinator can, on a 13" laptop, in one calm scroll-free-ish view:
1. Instantly see **what arrived** (real values).
2. See, in plain language, **everything that needs fixing** in exactly one place, with the fix inline.
3. Understand **what the supplier will receive** and believe it ("this is exactly it").
4. Find **one obvious primary action** (Send) that's only enabled when it's truly safe.
5. Never encounter a technical term, a dead control, a silent guess, or a screen state with no explanation.
6. Feel **calm and in control**, not overwhelmed.

> Design principle to hand to Claude Design: **"Make a nervous procurement coordinator feel certain."** Calm, one path, plain words, real values, real preview, one safe Send.
