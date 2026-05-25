# ProcuLink — Frontend UI Prompt for Claude Code (v2 — locked: "The Bridge Layer")

Hand this whole document to Claude Code as the implementation brief. It is self-contained: design system, screen-by-screen specs, signature components, motion language, and stack alignment for Next.js 15 + Tailwind + shadcn/ui + TanStack + Clerk + ASP.NET API.

---

## 1. Product brief (paste verbatim)

You are building the frontend for **ProcuLink** — an AI-assisted **outbound procurement bridge** between buyer/procurement teams, suppliers, ERPs, and procurement systems. ProcuLink ingests buyer-side purchase order sources (manual upload first, later email/API/SFTP/FTP), normalizes them into a canonical PO model, validates them against supplier-specific rules, lets a human review only the exceptions, and emits a clean supplier-ready output.

**Tagline:** *"Connecting Procurement — the missing link between buyers and suppliers."*

**Marketing line:** *"Buyers on one side. Suppliers on the other. We are the bridge."*

**Not:** a chatbot, a generic PDF parser, a Zapier clone, a marketplace, a procurement CRM. **Is:** a vertical integration workbench for B2B order documents and supplier-specific transformation rules.

**First ICP:** buyer/procurement teams sending purchase orders out to many suppliers. They need to reduce manual reformatting, prevent supplier rejection, prove what was sent, and handle supplier-specific delivery channels.

**Primary users:** procurement operators, purchasing coordinators, and integration specialists. They know supplier SKUs, EDI segments, cXML payloads, PunchOut/API quirks, and approval workflows.

**Core workflow:** `Parse → Normalize → Validate → Review → Transform → Deliver → Learn`

**Tooling rule:** do not use Lovable. All UI/UX and design decisions run through this local design system, `/frontend-design`, and Claude Design/reference images.

---

## 2. Visual direction: "The Bridge Layer" (LOCKED)

The product *is* a bridge between two parties — and the UI structurally shows itself as one. This is not styling; it is the architecture of every screen.

This is the locked direction from the reference images: **Direction 4 — The Bridge Layer**, supported by **Direction 3 — System Identity**. Do not drift into a generic SaaS dashboard or a new visual metaphor.

### Five spatial signatures (non-negotiable)

1. **Edge rails.** A 4px blue rail on the left edge of the work area and a 4px green rail on the right. Blue = buyer / incoming. Green = supplier / outgoing. Render port markers at the top of each rail. The rails frame every screen that handles an order.

2. **Wire Topology dashboard.** The home screen is not a grid of KPI cards. It's a network diagram: buyer ports down the left edge, supplier ports down the right edge, **wires arcing between them**. Wire thickness = volume. Wire color = health (blue→green normal, blue→amber if at-risk). Travelling pulses animate along active wires.

3. **Canonical Spine review.** Order detail is a 3-column ETL view:
   - **Left:** source document with anatomy overlay (header / parties / lines / totals zones, each with a confidence chip).
   - **Center:** the canonical PO schema as a **vertical spine** of nodes connected by a blue→green gradient line. Source-field refs sit to the left of each node; output-field paths sit to the right.
   - **Right:** supplier-ready output (cXML / CSV / JSON) with the same fields highlighted in place.

4. **Document Anatomy.** Source PDFs/spreadsheets are always presented with labeled zone overlays and per-zone confidence — the "x-ray" of the order. Operators see what ProcuLink saw and how sure it was.

5. **Cross-section card edge.** Primary cards have a 3px brand-gradient strip on **one edge** (the "wire seen end-on"). Blue strip = buyer surface. Green strip = supplier surface. Full gradient = bridge surface. Replaces decorative borders and notched corners.

### Supporting signatures

- **Navy app chrome / light work area.** Sidebar and topbar are navy (`#0B1A2F`); main content is warm light. The chrome is the brand frame; work is calm.
- **Link-spine.** A 2px blue→green gradient line runs across the bottom of every topbar.
- **Status as a journey.** Order status is a 5-node mini-track (Parse · Normalize · Validate · Transform · Deliver), not a static pill.
- **Monumental numbers.** KPIs use Bricolage Grotesque, weight 600, tight letterspacing. Numbers ARE the brand voice.
- **System Identity logo.** Evolve the mark toward Direction 3 — a family of link-glyphs, not just one symbol. The mark, the link-spine, the rail markers, the loading state, and the pipeline icons are all expressions of the same shape language.

---

## 3. Design tokens (drop into `tailwind.config.ts`)

