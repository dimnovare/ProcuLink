# ProcuLink — Order Workshop — Claude Design brief

> Paste everything below the line into Claude Design, and attach the 8 screenshots listed in §9.
> The screenshots are the visual source of truth for the current colours/spacing; this brief tells
> you what each thing is, what states it has, and what to improve.

---

## 0. The ask (read first)

Restyle and elevate **ONE screen** — the **Order Workshop** — to production-grade, beautiful,
trustworthy B2B SaaS quality. **Execute the existing locked visual system; do NOT invent a new
visual direction, new colour palette, or new vocabulary.** Improve hierarchy, density, rhythm,
affordance, and polish — keep the bones. Deliver high-fidelity mockups for the states in §8.

This is the single most important screen in the product (the "money-maker"): it's where a human
turns a received purchase order into the exact file a specific supplier requires, and sends it.

---

## 1. Product context

ProcuLink is a **B2B procurement order-conversion bridge.** A buyer/procurement team receives a
purchase order in *some* shape (CSV, XLSX, PDF, XML, cXML, UBL, X12) and must deliver it to a
**supplier in that supplier's exact required format and channel.** The Order Workshop is the one
screen where a person handles a single order end to end:

> **See what arrived → fix any blocking issues → map fields to the supplier's output → preview
> exactly what will be sent → send.**

## 2. Who you are designing for

A **procurement coordinator, NOT an integration engineer.** They think *"Supplier X wants a file
with THESE fields in THIS shape."* They are non-technical — no canonical paths, no code, no
version-control jargon — but they are **detail-obsessed and need to TRUST that what they see is
exactly what gets sent.** Information density is welcome; ambiguity is not. The emotional target is
**calm confidence**, not playful delight.

The north-star principle for every pixel:
> The user must always see **what arrived, what we changed, why, and exactly what will be sent.**

## 3. The locked visual system — polish WITHIN this

**Hard constraints (do not break):**
- **Palette is navy + violet** on cool light-grey neutrals, with **green reserved for "good / will
  send / mapped."** Pull exact colours from the attached screenshots. Confirmed values in use:
  - App background `#F6F7FA`; panels/cards `#FBFBFD` / `#FFFFFF`.
  - Ink (primary text) deep navy `#0B1A2F`; secondary `#56627A`; faint `#9AA3B2`.
  - Borders `#E2E6EE` / `#DCE0E8` / `#EEF0F4`.
  - **Violet = accent / AI / "edited"**: `#6F4FCE`, `#5E3DB0`, soft `#C4ABE8`.
  - **Green = success / mapped / send**: `#1E6D29`, `#2E8E3A`, fills `#EAF6EC` / `#F1F8F2`,
    border `#CDE7D1`.
  - **Amber = warning** `#9A6B00` on `#FFF7E6` / border `#F1E2BE`. **Red = blocking** (the issues banner).
  - Blue info note `#EEF3FB` / border `#D5E3F6`.
- **Monospace for data values + code/preview**: JetBrains Mono. UI text is the sans already in use.
- **Radii:** ~7–10px on cards/inputs, fully-rounded (999) pills/chips.
- **Motion:** subtle, **must respect `prefers-reduced-motion`** (a locked product rule). No bouncy
  decorative animation. Micro-transitions 150–250ms, transform/opacity only.
- **Keep the existing layout DNA**: left sidebar nav, top bar, three-zone working area, top-edge
  accent. This is "Direction 4 — The Bridge Layer" + "Direction 3 — System Identity."
- **Vocabulary is PLAIN.** Despite the internal "bridge" design name, user-facing copy uses ordinary
  procurement words — **Dashboard, Suppliers, Buyers, Deliveries, "send to supplier", "incoming
  order", "outgoing document."** Do **not** introduce metaphor words (bridge / dock / crossing /
  lane / spine) into visible copy.

**What you may freely improve:** spacing rhythm, type scale + weight hierarchy, the visual weight of
the primary CTA vs secondary actions, chip/row/badge styling, empty/loading/error states, the
density of the three columns, iconography (use a single consistent SVG set — Lucide-style; never
emoji), focus states, and the overall sense of "this is a precise, trustworthy tool."

