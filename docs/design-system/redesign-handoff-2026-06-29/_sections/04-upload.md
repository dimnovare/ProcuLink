## 04. Upload an order — `/upload`

- **File:** `src/app/(app)/upload/page.tsx` (thin wrapper) → renders `UploadWorkbench`
- **Key components:**
  - `src/components/bridge/UploadWorkbench.tsx` (the entire page; ~1,737 lines — dropzone, supplier picker, plan usage, recent uploads, all overlays, batch logic)
  - `src/components/bridge/FileChip.tsx` (format tag: PDF / XLSX / CSV / cXML / EDI / JSON)
  - `src/components/bridge/layout/PageShell.tsx` + `src/components/bridge/layout/PageHeader.tsx` (canonical page chrome)
  - `src/components/bridge/BridgeLoader.tsx` → `BridgePageLoader` (route `loading.tsx`)
  - Local sub-components defined **inside** `UploadWorkbench.tsx`: `XCard`, `StepBadge`, `StepHeading`, `InfoDisclosure`, `UsageLine`
  - Hooks: `useOrderDirection`, `useQueriesEnabled`, `useOnboardingStatus`, `useSampleOrder`
- **Capture URL (mock):** `/upload` (no route params; mock mode renders suppliers `FastParts Inc / ElectroSupply Co / GlobalComponents / PrecisionMfg`, a Pilot billing block 5/20 orders · 1/1 supplier, and 3 `DEMO_RECENT` rows)

### What it is & why it exists
This is the **intake / ingest** entry point of the parse → normalize → validate → review → transform → deliver → learn loop. The coordinator drops a buyer-side purchase order file (any messy shape) and points it at the supplier it must go to; ProcuLink then parses + normalizes it and hands the user off to the Order Workshop (`/inbox/{id}`) to review and ultimately deliver. It exists because procurement teams receive/produce POs in inconsistent formats (CSV, XLSX, PDF, cXML/UBL/Peppol XML, EDIFACT/X12) and need a single reliable "put the file in here" surface that doesn't lie about what it accepts.

### Who uses it & the primary job
**Persona:** procurement coordinator (the buyer who approves a €399 plan without a committee). **Single most important task:** pick a supplier, drop one (or several) order files, and press the green primary CTA to send them for review — landing in `/inbox/{id}`.

### Layout & structure (current)
Top-to-bottom on the grey page canvas (`PageShell` narrow variant, `max-w` 1040px content column; the intake card is additionally wrapped to `max-w-[1040px]`):

