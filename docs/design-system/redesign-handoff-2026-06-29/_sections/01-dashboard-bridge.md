## 01. Dashboard — `/bridge`

- **File:** `src/app/(app)/bridge/page.tsx` (thin wrapper; renders `<BridgeDashboard />`)
- **Key components:**
  - `src/components/bridge/BridgeDashboard.tsx` (the whole screen — ~1260 lines, all logic + layout inline)
  - `src/components/bridge/WireTopology.tsx` (the SVG "System map" canvas + its mobile lane-list fallback `WireTopologyLaneList`)
  - `src/components/bridge/LaneDrawer.tsx` (right-side drawer opened by clicking a wire)
  - `src/components/bridge/StatusJourney.tsx` (the compact 5-node Parse→Deliver mini-stepper in each "In transit" row; also exports `StatusCell`)
  - `src/components/bridge/FileChip.tsx` (the uppercase format tag: PDF/XLSX/cXML/EDI…)
  - `src/components/bridge/OnboardingChecklist.tsx` (the "Get your first order automated" band / hero card + one-time completion card)
  - `src/components/bridge/OnboardingWizard.tsx` (full-screen modal for orgs with no supplier yet)
  - `src/components/bridge/buildChecklistSteps.ts` (pure model deriving the 6 checklist steps)
  - `src/components/bridge/layout/PageHeader.tsx`, `.../layout/PageShell.tsx` (canonical title row + page wrapper)
  - `src/components/bridge/BridgeLoader.tsx` (`BridgePageLoader` used by `loading.tsx`)
- **Capture URL (mock):** `/bridge` — mock mode is config-driven (`NEXT_PUBLIC_USE_MOCK=true`), not a query param. In mock mode the topology renders 6 buyers / 5 suppliers / 11 wires (incl. alerts + a `down` lane), the funnel/KPIs count the 3 mock orders, and a wire is clickable to open the LaneDrawer. There is no per-page mock id.

### What it is & why it exists
This is the operational home screen — the first thing a procurement coordinator sees after sign-in. It answers "what is happening to my orders right now?" across the whole `Parse → Normalize → Validate → Review → Transform → Deliver → Learn` loop: an order-pipeline funnel (Received → Needs review → Ready → Delivered → Failed), a buyer→supplier "System map" of live connections, four headline KPIs, an "In transit" activity list, and a per-supplier "delivery success rate" health list. It also doubles as the onboarding surface: a brand-new org sees a guided wizard + a 6-step checklist instead of an empty map. Coordinators open it to triage exceptions, confirm deliveries are flowing, and jump to the inbox or a specific order.

### Who uses it & the primary job
**Procurement coordinator** (the buyer-side persona who approves the €399 plan). The single most important task: **spot orders that need a human and get into triage fast** — the amber exception strip, the "Needs review" funnel tile, and the "Needs attention" KPI all deep-link to `/operations/exceptions`. Secondary jobs: confirm throughput (Orders received/delivered), and onboard (checklist → first delivery).