## 4. Screen anatomy

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ TOP BAR: logo · breadcrumb (Inbox / Order …) ·         search · alerts · user  │
├──────────────────────────────────────────────────────────────────────────────┤
│ ORDER HEADER: «back   PO-PICKER-QA  ●Needs review                              │
│              Buyer not detected → Receiver JSON (test) · €23.10                 │
│                                   [ All | Mapping | Output ]   [ Fix 1 to send ]│   ← focus toggle + primary CTA
├──────────────────────────────────────────────────────────────────────────────┤
│ ZONE 1 — ISSUES ("FIX THESE TO SEND · 1 issue · 1 blocking")                   │
│   ▸ [BLOCKING] Needs a supplier code                          Where →          │
├──────────────────────────────────────────────────────────────────────────────┤
│ ZONE 2 — MAP THIS ORDER · 13 of 13 mapped   [Show connections][Design          │
│                                              structure][Enrich][Validate]      │
│ ┌─ INCOMING ORDER ──┐  ┌─ OUTGOING DOCUMENT ──┐  ┌─ LIVE PREVIEW · JSON ─────┐ │
│ │ 12 fields         │  │ + Add output field   │  │ What {supplier} receives  │ │
│ │ [All|Unmapped|    │  │ PoNumber              │  │ [CSV|JSON|XML|cXML|UBL|X12]│ │
│ │  Mapped|AI|Value] │  │  ← [Auto(1:1) ▾] ✕ ƒx │  │ Copy  Download            │ │
│ │ HEADER ▾          │  │  → PO-PICKER-QA       │  │ ──────────────────────────│ │
│ │  PO NUMBER  ⠿     │  │ OrderDate …           │  │ (rendered output, or      │ │
│ │  ORDER DATE ⠿     │  │ BuyerName …           │  │  honest warning/empty)    │ │
│ │ PARTIES ▾  …      │  │ …                     │  │                           │ │
│ │ LINE ITEMS ▾ …    │  │                       │  │                           │ │
│ └───────────────────┘  └──────────────────────┘  └───────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

The three working columns are **collapsible** via the `All | Mapping | Output` toggle (and each side
column can collapse to a thin labelled rail — see screenshots 6 & 7). "Collapse out of the way" is an
explicit founder requirement — make the collapsed rails feel intentional and elegant, not like a
broken column.

## 5. Component spec (the heart — restyle each, keep the behaviour)

### Zone 0 — Order header & primary CTA
| Element | What it is | States | Design ask |
|---|---|---|---|
| Title + status pill | `PO-PICKER-QA` + `●Needs review` | needs review / mapped / sent / failed | Make the status pill a clear, calm state chip; tie its colour to the state (review=amber, ready=green). |
| Sub-line | `Buyer not detected → Receiver JSON (test) · €23.10` | — | This is the "from → to · amount" summary. Make the → relationship legible; money tabular. |
| Focus toggle | `All / Mapping / Output` segmented control | one active | The collapse control. Make active segment obvious; this is how the user hides zones. |
| **Primary CTA** | `Fix 1 to send` (green) | enabled / disabled / "Send" when clean | The ONE primary action on the screen. When issues exist it nudges to fix; when clean it becomes "Send". Must be the visual anchor — exactly one primary button. |

### Zone 1 — Issues ("FIX THESE TO SEND")
| Element | What it is | States | Design ask |
|---|---|---|---|
| Header | `FIX THESE TO SEND · N issue · N blocking` | has-issues / all-clear | When all-clear, this should turn into a confident "Ready to send" state, not just disappear. |
| Issue row | `[BLOCKING] Needs a supplier code  Where →` | BLOCKING (red) / WARNING (amber) | Severity must read instantly. `Where →` jumps to the offending field — make it an obvious link/affordance. Plain-language only (already is). |

### Zone 2 header — "MAP THIS ORDER"
| Element | What it is | Design ask |
|---|---|---|
| `13 of 13 mapped` counter | output-coverage progress | A reassuring progress chip. Note it can read "all mapped" while Zone 1 still has 1 blocking issue (different axes: field-coverage vs line-resolution) — design so this is NOT confusing (e.g. subordinate it, or label it "fields mapped"). |
| Action buttons | `Show connections · Design structure · Enrich from catalog · Validate` | Secondary toolbar. Group/space them so they're clearly secondary to the primary CTA; consistent icon + label. |