1. **`PageHeader`** — H1 "Upload an order" (28–30px, Bricolage Grotesque, 600) + muted sub "Three steps: choose who it goes to, add the file(s), send for review. We parse and normalize any shape."
2. **Conditional Pilot "Book a 15-min demo" banner** — only when `billing.plan === "pilot"` AND `NEXT_PUBLIC_BOOK_DEMO_URL` is set. Grey card, green left-border accent, navy CTA button (opens external URL in new tab).
3. **First-run sample card** (only when `isEmptyOrg`: live mode, 0 suppliers, 0 recent) — rendered ABOVE the intake card; otherwise rendered BELOW it (see step 6).
4. **The stepped intake card** — the centerpiece. White card, radius 12, 1px `#E5E8EE` border, with a 3px top "Bridge edge" gradient (`#1E66C9` blue → `#2E8E3A` green). Inside is a **2-column grid** `grid-cols-1 lg:grid-cols-[minmax(0,340px)_minmax(0,1fr)]`:
   - **LEFT aside (`#FAFBFD`, 340px) — Step ① Choose a supplier:** numbered green `StepBadge` "1" + heading "Choose a supplier" / hint "Where these orders are sent". A native `<select>` (48px, 1.5px border that turns green `#1E6D29` once a supplier is chosen, custom chevron SVG). Below it a white "Routes to → {Supplier}" confirmation chip + a clock-icon note "Buyer fills in automatically once the document is parsed." Then a 1px divider and the **Plan usage** block: "{plan} plan" label + "Ready"/"Processing paused" pill, two `UsageLine` bars (Orders used/limit, Suppliers used/limit, mono tabular figures), and a Pilot end-date line.
   - **RIGHT section — Step ② Add your order file(s):** numbered blue `StepBadge` "2" + heading + hint "Drop one or more — each file becomes its own order". The **dropzone** is a focusable `role="button"` dashed card (1.5px dashed `#CBD0DA`, radius 10, `#FBFCFE` bg) with a 40px blue upload icon, a bold 18px headline ("Drop your order files here" / filename / "{n} files selected"), a 12.5px helper line ("One or more files · CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT/X12) · up to 10 MB each"), a blue **"Browse files"** button (44px), an italic muted caption ("or drop files anywhere in this area"), and (once a single file is picked) the **format-detection pill** + optional PO/line-count line + optional green "We've seen this layout N times before" fingerprint chip. Below the dropzone, a **multi-file batch list** appears when ≥2 files are selected (one row per file with pending → uploading → done/failed status).
   - **FOOTER (full width, white) — Step ③ Send for review:** numbered green `StepBadge` "3" + the **single primary CTA** (full-width green gradient button, 48px min height). Above it sits the `uploadError` banner (429 / supplier-required / upload-failed); below it the honest gating hint ("Choose a supplier in step 1…") and the single-file **pipeline progress** (3 segments: Reading file → Checking format → Preparing review).
5. **Sample card** (established orgs) — rendered here, below the intake card.
6. **Recent uploads card** — only when `recentRows.length > 0`. Header "Recent uploads" + "View all ↗" link to `/inbox`. Desktop: a `<table min-w-[760px]>` (File / Format / Route / Size / Age / Status). Mobile (`lg:hidden`): stacked card buttons.
7. **Tip card** — "✦ AI extraction" (violet label) explaining text-PDF extraction + number reconciliation.

**Spacing/type/density observations:** almost everything is **inline `style={{}}`** with hardcoded hex + non-4/8px values (gap 2.5, `text-[11.5px]`, `text-[12.5px]`, `py-2.5`, radius 6/7/8/10/12 all mixed). Color is hardcoded throughout (`#0B1A2F`, `#5E6779`, `#1E6D29`, `#B43838`, `#B36D14`) rather than CSS tokens, despite tokens existing (`var(--ink)`, `var(--surface)`, `var(--border)` are used only in the mobile recent-row cards). Type sizes drift across 9.5 / 10.5 / 11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 14 / 18 / 28px.

