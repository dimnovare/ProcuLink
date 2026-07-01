## 10. Inbound Invoices — `/inbound/invoices`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbound/invoices/page.tsx`
- **Key components:**
  - `src/app/(app)/inbound/invoices/page.tsx` (whole page — table, mobile cards, status badge, skeletons, row actions all defined inline here)
  - `src/components/bridge/layout/PageShell.tsx` (wide page wrapper, max-width `var(--container-wide)` ≈ 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` (title "Invoices" + count subtitle + actions slot)
  - `src/components/bridge/layout/Card.tsx` (table surface + empty/error surfaces)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile stacked card body)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (all CTAs and row actions)
  - `src/app/(app)/inbound/invoices/loading.tsx` → `BridgePageLoader` from `src/components/bridge/BridgeLoader.tsx` (route-level Suspense fallback)
  - Data: `getInvoices` / `uploadInvoice` / `approveInvoice` / `downloadInvoice` / `InvoiceDto` from `src/lib/api-client.ts`
- **Capture URL (mock):** `/inbound/invoices` (no ids/query needed — the list query `getInvoices` returns the two mock rows `inv-001`, `inv-002` in mock mode)

### What it is & why it exists
This is the inbound-direction sibling of the outbound PO loop: a flat list of supplier **invoices** the org has received, so a coordinator can review, approve, and export them (and, longer term, reconcile them against the matching purchase order — the empty-state copy promises "review, approve, and reconcile them against your purchase orders"). It sits at the review/approve stage of the inbound workflow rather than the outbound parse→transform→deliver path. A procurement coordinator opens it to clear the pending-invoice queue: confirm an invoice is acceptable (Approve) and pull a CSV copy for their AP/ERP system (Download CSV).

### Who uses it & the primary job
**Procurement coordinator / AP-adjacent operator.** The single most important task is **clearing pending invoices**: scan the list, find rows with status `pending`, and click **Approve** (the per-row green action that only appears while pending). Secondary jobs are uploading a new invoice file (XML/EDI) and downloading an invoice as CSV.

### Layout & structure (current)
Top-to-bottom inside a `PageShell variant="wide"` (centered, max ~1480px, gutter ramp 16→24→34px, vertical padding 20→28px, canvas `var(--bg)`):

1. **Page header** (`PageHeader`) — `<h1>` "Invoices" in display font (Bricolage Grotesque, 28→30px, weight 600, letter-spacing −0.02em). Subtitle line (13px, `--ink-muted`) shows the live count: `"{n} invoice(s)"`, or "Loading…" while fetching in non-mock mode. Right-aligned **actions slot** holds an "Uploading…" status hint (when a POST is in flight) and a green **"Upload invoice"** primary button — but that button is conditionally rendered only when `invoices.length > 0` (it is suppressed in the empty state, which has its own upload CTA in the card).
2. **Notice banner** (conditional, `mb-4`) — a dismissible inline alert; green-left-accent for success, red-left-accent for failure (chosen by string-matching `"failed"`/`"Failed"` in the message). Contains the message text + a `✕` dismiss button.
3. **Body**, which is one of four mutually-exclusive branches:
   - **Loading** (non-mock only): desktop renders a `Card dense` with the full table header (`Invoice #`, `Supplier`, `Date`, `Amount`, `Lines`, `Status`, ``) and three `SkeletonRow`s (animated gray bars at widths 120/140/80/80/60/60/80); mobile renders three `SkeletonCard`s.
   - **Error** (non-mock only): a `Card` with centered "Failed to load invoices" + a green **Retry** button (re-invalidates the query).
   - **Empty**: a `Card` with centered "No invoices yet", explanatory paragraph, and a green **Upload invoice** button.
   - **List**: desktop `Card dense` table + a mobile stacked-card list (`sm:hidden`).
4. **No footer / no action bar.**

**Desktop table** (`hidden sm:block`, `font-size: 12.5`): single `<table>` with a 2px bottom-border header row of uppercase 10.5px labels (`--ink-faint`, tracking 0.06em): **Invoice #, Supplier, Date, Amount, Lines, Status, Actions**. Body rows have 1px `--surface-2` separators (none on the last row), `px-4 py-3` cells. Invoice # is mono + semibold (`--ink`); Supplier/Date are `--ink-muted`; Amount is semibold `--ink`; Lines is faint; Status is the badge; Actions holds the buttons.

**Mobile cards** (`sm:hidden`): each invoice is a `MobileListRow` wrapped in a relatively-positioned div with a 3px green left-accent strip (the Bridge-Layer signature, clipped to the card radius). Card top row = mono invoice number + supplier name (left) and `StatusBadge` (right); a meta row = date · bold total · "{n} line(s)"; then the action buttons.

**Spacing/type/density observations:** values are a grab-bag of hardcoded literals — header cells `py-2.5`, body cells `py-3`, badge text `10.5px`, table body `12.5px`, mobile card `padding:14`, Card dense `padding:12`, notice text `12.5px`, accent strips `3px`. Numbers are NOT tabular (no `tabular-nums`) — only the invoice number is `font-mono`; amounts and line counts use the default proportional font, so the Amount column does not decimal-align.

### Data shown
**Entity:** `InvoiceDto` (one per received supplier invoice). Fields displayed:

| Column | Field | Notes |
|---|---|---|
| Invoice # | `invoiceNumber` (string\|null) | mono, semibold; `—` when null |
| Supplier | `supplierName` (string\|null) | `—` when null; `supplierId` is null in mock data |
| Date | `invoiceDate` (string\|null) | raw ISO date string, unformatted; `—` when null |
| Amount | `totalAmount` (number\|null) + `currency` (string\|null) | formatted via `Intl.NumberFormat("en-EU", currency)`, default EUR, 2 decimals; `—` when null |
| Lines | `lineCount` (number) | raw integer (mobile pluralizes "line/lines"; desktop shows bare number) |
| Status | `status` (string) | mapped to one of `pending` / `approved` / `rejected` badges; unknown values fall back to a neutral pill showing the raw string |
| (not shown) | `id`, `createdAt` | `id` used for actions/keys; `createdAt` never displayed |

**Source:** `getInvoices()` (TanStack `useQuery`, key `["invoices"]`, `staleTime 30s`) → mock: returns `_mockInvoices` (FastParts Inc `INV-2026-001` €2450 pending 3-line; ElectroSupply Co `INV-2026-002` €890.50 approved 1-line) after 400ms; real: `GET /api/invoices`. Mutations: `POST /api/invoices/upload` (multipart), `POST /api/invoices/{id}/approve`, `GET /api/invoices/{id}/download?format=csv` (binary → object URL).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Upload invoice** (header primary, green; only when list non-empty) | `fileInputRef.current?.click()` | Opens the OS-native file picker (hidden `<input type=file accept=".xml,.edi">`) |
| **Upload invoice** (empty-state card, green) | same `fileInputRef.current?.click()` | Same native file picker |
| Hidden `<input type="file">` `onChange` | `uploadMut.mutate(file)` | `POST /api/invoices/upload`; on success invalidates `["invoices"]`, sets success notice "Invoice {n} uploaded successfully.", clears the input; on error sets failure notice |
| **Approve** (row action, outline green; only when `status==="pending"`) | `approveMut.mutate(inv.id)` | `POST /api/invoices/{id}/approve`; sets `approvingId` (button shows "…", disabled); on success invalidates list + notice "Invoice {n} approved."; on error failure notice |
| **↓ CSV** (row action, outline blue; always shown) | `handleDownload(inv.id)` | `GET …/download?format=csv` → builds an `<a download>` and clicks it to save `invoice-{safeName}.csv`; sets `downloadingId` ("…", disabled). In mock/empty the client returns a `#…` sentinel URL → shows notice "Download isn't available in this preview (no file to export yet)." |
| **Retry** (error card, green) | `queryClient.invalidateQueries(["invoices"])` | Refetches the list |
| **✕ Dismiss notice** (icon button in banner) | `setNotice(null)` | Hides the notice banner |

There are no tabs, no sort controls, no filters, no search, no pagination, no row-level menu/kebab, and no row click-through to a detail view. Each row exposes only Approve (conditional) + Download.

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Notice banner | inline panel (in-flow, not an overlay) | Set automatically by any mutation outcome (upload success/fail, approve success/fail, download-unavailable, download fail) | One sentence of status text + `✕` button; green or red left-accent by string match | `✕` button (`setNotice(null)`), or replaced when the next action sets a new notice; **not** auto-dismissing, **not** Esc-closable |
| OS file picker | native browser dialog (not app UI) | "Upload invoice" buttons → `fileInputRef.click()` | Native file chooser filtered to `.xml,.edi` | OS-native (choose file / cancel) — outside React |
| CSV download | native browser file-save (not app UI) | "↓ CSV" → synthetic `<a download>.click()` | Browser save dialog / direct download of `invoice-{name}.csv` | Browser-native |

**No overlays — navigates in place.** This page opens **no** modal, drawer, sheet, dialog, popover, dropdown, tooltip, or toast component. All feedback is the single in-flow notice banner; all "opening" is delegated to native browser dialogs (file picker, download). This is the single most important structural fact for the redesign: there is currently nowhere to see an invoice's line items, totals breakdown, PO match, or rejection reason — the list is terminal.

### States
- **Empty:** Handled. Centered `Card` — "No invoices yet" (15px semibold) + paragraph ("Upload supplier invoices to review, approve, and reconcile them against your purchase orders.") + green **Upload invoice** button. (In mock mode the list is never empty, so this needs `NEXT_PUBLIC_USE_MOCK=false` + an empty real backend to see.)
- **Loading:** Two layers. Route-level `loading.tsx` → `BridgePageLoader` (animated blue→green wire mark + "Loading invoices…") shows during the Next Suspense/navigation boundary. In-component, while the `useQuery` is loading **and not in mock mode**, a table-shaped skeleton (3 `SkeletonRow`s desktop / 3 `SkeletonCard`s mobile) renders under the real header. In mock mode the loading branch is skipped (`!isApiMockMode`), so the 400ms mock delay shows nothing then pops the list.
- **Error:** Handled (non-mock only). `Card` with "Failed to load invoices" + green **Retry**. Note: the reason/status code is **not** surfaced — just a generic line. Mutation errors (upload/approve/download) surface as the red notice banner with the thrown `err.message`.
- **Success/feedback:** Inline notice banner only (no toast). Upload → "Invoice {n} uploaded successfully."; Approve → "Invoice {n} approved."; download-in-preview → the "not available in this preview" line. In-flight feedback: header "Uploading…" hint, and per-row buttons swap label to "…" and disable.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** `Card dense` table inside the ~1480px-capped `PageShell`. Header actions sit top-right on one row with the title. Full 7-column table.
- **Tablet 768:** Still desktop layout — the table/mobile switch is the Tailwind `sm` breakpoint (640px), so at 768px the `hidden sm:block` table is shown and `sm:hidden` cards are hidden. The 7-column table can feel cramped on a narrow tablet but does not restructure; columns are fluid (no min-widths), so long supplier names compete for space.
- **Mobile 390:** Below 640px the table is hidden and the stacked card list renders (invoice#/supplier + status badge, then date·total·lines, then action buttons). `PageHeader` stacks the actions below the title (`flex-col`). Buttons are `h-[44px]` on mobile (tap-min) and shrink to dense desktop heights at `sm`. **Cliff:** the `sm`-only loading/error gating means at exactly 640–767px a tablet still gets the full desktop table; and the empty-state upload button is the only upload entry on mobile when the list happens to be empty (the header button is suppressed at `length===0`).

### Current UX issues
- **No invoice detail view at all.** Rows are terminal — there is no way to open an invoice to see its line items, tax breakdown, or the PO it should match. The empty-state literally promises "reconcile them against your purchase orders," but no reconciliation/3-way-match surface exists. This is the biggest gap (DESIGN BAR: every data row should have a predictable detail/drill-in).
- **Numbers are not tabular and do not align** (BAR #3). Only `invoiceNumber` is `font-mono`; `Amount` and `Lines` use proportional figures and `Amount` is left-aligned, so the money column jitters and never decimal-aligns down the table. Dates are raw ISO strings (`2026-05-01`), not formatted.
- **Local, off-system status badge** (BAR #4). `StatusBadge` is defined inline with its own shape (`rounded` not `rounded-full`, 10.5px, 5px dot) and is explicitly NOT the app's `UnifiedStatusBadge`. It has a dot + word (good) but diverges in radius/size/padding from every other pill in the app, and only covers `pending`/`approved`/`rejected` (unknown statuses degrade to a gray pill of the raw string).
- **Row actions use glyph/emoji + ad-hoc inline colors** (BAR #4/#7/#8). "↓ CSV" uses a unicode arrow, not a Lucide icon; both row buttons override `borderColor`/`color` via inline `style` per-state instead of using `Button` variants. No single dominant primary per screen on the list rows — Approve (green) and Download (blue) read as co-equal.
- **Loading is invisible in mock/preview** — the in-component skeleton is gated on `!isApiMockMode`, so a designer reviewing in mock mode never sees the skeleton; only `loading.tsx` (a different visual) shows momentarily on navigation. Two different loading visuals for one page.
- **Error state hides the reason** (BAR #6) — "Failed to load invoices" with no status code or detail; Retry is the only affordance.
- **Notice banner is brittle and not a real status system.** Success vs error is decided by substring-matching `"failed"` in the message; it is not auto-dismissing, has no icon, no Esc handling, and is the page's only feedback channel (no toast).
- **Spacing/type literals everywhere** (BAR #1/#2). Padding/sizes are magic numbers (`py-2.5`, `py-3`, `12.5`, `10.5`, `14`, `12`) rather than a 4/8 scale; type hierarchy leans on color (`--ink` / `--ink-muted` / `--ink-faint`) more than weight, with faint `--ink-faint` used for the Lines column (contrast risk).
- **`uploadInvoice` has no mock branch** — in mock/preview, clicking Upload and choosing a file fires a real `POST /api/invoices/upload` against `API_BASE_URL`, which will fail and show a red error notice. Mock behavior is inconsistent with `getInvoices`/`approveInvoice`/`downloadInvoice` (which all mock).
- **Header tells a half-truth in mock mode** — the count subtitle is fine, but the "Loading…" sub and the in-component skeleton are suppressed in mock, so the page feels like it has no loading state.
- **Inbound vs outbound mental model is unmarked.** No breadcrumb / section context tells the coordinator this is the *inbound* (received) side; the parent `/inbound` nav grouping isn't reinforced on the page (BAR: nav active state + breadcrumbs for depth).

### Redesign recommendations (for Claude Design)
1. **Add an invoice detail surface (highest impact).** Make each row open a right **drawer** (or `/inbound/invoices/{id}` route) showing header fields, line items led by the **human field name**, totals/tax breakdown, currency, and — critically — the **PO match / reconciliation** status the empty state already promises. Approve/Reject/Download live in the drawer footer with a clear close (X + Esc + scrim, animate from the row). This is the missing core of the page.
2. **Adopt the app's `UnifiedStatusBadge` (or align this one to it).** One pill shape/size/padding, green/amber/red/neutral semantics with a Lucide icon + word, and add an explicit **Rejected** path in the UI (status exists in the badge map but there's no reject action). Never colour-only.
3. **Make every number tabular and right-align money** (BAR #3). Apply `tabular-nums` to Amount, Lines, dates, and the count; right-align the Amount column and decimal-align; format `invoiceDate` to a locale date (e.g. "1 May 2026"). Keep Invoice # mono.
4. **One row-action system.** Replace "↓ CSV" with a Lucide `Download` icon button + aria-label, use `Button` variants (green primary for the dominant action, ghost/outline secondary for the rest) instead of per-state inline `borderColor`/`color`; show a spinner inside the button on pending, not a "…" string. Make Approve the single dominant per-row action and demote Download.
5. **Unify loading + show it in all modes.** Drop the `!isApiMockMode` gate on the skeleton (or always render the skeleton during fetch), and reconcile `loading.tsx` (`BridgePageLoader` wire) with the in-component table skeleton so the page has one coherent loading story.
6. **Surface the error reason + retry** (BAR #6): include the failing status/detail under "Failed to load invoices," keep the green Retry.
7. **Replace the substring-driven notice with the real toast/feedback system** — typed success/error variants, Lucide icon, auto-dismiss with manual close + Esc, so feedback isn't keyed off the word "failed."
8. **Give the list a toolbar:** sortable columns (aria-sort) for Date/Amount/Status, a status filter (pending/approved/rejected), and search by invoice # or supplier — at any real volume the flat unsortable list is unusable. One row height, gray-200 gridlines, sticky header, hover (BAR #5).
9. **Normalize spacing/type to the 4/8 scale and carry hierarchy by weight** (BAR #1/#2): convert the magic literals to tokens, drop reliance on `--ink-faint` for data (contrast), use 600/500/400 weights. Keep navy/violet brand, green=approved/success, amber=pending, red=rejected.
10. **Fix the mock/preview honesty gaps:** add a `USE_MOCK` branch to `uploadInvoice` so preview uploads don't fire real POSTs, and add a breadcrumb/section header making the **inbound** context explicit with predictable back-nav to `/inbound`.
