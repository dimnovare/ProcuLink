# 11 — Unified Page Rules

**This document + the primitives in `src/components/bridge/layout/` are the
canonical way to build any new page or screen in the ProcuLink app.** It
extends `00-agent-quick-brief.md` and the token files (`02-tokens.md`,
`03-typography.md`, `04-color.md`). When this doc and a stale per-page pattern
disagree, this doc wins.

The goal is simple: **every new page should know exactly how to look without
re-deriving spacing, colors, type, or responsive behavior.** Compose the
canonical primitives instead of hand-rolling a shell, a header, a card, and a
button per screen. That is how the audit's two dominant problems —
inconsistent layout and stray hex/palette constants leaking into pages — stop
recurring.

The locked visual language is unchanged: **Direction 4 — The Bridge Layer**
(navy chrome, light work area, buyer-blue left / supplier-green right, the
link-spine gradient, the five-stage journey). These rules are about
*consistency of execution*, not a new direction.

---

## Canonical primitives

Build pages by composing these. Do not re-implement their responsibilities
inline.

| Primitive | Responsibility | Import |
|---|---|---|
| **PageShell** | Full-height scroll area on the page canvas; centered container with the right max-width + responsive gutter/padding. Replaces per-page `*-shell` / `work-inner` divs. | `@/components/bridge/layout/PageShell` |
| **PageHeader** | The one page-title row: display-type `<h1>`, optional subtitle, optional right-aligned actions slot that wraps on mobile. Replaces inline `<h1 style>`. | `@/components/bridge/layout/PageHeader` |
| **Card** | The one surface: white bg, 1px border, `--radius-md`, `--shadow-card`, standard padding, optional 3px blue/green/bridge accent edge, optional title/sub header. Replaces inline card divs. | `@/components/bridge/layout/Card` |
| **MobileListRow** | The "desktop table row becomes a stacked card on mobile" row: full-width surface, ≥44px hit area, press feedback, keyboard-operable when clickable. | `@/components/bridge/layout/MobileListRow` |
| **Button** | The one action control: variants (`primary`/`secondary`/`ghost`/`danger`/`ai`/`blue`/`green`), sizes (`sm`/`md`/`lg`), ≥44px mobile hit area, brand-green primary. | `@/components/bridge/DSPrimitives` |
| **UnifiedStatusBadge** | The one order/exception status badge + the canonical label/tone maps (`STATUS_LABELS`, `statusLabel()`, `statusTone()`). | `@/components/bridge/UnifiedStatusBadge` |

Supporting primitives already in the system (do not duplicate): `BridgeLoader`
/ `BridgePageLoader` (loading), `EmptyState` (empty), `AiSuggestion` (AI
content), `ConfidenceChip` / `SrcChip` (chips), `XCard` (signature
cross-section card), `StatusJourney` (five-stage journey).

> **Naming note:** the status badge is `UnifiedStatusBadge`, **not**
> `StatusBadge`, because an unrelated `StatusBadge` already lives at
> `src/components/ui/status-badge.tsx`. Use `UnifiedStatusBadge` for
> order/exception status going forward and migrate old call sites onto it; the
> `ui/status-badge.tsx` version is legacy.

---

## Token scales to use (and the gaps now filled)

Everything visual comes from tokens — never per-page constants.

### Breakpoints — Tailwind only

Use **only** the four Tailwind breakpoints. **Custom `px` media queries in
page `<style>` blocks are banned.**

| Prefix | Min width |
|---|---|
| `sm` | 640px |
| `md` | 768px |
| `lg` | 1024px |
| `xl` | 1280px |

### Container widths (newly tokenized)

| Token | Value | Use |
|---|---|---|
| `--container-narrow` | 1040px | forms, settings, single-column reading |
| `--container-wide` | 1480px | tables, dashboards, multi-column ops screens |

Pick via `PageShell variant="narrow"` (default) or `variant="wide"`.

### Page gutter ramp

`PageShell` applies the canonical horizontal gutter ramp and vertical rhythm:

```
px-4 sm:px-6 lg:px-[34px]   /* 16 → 24 → 34px horizontal */
py-5 sm:py-7                 /* 20 → 28px vertical        */
```

`--page-gutter` (16px) is the documented base value; the ramp lives in
`PageShell` so pages don't repeat it.

### Hit area (newly tokenized)

`--tap-min: 44px`. Every interactive control must have a ≥44px hit area on
mobile. `Button` and `MobileListRow` already enforce this; custom controls
must too.

### The ONE primary-action color decision

