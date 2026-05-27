# 05 — Components

This file specs the **signature components** that make a ProcuLink screen recognizable, plus the foundational primitives. Each section has:
- What it is
- Where it lives
- The component contract (props)
- A working reference implementation (TSX/JSX)

Drop these into `components/` and import them anywhere.

---

## A. Signature components

### A.1 `<EdgeRails>`

The blue + green vertical rails that frame any order-handling work area.

**Use on:** Canonical Spine review · Upload Workbench · any screen that handles a specific order.
**Don't use on:** Bridge dashboard (the topology IS the buyer/supplier expression) · marketing · auth · settings.

**Props**
```ts
{
  children: ReactNode
  intensity?: number  // 0..1, default 1 — for subtle/strong variants
  showLabels?: boolean // default true — show port markers + vertical labels
}
```

**Reference** — see `components/EdgeRails.tsx`.

---

### A.2 `<WireTopology>`

The Bridge dashboard centerpiece. Buyers on the left, suppliers on the right, wires arcing between them.

**Props**
```ts
{
  buyers: { id: string; name: string; code: string; volume: number; y: number }[]
  suppliers: { id: string; name: string; code: string; y: number; warn?: boolean }[]
  wires: {
    b: string; s: string;
    weight: 1 | 2 | 3 | 4 | 5 | 6;
    health: "ok" | "warn";
    alert?: number
  }[]
  width?: number   // viewBox width, default 1280
  height?: number  // viewBox height, default 600
  onWireClick?: (lane: { b: string; s: string }) => void
}
```

**Construction notes**
- SVG-driven. Wires are cubic Beziers with control points at 35% and 65% of the canvas width.
- Wire stroke = `url(#wire-grad)` (blue→green) or `url(#wire-warn)` (blue→amber) for at-risk lanes.
- Wire thickness from `weight` via `strokeFromWeight()` — log-compressed to ~1.2–5.2 px so volume hints at hierarchy without one wire dominating. Use the same helper for the legend.
- Travelling pulse: a `<circle>` with `style={{ offsetPath: 'path("...")' }}` and a CSS animation.
- Buyer port = rounded rect with a 3px blue left strip.
- Supplier port = rounded rect with a 3px green right strip; amber if `warn`.
- Alert badge sits at the path midpoint as a white circle with amber stroke.

**Reference** — see `components/WireTopology.tsx`.

---

### A.3 `<CanonicalSpine>` + `<SpineNode>`

The vertical schema spine that anchors the order review.

**`<CanonicalSpine>` props**
```ts
{ children: ReactNode }  // expects <SpineNode> children
```

**`<SpineNode>` props**
```ts
{
  id: string
  label: string                // "PO NUMBER"
  value: ReactNode             // string or rich node
  confidence: number           // 0..100
  mono?: boolean               // value uses mono font
  big?: boolean                // grand-total style
  tone?: "buyer" | "supplier"  // adds colored dot to label
  hint?: string                // amber inline hint under value
  srcRef: string               // "header" — shown in card footer left
  outRef: string               // "Order/@orderID" — shown in card footer right
  subnodes?: SubNode[]         // optional inline child rows (lines)
  onJump?: () => void          // jump-to-source action
}
```

**Construction notes**
- Container renders a 3px `--gradient-link-spine` vertical line down the column.
- Each node has a 13px white circle on the spine + a card to the right with label, value, confidence chip.
- Card footer has the source→output mapping refs (inside the card, **not** in margins).
- Connector stubs (dashed) extend 14px to the left and right of the card.

**Reference** — see `components/CanonicalSpine.tsx`.

---

### A.4 `<DocumentAnatomy>`

Wraps any rendered source document and overlays per-zone confidence annotations.

**Props**
```ts
{
  source: "pdf" | "xlsx" | "csv" | "xml" | "edi" | "json" | "email"
  document: ReactNode       // the rendered file (pdfjs, react-data-grid, shiki block, etc.)
  zones: {
    id: string
    label: string            // "Header zone"
    fields: string           // "PO # · Date"
    confidence: number       // 0..100
    top: number              // px from top of document
    height: number           // px
  }[]
  activeZoneId?: string      // for cross-highlighting with spine
  onZoneHover?: (id: string) => void
}
```

**Construction notes**
- Zone overlay is a 1.5px solid border in green (≥90%) or amber (<90%).
- Background is the same hue at 4% opacity.
- Zone label is anchored at the top-right of its own zone, inside the document page. A small white pill with border, padding 2/6, displays `{label} · {fields} · {confidence}%`.
- Hovering a zone calls `onZoneHover(id)` so the parent can highlight the matching spine node.

**Reference** — see `components/DocumentAnatomy.tsx`.

---

### A.5 `<XCard>`

The card with a cross-section edge strip — the "wire seen end-on."

**Props**
```ts
{
  children: ReactNode
  edge?: "left" | "right" | "top" | "bottom"  // default "left"
  color?: "buyer" | "supplier" | "bridge" | "danger" | "amber" | "ai" | "none"
  dense?: boolean         // tighter padding
  className?: string
}
```