```ts
// theme.colors
{
  brand: {
    blue:     "#1E66C9",   // buyer / incoming / structure / trust
    blueDeep: "#0F4FA8",
    blueSoft: "#E3EDFB",
    green:    "#2E8E3A",   // supplier / outgoing / completion
    greenDeep:"#1E6D29",
    greenSoft:"#E2F1E2",
  },
  navy: {
    DEFAULT: "#0B1A2F",    // sidebar + topbar
    surface: "#10243E",    // raised within chrome
    border:  "#1C2F49",
    text:    "#C5D2E4",
    muted:   "#7C8DA6",
  },
  bg:        "#F6F7FA",     // app background
  bgWarm:    "#F8F6F1",     // marketing surfaces
  surface:   "#FFFFFF",
  surface2:  "#EFF2F7",
  border:    "#E2E6EE",
  borderStrong: "#C6CDDA",
  ink: {
    DEFAULT: "#0B1A2F",
    muted:   "#56627A",
    faint:   "#8A93A5",
  },
  amber:     "#C97A14",
  amberSoft: "#FAEFD6",
  danger:    "#C53A3A",
  dangerSoft:"#FBE3E3",
  ai:        "#6F4FCE",     // ONLY for AI-generated content
  aiSoft:    "#EEE7FB",
}
// theme.fontFamily
{
  sans:    ['"Inter"',           "system-ui", "sans-serif"],
  display: ['"Bricolage Grotesque"', '"Inter"', "system-ui", "sans-serif"],
  mono:    ['"JetBrains Mono"',  "ui-monospace", "monospace"],
}
// theme.backgroundImage
{
  "link-spine":    "linear-gradient(90deg, #1E66C9 0%, #1E66C9 35%, #2E8E3A 65%, #2E8E3A 100%)",
  "bridge-deck":   "linear-gradient(90deg, #1E66C9, #2E8E3A)",
  "rail-buyer":    "linear-gradient(180deg, rgba(30,102,201,0.2), #1E66C9 50%, rgba(30,102,201,0.2))",
  "rail-supplier": "linear-gradient(180deg, rgba(46,142,58,0.2), #2E8E3A 50%, rgba(46,142,58,0.2))",
}
```

Scales: spacing 4px base, radii `card-sm: 6 / card: 8 / card-lg: 12`. Shadows: avoid heavy drops. Cards use borders + a single soft `0 1px 2px rgba(11,26,47,0.04)`. Popovers may use `0 8px 24px rgba(11,26,47,0.10)`.

Type: body 13/14px, table rows 12/12.5px, KPI display 32–48px, marketing display 60–78px.

---

## 4. App shell

A persistent **navy left sidebar (220px)** + **navy top bar (52px)** + **main work area** that uses **edge rails** on order-handling screens.

### Sidebar (navy)

- Logo (System Identity mark) + "ProcuLink" wordmark.
- Workspace switcher card (small avatar, name, ⌄).
- Nav groups:
  - **Bridge** (= dashboard / wire topology — the home)
  - **Inbox** (with sub-items: All / New / Needs review / Failed / Sent)
  - **Workbench** (Upload, Drafts)
  - **Library** (Supplier docks, Buyer docks, Mappings, Rules, Output templates)
  - **Operations** (Crossings log, Connectors, Webhooks)
  - **Settings**
- Active item: 2px link-gradient strip to the left, slightly raised navy surface, white text. Inactive: `navy.text` with low-opacity icon.
- Footer: green dot + "Bridge healthy · 12/min".

### Topbar (navy)

- Breadcrumbs left, cmd-K search center-right, notifications + help + avatar far right.
- **Link-spine** at the bottom edge (2px blue→green gradient). Animates left-to-right when an order advances a stage (see motion §8).

### Main work area

- Wraps the order-handling pages in an **EdgeRails** component (left rail blue, right rail green, port markers at top).
- Marketing/auth/settings pages do **not** use edge rails — they are calm and centered.

---

## 5. Required screens

### 5.1 Bridge (Home / Dashboard)

The product's signature screen. **A live network diagram, not a grid of cards.**

