# Handoff: Order Detail — Horizontal Pipeline Strip

## Overview

This handoff replaces the **vertical "Activity" card** in the right sidebar of the order detail page with a **horizontal 5-stage Pipeline strip** placed near the top of the page, between the order header (PO number / status / actions) and the meta strip (Source / Lines / Total / Currency / Status).

The strip serves two jobs at once:

1. **Where in the pipeline is this order?** — a left-to-right progress visualization (`01 Parse → 02 Map → 03 Validate → 04 Transform → 05 Deliver`).
2. **What just happened?** — the most recent activity for each completed/active stage shown inline under its node.

It replaces (does not augment) the vertical activity feed.

## About the Design Files

The files in this bundle are **design references created in HTML** — prototypes showing intended look and behavior, not production code to copy directly. The task is to **recreate these HTML designs in the target codebase's existing environment** (React, Vue, etc.) using its established patterns and libraries — or, if no environment exists yet, to pick the most appropriate framework for the project and implement the designs there.

The HTML prototype is built with React + Babel + inline styles for fast iteration; in your codebase you should likely re-express it with whatever pattern is already in use (CSS modules, styled-components, tailwind, etc.).

## Fidelity

**High-fidelity (hifi).** Final colors, typography, spacing, and visual states are locked in. Recreate the UI pixel-perfectly using your codebase's existing component primitives.

## Screen: Order Detail (`/orders/:id`)

### Page layout (unchanged, for context)

```
┌──────────────────────────────────────────────────────────────┐
│  ← Back to orders                                            │
│  PO-20260527180507  • Needs review                           │
│  Heinrich Industries → Acme Components Ltd. · Created …      │
│                                  [View source] [Save draft]  │
│                                            [Cross the bridge]│
├──────────────────────────────────────────────────────────────┤
│  ●─────●─────●─────○─────○      ← NEW: PipelineStrip         │
│  01    02    03    04    05                                  │
│  Parse Map   Valid Trans Deliv                               │
│  Parsed AI…  Paused —    —                                   │
├──────────────────────────────────────────────────────────────┤
│  SOURCE  LINES  TOTAL  CURRENCY  STATUS    ← MetaStrip       │
├──────────────────────────────────────────────────────────────┤
│  ┌─ Main column ────────────┐  ┌─ Sidebar ─┐                 │
│  │  Resolve form / banner   │  │  Details  │                 │
│  │  Line items table        │  │  Counter- │                 │
│  │                          │  │  parties  │                 │
│  └──────────────────────────┘  └───────────┘                 │
│                                  (no Activity card)          │
└──────────────────────────────────────────────────────────────┘
```

The change set is:
- **Insert `PipelineStrip` between `OrderHeader` and `MetaStrip`**.
- **Delete the `Activity` card from `OrderSidebar`**. The sidebar now contains just two cards: `Details` and `Counterparties`.

### Component: PipelineStrip

#### Purpose
A glanceable, low-prominence horizontal pipeline showing the 5 ProcuLink stages, with the latest activity message inline under each completed/active stage.

#### Anatomy

5 equal-width columns in a CSS grid (`grid-template-columns: repeat(5, 1fr)`), bracketed by **hairline dividers** above and below (no card chrome, no header).

Each column, top-to-bottom:

1. **Node** — 18px circle. State-dependent (see below).
2. **Connector hairlines** — 1px lines running horizontally from each node to the next, drawn behind the nodes via two absolutely-positioned half-spans per column.
3. **Stage label row** — `{number} {label}` on a single line (e.g. `01 Parse`).
4. **Activity row** — single line: `{message} · {when}` for stages with activity; em-dash `—` for pending stages.

#### Node states

| State          | When                                           | Ring             | Fill          | Inner glyph                          |
| -------------- | ---------------------------------------------- | ---------------- | ------------- | ------------------------------------ |
| `done`         | `idx < currentStage`                           | none             | `--success`   | White 10px check                     |
| `active-ok`    | `idx === currentStage`, activity state = `ok`  | 1.5px `--brand-blue`  | `--surface` | 6px dot, `--brand-blue`         |
| `active-ai`    | `idx === currentStage`, activity state = `ai`  | 1.5px `--ai`          | `--surface` | 6px dot, `--ai`                 |
| `active-warn`  | `idx === currentStage`, activity state = `warn`| 1.5px `--amber`       | `--surface` | 6px dot, `--amber`              |
| `pending`      | `idx > currentStage`                           | 1.5px `--border-strong` | `--surface` | empty                          |