### Zone 2 left — INCOMING ORDER pane
| Element | What it is | States | Design ask |
|---|---|---|---|
| Header | `INCOMING ORDER · 12 fields` | — | "What arrived, untouched." Communicate losslessness/trust. |
| Filter chips | `All 12 · Unmapped 4 · Mapped 8 · AI 0 · Has value 12` | one active | Faceted filters. "Unmapped" here = incoming fields not used in output — these are **preserved, not errors** — so don't make "Unmapped 4" look alarming. |
| Search | `Search incoming fields…` | — | Standard. |
| Group header | `HEADER · 3` / `PARTIES · 1` / `LINE ITEMS · 5` / `RAW EXTRAS · 3` ▾ | collapsed/expanded | Collapsible groups. |
| Field row | label (e.g. `PO NUMBER`) + value (`PO-PICKER-QA`) + drag handle `⠿` | default / hover / dragging | The drag handle lets a power user drag a field onto an output row (optional path). Value in mono. Make rows scannable + the handle discoverable-but-quiet. |

### Zone 2 middle — OUTGOING DOCUMENT pane + the **inline source picker** (the headline interaction)
This is the core mechanic the founder chose: **mapping is done by an inline searchable dropdown per
output row, not by dragging wires.** Each output row reads: **`FieldName  ←[ source chip ▾ ]  ✕  ƒx  → value`.**

| Element | What it is | States | Design ask |
|---|---|---|---|
| Header | `OUTGOING DOCUMENT` + `+ Add output field` | — | "What we will send." `+ Add output field` adds a row. |
| Output row | output field name + role caption + source chip + clear + transform + resolved value | default / focused / blocking | The row is busy (5 controls). **Make it calm and scannable** — clear visual order: name → source → value. The `→ value` is what will actually be sent. |
| **Source chip** | `←[ Auto (1:1) ▾ ]` (unsourced = dashed grey "pick a field"; sourced = solid green w/ field name; fixed = violet) | unsourced / sourced / fixed / readonly | THE control. Dashed `#C9D0DC` "pick a field" when empty; solid green `#CDE7D1`/`#F1F8F2`/`#1E6D29` when sourced. Make the empty state an obvious "click me to map." |
| **Picker dropdown** (screenshot 2) | opens on chip click: search + grouped incoming fields (each shows **label + actual value**) + AI suggestion floated to top with `✦ AI NN%` violet badge + `= Fixed value…` + `Clear` footer | open / typing / no-match / keyboard-nav | Panel `#FFFFFF`, border `#E2E6EE`, radius 10, soft shadow, width ~280. **This is where the magic happens** — make it feel fast, smart (AI-first), and effortless: the value preview under each field is what makes it trustworthy. Polish the AI badge, group labels, active-row highlight `#F1F8F2`. |
| Clear `✕` | resets the row's source to Auto | — | Quiet. |
| Transform `ƒx` (screenshot 4) | opens a small "TRANSFORMS" popover: list of applied transforms + `+ Add transform…` (Trim/Replace/Date/Number/Currency/…) + `Done` | none / has-transforms | The power-user value formatter. Keep it discoverable-but-secondary. |
| Resolved value `→ …` | the actual value that will be sent for this field | present / empty `—` / needs-review | Mono. This is the trust payoff per row. |