### Data shown
- **Suppliers** — `apiClient.getSuppliers()` → `GET /api/suppliers` (mock: `mockGetSuppliersFn`, 4 hardcoded suppliers). Fields used: `id`, `name`. Drives the `<select>`, "Routes to" chip, `selectedSupplier`.
- **Billing status** — `getBillingStatus()` → `GET /api/billing/status` (mock returns Pilot 5/20 orders, 1/1 supplier). Fields used: `plan`, `canProcessOrders` (→ `isReadOnly`), `ordersThisMonth`, `orderLimit`, `suppliersUsed`, `supplierLimit`, `trialEndsAt`, `isTrialExpired`.
- **Recent orders** — `apiClient.getOrders({ pageSize: 100 })` → `GET /api/orders` (live only; in mock mode the page uses a **hardcoded `DEMO_RECENT` array of 3 rows**, NOT the mock order store). Mapped to `RecentRow`: `id`, `name` (= `poNumber`), `fmt` (from `sourceFormat`), `buyer` (`buyerName`), `supplier` (`supplierName`), `size` ("—" for live), `age` (relative), `status`. Sorted by `createdAt` desc, sliced to 6.
- **Format detection** — `apiClient.detectFormat(file)` → `POST /api/upload/detect-format` (mock: CSV @ 0.92, PO "PO-DETECT-DEMO", 12 lines, seenCount 3). Fields: `format`, `confidence`, `detectedPoNumber`, `estimatedLineCount`, `reasoning[]`, `seenCount`.
- **Onboarding status** — `useOnboardingStatus()` (`["onboarding-status"]`); only `hasUpload` is read, to fire the `first_upload_started` analytics event.
- **Direction labels** — `useOrderDirection()`: swaps "Supplier"→"Customer" display text for inbound orgs (default outbound).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Supplier `<select>` (`#upload-supplier`) | `onChange` sets `supplierId` | Enables CTA; updates "Routes to" chip |
| "Add a {supplier}" link (no-suppliers empty state) | Navigate | `/library/suppliers` |
| Dropzone card (`role="button"`) | Click / Enter / Space | Opens native file picker (`fileInputRef.click()`) |
| Dropzone | `onDragOver` / `onDragLeave` / `onDrop` | Sets `dragging`/`dragReject` visual; drop calls `acceptFiles()` |
| Hidden `<input type=file multiple>` | `onChange` | `acceptFiles()` validates extensions, sets `selectedFile`+`extraFiles`, triggers detection |
| "Browse files" / "Change file(s)" button | Click (stops propagation) | Opens native file picker |
| "i" InfoDisclosure (detection reasoning) | Click toggle | Opens inline popover with `reasoning` joined |
| "i" InfoDisclosure (fingerprint) | Click toggle | Opens inline popover explaining layout recognition |
| Primary CTA ("↑ Upload & review" / "↑ Upload N files" / "Choose a file to upload" / "Processing paused") | Click → `handleUpload()` or `handleBatchUpload()` | Single: upload → animate pipeline → `router.push('/inbox/{id}')`; multi: sequential uploads, stays on page; no file: opens picker |
| Per-file "Reading document… · Open →" (batch row, done) | Click → `openOrder()` | `router.push('/inbox/{orderId}')` |
| "View all in inbox ↗" (batch success) | Navigate | `/inbox` |
| "Try with a sample order →" (sample card) | Click → `sample.runSample()` | `POST /api/onboarding/sample-order` → `router.push('/inbox/{id}?sample=1')` |
| "Book a 15-min demo →" (Pilot banner) | External link (new tab) | `NEXT_PUBLIC_BOOK_DEMO_URL` |
| Recent uploads row (desktop `<tr>` / mobile card button) | Click → `openOrder()` | `router.push('/inbox/{id}')` |
| "View all ↗" (recent card header) | Navigate | `/inbox` |
| uploadError banner CTA (contextual) | Link | `/library/suppliers` (supplier_required) · `/settings` (upload_failed) · `/settings?tab=billing` (limits) |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Native OS file picker | Browser file dialog | Dropzone click/Enter/Space, "Browse files"/"Change file(s)" button, or CTA when no file selected | OS file chooser (filtered by `accept=".csv,.xlsx,.pdf,.xml,.cxml,.edi,.x12,.txt"`, `multiple`) | OS dialog Cancel / file chosen (fires `onChange`) |
| Format-detection reasoning popover | Inline popover (`role="tooltip"`, `InfoDisclosure`) | Click the "i" circle next to the "Detected: …" pill | Detection `reasoning[]` joined with " · " | Click "i" again, outside-pointerdown, or **Escape** |
| Fingerprint explanation popover | Inline popover (`InfoDisclosure`) | Click the "i" inside the green "We've seen this layout N times before" chip | Static copy explaining org-scoped layout recognition | Click "i" again, outside-pointerdown, or **Escape** |
| Upload-error banner | Inline alert panel (`role="alert"`, footer) | Set by `handleUpload`/`handleBatchUpload` on 429 / missing supplier / upload failure / read-only | Title + message + contextual CTA link | Cleared on next upload attempt or successful file (no manual dismiss / no X) |
| Inline file-type error | Inline `role="alert"` text under dropzone | `acceptFile`/`acceptFiles` when an unsupported extension is dropped/picked | "{name} isn't a supported file type. We accept …" | Cleared when a valid file is picked / selection reset |
| Sample-error inline text | Inline text in sample card | `sample.error` after a failed sample POST | Error message | Cleared on next `runSample()` |
| Format-detection pill / batch list / pipeline progress | Inline transient panels (not overlays) | Selecting file(s) / uploading | Detection result, per-file status, 3-stage progress | State change (file cleared, upload completes, navigation) |

