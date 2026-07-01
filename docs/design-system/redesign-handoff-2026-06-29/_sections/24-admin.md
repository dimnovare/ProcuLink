## 24. Owner Admin — Revenue, Customers & Manual Invoicing — `/admin`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/admin/page.tsx`
- **Key components:**
  - `src/app/(app)/admin/page.tsx` (page; also defines local `MetricCard`, `SortableTh`, `StatusBadge`, `PlanBadge` helpers)
  - `src/app/(app)/admin/CreateInvoiceModal.tsx` (overlay)
  - `src/app/(app)/admin/AdjustLimitsModal.tsx` (overlay)
  - `src/components/bridge/layout/PageShell.tsx` (`variant="wide"`, 1480px)
  - `src/components/bridge/layout/PageHeader.tsx`
  - `src/components/bridge/layout/Card.tsx` (`dense` for metric cards)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile org cards)
  - `src/components/bridge/DSPrimitives.tsx` (`Button`)
  - Data: `src/lib/api/billing.ts` (re-exported via `src/lib/api-client.ts`)
- **Capture URL (mock):** `/admin` — **NOTE:** in mock mode this page renders the **non-access error state**, not the populated dashboard. `getAdminOverview()` / `getAdminOrganisations()` in `src/lib/api/billing.ts` have **no `USE_MOCK` branch** (only `setOrgLimits` is mocked). With `NEXT_PUBLIC_USE_MOCK=true` and no backend, the overview query fails its non-`AdminAccessError` path and the page shows "Could not load the admin overview." To capture the real dashboard you must run against a live API where the signed-in user is on the server-side admin allowlist. Overlay captures below assume a live-admin session OR a temporary mock that returns data.

### What it is & why it exists
This is the **platform owner's** console — it sits outside the per-order `parse → … → deliver → learn` workflow and instead reports on the business running the workflow: monthly/annual recurring revenue, account-status counts, trial→paid conversion, a per-customer table, and two operator actions (create a manual Stripe invoice, adjust an org's effective limits / extend its pilot). The owner opens it to reconcile MRR against Stripe, see which orgs are trialing/past-due/read-only, and manually intervene on a specific customer's billing or caps.

### Who uses it & the primary job
**Operator / platform owner** (not the procurement coordinator who uses the rest of the app; the page is a UX-only shell — the real gate is the backend, which 401/403s every `/api/admin/*` call). The single most important task: **monitor revenue + customer health and take a manual billing/limits action on a specific org** (create invoice, or adjust limits / extend pilot).

### Layout & structure (current)
Wrapped in `PageShell variant="wide"` (max-width `var(--container-wide)` = 1480px; gutter ramp 16→24→34px, vertical 20→28px on the `var(--bg)` canvas). Top to bottom:

1. **`PageHeader`** — title "Admin", subtitle "Revenue, customer health, and manual invoicing for the platform owner.", and a right-aligned **`+ Create invoice`** primary button (`Button variant="blue"` → actually renders brand-green per DSPrimitives, `size="md"`; disabled when there are zero orgs). On mobile the action wraps below the title.
2. **Overview metric grid** — `grid grid-cols-2 gap-3 lg:grid-cols-4` of **8 `MetricCard`s** (each a `Card dense`): MRR (with reconcile sub-line), ARR, Active, Trialing, Trial expired, Read-only, New orgs (mo), Trial → paid. Each card = uppercase 11px faint label, 24px bold tabular-nums value, optional 11.5px sub-line tinted by tone (muted / green "ok" / red "warn").
3. **Customers section** (`mt-7`) — a header row ("Customers" 18px display heading + a faint right-aligned `{n} orgs` / "Loading…" count), then EITHER an error panel, an empty panel, or the data:
   - **Desktop table** (`hidden md:block`, surface card, radius 12, `overflow-x:auto`, `min-width:920px`). Columns: Organisation (name + slug stacked), Plan, Status, MRR (right), 30d orders (right), Suppliers (right), Created, Last activity, Stripe, Actions. Header row has `var(--bg)` background; rows separated by `1px solid var(--surface-2)` top borders. `th` padding 9×14px / 10.5px uppercase 700; `td` padding 10×14px.
   - **Mobile cards** (`md:hidden flex flex-col gap-3`) — one `MobileListRow` per org: name+slug + status badge on top row; a wrapped meta row (plan badge, MRR, orders/30d, suppliers); a faint "Created … · last activity …" line; an actions row ("Adjust limits / extend pilot" + "View in Stripe ↗").
