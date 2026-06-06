# ProcuLink UI/UX Audit — Consolidated Fix Report

**Scope:** Bridge design system app (frontend `project-proculink`, backend `ProcuLink`). 7 audit groups (shell, dashboard, inbox, order-detail, upload, suppliers, settings, operations) merged and deduped.
**Bar:** simple enough for a 5-year-old, powerful for a 30-year purchasing veteran. Locked Bridge system (navy/violet/green, rails, top-edge accent, reduced-motion, ONE experience + Cmd+K, offer↔works honesty).
**Verification:** All five P0s and the highest-impact P1s were checked against live source before writing this report (file/line confirmed accurate).

**Totals:** 79 findings → after dedupe/merge: **6 P0 · 27 P1 · 31 P2 · 15 P3.**

---

## CROSS-CUTTING THEMES (fix once, fix many screens)

These systemic issues generate the bulk of individual findings. Prioritize the root fix.

1. **Dead/decorative controls that violate offer↔works (the dominant theme).** A large fraction of findings are inputs/buttons/toggles that look interactive but no-op or are never sent to the backend: Upload `Output template` + `Processing mode` (with a *false scary warning*), SpineReview inline field edits / `Save draft` / `XML|JSON` toggle / AI `Edit` button / Accept-Reject, global Validation Rules CRUD, Mapping `Description` field + Export scope/XLSX selects, LaneDrawer `Connection settings`, sidebar workspace switcher. **Root rule for the fix pass: every control either does what it says or is removed/relabeled. No exceptions.**

2. **Staged/demo data & internal literals leaking to real users.** Sidebar footer `Pipeline healthy · 12/min`, `alert to MK` in the send dialog, the `AI` inbox badge (mock-only), buyer `Volume /wk` + `This week` + `Suppliers reached` fabricated columns, supplier-list all-dashes columns, hardcoded confidence percentages (99/95/75), hardcoded cXML output preview. **One grep pass for staged literals + a "does this number come from the API?" check on every displayed metric.**

3. **Per-org direction relabelling (Supplier↔Customer) applied unevenly.** `useOrderDirection` is consumed by the dashboard legend and SupplierDockList but ignored by LaneDrawer, OnboardingWizard, UploadWorkbench, MagicMappingPreview, and the whole SupplierDockProfile. Inbound-mode orgs get a split-brain UI. **Fix: audit every user-facing "Supplier"/"Buyer" string and route it through `useOrderDirection` labels.**

4. **Mock-mode is the only mode with real signal.** Filter chip counts, header "N need review · N failed", the AI badge, LaneDrawer recent deliveries, supplier mappings tab table — all render only under `isApiMockMode`, so the *demo* is informative and the *paying customer* sees flat/empty screens. Backends (`GET /api/orders/summary`, `getSupplierMappings`, `getDeliveryConfig`) already exist. **Fix: wire the live queries that the mock path already proves out.**

5. **Reduced-motion is canonical but two animations opt out of it.** SVG SMIL `<animateMotion>` topology dots and the `.link-spine` topbar sweep keep animating under `prefers-reduced-motion: reduce` (CSS `animation:none` can't stop SMIL; the spine class isn't in the reduced-motion block). Accessibility + brand-rule violation.

6. **Navigation/labels don't match destinations.** Exceptions & Health pages have no nav/Cmd+K entry; "Dashboard" nav → "Order topology" H1/breadcrumb/title; inbox "Delivering"/"Failed" chips return the wrong rows; "Upload & send" doesn't send. **One naming pass to make label = destination = behavior.**

7. **Keyboard/a11y gaps on the densest power surfaces.** Inbox sort headers, dropzone, confirm dialogs, radio groups, SpineReview shortcuts (undiscoverable) — the 30-yr veteran lives on the keyboard and these are mouse-only or unannounced.

---

## P0 — BROKEN / BLOCKS / EMBARRASSING (ship blockers before any pilot)

- **[P0][bug] Bulk "Send selected" sends unauthenticated requests — always 401 in prod** — `InboxView.tsx:456-466` builds a raw `fetch(.../api/orders/{id}/redeliver)` with only `Content-Type`, no Clerk auth header, bypassing `api-client` + `fetchWithTimeout`. The endpoint is Clerk-protected, so every bulk send fails for real users while "working" in mock. **Fix:** replace the inline fetch with `apiClient.redeliverOrder(id)` inside the `Promise.all` map (catch→false). *(Verified.)*