The active state of the current stage is **derived from the activity entry's `state` field**, not from order status. So the same stage index 2 (`Validate`) could render `active-warn` (issues found) or `active-ok` (validating in progress) depending on what just happened.

#### Connector lines

Between stage `i` and `i+1`:
- If `i < currentStage` → `--success` (1px)
- Otherwise → `--border` (1px)

Implementation: each column draws its own **two half-spans** (left half and right half of the column) as 1px absolutely-positioned `div`s at `top: 9px` (vertical center of the 18px node). The first column omits its left half, the last omits its right half. This avoids needing a separate full-width line and lets each connector be independently colored.

#### Typography (in the strip)

| Element            | Token / value                                          |
| ------------------ | ------------------------------------------------------ |
| Stage number       | `font-mono`, **10px**, weight 500, `--ink-faint`, `letter-spacing: 0.04em` |
| Stage label        | `font-sans`, **11.5px**, weight 500 (active = 600)     |
| Stage label color  | active → `--ink`; done → `--ink-muted`; pending → `--ink-faint` |
| Activity message   | `font-sans`, **10.5px**, weight 400 (active = 500), `line-height: 1.35` |
| Activity tint      | `warn` → `--amber`; `ai` → `--ai`; `ok` → `--ink-muted` |
| Activity timestamp | inline after `·`, color `--ink-faint`, weight 400      |
| Pending placeholder | em-dash, 10.5px, `--ink-faint`                        |

#### Spacing

- Strip outer padding: `14px 4px 12px`
- Strip outer margin-bottom: `12px`
- Strip border-top + border-bottom: `1px solid --border-faint`
- Column gap: handled by grid + 6px horizontal padding inside each column
- Node → label row: `margin-top: 7px`
- Label row → activity row: `margin-top: 3px`
- Number → label gap: `5px`

### Data shape

The strip needs two inputs: `currentStage` (0..4) and an `activityByStage` map.

```ts
type StageActivityState = "ok" | "ai" | "warn";

interface StageActivity {
  state: StageActivityState;       // drives color of current-stage node + activity text
  primary: string;                 // single-line message, e.g. "Paused · 2 unresolved codes"
  when: string;                    // relative timestamp, e.g. "1h ago"
}

interface PipelineStripProps {
  currentStage: 0 | 1 | 2 | 3 | 4;
  activityByStage: Partial<Record<0 | 1 | 2 | 3 | 4, StageActivity>>;
}
```

The current order in the prototype has:

```ts
{
  currentStage: 2, // Validate
  activityByStage: {
    0: { state: "ok",   primary: "File parsed",                  when: "1h ago" },
    1: { state: "ai",   primary: "9 of 12 lines mapped · 92%",   when: "1h ago" },
    2: { state: "warn", primary: "Paused · 2 unresolved codes",  when: "1h ago" },
    // 3, 4 omitted → pending, render em-dash
  }
}
```

Stage indices map to: `0 Parse · 1 Map · 2 Validate · 3 Transform · 4 Deliver`. This matches the existing data-model order (`["Parsing","Normalizing","Validating","Transforming","Delivering"]`).

### Interactions & behavior

