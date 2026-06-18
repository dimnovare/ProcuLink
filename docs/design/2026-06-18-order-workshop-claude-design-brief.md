# ProcuLink — "Order Workshop" Design Brief for Claude Design

> **Goal:** Make the Order Workshop screen *beautiful, dense, and trustworthy* — **polishing within the locked Bridge Layer system below, not inventing a new visual direction.** Every hex, font, radius, and constraint in this brief is read from the live codebase and design tokens. Match them.
>
> **How to use:** paste everything below into Claude Design and attach the screenshots in §8. The screenshots are the visual source of truth for current spacing/colour; this brief tells you what each thing is, its states, and what to improve. The live screen lives behind a flag — open any order at `…/inbox/{id}?workshop=1` to capture more.
>
> **Note on copy you'll see in the screenshots:** the left pane's eyebrow currently reads **"INCOMING ORDER"** and the centre pane **"OUTGOING DOCUMENT"** (this brief also calls them "Received" / "Outgoing" conceptually — same things).

---

## 1. CONTEXT

**What ProcuLink is.** A B2B outbound procurement bridge. Buyer/procurement teams import a purchase order from their own system, and ProcuLink converts and delivers it to each supplier *in that supplier's exact required format and channel*. The core loop is: **Parse → Normalize → Validate → Review exceptions → Transform → Deliver → Learn.**

**Who the user is.** A **procurement coordinator**, *not* an integration engineer. She thinks: *"Supplier X needs a file with THESE fields, in THIS shape, sent THIS way."* She does not think in JSON Schemas, XPath, or EDI segment IDs. She wants to know: did the order arrive correctly, what's stopping it from being sent, and can I trust what will actually land at the supplier. The UI must surface power (standards mappings, raw output, transforms) **on demand**, but lead with plain language and her outcome.

**What the Order Workshop screen does.** It is the money-making screen — the single, unified order-review surface. One screen, one flow:

> **See what arrived → Fix the issues blocking send → Map incoming fields to the supplier's output → Preview *exactly* what will be sent → Send.**

It replaces a previous multi-step review. Everything happens here without leaving the screen. The screen is desktop-first (the field-mapping canvas needs width); mobile is a reduced "review issues + send" fallback.

**Honesty is a product value.** Never imply an order was *sent* just because a file was *generated*. The states are explicit: `ready_to_deliver` → `delivering` → `delivered` / `delivery_failed`. The preview must be byte-faithful to what the supplier receives.

---

## 2. THE LOCKED VISUAL SYSTEM — polish WITHIN this, do not reinvent

This is **Direction 4 "The Bridge Layer"** + **Direction 3 "System Identity"**. It is locked. Your job is to make it more refined, more legible, and more confident — not to introduce a new aesthetic.

### Hard constraints (non-negotiable)
- **Visual canon stands:** navy app chrome, light work area, blue (buyer / left) + green (supplier / right) edge rails, link-spine gradient lines, cross-section 3px card edges, the five-stage journey (Parse·Normalize·Validate·Transform·Deliver), reduced-motion respect.
- **User-facing vocabulary is PLAIN.** Use *Dashboard / Suppliers / Buyers / Deliveries / Received / Output / Send to supplier*. **Do NOT** use the internal words *bridge / dock / crossing / lane / spine / wire* in any visible label. (Those live in code/docs only.)
- **AI violet is reserved for AI** — suggestions, confidence, transforms. **Never** decorative.
- **No** generic SaaS gradients, sparkle icons, glassmorphism, decorative blobs, drop-shadow heroes, mode toggles, or two-mode "expert" branches.
- **One great experience:** smart defaults + progressive disclosure + Command Palette. Density is achieved through clarity, not clutter.

### Color tokens (exact)

| Role | Hex | Use |
|---|---|---|
| Navy chrome / ink | `#0B1A2F` | Sidebar, topbar, primary text, active segmented control |
| Buyer blue | `#1E66C9` / deep `#0F4FAB` | Buyer side, left rail, "Received" accents, links, focus ring |
| Supplier green | `#2E8E3A` / deep `#1E6D29` | Supplier side, right rail, "Output", **primary Send CTA** |
| AI violet | `#6F4FCE` / text `#5E3DB0` | AI suggestions, confidence chips, transforms — **AI only** |
| Danger (blocking) | text `#C53A3A` · bg `#FBE3E3` | Blocking issues |
| Warning | text `#C97A14` · bg `#FAEFD6` · alt `#9A6B00` on `#FFF7E6` | Warnings, "needs a source" |
| Success bar | text `#1E6D29` · bg `#E2F1E2` · border `#BFE0C2` | Ready-to-send |
| Info | text `#0F4FAB` · bg `#EFF4FB`/`#EEF3FB` · border `#D5E3F6` | Exploratory preview notice |
| Page bg | `#F6F7FA` | Work area |
| Card bg | `#FFFFFF`; pane bg `#FBFBFD` | Surfaces |
| Muted text | `#56627A` · faint `#5B6980`/`#7A8591` · disabled `#AEB6C4` | Secondary copy |
| Borders | `#E2E6EE` (card edge) · `#EEF0F4` (section) · `#F5F6F9` (row divider) · `#DCE0E8` (controls) | Lines |
| Source-pointer badge | fg `#1E66C9` · bg `#E3EDFB` | Cell-ref / provenance chips |