4. **No persistent footer / action bar.** Overlays render at the end of the tree.

Spacing/type/density observations: heavily **inline-styled** with hardcoded font sizes (10.5 / 11.5 / 12.5 / 13 / 18 / 24px) and pixel padding rather than the 4/8 scale; the modals hardcode hex colors (`NAVY = "#0B1A2F"`, `BLUE = "#1E66C9"`, `#56627A`, `#D9DEE8`, `#EEF0F4`, `#FBE3E3`, green `#E2F1E2/#1E6D29`) instead of CSS tokens, diverging from the page's own tokenized badges.

### Data shown
- **`AdminOverview`** (`GET /api/admin/overview`): `mrr`, `arr`, `stripeMrr` (nullable), `reconciled`, `countsByAccountStatus` (record keyed by `active`/`trialing`/`trial_expired`/`past_due`/`read_only`/`cancelled`), `newOrgsThisMonth`, `trialToPaidConversion` (fraction → `pct()`). The MRR sub-line is a reconcile note: null Stripe MRR → muted "Stripe MRR unavailable"; `reconciled` → green "Reconciled with Stripe (€X)"; else red "DB €X vs Stripe €Y — mismatch".
- **`AdminOrganisation[]`** (`GET /api/admin/organisations`): `id`, `name`, `slug`, `plan`, `accountStatus`, `stripeCustomerId` (nullable → "—" or "View ↗" Stripe link to `dashboard.stripe.com/test/customers/{id}`), `stripeSubscriptionId`, `mrrContribution` (`eurCents`), `createdAt` (`shortDate`), `lastOrderActivity` (nullable → `relativeTime`), `orderVolume30d`, `supplierCount`. Sorted client-side by `mrrContribution` desc by default.
- **`CreateAdminInvoiceResult`** (`POST /api/admin/invoices`): `invoiceId`, `hostedInvoiceUrl` (nullable), `status`.
- **`OrgLimitsResponse`** (`POST /api/admin/organisations/{id}/limits`): `effectiveOrderLimit`, `effectiveSupplierLimit`, `effectiveTrialEndsAt`, `orderLimitOverride`, `supplierLimitOverride` (this one IS mocked via `setOrgLimits` in billing.ts).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| `+ Create invoice` button (header) | `setInvoiceOrgId(null); setShowInvoice(true)` | Opens **CreateInvoiceModal** with no preselected org. Disabled when 0 orgs. |
| Metric cards (×8) | None | Static display only — not interactive. |
| `SortableTh` headers: Organisation, Plan, Status, MRR, Created, Last activity | `toggleSort(col)` | Re-sorts the org table client-side; same col toggles asc/desc; arrow glyph (▲/▼/↕) shows state. `aria-label="Sort by {label}"`. (30d orders / Suppliers / Stripe / Actions are **not** sortable.) |
| "View ↗" Stripe link (desktop row) | `target="_blank"` link | Opens that org's Stripe customer page in a new tab (TEST dashboard). "—" when no `stripeCustomerId`. |
| "Adjust limits" button (desktop row) / "Adjust limits / extend pilot" (mobile) | `setLimitsOrg(org)` | Opens **AdjustLimitsModal** for that org. |
| "View in Stripe ↗" link (mobile card) | `target="_blank"` link | Same as desktop Stripe link; only shown when `stripeCustomerId` present. |
| Access-gate CTA ("Go to sign-in" / "Back to dashboard") | `next/link` | Navigates to `/sign-in` (401) or `/bridge` (403). Only in the access-error state. |