**Layout:**
- Top: page header — title "Order topology", date range pill, period segmented control (Today / 7d / 30d / Quarter), "Export report" button.
- Hero panel (~580px tall): **Wire Topology canvas**.
  - Buyer ports rendered as labeled rounded rectangles down the left edge (~6 visible). Each shows buyer name + short code + volume (e.g. `412/wk`).
  - Supplier ports rendered the same down the right edge (~5 visible).
  - Wires drawn as cubic Bezier curves between them. **Stroke width = volume bucket**, **stroke = `link-spine` gradient** (or `link-spine → amber` for at-risk lanes).
  - Travelling pulse: a small white circle with green stroke animates along each active wire using CSS `offset-path`. 6s loop, staggered per wire.
  - Alert badges sit mid-wire where exceptions cluster.
  - Legend in the top-right shows the volume bucket → stroke-width mapping.
  - Clicking a wire opens a lane detail drawer.
- Below the canvas: a **5-column monumental KPI strip**.
  - Numbers in `display` font, weight 600, letter-spacing `-0.035em`, sized 36–48px.
  - Sub-line: trend (`+18% vs prev`, `−22%`, `4 urgent`) in semantic color.
  - First card has a `link-spine` top edge.
- Below KPIs: two columns —
  - **In transit · last 10 minutes** — dense compact list of orders currently moving (PO, buyer, supplier, file chip, stage name colored by stage).
  - **Supplier dock health** — five suppliers with acceptance % bars (green ≥95, amber 85–95, red <85).

### 5.2 Inbox (queue view)

- Header: "Inbox", count, sync state.
- **Time-strip ribbon** at the top: horizontal bar showing volume over 24h with a brushable selection (visual flourish — implement progressively).
- Status filter chips (All / New / Needs review / Ready / Sent / Failed) using the brand-blue tint when active.
- Dense table: Status (5-node mini-track preview, no big pill) · Received · Source (file-type chip) · Buyer · Supplier · PO # · Lines · Value · Exceptions · Assigned · Updated. 36px row height. ≥30 rows above the fold.
- Click row → opens **Canonical Spine review** as a full-page route (not a drawer — operators want the keyboard).

### 5.3 Canonical Spine Review (the showpiece)

This is the most important screen in the product.

**Page header (above edge rails):**
- Back arrow · buyer card (name, file chip, source filename) · **stage bridge graphic** with current stage highlighted (1m-tall horizontal SVG: parse → normalize → validate → transform → deliver, dots filled green for done, blue + pulsing for active) · supplier card (name, output file chip, channel) · "Save draft" / "Cross the bridge →" buttons.
- Primary button uses navy bg, white text, with a 12px link-gradient swatch on the right.

**Body (wrapped in EdgeRails):**

3-column grid: **35% · 30% · 35%**, all aligned to the spine.

**Left column — Source · Document Anatomy**
- The source PDF/spreadsheet rendered inside a light grey panel.
- Overlay rectangles tag the anatomy zones — Header / Parties / Terms / Lines / Totals — with the zone outlined in green if confidence ≥ 90, amber if 70–90, red below.
- Each zone has a small label OUTSIDE the document (in the margin to the left) showing zone name + field summary + confidence chip.
- Hovering a zone highlights the corresponding spine node (and vice versa).

**Center column — Canonical Spine**
- A vertical 3px `link-spine` line down the column.
- Each canonical field is a "node": a 13px white circle on the spine + a card to the right of the spine with label, value, confidence chip, optional hint.
- Each node has small dashed connector stubs — one to the left (source ref label) and one to the right (output path label).
- Source ref labels are in **brand blue mono** (e.g. `header`, `parties.billTo`, `lines.4`).
- Output ref labels are in **brand green mono** (e.g. `Order/@orderID`, `BillTo/Address`, `ItemOut[3]`).
- Fields below confidence thresholds get amber or red field backgrounds. The hint text appears under the value in amber.
- The Lines node expands inline to show the top 3 lines with mapped supplier SKUs (green) or unmapped/AI/error states.
- Grand total node uses the display font at 16px.

**Right column — Output preview**
- A dark-navy panel showing the supplier-ready output (cXML by default, tabs for CSV / JSON if the supplier template enables them).
- Syntax-highlighted (use `shiki` or `prismjs`).
- AI-mapped lines have a violet left-border accent + a small "← AI mapped 84%" trailing comment.
- Error lines have a danger left-border accent + a small "← will be rejected" comment.
- Sticky toolbar: file type tabs · "Show diff vs source" toggle · "Copy" / "Download" buttons.

**Issues rail (collapsible, 280px to the right of the output column):**
- Errors, Warnings, AI suggestions grouped. Each card shows confidence chip and a "Jump to field" action that scrolls the spine + highlights the matching node and source zone.

