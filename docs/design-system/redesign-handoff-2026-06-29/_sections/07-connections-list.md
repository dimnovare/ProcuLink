## 07. Connections (Versioned Supplier Connections) — `/connections`

- **File:** `src/app/(app)/connections/page.tsx` (thin Server Component wrapper) → renders `src/components/connections/ConnectionsList.tsx` (the actual `"use client"` view)
- **Key components:**
  - `src/components/connections/ConnectionsList.tsx` — the entire list view
  - `src/components/connections/RevisionStatusBadge.tsx` — the status pill ("Live" / "Draft")
  - `src/components/bridge/layout/PageShell.tsx` — page canvas + max-width container (`variant="wide"`, 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` — title row + subtitle + actions slot
  - `src/components/bridge/layout/Card.tsx` — surface used for the error and empty states
  - `src/components/bridge/EmptyState.tsx` — empty-state body (Mark + title + sub + action)
  - `src/components/bridge/DSPrimitives.tsx` — `Button` primitive used for header action / retry
  - `src/hooks/useQueriesEnabled.ts` — gate that lets the TanStack query run (mock / QA-bypass / signed-in)
  - Data: `listConnections` in `src/lib/api-client.ts` (`mockListConnections` in mock mode, `realListConnections` → `GET /api/connections`); types in `src/lib/api/types.ts` (`ConnectionSummary`).
  - *Not rendered by this page but reached by it:* the detail route `/connections/[connectionId]` renders `ConnectionDetail.tsx`, which (via `useConnectionRevisions.ts` + `ConnectionLifecycleUI.tsx`) owns the row-action verbs (Edit mapping / Make live / Test / Restore) and the make-live confirm dialog. The **list page contains none of those** — see "What opens / what closes".

- **Capture URL (mock):** `/connections` (mock mode seeds two rows from `MOCK_SUPPLIERS.slice(0,2)`; row ids are `conn-11111111-1111-1111-1111-111111111111` = "FastParts Inc" with **Live v1**, and `conn-22222222-2222-2222-2222-222222222222` = "ElectroSupply Co" with **Draft / Not live yet**). Detail capture (if needed) uses those ids, e.g. `/connections/conn-11111111-1111-1111-1111-111111111111`.

### What it is & why it exists
This is the directory of **versioned Supplier Connections** — one row per supplier integration, where a "connection" bundles that supplier's input mapping, output template/format, delivery channel, and item-code mappings into one versioned unit (draft → tested → live → previous). It sits at the **learn/remember** end of the parse→normalize→validate→review→transform→deliver→learn workflow: it's where the reusable, reproducible per-supplier setup lives so every future order delivers the same proven way. A coordinator opens it to see, at a glance, which suppliers are wired up, which version is live, and which still need to be published.

### Who uses it & the primary job
Primary persona: **procurement coordinator** (with the integration expert as the power user behind the detail screen). The single most important task on *this* page is **triage + drill-in**: scan which connections are Live vs still Draft, then click into the one you need to edit, test, or make live. All the actual lifecycle actions happen one level down on the detail page; the list is a launchpad.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px, gutter 16→24→34px, vertical padding 20→28px, page canvas `var(--bg)`):

1. **PageHeader** — `title="Connections"` (Bricolage Grotesque, 28→30px, weight 600, `var(--ink)`), `sub` = "Each supplier integration — input mapping, output template, delivery and item codes — bundled and versioned" (13px, `var(--ink-muted)`). Right-aligned **actions slot** holds a single secondary `Button` "Manage suppliers" → `/library/suppliers`. Header has `mb-5/mb-6`; on mobile the action wraps below the title.
2. **Body** — one of four mutually-exclusive blocks:
   - **Loading:** a `flex flex-col gap-2.5` stack of **3 pulse skeleton bars**, each `height: 76px`, `border-radius 8px`, background+border `var(--border)`, `animate-pulse`, wrapped in `aria-busy="true" aria-label="Loading connections"`.
   - **Error:** a centered `Card` (the one canonical surface) with a red 13px semibold "Could not load connections", a 12px muted "Check the API connection and try again.", and a secondary **Retry** `Button` calling `refetch()`.
   - **Empty:** a `Card` with `min-h-[360px]` centering an `EmptyState` (Mark glyph, "No connections yet", context sub, and a navy action button "Go to Suppliers").
   - **List:** a bare `<ul>` (`flex flex-col gap-2.5`, no bullets) of **row cards**. Each `<li>` is a single full-width `<Link>` to `/connections/{id}`, styled as a card: `var(--surface)` bg, 1px `var(--border)`, a **3px green left accent edge** (`var(--brand-green)`, the Bridge-Layer signature), `var(--radius-md)`, `padding 14px 16px`, `var(--shadow-card)`, `min-height var(--tap-min)`, `transition box-shadow 120ms`. Inside each row: a flexible **identity column** (left) and a fixed **meta cluster** (right), stacking on mobile (`flex-col` → `sm:flex-row sm:items-center`).
     - Identity: supplier **name** (14px, weight 600, `var(--ink)`, truncated) on line 1; a 12px muted sub on line 2 that reads either "Live version **v{N}** · since {date}" (name + version bolded `var(--ink)`) or italic "Not live yet".
     - Meta cluster: a `RevisionStatusBadge` ("Live" green / "Draft" neutral) + a faint chevron `›` (`var(--ink-faint)`, 16px, `aria-hidden`).
3. **No footer / no sticky action bar.**

Density/spacing/type observations: spacing is mostly on a clean 4/8 rhythm (gap-2.5 = 10px, padding 14×16), but the row uses **mixed pixel literals + Tailwind classes** (inline `padding: "14px 16px"`, `gap-2.5`, inline `min-height`), and the type sizes use odd fractional literals elsewhere in the family (12.5px in Card). Numbers (version, date) are **not** forced to tabular figures.

### Data shown
Entity: **`ConnectionSummary`** (one per supplier in V1). Source: `listConnections()` → mock `mockListConnections` or real `GET /api/connections` (org-scoped server-side via the Clerk JWT). Fields actually rendered:

| Field | Rendered as |
|---|---|
| `name` | Row title (supplier name) |
| `activeVersionNo` (number\|null) | "Live version v{N}" — when null → italic "Not live yet" |
| `updatedAt` (ISO) | "since {localized date}" via `formatDate` (`toLocaleDateString`, "—" on null/invalid) |
| `activeRevisionId` (string\|null) | drives `liveStatus()` → badge "Live" (published) vs "Draft" |
| `id` | row `<Link href>` target |
| `supplierId`, `createdAt` | present in payload, **not displayed** |

The badge string the user reads is humanized: internal `published` → **"Live"** (green), absence of a live revision → **"Draft"** (neutral). `RevisionStatusBadge` also knows `test`→"Tested" (info/blue) and `archived`→"Previous" (neutral) but the list only ever passes `published`/`draft`.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| "Manage suppliers" button (header, secondary) | `router.push("/library/suppliers")` | Navigates to Suppliers library |
| Connection **row** (entire `<Link>`) | click / Enter | Navigates to `/connections/{id}` (detail page) |
| "Go to Suppliers" button (empty-state action) | `router.push("/library/suppliers")` | Navigates to Suppliers library |
| "Retry" button (error state) | `refetch()` | Re-runs the `["connections"]` query |
| Status badge / chevron | none (display only, `aria-hidden` chevron) | — (part of the row link) |

### What opens / what closes
**No overlays — navigates in place.** The Connections **list** page opens **zero** modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts. Every actionable element is either a router navigation (row link, header button, empty-state button) or a query refetch (Retry). There is no row-action kebab menu and no make-live confirm on this screen.

The lifecycle overlays the FOCUS HINT mentions (Make live / Restore / Discard confirm dialog, the inline notice banner) live **one level down** on the detail route and are documented there: `ConnectionLifecycleUI.tsx` exports `ConnectionConfirmDialog` (a `role="dialog" aria-modal` centered/bottom-sheet, scrim `#0B1A2F66`, opened by the detail page's per-revision verbs, closed by Cancel / confirm action) and `ConnectionNotice` (a `role="status"` inline banner). They are *not reachable from this page* except by first clicking into a row. (For completeness in the handoff this is the most important fact about the list page: it is a pure launchpad.)

### States
- **Empty:** Handled. `Card` (`min-h-[360px]`) + `EmptyState`: Mark glyph, "No connections yet", and a sub that differs by mode — mock: "Connections appear here once a supplier integration exists." / real: "A connection is created the first time you configure a supplier. Add a supplier and set up its mapping, output and delivery — it becomes a versioned connection you can publish and roll back." Plus a navy "Go to Suppliers" action. This is a real next-action empty state. (Minor: the empty-state action button is a bespoke navy `<button>` inside `EmptyState`, not the shared `Button` primitive.)
- **Loading:** Handled with **skeletons** (3 pulse bars, 76px each), not a bare spinner. There is **no** `loading.tsx` route file — loading is purely the `isLoading` branch inside the component, gated by `useQueriesEnabled()` (so before Clerk is ready the skeleton can persist).
- **Error:** Handled. Red title + muted reason + **Retry** button (`refetch`). Reason is generic ("Check the API connection and try again.") rather than surfacing the actual `ApiHttpError` status.
- **Success/feedback:** None on this page (no toasts/inline confirmations) — feedback only happens after navigating into a row.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Container capped at 1480px and centered; rows are full-width single-column cards (the list is one column at all widths — it never becomes a multi-column grid or a true table). Header title 30px, action right-aligned.
- **Tablet 768 (`sm`):** Rows are `sm:flex-row sm:items-center` (identity left, badge+chevron right). Header still title-left / action-right.
- **Mobile 390:** Header stacks (`flex-col`), action wraps below title. Each row stacks **vertically** (`flex-col gap-3`): name + sub on top, then the badge/chevron meta cluster below — meta cluster uses `flex-wrap`. Tap target honored via `min-height: var(--tap-min)`. No drag/mapper canvas here, so nothing breaks on mobile.
- **Known cliffs:** none structural. The page is intentionally simple; the only responsive nit is that the meta cluster on a narrow row left-aligns under the name (chevron loses its "far right" affordance once stacked).

### Current UX issues
- **Not a table, just a stack of link-cards.** For a directory the user will scan/sort (which are live? when last changed? which supplier?), there's **no column structure, no sortable header, no aria-sort, no count**. Violates DESIGN BAR #5 (one table/list density with sortable affordance). At scale (many suppliers) this becomes hard to scan.
- **No counts / no filters / no search.** The header says "Connections" but never says *how many*, and there's no filter for Live vs Draft. A coordinator with 20 suppliers can't quickly find the un-published ones.
- **Numbers aren't tabular.** Version ("v{N}") and the "since {date}" use the default proportional figures; dates are also locale-`toLocaleDateString` so width/format jitters row-to-row. Violates DESIGN BAR #3.
- **Mixed styling system.** Rows are hand-styled with inline `style` objects (padding, border, accent, shadow, min-height) instead of the shared `Card` primitive the empty/error states use — two different "card" implementations on one screen. Violates DESIGN BAR #8 (consistent cards/elevation). Spacing mixes Tailwind classes and pixel literals (DESIGN BAR #1).
- **Green left-edge on every row reads as "all healthy/success".** The 3px `var(--brand-green)` accent is applied to *every* row including **Draft / Not live yet** ones — green is the success/output color, so a never-published draft visually signals "good to go". Borderline "showing healthy when it isn't." Violates the status-color discipline (DESIGN BAR #4 + the "never show healthy when something is failing" rule).
- **Status conveyed partly by a generic chevron.** The badge is fine, but the only "go here" affordance is a faint `›` glyph; there's no hover elevation feedback defined beyond a `box-shadow` transition with no actual hover shadow value set, so hover state is effectively invisible. Violates DESIGN BAR #9 (visible hover/pressed).
- **The only header action points away from the page.** The sole CTA is "Manage suppliers" (navigates to /library/suppliers) — there is **no primary action to create/add a connection here**, which is confusing given a "connection is created when you configure a supplier." No single dominant primary action (DESIGN BAR #7).
- **Error message is generic.** It never differentiates 401/403/timeout/500 — same copy for all (DESIGN BAR #6, error reason).
- **Empty-state button is a one-off** navy `<button>`, not the `Button` primitive — third button style on the page (header secondary, retry secondary, empty-state navy). Inconsistent primary-action treatment.

### Redesign recommendations (for Claude Design)
Keep navy `#0B1A2F` + violet Bridge-Layer brand; green=live/success, neutral=draft. Ranked:

1. **Promote to a real, scannable table/list (DESIGN BAR #5).** One row height, low-contrast `gray-200` gridlines, sticky header, hover row tint, and an aria-sort sortable header. Columns: **Supplier** (name) · **Status** (badge) · **Live version** · **Last changed** · trailing chevron. Keep it card-stacked on mobile (DESIGN BAR #10) but give desktop true columns so version/date align.
2. **Fix the green-edge semantics (DESIGN BAR #4, "never show healthy when it isn't").** Drive the left accent off status: green only for **Live**, neutral/grey for **Draft / Not live yet**, amber if a draft exists on top of a live version (i.e. "changes waiting to go live"). Never paint an unpublished draft green.
3. **Add count + Live/Draft filter + search (DESIGN BAR #6 affordances).** Header subtitle becomes "{n} connections · {m} live" with tabular figures; a simple segmented filter (All / Live / Draft) and a name search box. This is the highest-value scan affordance for a coordinator with many suppliers.
4. **Unify the surfaces (DESIGN BAR #8).** Render rows with the same `Card` primitive (or one shared row component) used by the empty/error states — one radius, one border color, one shadow tier. Remove the inline-style duplication and snap all spacing to the 4/8 scale (DESIGN BAR #1).
5. **Tabular figures everywhere (DESIGN BAR #3).** Version numbers, dates, counts in `font-variant-numeric: tabular-nums`; format the date consistently (e.g. "12 Jun 2026") rather than raw `toLocaleDateString`.
6. **Give the page a real primary action (DESIGN BAR #7).** Either a dominant green "New connection / Add supplier" primary CTA (>=44px) in the header with "Manage suppliers" demoted to ghost/outline, or — if connections are truly only born from suppliers — make that explicit in copy and keep one clear primary.
7. **Visible hover + focus-visible (DESIGN BAR #9).** Define an actual hover elevation/shadow + background tint and a focus-visible ring on each row link (currently the `box-shadow 120ms` transition has no target shadow). Ensure full row is keyboard-focusable with a clear ring.
8. **Specific error copy + retry (DESIGN BAR #6).** Differentiate auth vs network vs server errors and surface the status; keep the Retry button.
9. **Empty-state polish:** replace the bespoke navy `<button>` with the shared `Button` primitive so the page has exactly one button system, and make "Go to Suppliers" the same visual weight as the chosen primary action.