### Layout & structure (current)
Rendered inside `PageShell variant="wide"` (max-width `var(--container-wide)` ≈ 1480px; gutters 16→24→34px, vertical 20→28px) on the grey app canvas `var(--bg)` (#F6F7FA). Top to bottom:

1. **PageHeader** (`PageHeader.tsx`) — floating title "Dashboard" (Bricolage Grotesque, 28→30px, weight 600) directly on the canvas (no white bar). Sub-line: green status dot · "Live order view" · `{n} connections` · `{n} active suppliers`. Right-aligned actions (only shown when there is order data): a **time-window segmented control** (Today / 7d / 30d / All — inset white pill track, active pill = navy #0B1A2F) and an **Export report** button (outline, Download icon).
2. **OnboardingWizard** — full-screen modal overlay (only when no supplier; see overlays table).
3. **Branch A — Onboarding hero** (when `orderCount===0 && no endpoint topology && checklist incomplete`): a centered max-980px column with a one-line intro + the `OnboardingChecklist` card as the primary content. The rest of the dashboard is replaced.
4. **Branch B — Normal dashboard** (a `flex flex-col gap-4 sm:gap-5` stack):
   - **Exception strip** (conditional) — full-width amber link banner (`border #F0D39A`, 3px amber left border, bg #FFF8EA): "`N` orders need your attention" → "Review exceptions →". Only shows when the summary query has settled AND count > 0.
   - **Hero section** with a tab strip (`role="tablist"`): **Pipeline** (default, BarChart3 icon) | **System map** (Network icon), same navy-active inset-pill style as the window selector.
     - **Pipeline tab**: a white card (radius `--radius-card`, border #E5E8EE, 1px shadow, 3px blue→green top accent) containing a 5-tile funnel grid (`grid-cols-2 sm:grid-cols-3 lg:grid-cols-5`, gap 3 = 12px). Each tile: uppercase label + Lucide icon, a `monument` (display-font) `clamp(24–32px)` tabular number, and a proportional bar (5px). "Needs review" and "Failed" tiles are links to exceptions. Below: a plain-language flow caption "Order pipeline · Received → Needs review → Ready → Delivered · All time".
     - **System map tab**: same white card frame with a 3px blue→green accent, a legend header row (Buyer dot, Supplier dot, "At-risk connection" dash, and a right-aligned "⚠ N open exceptions" pill link), then the `WireTopology` SVG canvas (inner card chrome stripped so the wrapper is the single card). Canvas height is adaptive (`min 320 / max 520`, `150 + maxPorts*74`).
   - **OnboardingChecklist** band (renders again here as a full-width band while setup is incomplete; self-nulls when complete/loading/errored).
   - **KPI strip** — `grid-cols-2 xl:grid-cols-4`, four white cards (radius `--radius-card`, 1px border, 1px shadow, 3px colored top accent each). Each: uppercase label, `monument` `clamp(28–36px)` value, a sub-line with icon. The "Needs attention" card is itself a link to exceptions.
   - **Bottom row** — `grid-cols-1 xl:grid-cols-2`:
     - **In transit** card — header (Send icon + title + "moving through the pipeline now"), then a `divide-y` list of active orders. Each row: PO# (mono, blue-deep) · buyer · `FileChip` format · stage word, with a compact `StatusJourney` 5-node stepper underneath. Rows with an id link to `/inbox/{id}`.
     - **Supplier health** ("{noun} health") card — header (Activity icon + title + "Delivery success rate, last 30 days" + "All suppliers →" link), then a list of suppliers each as a 44px-min row: name · 160px health bar (desktop only) · `health%` (mono, bold, color-coded green/amber/red).

**Spacing/type/density observations:** heavy reliance on inline `style={{}}` with hard-coded hex (#0B1A2F, #5E6779, #E5E8EE, #B36D14 …) and px values, rather than tokens/Tailwind scale. Font sizes are fractional and ad-hoc (10.5, 11.5, 12.5, 13). Numbers DO use `tabular-nums` / JetBrains Mono in most places (good). Three distinct "inset pill" segmented controls (window, hero tabs) repeat the same markup.

### Data shown
- **Orders working set** — `apiClient.getOrders({ pageSize: 100 })` (`GET /api/orders`; mock `mockGetOrders`). Fields used per order (`OrderSummary`): `id`, `poNumber`, `buyerName`, `supplierName`, `status`, `sourceFormat`, `lineCount`, `unresolvedCount`, `totalValue`, `currency`, `createdAt`. Drives derived topology, in-transit rows, windowed counts, auto-processed %, and CSV export.
- **Orders summary** — `apiClient.getOrdersSummary()` (`GET /api/orders/summary`; mock `mockGetOrdersSummary`). `{ byStatus: Record<status,number>, total }`. Drives the funnel stages + the "Needs attention" (open-exceptions) KPI — all-time, full-population.
- **Suppliers** — `apiClient.getSuppliers()` (`GET /api/suppliers`; mock `mockGetSuppliersFn`). Used to align derived dock ids with configured supplier ids.
- **Server topology** — `apiClient.getDashboardTopology()` (`GET /api/dashboard/topology`; mock `mockGetDashboardTopology`). `{ buyers[], suppliers[], wires[] }` (buyer: id/name/code/volume; supplier: +health; wire: buyerId/supplierId/weight 1–6/health ok|risk|down/alert?). Preferred over client-derived topology when it has data.
- **Windowed counts** — two extra `getOrders({ pageSize:1, dateFrom })` queries (received total, delivered total) read only `totalCount`; disabled in mock mode.
- **Onboarding status** — `useOnboardingStatus()` (`GET /api/onboarding/status`). Booleans `hasSupplier`, `hasCatalog`/`hasItemMappings`, `hasUpload`, `hasResolvedMapping`, `hasDeliveryConfig`, `hasTestFired`, `hasDelivery` + ids → fed to `buildChecklistSteps()` for the 6-step checklist + wizard gating.
- **Direction labels** — `useOrderDirection()` swaps "Supplier"↔"Customer" copy (display only) for inbound orgs.
- Mock fallbacks: `IN_TRANSIT_MOCK_FALLBACK` (5 staged rows, only in mock mode when no live active orders); `MOCK_CROSSINGS` in LaneDrawer (mock mode only).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Time-window pills (Today / 7d / 30d / All) | `setWindowKey()` | Re-filters windowed KPIs (Received, Delivered, Auto-processed) + the CSV export scope; default 30d |
| **Export report** button | `handleExport()` | Client-side CSV download of the current window's orders (capped at loaded 100; truncation noted inside the file) |
| Hero tab **Pipeline** | `setHeroTab("funnel")` | Shows the funnel card |
| Hero tab **System map** | `setHeroTab("map")` | Shows the WireTopology canvas |
| Funnel tile **Needs review** | `Link` | `/operations/exceptions` (only when value > 0) |
| Funnel tile **Failed** | `Link` | `/operations/exceptions` (only when value > 0) |
| Exception strip banner | `Link` | `/operations/exceptions` |
| "⚠ N open exceptions" pill (System map legend) | `Link` | `/operations/exceptions` |
| KPI card **Needs attention** | `Link` | `/operations/exceptions` |
| **A wire** in the SVG topology | `onWireClick` → `setActiveLane()` | Opens the LaneDrawer (right drawer) |
| A lane row (mobile lane-list) | `onWireClick` | Opens the LaneDrawer |
| **In transit** row (with id) | `Link` | `/inbox/{orderId}` |
| **Supplier health** row | `Link` | `/library/suppliers/{id}` |
| "All suppliers →" link | `Link` | `/library/suppliers` |
| Topology empty-state "Add a supplier →" | `Link` | `/library/suppliers` |
| Topology/funnel/in-transit **Retry** buttons | `refetchOrders()` | Re-runs the orders query on error |
| Checklist **primary CTA** (active step) | `Link` | Step href (`/library/suppliers`, `/upload`, `/inbox/{id}`, `/library/suppliers/{id}?tab=catalog|delivery`) |
| Checklist "Use guided setup" | `onResumeSetup` → `resumeWizard()` | Re-opens the OnboardingWizard modal |
| Checklist "Try a practice order →" | `runSample()` (`useSampleOrder`) | Creates a sample order, routes to it; or "Open your practice order →" if one exists |
| Checklist intermediate "Send a test →" | `Link` | `/library/suppliers/{id}?tab=delivery` |
| Completion card "Done" | `onDismiss` | Marks celebrated (session); links: email intake / API key / add supplier |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **OnboardingWizard** | Modal (`role="dialog" aria-modal`, fixed, scrim `rgba(11,26,47,.45)` + 4px backdrop blur, z-50) | Auto-opens on mount when `onboardingStatus.hasSupplier === false` and not session-dismissed; re-opened by checklist "Use guided setup"; auto-dismissed via `?onboard=skip` | Step 0 choose order direction (2 radio cards) → Step 1 add first supplier (labeled text input + green submit) → "done" closing notice ("Open my setup guide"); step indicator dots at top | Top-right ✕ button; backdrop click; "Open my setup guide" / "Done" button; sets `sessionStorage` dismissed flag (no Esc handler) |
| **LaneDrawer** | Right-side drawer (fixed, 400px / `maxWidth:100vw`, `-8px 0 32px` shadow, z-8999) + dim scrim (`rgba(11,26,47,.3)`, z-8998) | Clicking a wire in the topology canvas (or a lane row on mobile) | "Connection detail" header; Buyer→Supplier card (codes, health label); 3-up stats (Volume / Health / Alerts); "Recent deliveries" list (live supplier-scoped orders, mock crossings, or honest empty state); footer "View all deliveries →" (`/inbox`) + "Connection settings" (`/library/suppliers/{id}`) | ✕ button; **Esc key**; backdrop click; clicking any recent-delivery row or footer button (navigates + closes) |
| Funnel tile / KPI / exception strip / supplier row / in-transit row | Inline `Link` (no overlay) | Click | — | Navigates away in place (not an overlay) |
| `title=""` attributes on window/export/tile/KPI controls | Native browser tooltip | Hover | Short helper text (e.g. "Export contains the most recent 100 of N…") | Mouse-out |
| OnboardingChecklist completion card | Inline card (not an overlay) | Reaching 6/6 in a session that saw it incomplete | Celebration + 3 next-step links | "Done" button (session-persisted) |

Note: There is **no toast system** on this page — all feedback is inline (errors, the practice-order error line, sample "Starting…" label). The CSV export gives no on-screen confirmation; it just triggers a download.

### States
- **Empty (brand-new org):** handled well — the **Onboarding hero** replaces the dashboard with the checklist; the **OnboardingWizard** modal fires first if no supplier. Topology empty (has data but no crossings) shows "No deliveries yet — Add a {supplier} →". In-transit empty: "No orders in flight right now." Supplier-health empty: "No {suppliers} yet."
- **Loading:** route-level `loading.tsx` → `BridgePageLoader` (animated blue→green "wire" mark, reduced-motion-safe — not a bare spinner). In-component: topology shows a pulsing grey rectangle skeleton; funnel shows 5 skeleton tiles; in-transit shows 3 skeleton rows; KPI values show "…" and pulse. Good skeleton coverage.
- **Error:** explicit, honest, and recoverable — topology, funnel, and in-transit each render a "Couldn't load…" message (`role="alert"`) with a **Retry** button (`refetchOrders`). KPIs show "—" and "Live data unavailable". Deliberately does NOT show the onboarding empty state on an error (guarded against `ordersError`). Strong adherence to the "never show healthy when failing" rule.
- **Success/feedback:** mostly state-driven re-render (counts update, exception strip appears/disappears). Wizard shows inline "Saving…" and per-step success advances. No toast confirmations; CSV export is silent.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full layout — KPI strip 4-up (`xl:grid-cols-4`), bottom row 2-up (`xl:grid-cols-2`), funnel 5-up (`lg:grid-cols-5`). WireTopology renders the full SVG canvas (`hidden lg:block`). Content capped at ~1480px and centered, so HD has large side gutters.
- **Tablet 768:** WireTopology switches to the **lane-list** fallback (`lg:hidden`) — stacked buyer→arc→supplier cards, no SVG. KPI strip drops to 2-up; bottom row to 1 column; funnel to 2/3-up. Supplier-health bar (`sm:block`, 160px) still shows at ≥640px. PageHeader actions wrap below the title.
- **Mobile 390:** everything stacks. Funnel 2-up; KPIs 2-up; in-transit rows wrap (PO/buyer/format on top, stage badge drops below); supplier-health hides the 160px bar, keeps name + %. LaneDrawer is `maxWidth:100vw` (full-bleed). OnboardingChecklist collapses its 2-col grid to single column.
- **Known cliffs:** the SVG canvas has `min-w-[760px]` with `overflow-x-auto`, so between `lg` (1024px) and ~1130px the canvas can horizontally scroll inside its card. The checklist grid uses `lg:grid-cols-[minmax(280px,0.85fr)_minmax(430px,1.15fr)]` — at exactly `lg` with a long step description it can feel cramped. KPI strip jumps 2→4 only at `xl` (1280px), so 1024–1279px shows a 2×2 grid that under-uses width.

### Current UX issues
- **Token drift / hard-coded values everywhere.** The screen is built almost entirely from inline `style={{}}` with literal hex (#0B1A2F, #5E6779, #E5E8EE, #B36D14, #FFF8EA…) and fractional px sizes (10.5/11.5/12.5/13). Violates "ONE spacing rhythm" and "ONE type scale" — sizes don't snap to a 4/8 grid and weights/colors are chosen per-call-site. (DESIGN BAR 1, 2)
- **Hierarchy carried by color, not size+weight.** Many secondary lines are #5E6779 on white at small sizes; the `--ink-faint` captions risk falling below 4.5:1. (DESIGN BAR 2)
- **No single status-badge system.** The "In transit" row uses a bare colored stage WORD (no pill), the funnel uses icon+uppercase-label tiles, the supplier-health uses a colored %, the LaneDrawer uses "Healthy/At risk/Down" text, and `StatusJourney`/`StatusCell` define yet another pill set. Five different status vocabularies on one screen. (DESIGN BAR 4)
- **Three near-identical inset-pill segmented controls** (window selector, hero tabs) each hand-rolled with duplicated markup and navy-active styling — should be one shared `SegmentedControl`. (DESIGN BAR 8)
- **More than one primary action competing.** Export, the window selector, the two hero tabs, the exception strip, the checklist green CTA, and multiple linked KPI/funnel tiles all read as roughly equal weight. There is no single visually dominant primary action; the green checklist CTA only appears during onboarding. (DESIGN BAR 7)
- **"In transit" list is not a real table** — it's a `divide-y` of flex rows with no header, no sort, no column alignment; PO/buyer/format/stage don't line up between rows, and the mini-stepper adds vertical noise. (DESIGN BAR 5)
- **LaneDrawer is 100% inline-styled, has an ✕ glyph button with no `aria-label`, and a `✕` text character instead of an icon.** Its "Recent deliveries" mock crossings only render in mock mode; the live path is supplier-scoped (not buyer↔supplier-pair scoped) which is a known honesty caveat. (DESIGN BAR 9, accessibility)
- **OnboardingWizard modal has no Esc-to-close** (only ✕ / backdrop / button), unlike LaneDrawer which does — inconsistent dismissal. (DESIGN BAR: modals need clear close/escape)
- **Icon-only buttons / glyphs** (wizard ✕ uses an SVG with `aria-label="Dismiss wizard"` — good; LaneDrawer ✕ is a bare text glyph with no label — bad). Inconsistent. (accessibility)
- **The "System map" SVG can horizontally scroll** on tablet/small-desktop (`min-w-[760px]`), an awkward in-card scroll. The mobile lane-list is good but the breakpoint switch is abrupt.
- **Mixed temporal bases** are explained in copy ("All time" vs "Last 30 days") but require the user to read fine print to know the four KPIs aren't comparable — the design could make scope a visible chip rather than buried sub-text.
- **No breadcrumb / page is the app root** — fine, but the header's status sub-line packs "Live order view · N connections · N active suppliers" as a single muted run-on rather than discrete labeled stats.

### Redesign recommendations (for Claude Design)
1. **Tokenize the entire screen.** Replace every inline hex + px with the existing CSS variables / Tailwind scale (navy #0B1A2F, violet brand, green #2E8E3A success, amber #B36D14 warn, red #B43838 block; surfaces #FFFFFF/#F6F7FA; border #E5E8EE). Snap all padding/gap/margins to a strict 4/8px rhythm and collapse the fractional font sizes to one type scale (heading 600 / label 500 / body 400). Keep the navy + violet Bridge brand. (BAR 1, 2)
2. **Unify status into ONE badge system.** One pill shape/size/padding with green/amber/red/neutral + icon-or-word, used by the in-transit stage, supplier health, funnel tiles, and LaneDrawer health alike. Consider reusing/upgrading `UnifiedStatusBadge`. Never color-only. (BAR 4)
3. **Make the funnel the single dominant hero and pick ONE primary action.** Elevate the pipeline funnel; demote Export and the window selector to quiet outline/ghost controls. Make "Review exceptions" the clear primary when exceptions exist (it is the coordinator's #1 job) — one green, ≥44px, visually dominant CTA. (BAR 7)
4. **Turn "In transit" into a real, dense table** with a sticky header, one row height, aligned columns (PO# | Buyer | Format | Stage | progress), tabular figures, low-contrast gridlines, hover, and aria-sort — instead of free-flow flex rows. Same density language as the inbox. (BAR 3, 5)
5. **Extract ONE shared `SegmentedControl`** for the window selector and hero tabs (and reuse app-wide). One radius, one active treatment (navy fill), one focus ring, ≥44px targets. (BAR 8, 9)
6. **Rebuild LaneDrawer with the shared drawer primitive:** tokenized, animate-from-trigger, scrim, Esc + ✕ (with `aria-label`) + backdrop close (it already has Esc — keep it as the standard and bring the wizard up to parity). Replace the ✕ text glyph with a Lucide `X` and an aria-label. (BAR: drawers/modals, accessibility)
7. **Give OnboardingWizard Esc-to-close** and a focus trap; align its close affordance and animation with LaneDrawer so all overlays behave identically. (consistency, accessibility)
8. **Surface KPI temporal scope as a visible chip** ("All time" / "Last 30d") on each card instead of fine-print sub-text, so the four headline numbers' bases are legible at a glance and never read as contradictory. (honesty + clarity)
9. **Standardize card chrome:** one radius (`--radius-card`), one border (#E5E8EE), one shadow tier for the KPI/funnel/list cards, and one elevated tier for the drawer/modal — remove the ad-hoc `0 1px 2px rgba(11,26,47,0.04)` repeated literally in ~8 places. (BAR 8)
10. **Fix the tablet topology cliff:** either let the SVG canvas scale down responsively (drop `min-w-[760px]`) or extend the lane-list up into the small-desktop range so there's no in-card horizontal scroll. Keep the mobile lane-list (good pattern). (BAR 10)
11. **Add visible focus-visible rings + hover/pressed states** to every pill, tile-link, and row (many rely on `hover:bg`/`hover:shadow` only). Ensure all interactive controls clear 44px. (BAR 9)