**Action bar (sticky bottom):**
- Grand total · output template · "Save draft" · primary "Cross the bridge →" with a confirm dialog ("I've reviewed the 3 exceptions. Send to Acme.")

### 5.4 Upload Workbench

- Dropzone card centered (~720px) with file-type chips. Accepts PDF, XLSX, CSV, XML, cXML, EDI, JSON, EML.
- Pipeline picker form: buyer · supplier · output template · mode (auto-process / stop after extraction).
- Auto-process toggle is a **deliberate per-supplier setting** with a visible audit trail — never enabled by default for a new supplier.
- Recently uploaded files list below.

### 5.5 Mapping Editor

- Buyer ↔ supplier code mapping list. Each row: buyer code (blue mono) → arrow → supplier code (green mono) → description → confidence bar → used count / source (Manual / AI / Imported).
- Inline editing, CSV import/export, supplier filter.
- Detail drawer for any selected mapping: history of changes, transformation rule chips (e.g. "strip leading zeros", "uppercase", "UoM map CN → EA").

### 5.6 Validation Rules

- Library of supplier/buyer/global rules.
- Card-grid OR list (toggleable). Each rule: name, scope, severity (Error/Warning/Info), 30-day trigger count.
- Detail page: visual rule builder — left "When" (field selector + operator + value), right "Then" (severity + message + auto-fix). No raw code for MVP.

### 5.7 Crossings Log (audit timeline)

- Append-only timeline of events per order: `received`, `parsed`, `extracted`, `mapped`, `validated`, `human-edited`, `transformed`, `delivered`, `failed`, `retried`.
- Each entry: actor (user / system / AI / supplier), timestamp, order link, expandable payload-diff drawer.
- Right panel metrics: crossings today, avg cycle time, exception rate by supplier, top failing rules.

### 5.8 Supplier dock / Buyer dock profile pages

- Header: name, total orders, avg cycle, exception rate.
- Tabs: Overview · Mappings · Rules · Output templates · Connectors · History.
- Each tab is a focused list. No marketing hero.

### 5.9 Connectors

- Top: a small **wire-topology overview** for connectors — same visual language, smaller scale. Email inboxes, SFTP, API endpoints, cXML PunchOut, webhook outs.
- Below: tables per channel with health (last successful poll, errors in 24h).

### 5.10 Auth

- Single sign-in card, centered, on a `bgWarm` background. Left: System Identity mark + tagline. Right: email/password + SSO. Clerk handles the actual auth.

---

## 6. Component library (build these in `components/` first)

### Signature components

- `<EdgeRails>` — wraps the work area, draws blue + green vertical rails with port markers.
- `<WireTopology>` — buyer ports / supplier ports / animated wires. Props: `buyers`, `suppliers`, `wires` (each with `b`, `s`, `weight`, `health`, `alert?`). SVG-driven with CSS `offset-path` animation.
- `<CanonicalSpine>` — vertical spine layout with `<SpineNode>` children. Each node: `id`, `label`, `value`, `pct`, `tone`, `srcRef`, `outRef`, `hint?`, `subnodes?`.
- `<DocumentAnatomy>` — wraps any rendered source (PDF via `pdfjs-dist`, spreadsheet via `react-data-grid`, XML/EDI as syntax-highlighted) and overlays zone rectangles + per-zone confidence chips.
- `<XCard>` — primary card with cross-section edge strip. Props: `edge="left|right|top|bottom"`, `color="buyer|supplier|bridge"`.
- `<StatusJourney>` — 5-node mini-track showing Parse → Normalize → Validate → Transform → Deliver. Props: `stage` (0–4), `compact?`.
- `<LinkSpine>` — 2px blue→green gradient line. Props: `animated?` (left-to-right fill on state change), `soft?`.
- `<MonumentNumber>` — display-font KPI. Props: `value`, `label`, `sub`, `accent`, `size`.
- `<LinkedPair>` — buyer card ↔ connector wire with node ↔ supplier card lockup.

### Primitives (extend shadcn/ui)

- `<Button>` — variants `primary` (navy bg) / `secondary` / `ghost` / `danger` / `ai` (violet). Never gradient.
- `<StatusPill>` — semantic colors (use sparingly; prefer `<StatusJourney>`).
- `<FileChip>` — uppercase tag, file-format colored (PDF red / XLSX green / cXML violet / EDI amber / CSV slate / EMAIL grey / API green / JSON gold).
- `<DataField>` — label + value + optional confidence chip + revert button + source-link popover.
- `<ConfidenceChip>` — pct + threshold color (≥90 green, 75–89 amber, <75 red).
- `<AiSuggestion>` — violet left-bar, "AI" tag, Accept/Edit/Reject. Confidence always visible.
- `<Drawer>` — slides from right; push-not-overlay; cmd+\ toggles width.
- `<CommandPalette>` — cmd+K, fuzzy across orders, suppliers, SKUs, named actions.
- `<EmptyState>` — illustration-free. Headline + sub + primary action.