### Typography (exact)
- **Bricolage Grotesque** — display only (PO title 22px / 700 / -0.02em, KPI numbers).
- **Inter** — all UI/body.
- **JetBrains Mono** — every data value, output preview, currency, source paths.
- Type ramp in use: 22 (PO title) · 13 (labels/buttons) · 12.5 (body) · 11.5 (small) · 11 (smallest) · 10.5 (tiny) · 9–10 (badges/chips). Section eyebrows are 11px / 700 / UPPERCASE / 0.06em.

### Radii & spacing (locked scale)
- Radius: `sm 4px` (chips/badges) · `6px` (buttons/controls) · `7px` (back button/inputs) · `md 8px` (rows/cards) · `lg 10px` (panels) · `xl 12px` (panes/modal) · `999px` (pills).
- Spacing base 4px; use 4·8·12·16·20·24·32·40·48·64.
- **Motion:** the six locked patterns only (link-spine-fill, wire-pulse, node-pulse, connector-draw, validate-to-deliver-flush, empty-state-link-close). All transitions ~120–150ms ease. Respect `prefers-reduced-motion`.

---

## 3. SCREEN ANATOMY — three zones

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ HEADER BAR  (white, 1px #E2E6EE bottom)                                        │
│ [←] PO-DEMO-2026-001 [● Pending review]   Buyer ▸ Supplier · €4,210.00         │
│                                  [ All | Mapping | Output ]   [ Send to supplier ]│
├──────────────────────────────────────────────────────────────────────────────┤
│ FLOW NOTICE (conditional: error/success/info strip — Generating…/Sending…/err) │
├──────────────────────────────────────────────────────────────────────────────┤
│ ISSUES PANEL  "Fix these to send · 2 issues · 1 blocking"                       │
│   ▌ BLOCKING  Supplier item code missing on line 3   [Accept] [Where →]         │
│   ▌ WARNING   Delivery date assumed from header      [Where →]                  │
│   …OR…  ✓ Ready to send — every blocker is cleared.                             │
├──────────────────────────────────────────────────────────────────────────────┤
│  MAPPER CANVAS (desktop xl+, min 1120px — one scrolling unit)                   │
│ ┌─────────────┐  ┌────┐  ┌─────────────┐   ┌──────────────────────────┐         │
│ │ RECEIVED    │  │ 64 │  │ OUTGOING    │   │ LIVE PREVIEW (docked)    │         │
│ │ (incoming)  │  │ px │  │ (supplier   │   │ "What {supplier}         │         │
│ │ left pane   │  │ gut│  │  output)    │   │  receives" · JSON ▸       │         │
│ │ blue accent │  │ ter│  │ green accent│   │ navy-or-light mono <pre>  │         │
│ │  ▸ port     │  │    │  │  port ◀     │   │ [Copy] [Download]         │         │
│ └─────────────┘  └────┘  └─────────────┘   └──────────────────────────┘         │
│   ↕ collapses to 40px rail   center 1fr      ↕ collapses to 40px rail           │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Zone A — Received (left, eyebrow "INCOMING ORDER"):** lossless incoming fields, grouped Header / Parties / Line items / Raw extras, blue (`#1E66C9`) left accent. Collapses to a 40px vertical rail.
- **Zone B — Outgoing (center, 1fr, eyebrow "OUTGOING DOCUMENT"):** the supplier's output document. Each row = one output field with an inline **source picker** ("pick a field"), `= value`, and `ƒx` transform controls. Green (`#2E8E3A`) accents. AI-/auto-mapped rows collapse behind a violet "N mapped by AI · review" chip so only fields *needing the user* show first.
- **Zone C — Live Preview (docked right):** exactly what will be delivered, in the supplier's real format, monospace, with format toggle pills + Copy/Download. Also collapses to a 40px green rail.

The **Focus control (All / Mapping / Output)** in the header drives which zones are expanded vs. railed (progressive disclosure). State persists in sessionStorage.

---

## 4. PER-COMPONENT SPEC — the heart of the brief

### ZONE: Header bar + global controls

| Component | What it is | States | Current treatment | Design ask |
|---|---|---|---|---|
| **Header bar** | Top info-density band | default | white, 1px `#E2E6EE` bottom, `px-4 lg:px-6 pt-3.5 pb-3.5`, flex-wrap | Establish a clear **scan order**: identity (left) → focus (center-right) → action (far right). Group identity tightly; let the Send CTA own the right edge. |
| **Back button** | Icon-only ← | default, hover | 30×30, r7, 1px `#E2E6EE`, `#56627A` arrow on white | Give a real hover (border `#1E66C9`/subtle bg) + focus-visible ring. Affordance currently flat. |
| **PO title** | Order number | default | Bricolage 22px / 700 / -0.02em / `#0B1A2F` | Anchor of the screen. Balance weight against the status badge beside it; keep it the clear h1. |
| **Status badge** | UnifiedStatusBadge | pending_review, delivered, other | size md, inline right of title | Confirm badge color reads correctly at each state; align baseline with the 22px title. |
| **Buyer ▸ Supplier subheader** | Who → who + total | default | 13px; buyer `#0F4FAB` bold · arrow `#C6CDDA` · supplier `#1E6D29` bold · total `#566982` JetBrains Mono | Reinforce the buyer-blue / supplier-green semantic. Make the grand total feel authoritative (mono, slightly heavier). Direction-agnostic labels — don't hardcode "Supplier". |
| **Focus control** | All / Mapping / Output segmented | one active | r7, 1px `#E2E6EE`; active `#0B1A2F` bg / white / 700 / 11.5px; inactive white / `#56627A` / 500 | Make the "this is a view switch" intent obvious. Add hover + focus-visible + `aria-current`. Active state is currently background-only. |
| **Send button** | Primary CTA | enabled, disabled, generating, sending, "Fix N to send" | 36px, `0 18px`, r8, 13px / 700; enabled `#2E8E3A` / disabled `#96C69C`, white text | The single hero action. Make enabled green feel decisive; make disabled clearly *blocked-not-broken*. Add a spinner on Generating…/Sending…. When disabled show **why** (tooltip listing blocking issues). |
| **Flow notice** | Transient status strip | error / success / info | `8px 16px`, 12.5px / 600; error `#FBE3E3`/`#C53A3A` · success `#E2F1E2`/`#1E6D29` · info `#EFF4FB`/`#0F4FAB`, 1px `#EEF0F4` bottom | Add a leading icon per tone and a subtle slide-down entrance (reduced-motion: instant). Keep it calm, not a toast. |

### ZONE: Issues Panel ("Fix these to send")

| Component | What it is | States | Current treatment | Design ask |
|---|---|---|---|---|
| **Card container** | Wraps the issue list | with-issues | r10, white, 1px `#E2E6EE` | Make this read as the *task list that gates Send*. Tie it visually to the Send button (shared severity language). |
| **Header** | "Fix these to send" + counts | default | `10px 12px`, 1px `#EEF0F4` bottom; label 11px / 700 / UPPERCASE / 0.06em / `#0B1A2F`; count 11px `#5B6980` | Strengthen the count summary ("2 issues · 1 blocking") as a separate, scannable cluster. |
| **Issue row — blocking** | One blocker | blocking | left border 3px `#C53A3A`, white bg, `10px 12px`, gap 10; title 12.5px / 600 `#0B1A2F`; why 11.5px `#56627A` | Blocking must feel *heavier* than warning — consider a faint row tint (`#FFFCFC`) or 4px accent, plus a leading ⊘/alert glyph (not color-only). |
| **Issue row — warning** | One warning | warning | left border 3px `#C97A14`, else identical | Keep clearly subordinate to blocking. Amber glyph, lower visual weight. |
| **Severity chip** | BLOCKING / WARNING badge | blocking, warning | 9px / 800 / UPPERCASE / 0.03em, r4; blocking `#FBE3E3`/`#C53A3A`, warning `#FAEFD6`/`#C97A14` | Add a tiny icon inside the chip. Ensure legibility at 9px (consider 9.5–10px). |
| **Accept (fix) button** | One-click deterministic fix | default, (needs hover/active) | 26px, `0 10px`, r6, 11px / 700, `#2E8E3A`, white | **Add hover (darker `#1E6E1F`) + active + focus-visible.** Only renders when a fix exists. Consider a confirm/undo affordance since it fires immediately. |
| **Where → button** | Jump to field in mapper | default, (needs hover) | 26px, `0 9px`, r6, 11px / 600, 1px `#DCE0E8`, white, `#345470` | Add hover (border deepen + bg tint) + focus-visible. It scroll-highlights the field — design the **landing highlight** too (ring/underline, not just scroll). |
| **Ready-to-send bar** | 0-issues success state | ready | flex, r10, `#E2F1E2` bg, 1px `#BFE0C2`, `#1E6D29`; ✓ 14px / 800; "Ready to send" 13px / 700; subtext 11.5px `#2E7D38` | This is the *reward moment*. Make it feel earned (a single calm fade-in / link-close mark, reduced-motion safe). Replace the Unicode ✓ with a real icon. Copy: "Every blocker is cleared — ready to send to {supplier}." |

### ZONE: Received pane (incoming, eyebrow "INCOMING ORDER")

| Component | What it is | States | Current treatment | Design ask |
|---|---|---|---|---|
| **Pane frame** | Left container | default | r12, 1px `#E2E6EE`, bg `#FBFBFD`, 3px `#1E66C9` left accent | Strengthen the buyer-blue identity of this pane so left=incoming reads instantly. |
| **Header** | "INCOMING ORDER · N fields" | default | `9px 12px`, 1px `#EEF0F4` bottom; eyebrow 10.5px / 700 / UPPERCASE / 0.07em `#5E3DB0` (violet today — reconsider) + faint count | Eyebrow currently violet — but this is **not AI**. Move it to ink/blue so violet stays AI-reserved. Subordinate the count (smaller/fainter than the label). |
| **Search input** | Filter incoming fields | default, focus, with-text | 100%, `6px 9px`, r7, 1px `#DCE0E8`, 11.5px | Add a visible focus ring (2px `#1E66C9` offset) + a search glyph. Currently no focus feedback. |
| **Filter chips** | All / Unmapped / Mapped / AI / Has value (+counts) | inactive, active, hover | pill r999, `3px 9px`, 10px / 700; active 1px `#6F4FCE` / `#EEE7FB` / `#5E3DB0` | Active chips use violet — fine ONLY for the "AI" chip; for the others prefer a neutral/blue active so violet stays AI-only. Make counts a clearer secondary tier. "Unmapped" here = incoming fields not used in output — these are **preserved, not errors** — so don't make the count look alarming. |
| **Group header** | Header / Parties / Line items / Raw extras + ▾ | expanded, collapsed, forceOpen | 10.5px / 700 / UPPERCASE / 0.05em faint; chevron 9px rotates -90° | Give the collapse a real hover and a clearer chevron (Lucide). Disable affordance when searching (forceOpen). |
| **Incoming row** | One source field | default, hover, wired, connecting, suggested | r8, 1px border + 3px left accent; wired left `#6F4FCE` / border `#D9CEF2`; hover `#C4ABE8`/`#F7F4FD`; connecting fully `#6F4FCE`/`#EEE7FB` | Five interdependent states are visually noisy — **consolidate to ~3 legible tiers** (resting / hovered / wired-or-connecting). Keep wired = violet-accented (it's a mapping). Make the connecting (drag) state unmistakably the strongest (add box-shadow). |
| **Field label** | Source name | default, wired, unmapped | 10px / 700 / UPPERCASE; wired `#5E3DB0`, else faint | 10px small-caps is hard to scan — consider 10.5–11px. Don't let ellipsis hide meaningful field names; allow a tooltip. |
| **Field value (mono)** | Actual incoming data | with-value, empty | JetBrains Mono 11.5px `#0B1A2F`; "(empty)" `#C6CDDA` | This is the trust signal — the user sees real data. Keep mono, ensure contrast. Make "(empty)" visually distinct (italic + subtle icon). |
| **Source pointer badge** | Cell ref / provenance (e.g. "B2") | default | 9.5px mono, `#1E66C9` on `#E3EDFB`, r4, max-w120 | Lovely trust detail — keep it. Add a hover tooltip with the full path; consider a tiny header/line glyph. |
| **AI badge (✦ AI)** | AI suggestion present | shown, hidden-when-wired | 8.5px / 800 `#6F4FCE` | At 8.5px it's nearly invisible. Bump to ~9.5px and pair with the violet system; keep it the *only* violet on a non-wired row. |
| **Drag handle / port** | Right-edge wire port | default, hover, wired, connecting, grabbing | 22px circle, 1.5px `#C9D0DC`, `⠿`→`→` on drag; connecting adds `0 0 0 3px rgba(111,79,206,.18)` | `⠿` braille is cryptic — use a clear grip icon. 22px is below the 44px touch target — keep visual size but enlarge hit area. On row-hover the icon color changes but the border doesn't — unify. |
| **Empty / honest states** | No fields / no results / extraction fallback | several | 11–11.5px faint, multi-line copy | These carry honesty copy (extraction fell back to deterministic parser; already-structured order has no raw extras). Give them a calm icon + tighter typographic rhythm so they don't read as errors. |

### ZONE: Outgoing pane + Source Picker (the mapping money zone, eyebrow "OUTGOING DOCUMENT")

| Component | What it is | States | Current treatment | Design ask |
|---|---|---|---|---|
| **Pane header** | "OUTGOING DOCUMENT" | default | r12, bg `#FBFBFD`, eyebrow 10.5px / 700 / UPPERCASE / 0.07em `#1E6D29` green | Make "this is the supplier's document" unmistakable; green identity mirrors the blue Received pane. |
| **Add output field button** | Add a new output field | default, hover | dashed 1px `#A9D3AF`, bg `#F4FBF5`, `#1E6D29`, r7, 10.5px / 700, "+" | Dashed = additive is right. Give it a hover fill + focus ring. Keep it secondary to row controls. |
| **Outgoing row** | One output field | default, hover, snapped, mapped, unmapped-required | r8, 1px + 3px right accent; snapped `#F4EFFC`/`#6F4FCE`+shadow; required-unmapped `#FFFCF4`/`#F1E2BE`/right `#E0B23C`; mapped border `#D7E7DA`/right `#2E8E3A` | **Consolidate the 5+ border colors** into a coherent 3-tier scale (resting / mapped-green / needs-source-amber), with snap (violet) as the transient drag state. Keep the right-edge accent idiom — it's the cross-section signature. |
| **Left drop port** | Wire target circle | default, hover, snapped, mapped | 12px circle, left -6px, 2.5px border; mapped `#2E8E3A`, snapped `#6F4FCE`+shadow | Semi-hidden at -6px is intentional for density, but **improve discoverability** on hover/drag (grow + glow). Ensure it never gets clipped. |
| **Field name** | Output field name (editable) | default, renaming | 11.5px / 700 `#0B1A2F`; sub-label 9.5px faint | Clear primary/secondary hierarchy; 9.5px sub-label is borderline — nudge up. Inline rename should show a clear edit affordance. |
| **SourcePickerChip** | Inline "pick a field" dropdown trigger | unsourced, sourced, open | pill r999, 22px, 9.5px / 700, max-w150; unsourced dashed `#C9D0DC`/white/`#56627A` "pick a field"; sourced solid `#CDE7D1`/`#F1F8F2`/`#1E6D29` (shows source label) | **The primary mapping interaction in this screen (picker mode).** Make unsourced clearly *invitational* (the dashed `#C9D0DC` is too faint — strengthen, add a small "+source" cue). Sourced should feel resolved/calm. This is the verb the coordinator performs all day — make it delightful. |
| **Clear (✕) on chip** | Remove source | visible, hidden | text ✕, faint | Use a real icon; reveal on hover/focus; add aria. |
| **OutgoingStatusTag** | wired / fixed / auto / needs-source / not-set | five | wired green `#1E6D29`; fixed violet on `#F4EFFC`/`#E2D6F6` (+ ✎); auto gray 8.5px UPPERCASE; needs-source amber `#9A6B00`/`#FFF7E6`; not-set faint | Five colored states is palette-heavy. **Reduce to 3 primary** (mapped / fixed / needs-attention) + muted auto. Keep amber = required-blocked. Keep fixed = violet (it's a transform-adjacent power feature). |
| **RowChipButton (`= value`, `ƒx · N`)** | Inline fixed-value / transform controls | resting, lit, disabled | 20px, r6, 9.5px / 700; resting opacity 0.55 → lit 1.0; lit border `#C4ABE8` / bg `#F4EFFC` / `#5E3DB0` | Opacity-ramp keeps the row calm but hides power. Find the balance: discoverable at rest (subtle persistent outline) yet not noisy. Disabled state must read clearly "unavailable here" (with reason tooltip). |
| **Value preview (→ mono)** | What this field will output | mapped | mono 11px `#0B1A2F`, arrow `#9AA3B2`, "—" placeholder | Keep it — it's the per-field trust echo. Ensure WCAG AA at 11px mono. |
| **SourcePicker dropdown** | Portal listbox of incoming fields | open | 280px, white, r10, shadow `0 12px 30px rgba(11,26,47,.16)`; search + grouped options + footer | Fixed 280px truncates long labels — design graceful truncation + tooltip, or allow a wider panel. Group headers (Header/Parties/Line items/Raw extras) at 9px are tiny — keep scannable. |
| **Dropdown option** | One incoming field | default, active, suggested | `5px 6px`, r6; active `#F1F8F2`; label 11px / 600; value 9.5px mono faint; AI badge "✦ AI 92%" 8px / 800 `#6F4FCE` | Show the **value** alongside the label (it already does — keep, it's why this beats a bare picker). Make the AI-confidence badge a proper confidence chip, not styled text. |
| **AddOutputFieldMenu** | Add canonical or custom field | open | 280px panel; scope toggle Header/Line (active `#0B1A2F`); green "Add custom field" CTA | Scope toggle's dark-navy active can clash near the violet/green — soften or keep but tighten. Surface canonical-vs-custom distinction clearly. Standards ref ("Maps to {standardsRef}") should be quietly visible — **never hide standards.** |

### ZONE: Live Preview pane

| Component | What it is | States | Current treatment | Design ask |
|---|---|---|---|---|
| **Outer container** | Docked preview | default | 1px `#E2E6EE`, r10, bg `#FBFBFD` | Frame it as a *companion, not hero* column. Keep it visually quieter than the mapper. |
| **Header eyebrow** | "Live preview · {FORMAT}" + "What {supplier} receives" | default | 11px / 700 / UPPERCASE / 0.06em `#1E6D29`; subtitle 10px faint, ellipsis | Green = supplier output is correct. Allow full supplier name on hover (tooltip). |
| **Last-touched indicator** | "edited {field}" | shown | 10px `#5E3DB0` violet | Ties to the violet highlight in the body — good. Add a tiny pencil glyph. |
| **Format toggle pills** | CSV / JSON / XML / cXML / UBL / X12 | inactive, active | pill r999, `2px 8px`, 10px / 700; active 1px `#2E8E3A` / `#EAF6EC` / `#1E6D29` | Six pills crowd the header. Consider wrapping or a "more formats" overflow on narrow widths. Active = supplier green. Make the **delivered** format visually distinct from an *exploratory* one. (Defaults to the supplier's real delivered format.) |
| **Copy / Download** | Export actions | default, hover, disabled, copied | `2px 8px`, r6, 1px `#DCE0E8`, 10px / 700, `#345470` / disabled `#AEB6C4`; "Copied" flashes 1.4s | Differentiate the two slightly (icons). Add hover + focus-visible. Make "Copied" confirmation clearer. |
| **Info note (blue)** | Exploratory-format explainer | shown, hidden | `8px 12px`, 11px `#56627A`, bg `#EEF3FB`, 1px `#D5E3F6` bottom | Add an info-circle glyph. This is the honesty guardrail ("Preview as X · **not** the delivered format"). Keep copy crisp. |
| **Error note (amber)** | Render/validation failure | shown, hidden | `8px 12px`, 11px `#9A6B00`, bg `#FFF7E6`, 1px `#F1E2BE` | Constrain long backend errors (max-height + scroll or summary). Amber, not red — it's a fix-this, not a crash. |
| **Output `<pre>`** | The rendered document | rendering, done, empty | `12px 14px`, max-h 300, JetBrains Mono 12px / 1.55 `#0B1A2F`; busy opacity 0.55; "Rendering…" / "(no preview)" | This is the *proof*. Keep it monospace, faithful, scrollable. The busy fade + 460ms flash on update is good — keep reduced-motion safe. Style the scrollbar to match. Design a proper empty state (see below) instead of bare "(no preview)". |
| **Last-touched `<mark>`** | Highlights the edited output line | highlighted, absent | bg `#EEE7FB`, `#5E3DB0`, r3 | Excellent live-feedback. Keep the violet tie to "edited {field}". Ensure the highlight is obvious but not garish. |
| **Empty state** | No sample / blocked preview | shown | `16px 12px`, 11px faint | Add a calm icon. Copy: "The preview appears once the blocking issues are fixed." (`(no preview)` correctly shows when a line still needs a supplier code — it's honest, not broken.) |

### ZONE: Output Structure Designer (overlay) + Transform Popover

| Component | What it is | States | Current treatment | Design ask |
|---|---|---|---|---|
| **Designer modal** | Full design of the output tree | open | overlay `rgba(8,16,28,0.55)`; white container r12 max-1100px, shadow `0 24px 64px rgba(8,16,28,0.4)`; **3px gradient edge `#2D6BD4`→`#1E6D29`** (buyer→supplier) | This is the *power* surface. Keep the signature gradient edge. Two-pane: tree editor (light) \| live preview (dark navy `#0B1626`). Make it feel like a calm, confident pro tool — not a dev console. |
| **Modal header** | Title + format + close | default | navy `#0B1A2F`, white, 14px title / 11.5px subtitle 80%; close 30×30 `rgba(255,255,255,.15)` | Title hierarchy clear; give the bare format `<select>` a visible border/focus; give close a hover. Copy: "Design the output structure" / "What the supplier receives — live preview on the right." |
| **Paste-sample accordion** | Infer structure from a pasted sample | collapsed, expanded | dashed `#C6CDDA`, bg `#F7F9FC`, ▾/▸; mono textarea; navy "Infer structure" | Make "paste a sample → we build it" feel magical and obvious — it's the fastest path. Add success feedback after infer. |
| **NodeEditor row** | One node (object/array/field/attr) | default | type badge `#EEF1F6`/`#56627A` r4; name input; source dropdown; format preset; "only include when" | Differentiate the four node types visually (icon/color per `{ }` / `[ ]` / value / `@attr`). Keep it scannable as a tree (indentation, connectors). |
| **Format preset dropdown** | Date/Number/Currency presets | empty, with-preset | 30px, r6, 1px `#C6CDDA`; `#56627A`→`#0B1A2F` when set | Color-shift-on-set is good. Make presets feel like safe, named choices ("Date · 2026-06-15", "Currency · €1.234,50"). |
| **Only-include-when input** | Conditional field/line | empty, with-condition | 26px, mono, border blue `#2D6BD4` when set | Blue border = conditional-logic-active is a nice signal — keep. Hint copy: "always — e.g. line.Quantity > 0". This is power-user; keep it quiet until used. |
| **Add-child buttons** | + value / object / list / @attr | default | dashed `#C6CDDA`, bg `#F7F9FC`, `#3A4A60`, 26px | Additive dashed idiom. Add hover. |
| **Live preview (dark)** | Real output, live | default, loading, error | bg `#0B1626`, mono `#D7E2F2` 12px; error `#FF9B9B` | Reversed dark panel for the "what they receive" proof — keep. Add a subtle loading shimmer (reduced-motion safe) instead of text-only "· updating…". |
| **Save / Cancel** | Commit / dismiss | default, saving, saved | Save 34px r7 `#1E6D29` green "Save structure"/"Saving…"/"✓ Saved"; Cancel white 1px `#C6CDDA` | Green primary. "✓ Saved" needs a graceful fade/dismiss. Add hover/focus to both. |
| **TransformPopover** | The `ƒx` chain editor | open | 300px, white, r10, shadow `0 10px 28px rgba(11,26,47,.18)`; **violet system** | This is AI/power = violet, correctly. "TRANSFORMS" 10px / 800 / `#5E3DB0`; rows on lavender `#FBF9FE`/`#E2D6F6`. Make the chain read as ordered steps (1→2→3). Help copy: "Applied in order to the resolved value before delivery. Changes save + preview live." |
| **ManipRow** | One transform (Trim/Replace/DateFormat/…) | default | lavender pill, type label `#5E3DB0` minWidth 64, mono param inputs 70px, ✕ remove | Show params inline clearly; tiny mono inputs need focus rings. Remove ✕ should be discoverable on hover. |
| **Add-transform dropdown** | Pick a manipulator | default | dashed `#C6CDDA`, bg `#F6F7FA`, lists 8 types with hints | Keep the helpful descriptions ("Replace — Replace text", "Fallback — Use a default when empty"). Dashed = additive. |

---

## 5. STATES & RESPONSIVE

- **Loading:** a three-column skeleton of pulsing bones (`#EDF0F5`). **Ask:** make the skeleton reflect the *actual* current layout (collapsed panes → narrow rails, not three equal columns), define an explicit pulse duration, and disable/slow it under reduced-motion.
- **Empty:** distinct, calm honest states per zone — *no issues* (green ready bar), *no received fields* ("No received fields captured for this order."), *nothing to map* ("Nothing needs your attention — the AI mapped every field…"), *no preview sample / blocked preview*. Each should get a real icon (not Unicode) and a consistent tone: success-green for ready, neutral-gray for none, amber for needs-attention.
- **Error / not found:** centered on `#F6F7FA`, ⊘ icon `#C6CDDA` 28px, title 14px / 600 `#0B1A2F` ("Order not found" / "Failed to load order"), navy button "← Back to inbox". **Ask:** real icon component, design-system Button, a touch more warmth.
- **Mobile (< xl):** the drag/picker mapper is desktop-only. Today mobile shows the Issues panel + an honest note ("Open this order on a larger screen to drag-wire fields. You can still review issues and send from here.") + a full-width 44px Send button. **Ask:** design a genuine *reduced* mobile experience — Received and Output as collapsible **summaries or rails** (field counts + format), Issues as a full inline list (or sheet), large touch targets (≥44px), and a persistent bottom Send. The coordinator must be able to *triage and send* on mobile, even if she can't re-map. *(This is currently a TODO in code — your mock defines the target.)*
- **Reduced-motion:** chevron rotations, the preview flash, ready-bar entrance, skeleton pulse, and any scroll-into-view must all honor `prefers-reduced-motion: reduce` (instant states, no keyframes).
- **A11y:** every button needs a visible **focus-visible ring** (2px `#1E66C9`, offset 2px) — currently missing on most. PO title = `<h1>`, section titles (Received / Output / Issues) = `<h2>`. Keep `role=status aria-live=polite` on counts and the flow notice. Drag handles need labels, not just `aria-hidden` glyphs. WCAG AA minimum on all text (verify the many sub-11px mono values and faint grays).

---

## 6. DO / DON'T

**DO**
- Lead with **density + clarity + trust**: real values, real previews, real provenance, plain language.
- Use **progressive disclosure**: Focus control, collapsible panes/rails, auto-mapped-collapsed-behind-a-chip, "show more".
- Keep **one** primary CTA per state (Send), with an honest disabled reason.
- Honor the **buyer-blue / supplier-green** spatial semantic and the **3px cross-section** card edges.
- Keep **standards mappings and raw values visible on demand** — they're what wins 30-year procurement veterans.
- Add the missing **hover / active / focus-visible** states everywhere, calmly (120–150ms).
- Make the **mapping interaction** (SourcePickerChip) and the **send moment** (ready bar → Send) feel earned and delightful.

**DON'T**
- ❌ Invent a new color direction, add gradients/glassmorphism/blobs/sparkle decoration.
- ❌ Use violet for anything but **AI / suggestions / transforms**.
- ❌ Use the words *bridge / dock / crossing / lane / wire / spine* in visible copy.
- ❌ Hide standards references, raw output, or the real incoming values "to look cleaner".
- ❌ Add mode toggles or two-mode "expert" branches.
- ❌ Imply *delivered* when only *generated* — respect `ready_to_deliver` / `delivering` / `delivered` / `delivery_failed`.
- ❌ Ship motion as flair, or ignore reduced-motion.
- ❌ Over-saturate rows: consolidate the 5+ state-border colors per pane into ~3 legible tiers.

---

## 7. DELIVERABLES (what we want back from Claude Design)

High-fidelity mockups, in the locked system, at desktop 1440px unless noted:

1. **Full screen — default desktop** (Focus = All): header + issues (2 issues, 1 blocking) + three zones (Received | Outgoing | docked Preview), all expanded.
2. **Full screen — Focus = Mapping** (Received + Preview railed, Outgoing 1fr) showing the auto-mapped-collapsed chip + 2 "needs you" rows.
3. **Source Picker dropdown open** — an Outgoing row with the 280px portal listbox: grouped incoming options, each showing label + mono value + an "✦ AI 92%" confidence option.
4. **Live Preview pane** — both modes: (a) delivered format (e.g. JSON), (b) exploratory format with the blue info note, plus the violet last-touched `<mark>` highlight.
5. **Output Structure Designer overlay** — tree editor (light) + live preview (dark navy), gradient edge, a node with a "Date · EU" preset and an "only include when" condition; plus the **Transform Popover** (violet) with a 2-step chain (Trim → DateFormat).
6. **Ready-to-send state** — 0 issues green bar + enabled green Send, and the *disabled* "Fix 1 to send" variant with its blocking-reason tooltip.
7. **Mobile (< xl)** — the reduced triage layout: Received/Output summaries, full Issues list, persistent bottom Send.
8. **Empty + error** — order-not-found error screen, "No received fields" empty, and "Nothing needs your attention" all-mapped state.

Spec each with the exact tokens above. Where you propose a change (e.g. consolidating row-state colors), show before/after and keep it inside the system.

---

## 8. SCREENSHOT MANIFEST (current-state attachments)

> ✅ = already captured (attached). ◻ = capture from the live app at `…/inbox/{id}?workshop=1` (Focus control + chips reach every state).

| # | Shot | Status | Shows |
|---|---|---|---|
| 1 | Fullscreen, default (Focus=All) | ✅ | §3 anatomy: header, Issues panel, all three zones, picker chips, blocked preview |
| 2 | Header bar (close crop) | ◻ | Back, PO title, status badge, Buyer▸Supplier+total, Focus control, Send |
| 3 | Issues panel with issues | ✅ (within #1) | issue rows, severity chips, Where→ (and Accept when a fix exists) |
| 4 | Issues ready-bar (0 issues) | ◻ | the green "Ready to send" state |
| 5 | Flow notice strip | ◻ | Generating…/Sending…/error strip |
| 6 | Received pane | ✅ (within #1) | header eyebrow, search, filter chips, groups, mono values, drag handles |
| 7 | Received rail collapsed | ✅ (Output focus) | the 40px collapsed left rail |
| 8 | Outgoing pane | ✅ (within #1) | output rows, ports, names, status tags, `=value`/`ƒx`, "Add output field" |
| 9 | Source picker chip (sourced + unsourced) | ✅ (within #1) | the "pick a field" chip vs a sourced green chip |
| 10 | Source picker **dropdown open** | ✅ | the 280px listbox: search, grouped fields with values, AI-first |
| 11 | Add-output-field menu | ◻ | the canonical/custom combobox + scope toggle |
| 12 | Auto-mapped-collapsed chip | ◻ | the violet "N mapped by AI · review" chip |
| 13 | Live preview — delivered format | ◻ | mono `<pre>` populated on a clean (resolved) order + format pills |
| 14 | Live preview — exploratory | ◻ | the blue info note + violet last-touched highlight |
| 15 | Output Structure Designer overlay | ✅ | tree editor + dark live preview + gradient edge + footer |
| 16 | Transform popover (`ƒx`) | ✅ | the violet transform chain + add-transform |
| 17 | Loading skeleton | ◻ | the 3-column skeleton |
| 18 | Error / not-found | ◻ | "Order not found" / "Failed to load order" |
| 19 | Mobile (reduced) | ◻ | current mobile: Issues + honest note + full-width Send |
| 20 | Focus=Mapping (both panes railed) | ✅ | Outgoing full-width, side panes as rails |
| + | Wire mode ("Show connections") | ✅ | the OPTIONAL drag-wire overlay (mapping's escape hatch) |

**Already captured this session (8):** 1, 7, 10, 15, 16, 20, the picker-open + wire-mode. **To capture (12):** 2, 4, 5, 11, 12, 13, 14, 17, 18, 19 — easiest from a fully-resolved order (so the preview renders) plus a narrow viewport for mobile.
