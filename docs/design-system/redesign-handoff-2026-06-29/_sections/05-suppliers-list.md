## 05. Suppliers (directory list) — `/library/suppliers`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/suppliers/page.tsx` (a 2-line wrapper that renders `<SupplierDockList />`)
- **Key components:**
  - `src/components/bridge/SupplierDockList.tsx` (the entire page — list, add-supplier inline panel, billing banners, loading/empty/error, mobile cards; also defines the sub-components `SupplierTableHeader`, `SupplierTableRow`, `SupplierMobileCard`, `AutoProcessPill`, `NotSetPill`, `CellValue`, `MobileStat`, `SupplierGlyph`)
  - `src/components/bridge/layout/PageShell.tsx` (page canvas + max-width container, `variant="wide"` → 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` (canonical title row + actions slot)
  - Hooks: `src/hooks/useOrderDirection.ts` (Supplier↔Customer label swap), `src/hooks/useQueriesEnabled.ts` (mock || QA-bypass || signed-in gate)
  - Data: `apiClient.getSuppliers` / `apiClient.createSupplier` (`src/lib/api-client.ts`), `listConnections` (`src/lib/api-client.ts`), `getBillingStatus` (re-exported from `src/lib/api/billing.ts`), `getDeliveryConfig` (`src/lib/api/delivery.ts`)
- **Capture URL (mock):** `/library/suppliers`

### What it is & why it exists
This is the buyer's **supplier directory** — the roster of every counterparty the org delivers purchase orders to. It sits in the **learn / configure** band of the workflow (not the per-order parse→deliver run): each row is one supplier whose versioned integration (input mapping, output format, delivery channel, auto-process flag) is set up once and then reused for every order routed to them. A procurement coordinator opens it to see at a glance which suppliers are wired up (Format + Channel set) versus still "Not set", to drill into a supplier to configure it, to reach a supplier's version history, or to add a new supplier before uploading their first PO.

### Who uses it & the primary job
**Persona:** procurement coordinator (the buyer who owns supplier setup), occasionally the integration expert who configures the delivery details. **Primary job:** scan the directory, then either **add a new supplier** (the one primary action) or **open an existing supplier** to configure/inspect its delivery setup. It is a hub/launcher, not a working surface.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px; gutter ramp 16→24→34px, vertical padding 20→28px; page canvas `--bg #F6F7FA`):

1. **PageHeader** — `h1` "Suppliers" (Bricolage Grotesque display, 28→30px, weight 600, `-0.02em`, ink `#0B1A2F`) + a muted 13px subtitle: `Your suppliers directory — each one's versioned integration lives in Connections. {n} active supplier(s).` (subtitle reads "Loading…" while fetching). Right side: the single **primary action button**.
2. **Primary action** — a blue (`#1E66C9`) pill button, height 34px, 12.5px semibold, plus-icon + "New supplier". When the billing supplier limit is hit it degrades to a disabled neutral (`#F1F3F7`) pill reading "Supplier limit reached" (no plus icon).
3. **Billing limit banner** (conditional) — amber card (`#FFF8EA` fill, `#F0D39A` border, 3px left rule `#B36D14`) shown when `billing.canAddSupplier === false`: "Your {plan} plan includes {limit} suppliers." + helper line + an outline "View billing" button → `/settings`.
4. **Billing-unavailable notice** (conditional) — amber text card when the billing query errored: "Supplier limits could not be checked because the billing API is unavailable."
5. **Add-supplier inline panel** (conditional, toggled by state) — a white card with a green supplier badge tile, "New supplier" heading, a labelled name input with a green Save button, an info hint, and inline error text. (See "What opens / what closes".)
6. **Body — desktop table card** (`hidden sm:block`) — one white rounded-10 card (`#E5E8EE` border, `0 1px 2px rgba(11,26,47,0.04)` shadow) containing a `<table>` with a `<colgroup>` of fixed widths (name = flex, Format 160, Channel 160, Auto-process 170, History 110, chevron 44). Header row: 10.5px uppercase faint labels (`Supplier / Format / Channel / Auto-process` + two blank cols), 1px bottom border. Rows are 14px-padded, clickable, full-width green hover band (`#E9F1EA`).
7. **Body — mobile card list** (`sm:hidden`) — a `<ul>` of rounded-12 cards, one per supplier, with a label/value `<dl>` grid (Format · Channel, then a full-width Auto-process row), chevron, and a History link sitting *outside* the card button.

**Density/type observations:** the table uses a single ~52px row height (14px vertical padding), name cell pairs a 32px green badge tile + 13.5px semibold name + a 10.5px **monospace derived short code** (e.g. "FastParts Inc" → "FI", "ElectroSupply Co" → "EC"). All colours, spacing, and font sizes are **hard-coded hex + px in inline `style`** (a local const palette: `GREEN #2E8E3A`, `GREEN_DEEP #1E6D29`, `GREEN_SOFT #E9F1EA`, `BLUE #1E66C9`, `INK #0B1A2F`, `BORDER #E5E8EE`, etc.), not Tailwind tokens — so this page does not share the design-system token pipeline.

### Data shown
**Entities:** `Supplier` (`{ id, name }`) plus per-row enrichment from its `DeliveryConfig` and `ConnectionSummary`.

| Field shown | Source |
|---|---|
| Supplier name + derived short code | `apiClient.getSuppliers()` → `GET /api/suppliers` (mock `mockGetSuppliersFn` → `MOCK_SUPPLIERS`: FastParts Inc, ElectroSupply Co, GlobalComponents, PrecisionMfg). Code is computed client-side from the name via `shortCode()`. |
| Format | `config.outputFormat` (uppercased) from `getDeliveryConfig(id)` → `GET /api/suppliers/{id}/delivery-config`. `null`/no-config → faint "—". |
| Channel | `config.protocol` mapped through `PROTOCOL_LABEL` (http→HTTP, sftp→SFTP, ftps→FTPS, smtp→Email, erp_erply→Erply ERP, erp_directo→Directo ERP). No config → "—". |
| Auto-process | `config.autoDeliver` → `AutoProcessPill` (On / Off / Not set). |
| History link | presence of a `ConnectionSummary` for that supplier via `listConnections()` → `GET /api/connections` (mock covers only the **first 2** suppliers, so History shows only on FastParts Inc + ElectroSupply Co). Links to `/library/suppliers/{id}?tab=history`. |
| Billing limit / plan / supplier limit | `getBillingStatus()` → `GET /api/billing/status` (mock returns Pilot, `supplierLimit: 1`, `canAddSupplier: false`). |

**Note (offer↔works):** the comment block in the file documents that the "Orders" and "Acceptance" columns the design once showed were **deliberately dropped** because no per-supplier order/delivery-success aggregate is exposed here — so there is currently **no health/acceptance indicator on this list** despite the focus hint. The only "health-ish" signal is the Auto-process pill, which is a config flag, not a success rate.

**Per-row fetch bound:** only the first `DELIVERY_FETCH_CAP = 50` rows fetch their delivery config (each row owns its own query with a 5-min `staleTime`).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| "New supplier" header button | `setShowAddPanel(true)` | Opens the inline add-supplier panel. Disabled (→ "Supplier limit reached") when `!canAddSupplier`. |
| "View billing" button (limit banner) | `router.push("/settings")` | Navigates to Settings. |
| Add-panel **name input** | `onChange` updates `newName`; `Enter` calls `handleSave()` | Local state; submits on Enter. |
| Add-panel **Save supplier** button | `createMutation.mutate(name)` → `POST /api/suppliers` | On success: invalidates `["suppliers"]`, closes panel, clears field. On error: shows inline error. Disabled + "Saving…" while pending. |
| Add-panel **X / close** button | `setShowAddPanel(false)` + clears name/error | Closes the panel (aria-label "Close add supplier panel"). |
| **Table row** (whole `<tr>`) | `onClick` → `router.push('/library/suppliers/{id}')` | Opens the supplier detail page. Hover paints the green row band. |
| Row **History ›** link | `next/link` → `/library/suppliers/{id}?tab=history` (calls `e.stopPropagation()`) | Opens the supplier's version-history tab without triggering the row click. Only rendered when a connection exists. |
| Row **chevron** | decorative (inside the clickable row) | No separate action. |
| **Mobile card** (`<button>`) | `onClick` → `router.push('/library/suppliers/{id}')` | Opens supplier detail. |
| Mobile **History ›** link | `next/link` → `?tab=history` | Opens version history (sits outside the card button to avoid nested interactives). |
| Empty-state **New supplier** button | `setShowAddPanel(true)` | Opens the add panel (only when `canAddSupplier`). |

There is **no row context-menu, no row dropdown, no rename/delete affordance, no search, no filter, no sort, and no column controls** on this list.

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Add-supplier panel** | Inline panel (NOT a modal/drawer — it's an in-flow white card above the table; no scrim, no portal) | The "New supplier" header button OR the empty-state "New supplier" button (both `setShowAddPanel(true)`) — **only renders when `showAddPanel && canAddSupplier`** | Green supplier badge tile, "New supplier" heading + helper, a "Supplier name *" labelled input (autofocus), a green "Save supplier" button, a green info hint explaining auto-process, and inline red error text on failure | The X button (`setShowAddPanel(false)`), or a successful save (mutation `onSuccess` closes it). **No Esc handler, no backdrop** (it's not an overlay). Enter in the field submits (does not close on failure). |
| **Billing limit banner** | Inline notice (conditional) | Auto-renders when `billing.canAddSupplier === false` | Plan/limit message + "View billing" button | Not dismissible — disappears only when the condition changes. |
| **Billing-unavailable notice** | Inline notice (conditional) | Auto-renders when the billing query errors | "limits could not be checked…" text | Not dismissible. |
| **Inline add error** | Inline text | `createMutation.onError` | Parsed API error (e.g. "A supplier named 'X' already exists.") | Cleared on next open / successful save. |

**Summary:** this page opens **no true overlays** (no Dialog, Sheet, Popover, Dropdown, Tooltip, or Toast). Every transient surface is an **inline, in-flow panel/notice**, and every navigation (open supplier, open history, view billing) happens **in place via the router**. This is the single most notable structural fact for the redesign: the "add supplier" flow is an inline card, not a modal, and there is no row-action menu at all.

### States
- **Empty:** Handled well. A dashed-border white card centered with a green supplier glyph, "No suppliers configured", a one-line next-action explainer, and (when allowed) a "New supplier" button. Honest and actionable.
- **Loading:** Handled inline (no `loading.tsx` in the route folder). Renders the desktop table card frame with the column header and **4 skeleton rows** (pulsing badge tile + two text bars), `role="status" aria-busy`. Per-row Format/Channel/Auto-process show their own pulsing shimmer while their delivery-config query is in flight (`CellValue` / pill shimmer).
- **Error:** Handled. A red card (`#FEF2F2` / `#F1C9C9`): "Could not load suppliers. Check your connection and try refreshing." — but it gives **no retry button** (relies on a manual page refresh). Billing errors degrade gracefully (optimistically allow add + show the amber notice).
- **Success/feedback:** No toast. Add-supplier success simply closes the panel and the new row appears after `["suppliers"]` invalidation. No "Supplier added" confirmation message.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** identical table layout, centered at the 1480px wide container; on 1920 there is large left/right whitespace (table never exceeds 1480px). Fixed col widths mean wide viewports just pad the gutters.
- **Tablet 768:** still the desktop table (the `sm` breakpoint = 640px, so ≥640px shows the table). The header action row goes horizontal. Fixed Format/Channel/History widths can feel sparse but don't break.
- **Mobile 390 (<640px):** switches to the stacked card list; the table is `hidden`. Each supplier is a rounded-12 card with a 2-col label/value grid + full-width Auto-process row + chevron, and the History link below the card. The "New supplier" button goes full-width (`w-full sm:w-auto`). This is a proper stacked layout, not a shrunk table — good.
- **Known cliff:** none structurally; the main mismatch is that the **add-supplier panel and banners are full-width inline cards** that push the whole list down on every viewport rather than overlaying.

### Current UX issues
- **No status-badge / health system for the actual page job.** The focus hint expects a supplier health/acceptance indicator; there is none. The only pill is "Auto-process: On/Off/Not set" (a config flag). A supplier with a broken/failing delivery looks identical to a healthy one — violates "never show healthy when something is failing" because there is simply no truth signal here.
- **Two competing primary colours.** The page primary action is **blue** ("New supplier") but the in-panel confirm CTA is **green** ("Save supplier"), and green is also the supplier-entity accent and the success colour. This breaks DESIGN BAR #7 (one dominant primary) and the green/blue semantics blur (blue = add, green = save/success/entity/auto-on all at once).
- **Everything is hard-coded inline hex + px, off the token system.** ~15 local colour consts and dozens of literal `px` font/padding values mean this page can't inherit the redesign's spacing rhythm or type scale (DESIGN BAR #1/#2). Sizes drift (10.5/11/11.5/12/12.5/13/13.5/15px) instead of a 4/8 + single type scale.
- **Add-supplier as an inline card, not a modal.** It shifts the entire list when it opens/closes, has **no Esc, no scrim, no focus trap** — inconsistent with a real form-entry surface and with DESIGN BAR (modals/drawers get clear close/Esc/scrim).
- **Short code is invented, not real.** The monospace code under each name is derived from the name (`shortCode()`), not a real supplier code from the API — it reads like meaningful data but isn't, which can mislead a coordinator.
- **No retry on the error state.** "try refreshing" with no button (DESIGN BAR #6 wants reason + retry).
- **No table affordances.** No sort, no `aria-sort`, no search/filter — fine for 3–20 suppliers but the header looks like a sortable table without being one. Gridlines/hover are bespoke rather than the shared list density.
- **Numbers aren't tabular-figured.** The "{n} active suppliers" count and the (absent) numeric columns don't use tabular figures; the derived code uses JetBrains Mono but the rest don't (DESIGN BAR #3).
- **History link discoverability.** "History ›" appears only on suppliers that already have a connection (2 of 4 in mock), so the column looks half-empty and the feature is easy to miss.
- **Hover band only on desktop; rows aren't keyboard-operable as a unit.** The `<tr>` is click-only (no `role="button"`/`tabindex`/Enter), so keyboard users can only reach the inner History link, not "open supplier" (DESIGN BAR #9 focus/interaction).
- **Mock-mode trap for QA.** In mock mode billing returns `canAddSupplier:false`, so the add button is disabled and the add panel can't be opened without overriding billing — worth flagging for the capture plan and for anyone QA-ing the add flow.

### Redesign recommendations (for Claude Design)
1. **Add a real per-supplier status/health badge** (most impactful) — surface a single status pill driven by truthful data: e.g. "Configured · Auto" / "Configured · Manual" / "Not set up" / "Delivery failing". If a last-delivery-failed signal is available, show amber/red; never green when failing. This is the column the page is missing and the reason a coordinator scans the list. Keep it in ONE badge system (one shape/size/padding, icon + word, green/amber/red/neutral) shared with the rest of the app.
2. **Resolve the primary-action colour to one system.** Pick green as the *entity/success* accent and make the single page primary action visually dominant in **one** colour (per the brand, green primary ≥44px); demote the in-panel Save to that same primary, not a second hue. Don't run blue "Add" + green "Save" side by side.
3. **Promote add-supplier to a real modal/drawer** with scrim, Esc-to-close, focus trap, and animate-from-trigger — or, if inline is preferred, make it a compact top-anchored form that doesn't reflow the list and still has Esc + a clear close. Add a success toast ("Supplier added").
4. **Re-base the whole page on design tokens.** Replace the inline hex/px consts with the shared spacing scale (4/8), one type scale (heading 600 / label 500 / body 400), token colours (`--ink`, `--ink-muted`, `--border`), and the canonical card radius/border/shadow tier. This alone fixes the size drift and brings it into the system.
5. **Make rows fully keyboard-operable and consistent with the shared table density** — single row height, `gray-200` gridlines, sticky header, focus-visible ring, `role`/`tabindex` so Enter opens the supplier; give the History action a clearer secondary affordance (ghost link or kebab) rather than a sometimes-present text link.
6. **Replace the invented short code** with a real supplier code/identifier when the API exposes one (or drop it). If kept as a display aid, label it as derived so it's not mistaken for a stored code; render it in tabular/mono consistently.
7. **Add a retry button to the error state** and keep the graceful billing-degrade behaviour.
8. **Add lightweight search/filter once the list can grow** (with `aria-sort` if columns become sortable), and tabular figures for any count/number. For mobile, keep the stacked-card pattern (it's already correct) but unify the card radius/shadow with the desktop card tier.
9. **Reconsider surfacing acceptance/order volume** — if a per-supplier orders/acceptance aggregate becomes available, restore those as honest columns (they were dropped for offer↔works reasons); the health badge in #1 is the conservative first step.