**CreateInvoiceModal controls:** Organisation `<select>` (options labelled `name (plan)` + "— no Stripe customer"); per-line Description input, Amount-EUR input (`inputMode=decimal`), Quantity input (`inputMode=numeric`), per-line "✕" remove button (disabled at 1 line); "+ Add line item" dashed button; Currency input (uppercase, default "eur"); live Total (tabular-nums); Cancel / Create invoice footer buttons; on success → green confirmation, Invoice ID (mono), "Open hosted invoice ↗" link, "Done" button. Close "✕" top-right.

**AdjustLimitsModal controls:** Order-limit-override `NumField` (+ "Clear" checkbox), Supplier-limit-override `NumField` (+ "Clear" checkbox), Pilot/trial-window `<select>` (Leave unchanged / Extend by N days / Set an end date), conditional Extend-days input OR date input, "Clear trial end" checkbox (mutually exclusive with the select), error panel, Cancel / Save limits footer; on success → green confirmation with effective orders/suppliers/trial-ends + override rows, "Done". Close "✕" top-right.

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Create-invoice dialog** | Modal (`role="dialog" aria-modal="true"`, fixed inset, `rgba(11,26,47,0.55)` scrim, white panel max-w 560px, `boxShadow 0 24px 60px rgba(11,26,47,0.28)`, body-scroll lock, focus-in on first field) | `+ Create invoice` header button (`setShowInvoice(true)`) | Org `<select>`, repeatable line-item rows (desc/amount/qty/remove), "+ Add line item", currency, live total, validation error panel; on submit → success view (Invoice ID + hosted-invoice link) | Top-right "✕", **Cancel** button, **Esc** key (`keydown` handler), **backdrop mousedown** (`onMouseDown` target check); after success → **Done** button. All call `onClose` → `setShowInvoice(false)`. |
| **Adjust-limits dialog** | Modal (same a11y/scrim/shadow pattern, max-w 520px) | Row "Adjust limits" / "Adjust limits / extend pilot" button (`setLimitsOrg(org)`) | Header shows `org.name · plan · status`; order/supplier override fields with Clear checkboxes; pilot-window select + conditional inputs + Clear-trial checkbox; error panel; on submit → success view with effective limits; calls `onSaved()` → `orgsQ.refetch()` | Top-right "✕", **Cancel**, **Esc**, **backdrop mousedown**; after success → **Done**. All → `onClose` → `setLimitsOrg(null)`. |
| **Native `<select>` dropdowns** | Browser-native dropdown | Clicking the Org / Currency / Pilot-window selects inside modals | OS-rendered option list | Native (selecting an option / blur / Esc). |
| **Native date picker** | Browser-native popover | "Set an end date" → `<input type="date">` in AdjustLimitsModal | OS date picker | Native. |

There are **no toasts, no app-level drawers/sheets, no tooltips, and no row dropdown menus** — all feedback is inline within the two modals. The base page itself navigates in place / re-sorts in place; the only overlays are the two modals (plus native form widgets).