---

## 7. Logo & identity system

Use the **System Identity** mark (Direction 3 from the canvas exploration): the brand is not just a logo, it's a shape language. The mark is one expression. Other expressions live in the product:

- **Rail markers** at the top of edge rails use the same geometry.
- **Spine nodes** use the same circle-on-gradient construction.
- **Loading state** is the mark itself completing its link — never a generic spinner.
- **Pipeline glyphs** (parse / normalize / validate / transform / deliver icons) are a family of stage icons in the same construction.

Provide the mark in three sizes (16 / 24 / 64) and in mono (white-on-navy) form. Use the mono form in the sidebar and topbar.

---

## 8. Motion language

Six patterns, each with **one job in the product**. All respect `prefers-reduced-motion`.

| Pattern | When | Behavior |
|---|---|---|
| Link-spine activation | Order advances a stage | The 2px topbar spine fills left→right over 1.2s. |
| Wire-topology travellers | Always (subtle) | Tiny white-dot pulses move along active wires using CSS `offset-path`, 6s loop, staggered. |
| Status node pulse | Stage activates on the current order | The active node in `<StatusJourney>` pulses once with a brand-color ring. |
| Connector draw | Mapping saved/accepted | The buyer-SKU ↔ supplier-SKU line draws blue→green via stroke-dashoffset, 0.8s ease-out. |
| Validate-to-deliver flush | "Cross the bridge" clicked | Status journey advances stage by stage in 40ms stagger; output preview gets a single subtle highlight. |
| Empty-state link-close | Hovering a placeholder card | The mark's link completes its loop. Invites the action calmly. |

**Motion budget:** loops max 6s · no motion above the action bar during keyboard navigation · disable all wire-topology animation under `prefers-reduced-motion: reduce`.

---

## 9. Copy guidelines

### Marketing
- H1: *"Buyers on one side. Suppliers on the other. We are the bridge."*
- Sub: *"ProcuLink turns messy purchase orders into supplier-ready outputs. Upload Excel, PDF, cXML or EDI orders, review only exceptions, deliver clean."*
- Primary CTA: *"Cross the bridge free for 30 days"* · Secondary: *"Watch a 90-second walkthrough →"*
- Stat block: 84% automation · 1m 42s avg crossing · €4.20 per order · 9 connectors.

### App
- Dashboard title: *"Order topology"* (not "Dashboard").
- Order detail primary action: *"Cross the bridge →"* (not "Send").
- Stage labels: *Parse · Normalize · Validate · Transform · Deliver*.
- Empty inbox: *"No orders yet. Drop a file or connect a buyer dock."*
- AI suggestion CTAs: *"Accept" · "Edit" · "Reject"* — never "Apply magic" or sparkles.
- Confirm dialog: *"I've reviewed the 3 exceptions. Send to Acme."* (checkbox + recipient + total + retry behavior visible).
- Success toast: *"Crossed to Acme · accepted · 1m 42s"*.

### Vocabulary
- *Bridge* = the product · *crossing* = a single order transit · *dock* = a supplier or buyer endpoint · *lane* = a buyer↔supplier pairing · *spine* = the canonical model · *anatomy* = the source-document zone overlay.

---

## 10. Trust rules (designed in, not aspirational)

1. **Provenance everywhere.** Every AI suggestion shows confidence + source attribution + a one-click "show me where this came from in the source file" that highlights the matching anatomy zone.
2. **No silent automation.** Auto-process is a deliberate per-supplier toggle with a visible audit trail in Crossings Log. Never default.
3. **Failure is loud, recoverable, and explained.** The Failed view must be the most useful screen in the product — not a tombstone. Every failure shows: what the supplier said, what we sent, what we'd retry, and a one-click "fix and resend" path.

---

## 11. Tech stack alignment