- **[P0][honesty] Order-review inline edits (PO#, date, buyer, currency) are never saved — original data is delivered** — `SpineReview.tsx` `fieldValues` is local React state; `handleConfirm` (L1550-1637) calls only `transformOrder`/`redeliverOrder`, never `resolvePurchaseOrder`/`saveMappings`. A reviewer corrects a misparsed buyer, sees the "← edited" badge, clicks Send → the **unedited** payload goes to the supplier. **Fix:** persist edits via a real PATCH/resolve on commit + refetch, OR make those fields read-only until persistence exists. Do not show an edit UI the backend ignores. *(Verified.)*

- **[P0][honesty] AI Accept/Reject can't actually resolve a line — Send stays blocked with no path forward** — `SpineReview.tsx` Accept writes only to local `acceptedSubnodes`; the send guard reads `order.lines.some(l => l.needsReview)` from server truth (L1554) and `exceptionCount` checks `fieldValues` not `acceptedSubnodes`. Accepting every suggestion never decrements the count and never unblocks send; there is **no working way on this screen to clear a needs-review line.** **Fix:** wire Accept to a real line-resolution mutation (`resolvePurchaseOrder` with the chosen code) + invalidate the order query so guards recompute from server. *(Verified.)*

- **[P0][honesty] Upload "Output template" + "Processing mode" are dead controls, and Auto-process shows a FALSE scary warning** — `UploadWorkbench.tsx:1151` `template` and `1180-1211` `mode` are local state never sent to `uploadPurchaseOrder` (file+supplierId only). "SAP IDoc ORDERS05" isn't a real output anywhere. The Auto-process branch renders "Auto-process will send to the supplier without human review" — **factually false** (every upload still goes to review). **Fix:** remove both controls and the warning; output is supplier-config-driven. *(Verified.)*

- **[P0][honesty] Global "Validation rules" screen is full CRUD that never validates or blocks anything** — `ValidationRules.tsx` header promises "Block bad orders before they reach a supplier"; backend `ValidationRule.cs` has **no executable condition** (only Name/Description/Severity/Entity/Enabled/AutoBlock), and `IValidationRuleService` is referenced **only** by its own controller + DI — never by transform/delivery. The *only* working validation is the per-supplier `AcceptanceTab`. Two competing "Validation rules" surfaces; one silently no-ops. **Fix:** until executable, relabel global as a descriptive "Rule catalog — enforcement is per supplier", link to the supplier tab, and hide the Block/Warn/Auto-block affordances that imply gating. *(Verified.)*

- **[P0][ux] Exceptions & Operations Health — the two "fix the trouble" screens — have no sidebar or Cmd+K entry** — `BridgeSidebar.tsx:46-52` Operations group lists only Delivery log/Connectors/Webhooks; `CommandPalette.tsx:89-95` omits both. `/operations/exceptions` and `/operations/health` are reachable **only by typing the URL** (the lone in-app link is a tile on the health page). For a product whose headline value is "reliable outbound PO processing," operators can't navigate to where blocked orders and a dead Worker surface. **Fix:** add Exceptions (with open-count badge) + System health to the Operations NAV and add matching Cmd+K actions; put Exceptions first. *(Verified.)*

---

## P1 — CLEAR UX / CONSISTENCY / HONESTY

- **[P1][honesty] Exceptions "Resolve" button doesn't fix the order and the exception reappears** — `exceptions/page.tsx:308` → `SetStateAsync` (`OrderExceptionService.cs:126`) only flips the row to `resolved`; the order stays blocked. `ReconcileAsync` dedup (L60/L81) only checks `open`/`ignored`, so the next pipeline touch recreates the same open exception. False sense of clearing. **Fix:** for codes that can't be cleared here (unresolved_mapping/transform_failed/delivery_failed/supplier_rejected/dead_letter) make the primary action "Open order" and let Reconcile auto-resolve; OR have dedup also respect recently-resolved rows. *(Verified.)*

- **[P1][bug] Inbox "Failed" chip misses 4 of 5 failure statuses** — `InboxView.tsx:212` sends `failed`; backend `OrderService.cs:833` is exact-match. Red "Failed" pill collapses {failed, transform_failed, delivery_failed, delivery_dead_letter, rejected_by_supplier} but the chip filters one. The most important triage filter silently under-reports. **Fix:** treat `status=failed` as an IN-bucket over all five (or pass a `failedBucket` flag).

- **[P1][bug] Inbox "Delivering" chip returns rows labeled "Ready"** — `InboxView.tsx:210` filters `ready_to_deliver`, which `mapStatus` renders as the green "Ready" pill; there is no persisted `delivering` status. **Fix:** rename the chip "Ready to send" or drop it.

- **[P1][honesty] AI "Edit" button is a byte-identical duplicate of Accept** — `SpineReview.tsx:644` `onClick={() => onAcceptSubnode(sn.id)}` is the same handler as Accept (L636). A reviewer wanting to override the AI code instead silently accepts it. **Fix:** make Edit open an inline input prefilled with the suggested code, or remove it. *(Verified.)*

- **[P1][honesty] Grand total in header/sticky/dialog ignores backend `grandTotal`** — `SpineReview.tsx:1470` `dialogGrandTotal` recomputes raw Σ(price×qty) shown in header (L1735), sticky bar (L2136), and confirm dialog (L1129), while TotalsSummary uses `resolvedGrandTotal(order)` (L84). On real POs with tax/discount the confirm dialog shows a total contradicting the document. **Fix:** use `resolvedGrandTotal(order)` everywhere a grand total renders.

- **[P1][ux] SpineReview "Save draft" saves nothing and admits it only after click** — `handleSaveDraft` (L1639) just sets a notice confessing drafts aren't kept. Combined with un-persisted edits, users lose corrections on navigate. **Fix:** implement draft persistence or remove the button. *(Verified.)*

- **[P1][ux] Order review caps line items at first 10/12 — line 11+ is invisible but can still block send** — `SpineReview.tsx:113` `slice(0,10)`, DocumentAnatomy/OutputPreview `slice(0,12)`. For the ICP (many lines/PO) an unresolved line beyond 10 gets no AI card/Accept control yet still trips the server `needsReview` guard — user stuck with no visible cause. **Fix:** render/virtualize all lines, or float every needs-review line to the top.

- **[P1][honesty] SpineReview output preview is a hardcoded cXML scaffold + dead XML|JSON toggle** — `L985-989` toggle segments are `<span>`s with no onClick; `L1006-1073` always renders cXML even when the supplier output is CSV/UBL/EDIFACT/X12. The "preview" can't match what's delivered. **Fix:** drive from the real artifact or relabel "canonical mapping preview" and drop the fake toggle.

- **[P1][honesty] "alert to MK" internal literal leaks into the customer send dialog** — `SpineReview.tsx:1159` "3 retries · 30-min intervals · alert to MK". **Fix:** replace with generic copy ("we'll email you") and verify the retry numbers against backend config.

- **[P1][ux] SpineReview power shortcuts (A=accept, C=confirm) are completely undiscoverable** — `L1646-1662` binds global single keys with no kbd hint/tooltip/palette entry, and fires even when not focused in the review. **Fix:** add `Accept (A)`/`Send (C)` kbd hints, register in Cmd+K, scope the listener.

- **[P1][honesty] Inbox "AI" badge only lights up on mock data** — `InboxView.tsx:258-273/798-813`; live `summaryToRow` hardcodes `assigned:"—"`. Dead chrome for all real customers. **Fix:** wire to a real AI-extracted/has-suggestions flag on `OrderSummary`, or remove.

- **[P1][inconsistency] Inbox empty state says "Your inbox is clear" even when a filter returns 0** — `InboxView.tsx:764/979` heading is hardcoded; filter-to-Failed with 0 matches falsely claims no work and offers only "Upload". **Fix:** branch on active filter/search → "No matching orders" + "Clear filters".

- **[P1][ux] Inbox shows no action counts in live mode** — chip badges + header "N need review · N failed" are gated on `isApiMockMode` (`InboxView.tsx:615/710`). `GET /api/orders/summary` (byStatus) already exists. The single most important question ("what needs me?") is answered only in the demo. **Fix:** call `getOrdersSummary()` and surface real counts live.

- **[P1][bug] Dashboard topology pulse dots ignore prefers-reduced-motion** — `WireTopology.tsx:296-309` SVG SMIL `<animateMotion>` on classless `<circle>`; the reduced-motion CSS block can't stop SMIL. Vestibular/a11y + brand-rule violation. **Fix:** gate `<animateMotion>` on a JS `matchMedia('(prefers-reduced-motion: reduce)')` check.

- **[P1][honesty] Dashboard supplier health labeled "last 30 days" but is all-time** — `BridgeDashboard.tsx:848`; backend `DashboardController.cs:96` has no date filter and the window selector doesn't affect it. A veteran reads it as a 30-day SLA. **Fix:** relabel "all time" or add a `CreatedAt >= now-30d` filter.

- **[P1][ux] Dashboard topology + In-transit show the onboarding empty state on API error** — `BridgeDashboard.tsx:536-574/795` have no error branch; on `ordersError` an established org is told "No deliveries yet — Add a supplier." Alarming on a demo backend hiccup. **Fix:** add an explicit error branch ("Couldn't load your topology — retry"); reserve onboarding empty for success+0 rows.

- **[P1][honesty] LaneDrawer "Recent deliveries" is mock-only (always empty live) + dead "Connection settings" button** — `LaneDrawer.tsx:317-323/406`. Clicking a wire with 12 orders shows none; the settings button has no onClick. **Fix:** fetch real orders for the buyer↔supplier pair (or remove the section live); wire the button to `/library/suppliers/{id}`; drop MOCK_CROSSINGS from prod.

- **[P1][honesty] Settings → Connectors "Open Zapier"/"Open Make.com" link to unpublished listings (404)** — `settings/page.tsx:1112/1134`. SUBMISSION.md says "ready when API is live"; CLAUDE.md freezes the Zapier/Make layer. **Fix:** remove the external links, lead with the working custom-webhook + REST ingress path ("native apps coming soon").

- **[P1][honesty] Org slug + ingress URL never shown, but required to use the API/connectors** — `settings/page.tsx` ApiKeysSection (808-1048) shows only the key prefix. SUBMISSION.md tells users to find the slug here. Customer creates a key but can't find their endpoint. **Fix:** add a read-only "Your ingress endpoint" row (slug + `{API_BASE}/api/ingress/<slug>/orders` + `X-ProcuLink-Key`, copy button).

- **[P1][ux] SFTP/S3 don't gate the Integration plan up front — raw error code surfaces** — `PullIngressSettings.tsx:115-224`; backend returns `{error:"sftp_ingestion_requires_integration"}` which is rethrown verbatim into the red notice. Email gates proactively; SFTP/S3 don't. **Fix:** mirror Email's `canEnable`/disabled-toggle/amber upgrade notice; map error codes to human sentences.

- **[P1][inconsistency] SFTP/S3 have no loading skeleton or API-unavailable state** — `PullIngressSettings.tsx:117/172` destructure only `data`; a slow/failed GET shows empty default fields a user can overwrite on Save. **Fix:** add isLoading/isError + shared skeleton/error panel + `retry:false`.

- **[P1][ux] "Default supplier" required to enable ingest, but the dropdown can be empty with no way to create one** — `settings/page.tsx:644` + `PullIngressSettings.tsx:53-69`; backend 400 "Default supplier is required." New org dead-ends. **Fix:** when supplier list is empty, show "No suppliers yet — add one first" linking to `/suppliers`; mark field required; validate client-side.

- **[P1][honesty] Buyers list "Volume /wk", "This week", "Suppliers reached" are fabricated from all-time order count** — `library/buyers/page.tsx:125-140` renders `orderCount` with a "/wk" suffix and the *same* number again under "This week"; suppliersReached = `min(4, formats.length)`; inbound channel heuristic from format. **Fix:** drop "/wk" or label "Orders (all time)"; remove the duplicate weekly column; replace fabricated columns with "—" or relabel "Primary format".

- **[P1][bug] Mappings "All suppliers" default shows 0 and "X saved" is wrong** — `MappingEditor.tsx:126-192`; the live query runs only when a supplier is selected, but the default route is "All suppliers" (null). Populated accounts look empty. **Fix:** aggregate across suppliers for the All view, or default to the first supplier / require selection with clear copy.

- **[P1][honesty] Mapping modal "Description" field is collected but never saved** — `MappingEditor.tsx:832` uncontrolled input; `handleAction` sends only `{buyerItemCode, supplierItemCode}`; converter hardcodes `description:""`. Users type, save succeeds, it vanishes. **Fix:** round-trip description through DTO/payload, or remove the field + column.

- **[P1][ux] Suppliers list — 5 of 6 columns are permanent "—"/"Not set"** — `SupplierDockList.tsx:464-558` only fetches `{id,name}`; Format/Channel/Auto-process/Orders/Acceptance always render dashes even for fully configured suppliers. Table reads as broken. **Fix:** populate Format/Channel/Auto-process from `getDeliveryConfig` (per row or a list endpoint); drop columns with no data source.

- **[P1][inconsistency] Supplier profile page is not direction-aware** — `SupplierDockProfile.tsx:700-1013` hardcodes "Suppliers"/"Delete supplier"/"removes it from your supplier list" while the list relabels to Customer. Split-brain. **Fix:** thread `useOrderDirection` through the profile (back link, delete button+dialog, empty states).

- **[P1][inconsistency] Direction relabelling missing on Upload + Mapping preview** — `UploadWorkbench.tsx` and `MagicMappingPreview.tsx` hardcode "Buyer"/"Supplier"/"routes to"/"Supplier item code"; neither imports `useOrderDirection`. The two highest-traffic screens read wrong for inbound orgs. **Fix:** consume `useOrderDirection` for the field labels, rail, and preview headers.

- **[P1][honesty] Upload dropzone offers `.xls`/`.json` (backend rejects) and claims X12 it can't take by extension** — `UploadWorkbench.tsx:576` accept includes `.xls,.json`; backend whitelist (`OrdersController.cs:98-101`) is `.csv .xlsx .pdf .xml .cxml .edi .txt`; no JSON parser; `.x12` in neither list. Users get a 400 after upload. **Fix:** trim accept to exactly the whitelist; add `.x12` to both lists or drop the X12 claim; drop `.json`/`.xls`.

- **[P1][honesty] "↑ Upload & send" doesn't send — it uploads and routes to review** — `UploadWorkbench.tsx:1309` label vs `:414` push to preview. First-timers think the PO already went out. **Fix:** rename "Upload & review"; reserve "Send to supplier" for actual delivery.

---

## P2 — POLISH

- **[P2][honesty] Dashboard "Acceptance rate" counts internal pipeline failures, not supplier rejections** — `DashboardController.cs:170` = 100·(orders−failed)/orders over {failed, transform_failed, delivery_failed, dead_letter}. A failed PDF parse defames the supplier. **Fix:** rename "Delivery success rate", or split true acceptance (rejected_by_supplier vs delivered) from internal failures.
- **[P2][honesty] Dashboard "Auto-processed %" sampled over latest 100 orders while "Orders received" shows true total** — `BridgeDashboard.tsx:289/382` two headline numbers disagree on basis. **Fix:** server-aggregate auto counts, or sub-label "based on latest 100".
- **[P2][honesty] Document Anatomy confidence numbers (99/95/75) are invented** — `SpineReview.tsx:682-751`; only the lines zone uses real avg. **Fix:** feed real per-section confidence or use qualitative high/med/low chips.
- **[P2][honesty] SpineReview output preview always renders cXML regardless of real output format** — (see P1 toggle finding; the cXML body itself misleads when format badge says CSV/EDIFACT). **Fix:** don't emit cXML tags when the format badge says otherwise.
- **[P2][honesty] Mapping Export "scope" + "XLSX" options are decorative** — `MappingEditor.tsx:684-781`; always writes a CSV of the selected supplier. **Fix:** implement scope/XLSX or remove the selects and label "Export this supplier's mappings as CSV".
- **[P2][honesty] Upload pipeline animation shows "Transform" before anything is transformed** — `UploadWorkbench.tsx:17/410-417` fixed 600ms timers run Parse/Normalize/Validate/**Transform** then redirect; transform hasn't happened. **Fix:** drop "Transform" from this step, or redirect immediately and let the preview's real parsing state drive feedback.
- **[P2][honesty] flowNotice renders green "success" styling even for failure messages** — `SpineReview.tsx:1792-1811` color keyed on `order.status`, not message nature; "Delivery failed…"/"Transform failed…" show green. **Fix:** track severity (info/success/error) and color from that.
- **[P2][ux] No auto-refresh while an order is parsing** — `SpineReview.tsx:1353` staleTime 30s, no refetchInterval; the stuck banner only fires after 2 min. Screen feels broken at first impression. **Fix:** refetchInterval ~2–4s while status is parsing/transforming/delivering.
- **[P2][ux] "Validate against profile" result has no effect on send** — `SpineReview.tsx:1381-2005`; a "Failed — acceptance issues" result doesn't warn/block send (handleConfirm only checks needsReview). **Fix:** surface failing-rule count in the confirm dialog; require ack or block.
- **[P2][a11y] Confirm dialog + toast lack dialog/status roles and focus trapping** — `SpineReview.tsx:1113-1205`; no role=dialog/aria-modal/aria-labelledby, focus not trapped; CrossedToast has no role=status. **Fix:** add roles, trap+restore focus, aria-live on toast/flowNotice.
- **[P2][a11y] Inbox sortable headers are mouse-only** — `InboxView.tsx:888-908`; no tabIndex/role/aria-sort/keyboard handler. **Fix:** add tabIndex={0}, aria-sort, Enter/Space toggle.
- **[P2][ux] No bulk select/send on mobile** — `InboxView.tsx:785-860`; mobile cards have no selection affordance. **Fix:** add a per-card Retry action for failed cards (simplest), or document the omission.
- **[P2][a11y] Upload dropzone is a non-focusable div** — `UploadWorkbench.tsx:546-572`; onClick/onDrop but no tabIndex/role/onKeyDown (inner Browse button mitigates partially). **Fix:** role=button, tabIndex=0, aria-label, Enter/Space handler.
- **[P2][ux] Drag-drop bypasses the accept filter — error only after upload** — `UploadWorkbench.tsx:549-558` accepts any dropped file. **Fix:** validate extension in onDrop + onChange; inline error at drop time.
- **[P2][honesty] Sidebar footer "Pipeline healthy · 12/min" is a static fake metric for all users** — `BridgeSidebar.tsx:299/304`; real signal exists at `/api/ops/health`. **Fix:** drive dot+text from ops health; drop or compute the rate; link to `/operations/health`.
- **[P2][honesty] Workspace switcher is a dead control (chevron + pointer + aria-label, no menu)** — `BridgeSidebar.tsx:234-253`. **Fix:** wire to Clerk `setActive`, or strip chevron/pointer so it reads as a static badge.
- **[P2][ux] Help nav ejects the user into the marketing layout** — `BridgeSidebar.tsx:66` → `/help` (marketing group, no app shell). Jarring mid-task. **Fix:** move help into the (app) group, open in a new tab, or keep in HelpSlideover with a "Back to dashboard" affordance.
- **[P2][bug] Breadcrumb shows raw lowercase slugs (admin/help/inbound/invoices/asns/exceptions/health)** — `BridgeTopbar.tsx:54-100` LABELS map is missing them; on an owner's first /admin visit the breadcrumb reads "admin". **Fix:** add the keys + title-case unknown single-word slugs.
- **[P2][a11y] Topbar `.link-spine` sweep ignores prefers-reduced-motion** — `globals.css:542-550` not in the reduced-motion block (L345-363), re-fires on every route via `key={pathname}`. **Fix:** add `.link-spine[data-animated='true']{animation:none!important}` inside the reduced-motion media block.
- **[P2][ux] Top-level "Admin" item shown to every customer (most hit a no-access page)** — `BridgeSidebar.tsx:62-69` + in LAUNCH_CORE_HREFS. IA clutter advertising an unreachable destination. **Fix:** gate on an isPlatformAdmin signal, or at least remove from core nav.
- **[P2][ux] Mobile loses field↔zone/output lineage with no equivalent** — `SpineReview.tsx:1281` MobileSpineAccordion drops zone wiring; SpineConnectors is xl-only. The product's core lineage value prop is gone on phones. **Fix:** tap-to-highlight linked zone/line, or a per-field "maps from/to" line; confirm StandardsFieldPopover opens on tap.
- **[P2][improvement] Mapping preview never shows the actual supplier name** — `MagicMappingPreview.tsx:615` always "your supplier"; MappingPreview DTO has no supplierName. **Fix:** add supplierName/code to the DTO and render "…map to Acme Components (ACME)".
- **[P2][inconsistency] In-transit stage badges use non-canonical "Extract"/"Ready" for transforming/delivering** — `BridgeDashboard.tsx:79-89`; "Ready" (green) for an actively-delivering order contradicts its own stepper. **Fix:** map transforming→"Transform", delivering→"Delivering".
- **[P2][design-direction] Onboarding wizard active step uses non-canonical lime `#28C55E`** — `OnboardingWizard.tsx:16` token `blue` set to green; active+done both read green, collapsing the blue→green progression. **Fix:** set T.blue = `#1E66C9`.
- **[P2][inconsistency] Wizard Step 1 hardcodes "supplier" even after inbound choice in Step 0** — `OnboardingWizard.tsx:253-265`; contradicts OnboardingChecklist (uses nounLower). **Fix:** thread direction into Step1.
- **[P2][a11y] Wizard direction radios report aria-checked=false on all options** — `OnboardingWizard.tsx:157` hardcoded false. **Fix:** track selection state, set aria-checked accordingly + arrow-key nav.
- **[P2][bug] "Skip the wizard for now" link is dead** — `welcome/page.tsx:69` → `/bridge?onboard=skip`; `BridgeDashboard.tsx:484` never reads searchParams, wizard auto-opens anyway. **Fix:** read `useSearchParams()`, init wizardDismissed on `onboard=skip`.
- **[P2][inconsistency] LaneDrawer hardcodes "Buyer"/"Supplier"** — `LaneDrawer.tsx:163/231` ignores `useOrderDirection`. **Fix:** use labels.counterpartyNoun. (Part of theme 3.)
- **[P2][inconsistency] PO Mapping required gate uses BuyerItemCode but delivery needs the SUPPLIER code** — `PoMappingEditor.tsx:72-79`; user can satisfy "All required fields mapped" yet have nothing the supplier can fulfil. **Fix:** reconcile the gate language across PO-mapping / SKU mappings / validation so they tell one story.
- **[P2][ux] Delivery editor leaks `ready_to_deliver` jargon + no standards popover on Output format** — `DeliveryConfigEditor.tsx:340-871`; raw snake_case state, "Delivery state boundary" note, always-on raw JSON `<pre>`. **Fix:** plain copy, add StandardsFieldPopover to Output format, tuck JSON under "Advanced" disclosure.
- **[P2][ux] Acceptance rule editor rows are unlabeled selects on mobile** — `SupplierDockProfile.tsx:427-496`; edit mode has no per-control labels. **Fix:** add Scope/Field/Condition/Value/Severity labels, or compose as a sentence.
- **[P2][ux] Supplier "Mappings" tab shows no table in real mode** — `SupplierDockProfile.tsx:1008-1014` only a paragraph linking out, though `getSupplierMappings(id)` exists and the mock view renders a full table. **Fix:** fetch and render the real table with Add affordance.
- **[P2][ux] Native confirm()/alert for destructive actions** — `settings/page.tsx:944/984/1297` revoke key + delete webhook. Off-brand. **Fix:** inline confirm two-button state or styled dialog.
- **[P2][ux] Webhook header doesn't wrap on mobile** — `settings/page.tsx:1146-1159` no flexWrap. **Fix:** add flexWrap / flex-col→sm:flex-row like the connector-row/IMAP header.
- **[P2][bug] Webhook 2-col grid collapses via a brittle `[style*="grid-template-columns: 1fr 1fr"]` selector** — `settings/page.tsx:167/1165`; any whitespace/refactor silently breaks the mobile collapse and can hit unrelated grids. **Fix:** use `grid grid-cols-1 sm:grid-cols-2` and delete the attribute selector.
- **[P2][improvement] "Default currency"/"Workspace region" look editable but are hardcoded** — `settings/page.tsx:258-269`. **Fix:** group under an "About this workspace" info block with a "fixed" caption.
- **[P2][ux] Email save relies on backend 400s; no client-side validation or required markers** — `settings/page.tsx:502-670`. **Fix:** validate all required fields client-side, mark with *, show inline errors.
- **[P2][inconsistency] Dashboard nav "Dashboard" vs breadcrumb/H1/title "Order topology"** — `BridgeSidebar.tsx:25` vs `BridgeTopbar.tsx:55`/`BridgeDashboard.tsx:586`. "Topology" is jargon. **Fix:** "Dashboard" everywhere; keep "Order topology" only as the canvas aria-label.
- **[P2][inconsistency] Several real routes lack loading.tsx** — inbound/invoices, inbound/asns, library/standards, operations/exceptions, operations/health, upload/preview/[orderId]. **Fix:** add the 2-line `BridgePageLoader` loading.tsx to each.

---

## P3 — NICE TO HAVE

- **[P3][ux] BridgePageLoader prints "Loading…" twice** — `BridgeLoader.tsx:142-172`. Drop the hardcoded sub-line.
- **[P3][ux] Mobile topbar shows no breadcrumb/page label** — `BridgeTopbar.tsx:321-327`. Render the last crumb segment as a compact mobile title.
- **[P3][honesty] "Demo data" badge hidden on mobile** — `BridgeTopbar.tsx:289-319` `hidden sm:`. Show a compact pill on small screens.
- **[P3][inconsistency] Command Palette label drift** ("View all deliveries"→/inbox; "standards comparison" subtitle) — `CommandPalette.tsx:90/94`. Align labels + add Exceptions/Health actions (pairs with P0).
- **[P3][inconsistency] Topology subtitle "N suppliers" counts only suppliers-with-orders** — `BridgeDashboard.tsx:597`; disagrees with Suppliers page count. Relabel "N active connections" or source from suppliers query.
- **[P3][ux] In-transit row crowds PO+buyer+chip+stage at 375px** — `BridgeDashboard.tsx:805-819`. Allow wrap / stack on mobile.
- **[P3][inconsistency] Supplier-health color thresholds differ in 3 places** — `BridgeDashboard.tsx:868` (90/80) vs `WireTopology.tsx:85` (95/85) vs `:381-385` (85/95). Centralize `healthColor(pct)`.
- **[P3][ux] Inbox "Sync" gives no feedback** — `InboxView.tsx:621-627`. Tie to `isFetching` (spinner + disable).
- **[P3][ux] Inbox loading skeleton is desktop-only and drops chrome (layout shift)** — `InboxView.tsx:541-559`. Keep header/chips/search, swap only body; card-shaped mobile skeleton.
- **[P3][inconsistency] Inbox total count rendered twice (header + footer)** — `InboxView.tsx:614/1015`. Keep one; replace header with "N need review · N failed".
- **[P3][ux] Search hides selected rows but selection survives silently** — `InboxView.tsx:440-446` handleSearch doesn't clear rowSelection. Clear it (mirror handleChip) or warn "N selected (not all shown)".
- **[P3][improvement] Inbox has no keyboard row nav / column-visibility / Cmd+K** — densest power surface, design direction calls for all three. Add j/k+Enter, TanStack column visibility, palette actions.
- **[P3][design-direction] SpineReview triptych hard-gated at xl (1280px); 768–1279 tablets get the mobile accordion** — `SpineReview.tsx:2010/1278`. Consider a 2-col intermediate layout for the md–xl band.
- **[P3][improvement] "hover a zone"/"hover a field" helper labels are mouse-centric clutter** — `SpineReview.tsx:2039-2089`. Drop or replace with one subtle info affordance.
- **[P3][ux] Mapping commit-bar flashes "Committed ✓" then immediately redirects** — `MagicMappingPreview.tsx:340-1266`. Pick one: stay with a "Go to order" affordance, or redirect + toast on destination.
- **[P3][ux] Sample-order card sits below the dropzone on first run** — `UploadWorkbench.tsx:546-817`. Promote the zero-friction "Try a sample" path above/alongside the dropzone when no supplier/orders exist.
- **[P3][a11y] Settings direction radiogroup lacks roving-tabindex/arrow-key nav** — `settings/page.tsx:341-385`. Add roving tabindex + arrow handling or native radios.
- **[P3][improvement] API-keys/connectors empty states push Zapier/Make (currently dead-ends)** — `settings/page.tsx:906-1255`. Reword to lead with REST API + webhook (the working path).
- **[P3][ux] PO Mapping mobile drops the wire connectors with a too-subtle "← column" cue** — `PoMappingEditor.tsx:640-864`. Add "from: <column> · e.g. <sample>" + a "8 of 10 matched" summary.
- **[P3][improvement] PoMappingEditor maintains its own standards strings instead of the shared catalog** — `PoMappingEditor.tsx:977-990` literals can drift from `src/lib/standards/catalog.ts`. Read from the shared catalog; reuse StandardsFieldPopover.
- **[P3][ux] Email settings gives no success confirmation after Save** — `settings/page.tsx:489-678` shows only errors, unlike the other 3 savable sections. Add a green "Email settings saved." line.
- **[P3][inconsistency] SFTP/S3 use a different card shell + ink Save button than the rest of Settings** — `PullIngressSettings.tsx:71-109` vs SettingsGroup/primaryGreenButton. Refactor to shared SettingsGroup + brand-green Save.

---

## TOP 10 QUICK WINS (high impact, low effort)

1. **Fix bulk send auth** — swap the raw fetch for `apiClient.redeliverOrder(id)` in `InboxView.tsx:456`. One-line change, unblocks a P0 that fails 100% in prod.
2. **Remove the Upload "Output template" + "Processing mode" controls and the false warning** — delete `UploadWorkbench.tsx:1143-1212`. Kills 3 dead/lying controls at once.
3. **Rename "↑ Upload & send" → "Upload & review"** — `UploadWorkbench.tsx:1309`. One string, removes a scary honesty bug.
4. **Trim the dropzone accept list to the real whitelist** — `UploadWorkbench.tsx:576` → `.csv,.xlsx,.pdf,.xml,.cxml,.edi,.txt`. Removes `.xls/.json` false offers.
5. **Add Exceptions + System health to the sidebar + Cmd+K** — `BridgeSidebar.tsx:46` + `CommandPalette.tsx:89`. Makes the two headline ops screens reachable.
6. **Relabel global Validation Rules as a descriptive catalog + hide Block/Auto-block** — `ValidationRules.tsx` header copy. Defuses a dangerous false-promise P0 without backend work.
7. **Remove/relabel the SpineReview "Save draft", "Edit"(=Accept), and XML|JSON toggle** — `SpineReview.tsx:639-989/1639`. Three dead controls in the core screen.
8. **Replace "alert to MK" + the static "Pipeline healthy · 12/min"** — `SpineReview.tsx:1159` + `BridgeSidebar.tsx:299`. Two literal leaks that embarrass on a demo.
9. **Add the missing breadcrumb LABELS + title-case fallback** — `BridgeTopbar.tsx:54`. Fixes the bare lowercase "admin" on an owner's first visit.
10. **Reduced-motion: gate the SMIL topology dots (JS matchMedia) + add `.link-spine` to the reduced-motion CSS block** — `WireTopology.tsx:296` + `globals.css:345`. Closes both brand-rule a11y violations.

---

## BIGGEST LEVERS FOR A MORE LOGICAL, EFFECTIVE UI

1. **Run a single "offer↔works" sweep and adopt a hard rule: no control ships unless it does what it says.** The largest cluster of findings — across upload, order review, mappings, validation, settings, dashboard — is interactive chrome that no-ops or is never sent to the backend. This is the #1 threat to the 30-yr veteran (who *will* try the toggle) and to demo credibility. A one-time audit ("for every button/select: where does its value go?") plus a lint/PR-checklist gate prevents regression. This single discipline resolves ~20 findings and most P0/P1 honesty issues.

2. **Make the live (non-mock) path as informative as the mock path — then delete the mock branches from production code.** Counts, badges, recent-deliveries, supplier mappings, supplier-list columns all work in the demo and are blank for paying customers, because the real queries (`getOrdersSummary`, `getSupplierMappings`, `getDeliveryConfig`, ops health) are wired only behind `isApiMockMode`. Inverting this — live-first, mock as a thin fixture — fixes the "powerful, dense, scannable" half of the bar and removes the staged-data leak risk.

3. **Centralize the few shared decisions: direction labels, status vocabulary, health thresholds, standards catalog, and page names.** Five separate inconsistency clusters all stem from copy-pasted logic. One `useOrderDirection` everywhere, one canonical 5-stage status label map, one `healthColor()`, one standards catalog source, and one "label = nav = breadcrumb = H1 = behavior" naming pass collapse a dozen findings and make the app feel like one coherent product rather than screens built in isolation.

4. **Close the loop on exception → resolution → unblock.** Today the operator's core job (clear a blocked order) is broken at three points: the screen isn't navigable, "Resolve" doesn't fix the order (and the exception reappears), and the order-review Accept/edit actions don't persist or unblock send. Treating "find the trouble → fix the cause → it stays fixed" as one designed flow (nav entry → Open order → real line resolution mutation → Reconcile auto-resolves) is the difference between a demo and a tool a procurement team trusts daily. This is the product's stated near-term value; it must work end to end.

5. **Progressive disclosure + keyboard depth for the power user; plain words + honest defaults for the novice.** The pieces exist (Cmd+K, StandardsFieldPopover, column selectors) but are under-wired: shortcuts are undiscoverable, sort/dropzone/dialogs aren't keyboard-operable, jargon (`ready_to_deliver`, "topology", raw JSON) sits in the novice's face while power affordances hide. Push density/standards/hotkeys into discoverable disclosure (kbd hints, Cmd+K registration, "Advanced" sections) and replace internal vocabulary with plain procurement words. This is the literal 5yo-simple / 30yr-powerful bar, currently met by neither end consistently.