- **No hover/click affordance on stages.** The strip is informational. (If product wants stages to be clickable later — e.g. jump to a stage's logs — add an underlined-on-hover affordance to the stage label only; do not turn the node into a button.)
- **No animations** on mount. The strip is a quiet status indicator; transitions on stage advancement are deliberately omitted.
- **Live updates**: when the backend advances the order to the next stage, the previous active node should rerender as `done` (fill flips from white-on-ring to solid `--success`) and the connector to its right turns `--success`. Use whatever your framework's diffing handles by default; no explicit animation required.

### Accessibility

- The strip is a list of completed/in-progress steps. Render as an ordered `<ol>` if it helps your stack semantically; otherwise a plain grid is fine.
- Each stage's textual content (number + label + activity) is the readable signal — the node itself is decorative. Either mark the node `aria-hidden="true"` or wrap the column in an element whose accessible name combines number, label, and the activity message + state.
- Color is never the sole signal — the activity message text always conveys the state in words.

### Empty / edge states

- **Order just created, no activity yet** → `currentStage = 0`, all `activityByStage` empty. Renders five pending nodes with em-dashes. Fine.
- **Order fully delivered** → `currentStage = 4`, activity for all five. Render all done except stage 4 which is `active-ok` (or you can elect to mark stage 4 as `done` once delivery is confirmed; the prototype's logic is "current stage is always active, never done").
- **Failed order** → represent as `currentStage = <failed stage>` and the activity entry's state = `"warn"` (or a new `"err"` state — if you add it, render the node ring/dot in `--danger` and tint the activity message `--danger`).

## Sidebar changes

`OrderSidebar` now contains exactly two `Section` cards, in this order:

1. **Details** — order date, reference, currency, incoterm, payment, ship-to, by
2. **Counterparties** — buyer + supplier rows with colored dots, plus the output template

The `Activity` `Section` and its content (the dotted timeline list of activity entries) are **removed**. The activity data has been promoted to the `PipelineStrip` above the meta strip.

## Design Tokens

All values come from `tokens.css`. Key ones used by the strip:

```css
/* Surfaces */
--surface:        #FFFFFF;
--border:         #E2E6EE;
--border-faint:   #EEF0F4;   /* via JS `borderFaint` token in components.jsx */
--border-strong:  #C6CDDA;

/* Text */
--ink:            #0B1A2F;
--ink-muted:      #56627A;
--ink-faint:      #8A93A5;

/* State */
--success:        #2E8E3A;   /* done node + done connector */
--brand-blue:     #1E66C9;   /* active-ok */
--amber:          #C97A14;   /* active-warn */
--ai:             #6F4FCE;   /* active-ai (AI/mapping events) */

/* Type */
--font-sans: "Inter", system-ui, sans-serif;
--font-mono: "JetBrains Mono", ui-monospace, monospace;
```

Note: the prototype's `components.jsx` defines a small JS token object `T` whose values are very close to but not identical to `tokens.css` (e.g. `T.green = #2E8E3A`, `T.borderFaint = #EEF0F4`). When porting, **use the CSS tokens**, not the JS object — the CSS is the source of truth.

## Files in this handoff

| File                          | Purpose                                                  |
| ----------------------------- | -------------------------------------------------------- |
| `screen-order.jsx`            | The full order detail screen including the new `StagesStrip` component and the trimmed `OrderSidebar` (no Activity). Reference implementation. |
| `components.jsx`              | App-wide primitives — `T` token object, `StatusPill`, `Button`, `SrcChip`, `Icon` set, `Sidebar`, `Topbar`. Reference only; rebuild with your own primitives. |
| `data.jsx`                    | Mock data for one order (`ORDER_DETAIL`) showing the expected shape of `lineItems`, `activity`, stages, etc. |
| `tokens.css`                  | Design tokens (CSS custom properties). Source of truth for colors, spacing, typography, radii, shadows, motion. |
| `ProcuLink Orders.html`       | The host HTML page that wires everything together. Useful to open locally to see the live design. |

## Assets

No new image or icon assets are introduced by this change. The strip uses one inline SVG checkmark (10×10) for the `done` state — feel free to substitute your icon library's `check` icon at 10px.

## Acceptance checklist

A correct implementation:

- [ ] Renders a horizontal 5-column strip between order header and meta strip.
- [ ] No card chrome — strip is bracketed by 1px `--border-faint` hairlines above and below only.
- [ ] Node size: 18px circle, 1.5px ring (or 0 ring + solid fill for `done`).
- [ ] Connector lines: 1px, run from node center to node center, colored per the rules above.
- [ ] Stage label format: `{NN} {Label}` on one line with mono number + sans label.
- [ ] Activity format: single line `{message} · {when}`, tinted per activity state.
- [ ] Pending stages show em-dash `—`.
- [ ] Current stage label is weight 600; others are weight 500.
- [ ] Right sidebar contains only `Details` and `Counterparties` — no `Activity` card.
- [ ] No hover/click affordances on the strip (unless product later asks for them).