### States
- **Empty (no orgs):** Customers section renders a bordered surface panel "No organisations yet." (the `+ Create invoice` button is also disabled). Metric cards still render with zeroes.
- **Empty (access denied):** dedicated **access-gate view** — centered 480px card, 🔒 emoji in a circle, heading ("Please sign in" for 401 / "You don't have access to the admin area." for 403), explanatory paragraph, and a blue link CTA to `/sign-in` or `/bridge`. Triggered when either query throws `AdminAccessError`.
- **Loading:** **bare text** "Loading admin overview…" (muted, 14px) while `!queryEnabled || overviewQ.isLoading` — **no skeleton, no spinner**. The Customers count shows "Loading…".
- **Error (overview):** if overview errors with a non-access error (or returns undefined), a red-bordered surface panel: "Could not load the admin overview. The API may be unavailable — retry shortly." (**no retry button**). **This is the state mock mode lands in** (see capture note).
- **Error (orgs only):** if only the orgs query fails (non-access), the Customers section shows a red-bordered "Could not load organisations." panel while metric cards still render.
- **Success/feedback:** purely inline inside modals — green confirmation banner ("✓ Invoice created — status X" / "✓ Limits updated for X") then a result detail block + "Done". No global toast. After AdjustLimits success the table refetches via `onSaved`.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** content capped at 1480px and centered; metric grid is 4-up (`lg:grid-cols-4`); full org table visible (min-width 920px, no horizontal scroll until very narrow).
- **Tablet 768:** at the `md` breakpoint the desktop table is shown (`hidden md:block`) but the metric grid is still 2-up (4-up only kicks in at `lg` ≈1024px), so 768–1023px shows a 2-column metric grid above a full-width table that may need horizontal scroll near 768px (table min-width 920px > viewport).
- **Mobile 390:** metric grid collapses to 2-up; org table is replaced by stacked `MobileListRow` cards; header action wraps below the title; modals switch to top-aligned (`items-start`) full-width-minus-16px panels with `max-height 70vh` scroll. Buttons hit the 44px `--tap-min` on small screens.
- **Breakpoint cliff:** the **768–1023px band** shows a 920px-min-width table inside a 768px viewport, forcing horizontal scroll on the table even though it's the "desktop" view — the metric grid (2-up) and table (wide) are out of step because the table flips at `md` but the grid only goes 4-up at `lg`.

### Current UX issues
- **Loading is a bare text line** ("Loading admin overview…"), violating the skeleton requirement (bar 6). No `loading.tsx` exists for the route.
- **Error states have no retry affordance** — both the overview and orgs error panels are dead text; the user must reload the page (bar 6: error = reason + retry).
- **Mock mode shows the error state, not the dashboard** — because the admin GETs have no `USE_MOCK` branch. This makes the page un-demoable without a live admin backend and is a trap for the redesign capture pass.
- **Two badge systems, neither matching the app's unified badge** — `StatusBadge`/`PlanBadge` here are local 10.5px uppercase squared pills, separate from the app's `UnifiedStatusBadge`. They use color but the icon/word rule is satisfied only by text (no icon), and PlanBadge is color-flat neutral, so plan tier carries no visual weight (bar 4).
- **Modals are entirely off-token** — `CreateInvoiceModal`/`AdjustLimitsModal` hardcode hex (`#0B1A2F`, `#1E66C9`, `#56627A`, `#D9DEE8`, `#FBE3E3`, etc.) and ad-hoc radii (8/10/14) + a one-off shadow `0 24px 60px`, diverging from `var(--shadow-modal)`/tokens used elsewhere and from the page's own tokenized badges (bar 8).
- **Disabled-primary uses a custom muted blue** (`#9FB6DC`) rather than the `disabled:opacity-50` convention in `Button`, and the modal submit buttons are hand-rolled `<button>`s, not the `Button` primitive — inconsistent height, focus ring, and hover/pressed states (bars 7, 9).
- **Spacing/type drift** — values like 10.5/11.5/12.5px font sizes and 9px/14px paddings are off the 4/8 rhythm; the page mixes Tailwind classes with inline `style` for the same properties (bars 1, 2).
- **Close "✕" / remove "✕" / Stripe "↗" / sort arrows are glyph characters**, not Lucide icons; the close buttons are ~28px (`h-7 w-7`) — below the 44px tap target, and icon-only without a hover/pressed treatment (bars 9, 10).
- **Access-gate uses an emoji 🔒** rather than a Lucide lock icon, inconsistent with the rest of the icon system.
- **Sortable headers lack `aria-sort`** — they have `aria-label="Sort by X"` and a visual arrow, but no `aria-sort="ascending|descending"` on the `<th>` (bar 5).
- **MRR reconcile mismatch is shown in red** under a metric but the "Active"/"Trialing" cards never warn — health signals (past_due, cancelled, read_only) are buried as plain count cards or as a sub-line ("X past due · Y cancelled"), so a failing business state can read as neutral (bar: never show healthy when something is failing).
- **No "destructive separated / confirm-before-destroy"** treatment: AdjustLimits "Save limits" can shorten a trial or change caps with no confirm step; it's framed identically to the benign invoice flow (bar: destructive actions separated + confirm).
- **No breadcrumb / active-nav indication on the page itself** for a deep operator area (bar: nav active state + breadcrumbs for depth).