### Zone 2 right — LIVE PREVIEW pane
| Element | What it is | States | Design ask |
|---|---|---|---|
| Header | `Live preview · JSON` + `What {supplier} receives` + `edited {field}` | — | "Exactly what will be sent." Make this pane feel authoritative — it's the trust anchor. |
| Format toggle | `CSV · JSON · XML · cXML · UBL · X12` pills | one active (defaults to the supplier's real format) | Active pill green `#EAF6EC`/`#2E8E3A`. Picking a non-delivered format is "explore" mode (shows the blue info note "not the delivered format"). |
| Copy / Download | export the preview | enabled / disabled | Secondary. |
| Info note (blue) | "Preview as X · not the delivered format (the supplier receives Y)" | shown in explore mode | Calm, informative — not alarming. |
| Warning/error note (amber) | e.g. "Cannot transform: lines N still need review." | when blocked | Honest blocking message. |
| Output body | rendered file, with the last-edited line highlighted; or `(no preview)` when an issue blocks it | content / highlighted / empty | Mono code block. The `(no preview)` empty state currently looks bare — design a proper "preview will appear once issues are fixed" empty state. |

### Output Structure Designer (screenshot 5 — overlay reached via "Design structure")
A full modal: **`Design the output structure — What the supplier receives · live preview on the
right`**, a `Format JSON/XML/CSV` switch, a **structure tree** (group → value/list/object/@attr,
each row binds to a field or fixed value, a format dropdown, and an `only include when` conditional),
a **`Paste a supplier sample to start` → `Infer structure`** box, and `Cancel` / `Save structure`.
Ask: make the tree readable and editable without feeling like a code editor; the "paste a sample,
we infer it" path should feel like the easy on-ramp.

## 6. States, responsive, accessibility

- **Loading:** skeletons for the three columns + preview (no blocking spinners).
- **Empty / all-clear:** issues→"ready to send"; preview empty→"appears once mapped/fixed."
- **Error / not-found:** honest, recoverable.
- **Mobile / narrow:** the founder explicitly wants a **reduced** mobile experience — design a sane
  stacked layout (issues → outgoing+preview primary; incoming behind a toggle). It does **not** need
  full drag/power-user parity on mobile — clarity over completeness. *(This is currently TODO in
  code — your mock defines the target.)*
- **A11y:** visible focus rings, 44px tap targets, keyboard support for the picker (Esc/↑/↓/Enter
  already implemented), AA contrast, `prefers-reduced-motion` honoured.

## 7. Do / Don't

**Do:** one primary CTA; progressive disclosure (power features quiet but reachable); show real
values everywhere (trust); calm density; tabular money/numbers; a single consistent SVG icon set;
make "what will be sent" the emotional centre.

**Don't:** invent a new colour direction or theme; add decorative sparkle/illustration that doesn't
carry meaning; use emoji as icons; introduce metaphor vocabulary (bridge/dock/lane/spine) into
visible copy; add a "novice/expert mode" toggle; hide the standards/format or the resolved values;
animate width/height or ignore reduced-motion.

## 8. Deliverables (mockups requested)

1. **Order Workshop — default** (all three zones, an order with one blocking issue) — *ref shot 1.*
2. **Source picker open** (the dropdown over an output row) — *ref shot 2.*
3. **Live preview pane** populated with a clean rendered output (no blocking issue) + the explore-format info note.
4. **Output Structure Designer** modal — *ref shot 5.*
5. **Transform popover** — *ref shot 4.*
6. **Collapsed/focus modes** ("Output" and "Mapping") — *ref shots 6 & 7.*
7. **Mobile (reduced)** — your proposed stacked layout.
8. **All-clear / ready-to-send** state (no issues, preview populated, CTA = "Send").

## 9. Screenshot manifest (attach these — current live screens)

| # | Screenshot | Shows |
|---|---|---|
| 1 | Order Workshop, default `All` view | full screen: header + CTA, Issues zone, the three columns, picker chips, preview-blocked state |
| 2 | Source picker **open** | the inline mapping dropdown: search, grouped incoming fields with values, AI-first suggestion, Fixed value / Clear footer |
| 3 | Transform `ƒx` popover | the per-row value transform list + "Add transform…" |
| 4 | Output Structure Designer overlay | structure tree, paste-sample→infer, "only include when" conditional, live preview, Save/Cancel |
| 5 | "Show connections" / wire mode | the optional drag-wire overlay (mapping's escape hatch) |
| 6 | `Output` focus mode | incoming pane collapsed to a labelled rail; outgoing + preview shown |
| 7 | `Mapping` focus mode | both side panes collapsed to rails; outgoing document full-width |

> To capture more: open any order in the app at `…/inbox/{id}?workshop=1` (the workshop is currently
> behind that flag) and screenshot the states above.