**Construction notes**
- 3px strip on one edge using the matching brand token or `--gradient-link-spine` for `bridge`.
- Default card body is `--surface` with `1px solid var(--border)` and `--radius-md`.
- `color="bridge"` uses the link-spine gradient — the "this surface belongs to both sides" signal.

**Reference** — see `components/XCard.tsx`.

---

### A.6 `<StatusJourney>`

The 5-node mini-track that replaces a static status pill.

**Props**
```ts
{
  stage: 0 | 1 | 2 | 3 | 4    // Parse · Normalize · Validate · Transform · Deliver
  compact?: boolean            // tiny variant for inbox rows
  failed?: boolean             // mark current stage as failed
}
```

**Construction notes**
- 5 circles connected by 1.5px lines.
- Completed nodes (`i < stage`) filled green.
- Active node (`i === stage`) filled blue with a 3–4px blueSoft ring (pulse on activation).
- Failed: the active node becomes danger-colored with an X icon.
- Compact variant: 14 px nodes / one-line labels below; used in mobile sticky strips and inbox rows. Full variant: 28 px nodes (was 18 px) / labels below; centered, ≤ 720 px wide.

**Reference** — see `components/StatusJourney.tsx`.

---

### A.7 `<LinkSpine>`

The 2px gradient line that runs across every topbar and serves as a section divider.

**Props**
```ts
{
  height?: 1 | 2 | 3        // default 2
  soft?: boolean             // fades at the ends
  animated?: boolean         // left-to-right fill animation (state change)
  duration?: number          // ms, default 1200
}
```

**Construction notes**
- Standard: a div with `background: var(--gradient-link-spine)`.
- Animated: a flowing background-position keyframe (see motion.md).

**Reference** — see `components/LinkSpine.tsx`.

---

### A.8 `<MonumentNumber>`

The display-font KPI block used on the Bridge dashboard.

**Props**
```ts
{
  value: string               // "1,284" — pre-formatted
  label: string               // "Orders this week"
  sub?: string                // "+18% vs prev"
  accent?: "success" | "warning" | "danger" | "muted"  // sub color
  size?: 28 | 32 | 36 | 44 | 48 | 56  // display font size
}
```

**Reference** — see `components/MonumentNumber.tsx`.

---

## B. Primitives (extend shadcn/ui)

### B.1 `<Button>`

```ts
type ButtonProps = {
  variant?: "primary" | "secondary" | "ghost" | "danger" | "ai"
  size?: "sm" | "md" | "lg"
  loading?: boolean
  children: ReactNode
}
```

- `primary` → navy bg, white text. *Use for the main page action* (e.g. "Cross the bridge →").
- `secondary` → white bg, ink text, border. *Use for save-draft, secondary actions.*
- `ghost` → transparent bg, ink-muted text. *Use for nav, less-important actions.*
- `danger` → danger bg, white text. *Reserve for destructive actions.*
- `ai` → ai violet bg, white text. *Only for "Accept AI suggestion" type actions.*

Heights: sm 26px · md 30px · lg 36px. Radius `--radius`.

Never use gradient backgrounds on buttons.

### B.2 `<ConfidenceChip>`

```tsx
<ConfidenceChip value={84} />
// Renders "84%" with threshold color (green / amber / danger).
```

### B.3 `<SrcChip>` (file-type chip)

```tsx
<SrcChip type="PDF" />
// Uppercase mono tag, file-type colored background.
```

Supported types: PDF · XLSX · CSV · XML · cXML · EDI · EMAIL · API · JSON.

### B.4 `<StatusPill>`

For places too small for `<StatusJourney>`. The Inbox uses `<StatusJourney compact>` paired with a small pill — see the reference Inbox screen.

```tsx
<StatusPill status="review" />
```

States: `new · extracting · review · ready · sent · failed`.

### B.5 `<AiSuggestion>`

```tsx
<AiSuggestion confidence={84} title="..." description="...">
  <Button variant="ai">Accept</Button>
  <Button variant="secondary">Edit</Button>
  <Button variant="ghost">Reject</Button>
</AiSuggestion>
```

Renders a card with a 3px violet left bar, "AI · 84%" tag in the top-right, title, description, and action slot.

### B.6 `<Drawer>`

Slides from the right. **Push-not-overlay** — the table behind it shrinks. `cmd+\` toggles width between 360px and 560px.

### B.7 `<CommandPalette>`

`cmd+K`. Fuzzy across orders, suppliers, SKUs, named actions. Use `cmdk` or similar.

### B.8 `<EmptyState>`

Illustration-free. Headline + sub + primary action. Optional: a small System Identity glyph at the top (the link literally closes on hover — see motion.md).

---

## C. Composition rules

- A page has **one** `--font-display` heading. Other headings use Inter.
- A page has **one** `<Button variant="primary">`. Other actions are secondary or ghost.
- Cards in a grid use the same `XCard` edge color. Don't mix blue / green / bridge edges in one row unless you're explicitly showing buyer→bridge→supplier flow.
- `<StatusJourney>` and `<StatusPill>` together at row level: pill says the named state, journey shows where in the pipeline.
- Edge rails wrap an entire screen body OR none of it. Don't put rails around individual cards.