**Note:** This page has **NO modal, drawer, dialog, sheet, or toast.** Every transient surface is an inline panel or the native file picker. There is **no "more ways to receive" (email / API / SFTP) disclosure** here despite the focus hint — those channels live in Settings/Connectors, not on `/upload`. The only true overlays are the two `InfoDisclosure` "i" popovers (which DO support Escape + outside-click). All errors are inline; none auto-dismiss or have a close affordance.

### States
- **Empty (first-run org, `isEmptyOrg`):** the sample card is promoted ABOVE the intake card; the supplier `<select>` is replaced by a dashed "No suppliers yet. Add a supplier to send orders to." panel with a link to `/library/suppliers`; recent-uploads card is hidden. CTA stays disabled (no supplier).
- **Loading:** route-level `loading.tsx` → `BridgePageLoader` (the animated buyer→supplier "wire" mark, label "Loading upload workbench…", reduced-motion safe). Within the card: "Loading suppliers…" inline row, "Checking plan limits…" inline row, "Detecting format…" pill.
- **Error:** suppliers fetch error → amber inline panel "Could not load suppliers. Check the API connection and try again." Billing error → amber "Plan status is unavailable…" panel. Upload/429/supplier-required → amber footer banner with CTA. Unsupported file → red inline alert. Detection failure is **silently swallowed** (hint only).
- **Success/feedback:** single-file → 3-stage pipeline animation then auto-redirect to `/inbox/{id}`. Multi-file → per-row "Reading document… · Open →" links + "Orders created…" summary + "View all in inbox ↗". Read-only (expired Pilot) → CTA reads "Processing paused", usage block turns amber. No toast system is used.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** intake card capped at 1040px, centered (large empty gutters on HD). 2-col grid (340px supplier rail + fluid dropzone). Recent uploads = full table.
- **Tablet 768:** still desktop 2-col grid (the `lg:` breakpoint is 1024px, so 768 collapses to **single column** — supplier rail stacks above dropzone). Recent uploads = mobile stacked cards (table is `lg:block`).
- **Mobile 390:** fully stacked. Supplier rail → dropzone → footer CTA all single-column. Dropzone padding shrinks (`px-6 py-10`). Sample card stacks button full-width. Recent uploads = stacked card buttons (route shown as buyer → gradient bar → supplier).
- **Known cliffs:** the supplier rail is fixed 340px until 1024px, so the **768–1023px** range is a single narrow column (rail full-width) — acceptable but the dropzone loses its side-by-side context exactly in the tablet band. No drag-canvas here, so no mapper breakage.