- **In-app primary action = `var(--brand-green)` (#2E8E3A).** This is the
  `Button` `primary` variant.
- **Neutral / navy CTA = `var(--navy)`** — reserved for the rare neutral or
  destructive-adjacent action; use `secondary` styling, not the green primary.
- Buyer-blue (`--brand-blue`) stays the buyer/structure/active accent (rails,
  nav active, links, in-progress states) — it is **not** the generic primary
  button color.

> The retired emerald greens **`#28C55E`, `#1DAF50`, `#1AAF50`** are banned.
> They have been purged from `DSPrimitives.tsx`; use `--brand-green`
> (#2E8E3A) / `--brand-green-deep` (#1E6D29) / `--brand-green-soft` (#E2F1E2).

### Known size drift — reconcile later

There is a small, intentional-to-document mismatch between the CSS variables
and the Tailwind spacing tokens for chrome dimensions. Do **not** "fix" one
side ad hoc; reconcile both together in a dedicated change.

| Concept | `globals.css` var | Tailwind token | Drift |
|---|---|---|---|
| Topbar height | `--topbar-h: 56px` | `spacing.topbar: 52px` | 4px |
| Sidebar width | `--sidebar-w: 236px` | `spacing.sidebar: 220px` | 16px |

Until reconciled, prefer the CSS vars (`--topbar-h` / `--sidebar-w`) for new
work and note the drift in any PR that touches chrome.

---

## Every new page MUST

1. **Wrap in `PageShell`** (`variant="wide"` for tables/dashboards, default
   narrow otherwise). No bespoke full-height/scroll/centering wrappers.
2. **Use `PageHeader`** for the title row. No inline `<h1 style={...}>`.
3. **Use `Card`** for every surface. No inline `background`/`border`/
   `border-radius`/`box-shadow` card divs.
4. **Use `Button`** for every action. No raw `<button style={{ background }}>`.
   The primary action's color comes from the primitive (brand-green), never a
   per-page hex.
5. **Zero hex literals in pages.** Use token classes (`bg-brand-green`,
   `text-ink-muted`, `border-border`) or `var(--token)`. **No per-page palette
   constants** (`const BLUE = "#1E66C9"` and friends are banned — they are the
   #1 source of drift in the audit).
6. **Only Tailwind breakpoints.** No custom `<style>` media queries
   (`@media (max-width: 720px)`) in page code.
7. **Every interactive control ≥44px hit area on mobile**, and **inputs use
   ≥16px font on mobile** (prevents iOS auto-zoom). Use the primitives or
   `min-height: var(--tap-min)`.
8. **Lists ship both a desktop table and a `MobileListRow` mobile view** — not
   a single layout that overflows on phones.
9. **Loading / empty / error use the shared primitives:** loading =
   `BridgeLoader` / `BridgePageLoader`; empty = `EmptyState`; error = the
   shared error card pattern. No ad-hoc spinners or "something went wrong"
   strings.
10. **Status / severity uses `UnifiedStatusBadge`** (and `statusLabel()` /
    `statusTone()` for non-badge uses). **AI-generated content uses AI violet
    only** (`--ai`), never as decoration — with visible confidence +
    Accept/Edit/Reject.
11. **Respect `prefers-reduced-motion`** — motion communicates state, not
    flair. The reduced-motion guards in `globals.css` already cover the shared
    animation classes; don't add page-level motion that bypasses them.

---

## Migration order

Convert the worst offenders first (most inline hex / bespoke shells / missing
mobile view), in roughly this order:

1. `src/app/(app)/settings/page.tsx`
2. `src/app/(app)/operations/health/page.tsx`
3. `src/app/(app)/operations/connectors/page.tsx`
4. `src/app/(app)/operations/webhooks/page.tsx`
5. `src/app/(app)/admin/page.tsx`
6. `src/app/(app)/inbound/invoices/page.tsx`
7. `src/app/(app)/inbound/asns/page.tsx`
8. `src/app/(app)/library/templates/page.tsx`

**Closest existing template to copy:** `src/app/(app)/drafts/page.tsx` — it
already follows the centered-container + display `<h1>` + card-row +
`EmptyState` shape. The migration is mostly: replace its inline palette
constants and shell/header/card markup with `PageShell` + `PageHeader` +
`Card` + `Button` + `MobileListRow`.

---

## Enforcement

A CI lint rule should fail the build on the two patterns that caused the
drift, scoped to `src/app/**`:

- **Raw 6-digit hex literals** — regex `#[0-9A-Fa-f]{6}` anywhere under
  `src/app/**` (pages must use token classes or `var(--token)`; primitives in
  `src/components/bridge/**` are the only place raw color values are allowed,
  and even there they should map to tokens).
- **Inline-styled buttons** — `<button` with an inline `background:` (or
  `style={{ background` ) under `src/app/**` (use `Button`).

Until the lint exists, reviewers enforce the "Every new page MUST" checklist
by hand. New pages that violate it should not merge.