### Redesign recommendations (for Claude Design)
1. **Replace bare-text loading with a real skeleton** — 8 metric-card skeletons + a table skeleton (sticky header + ~6 ghost rows). Add a route `loading.tsx` mirroring the `PageShell`+`PageHeader` so the chrome is stable.
2. **Give both error panels a Retry button** wired to `overviewQ.refetch()` / `orgsQ.refetch()`, and state the reason + last-checked time. Keep red `--danger` token, navy/violet brand intact.
3. **Fix the mock path** so the admin GETs return seed data under `USE_MOCK` (mirror the `setOrgLimits` mock), letting the dashboard be captured/demoed without a live admin backend. (Non-design but blocks the handoff capture.)
4. **Unify the badges** — fold `StatusBadge`/`PlanBadge` into the app's one badge system: one shape/size/padding, green/amber/red/neutral semantics, a Lucide icon **or** word (never color alone), tabular figures. Map org statuses: active=green, trialing=blue/neutral, trial_expired/past_due/cancelled=amber/red, read_only=neutral. Give the plan a subtle tier ramp instead of flat neutral.
5. **Tokenize the modals** — convert `CreateInvoiceModal` + `AdjustLimitsModal` to CSS variables (navy/blue/green/danger, `--radius-md`, a single modal shadow token), and swap their hand-rolled footer/close/remove buttons for the `Button` primitive (green primary `Save`/`Create`, ghost/outline `Cancel`, ≥44px, focus-visible ring, hover/pressed). Replace `#9FB6DC` disabled with the standard `disabled:opacity-50`.
6. **Enforce one spacing + type rhythm** — round all paddings/gaps to the 4/8 scale, move inline font sizes to the type scale (heading 600 / label 500 / body 400), and carry hierarchy by size+weight not color; ensure all numeric cells (MRR, ARR, counts, 30d orders, suppliers, %s) stay `tabular-nums` (most already are — keep it).
7. **One table density + sortability** — single row height, low-contrast `gray-200` gridlines, sticky header, zebra/hover, and add `aria-sort` to the active `<th>` alongside the existing arrow. Replace the ↕/▲/▼ glyphs with Lucide `ChevronsUpDown`/`ChevronUp`/`ChevronDown`.
8. **Surface failing business health prominently** — promote past_due / cancelled / read_only and an MRR-mismatch into a clearly amber/red treatment (e.g. a small alert strip above the grid when `!reconciled` or when past_due>0), so the owner never reads a failing state as neutral.
9. **Separate + confirm destructive limit changes** — when AdjustLimits would *shorten* a trial or *lower* a cap, show a confirm step and visually distinguish it (amber), keeping benign extensions one-click.
10. **Replace glyphs with Lucide icons** (Plus for create-invoice, X for close/remove, ExternalLink for Stripe, Lock for the access gate), give icon-only buttons aria-labels (close already has one; remove-line has one — keep), and bump the modal close button to ≥44px tap area with visible hover/pressed.
11. **Resolve the 768px table cliff** — either flip the org list to `MobileListRow` cards through `lg` (so the wide table only appears ≥1024px where the metric grid is also 4-up), or make the table fluid below 920px; keep the metric grid and table breakpoints in step.
12. **Add a fixed-position bottom action bar on mobile for the primary action** (`+ Create invoice`) instead of relying on it wrapping under the title, keeping the one dominant green primary always reachable (bar 7/10).