### Current UX issues
- **Token bypass / two systems:** the whole card is hardcoded hex inline styles while CSS tokens exist; only the mobile recent rows use `var(--surface/border/shadow-card)`. Violates the single-source design system and makes dark-mode/brand changes impossible without touching this file.
- **Spacing rhythm drift (Bar 1):** mixed radii (6/7/8/10/12), non-4/8 gaps (`gap-2.5`, `py-2.5`), font sizes spanning 9.5–28px with half-pixel values (`11.5`, `12.5`, `13.5`). No single scale.
- **Status-pill inconsistency (Bar 4):** at least three different pill systems — `STATUS_PILL` map (recent uploads, 6 colors), the usage "Ready"/"Processing paused" pill, the detection confidence dot, the fingerprint chip, and the batch-row text statuses. None share shape/size/padding; some are color-only-ish.
- **Number alignment (Bar 3):** PO numbers, KB sizes, and ages in the recent table are not consistently tabular (only `UsageLine` and the detection PO line use mono); the recent table "Size" is always "—" in live mode (dead column).
- **Empty/dead column:** recent-uploads "Size" column shows "—" for every real order (size isn't tracked server-side) — an always-empty column.
- **No primary-action dominance contract (Bar 7):** there are effectively two strong CTAs competing — the blue "Browse files" inside the dropzone and the green footer CTA — plus the navy sample button; a first-timer can't tell which is "the" action.
- **Errors never dismiss (Bar 6):** the footer `uploadError` banner and inline file error have no X/close; they linger until the next attempt. No toast layer at all.
- **Detection failure is invisible:** if `/upload/detect-format` fails it's swallowed silently — the user gets no "format unknown" signal, only the absence of a pill.
- **Decorative sparkle copy:** the tip card uses "✦ AI extraction" — the design bar elsewhere in the repo flags decorative sparkle copy.
- **Icon-only "i" buttons** are 16px (below the 44px target) though they are keyboard/aria-labelled.
- **Mock vs real divergence:** recent uploads in mock mode show a hardcoded `DEMO_RECENT` set unrelated to the mock order store — confusing for design QA.

### Redesign recommendations (for Claude Design)
1. **Re-token the entire card** — replace every inline hex with the navy/violet token set (`--ink`, `--ink-muted`, `--border`, `--surface`, `--brand-green-deep`, `--brand-blue`, status tokens). Keep the locked Bridge-edge gradient and step colors (supplier-green / buyer-blue) but source them from tokens. This is the highest-leverage change.
2. **Collapse to one spacing + type scale (Bars 1–2):** snap all padding/gap/radius to the 4/8 scale and one radius tier (cards 12, controls 8, pills full); flatten font sizes to the documented heading-600 / label-500 / body-400 scale (kill the 9.5/11.5/13.5 half-steps).
3. **Unify the status-badge system (Bar 4):** one pill shape/size/padding with green/amber/red/neutral + icon-or-word for recent-upload status, usage "Ready/Paused", and batch-row results. Render the detection confidence as that same pill (e.g. green "High confidence", amber "Likely", neutral "Unknown") instead of a bare colored dot.
4. **Establish one dominant primary (Bar 7):** make the green footer "Upload & review" the sole green primary (≥44px, dominant). Demote "Browse files" to an outline/ghost secondary and the sample button to a ghost link-style — so the eye lands on one action.
5. **Add a real toast/inline-confirm layer + dismissible errors (Bar 6):** give the footer error banner and file-type error an X and Escape close; show a success toast on multi-file batch completion instead of only inline text. Surface detection failure as an honest neutral "Couldn't detect the format — we'll still try" pill rather than silence.
6. **Tabular figures everywhere (Bar 3):** PO#, KB, age, counts, usage all `font-feature-settings: tnum` / mono so the recent table and usage bars don't jitter.
7. **Fix the recent-uploads table density (Bar 5):** drop the always-empty "Size" column (or track size), give one row height + low-contrast gridlines, sticky header, and an `aria-sort` affordance; keep the buyer→supplier route cell.
8. **Tablet band (768–1023):** consider dropping the 2-col breakpoint to `md` (768) so the supplier rail + dropzone stay side-by-side on tablet, or explicitly design the stacked tablet order.
9. **De-decorate the tip card:** replace "✦ AI extraction" with a Lucide icon + plain "AI extraction" label; keep the honest reconciliation copy.
10. **Consider surfacing the other intake channels** (email / hosted inbound / API / SFTP) as a genuine "More ways to send orders" disclosure linking to Settings/Connectors — today `/upload` implies file-drop is the only path, which under-sells real capabilities (offer ⇔ works in the other direction).