- **Framework:** Next.js 15 App Router. NO Pages Router. NO Vite.
- **Language:** TypeScript.
- **Styling:** Tailwind, theme tokens above. shadcn/ui primitives **restyled** to the Bridge Layer system — no out-of-the-box shadcn look.
- **Data:** TanStack Query for fetching, TanStack Table for the Inbox grid (virtualized > 100 rows).
- **Auth:** Clerk.
- **Backend contract:** ASP.NET Core API. Postgres. Type all DTOs from the OpenAPI schema (use `openapi-typescript` or the project's existing codegen).
- **File rendering:** `pdfjs-dist` (PDF), `react-data-grid` (spreadsheets), `shiki` (XML/cXML/EDI syntax).
- **Mock data:** MSW handlers for every endpoint so the entire UI is browsable offline. Seed: 50 orders across 6 suppliers and 4 buyers, 8 file-format examples, 200 SKU mappings, 12 validation rules, 30 days of crossings log.
- **Hosting:** Vercel (frontend) + Railway (backend).

### Route structure (App Router)
```
app/
  (marketing)/
    page.tsx                    # hero
    pricing/page.tsx
    customers/page.tsx
  (auth)/
    sign-in/[[...sign-in]]/page.tsx
  (app)/
    layout.tsx                  # navy sidebar + topbar
    bridge/page.tsx             # wire topology dashboard
    inbox/page.tsx
    inbox/[orderId]/page.tsx    # canonical spine review (full page)
    upload/page.tsx
    drafts/page.tsx
    library/
      suppliers/page.tsx
      suppliers/[id]/page.tsx
      buyers/page.tsx
      mappings/page.tsx
      rules/page.tsx
      templates/page.tsx
    operations/
      log/page.tsx
      connectors/page.tsx
      webhooks/page.tsx
    settings/page.tsx
```

---

## 12. Build order (do these in order, ship in slices)

1. **Tokens + shell.** Tailwind config, `<EdgeRails>`, navy sidebar, navy topbar with link-spine.
2. **System Identity mark** in all sizes + mono.
3. **Inbox** with mocked TanStack Table. Status journey preview in row.
4. **Canonical Spine Review** at `/inbox/[orderId]`:
   - Page header bridge graphic
   - 3-column body with EdgeRails wrap
   - `<DocumentAnatomy>` with PDF rendering and zone overlays
   - `<CanonicalSpine>` with 9 sample nodes
   - Output preview with shiki-highlighted cXML
   - Issues rail
   - Sticky action bar
5. **Bridge dashboard** at `/bridge`:
   - `<WireTopology>` with offset-path animated wires
   - Monumental KPI strip
   - In-transit list + supplier dock health
6. **Upload Workbench**, **Mapping Editor**, **Validation Rules**, **Crossings Log**.
7. **Marketing pages** with the bridge hero illustration (separate route group).
8. **Motion layer** — implement the six patterns from §8 last, so they layer cleanly over a working app.

---

## 13. Acceptance criteria

The build is done when:

1. A user can land on `/bridge`, see the live wire topology with travelling pulses, click a wire, get a lane detail drawer, and jump from there into a specific order's Canonical Spine review without losing context.
2. From the Spine review, the user can drop a PDF on Upload, watch the four pipeline stages, land in a Spine with at least 3 flagged exceptions, accept 2 AI mapping suggestions, fix one quantity, and click "Cross the bridge" to see the resulting cXML on screen — all without leaving the keyboard.
3. The Inbox renders 1,000 mocked orders at 60fps with full sort/filter/bulk-select.
4. Every screen has a thoughtful empty state, loading skeleton (using the link-close motion, not a spinner), and error boundary.
5. Cmd+K opens a working command palette indexed across orders, suppliers, SKUs, and named actions.
6. Light theme only for v1; tokens structured so a dark theme can be added later by swapping the neutral scale.

---

## 14. Anti-patterns (refuse to build)

- Big editorial serif inside the app (kept for marketing accents only).
- Decorative gradient backgrounds, sparkle icons, illustrated mascots, glassmorphism.
- Modals when a drawer or inline editor will do.
- Modal wizards that hide the source file during review.
- "Good morning, Maria" greetings on the dashboard. Operators want the queue.
- Auto-applying AI corrections without a visible accept step.
- Notched corners everywhere — use `<XCard>`'s cross-section edge as the primary signature instead.
- The directional-field background gradient on every screen — keep it only for marketing hero areas.
- Per-screen color themes. One token system across the entire product.
- Hand-rolled icons that don't share the System Identity construction language.

---

**End of brief.** Start with §12 step 1 (tokens + shell + EdgeRails) and step 4 (Canonical Spine Review). Those two prove the whole design system. Everything else is downstream.
