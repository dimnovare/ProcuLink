## 22. Webhooks (outbound events) — `/operations/webhooks`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/webhooks/page.tsx`
- **Key components:** all defined inline in the page file above —
  - `WebhooksLayout` (shared shell for mock + live), `EndpointsCard`, `EndpointPill`, `DeliveriesCard`, `WebhookPanel` (the add/edit modal), `CardHead`, `SkeletonEndpointRow`, inline SVG icons (`PlusIcon`, `WebhookIcon`, `CheckIcon`, `SendIcon`), `MockWebhooksPage`, `LiveWebhooksPage`.
  - Imported primitives: `EmptyState` (`src/components/bridge/EmptyState.tsx` → `MarkSystem`), `PageShell` (`src/components/bridge/layout/PageShell.tsx`, `variant="wide"` → 1480px), `PageHeader` (`src/components/bridge/layout/PageHeader.tsx`).
  - Status logic: `deriveWebhookStatus()` from `./webhookStatus.ts` (pure, unit-tested).
  - Data: `getIntegrations` / `createIntegration` / `toggleIntegration` / `deleteIntegration` / `IntegrationSubscription` / `isApiMockMode` from `src/lib/api-client.ts`.
- **Capture URL (mock):** `/operations/webhooks` — in mock mode the page renders `MockWebhooksPage`, which is pre-seeded with three local `MOCK_WEBHOOKS` rows (`w1`, `w2`, `w3`) and five `MOCK_DELIVERIES`, so no ids/query are needed. (Note: the **live** `api-client` mock array `_mockIntegrations` is empty, but this page does NOT use it — `MockWebhooksPage` keeps its own in-component state, so the page is fully populated under mock.)

### What it is & why it exists
This is the outbound side of the `learn` loop: instead of pulling order state, ProcuLink **pushes** lifecycle events (`order.created`, `order.delivered`, `order.failed`) to a buyer's own systems (their ERP, an ops ingest endpoint, etc.) over signed HTTP webhooks. It lets a customer wire ProcuLink into their internal automation so a delivered/failed PO can trigger downstream work without anyone watching the inbox. The page is both the **configuration surface** (add/enable/disable/delete endpoints) and a lightweight **monitor** (per-endpoint health pill + a recent-deliveries log).

### Who uses it & the primary job
Persona: **integration expert / operator** (a developer or technical procurement ops person), not the everyday coordinator. Primary job: **register an HTTP endpoint that should receive order events, with an optional HMAC signing secret, and confirm it's healthy.** Secondary job: glance at the deliveries log / health pills to spot a failing endpoint.

### Layout & structure (current)
Top-to-bottom, inside `PageShell variant="wide"` (max-width 1480px, gutter ramp `px-4 / sm:px-6 / lg:px-[34px]`, vertical `py-5 / sm:py-7`):

1. **`PageHeader`** — title "Webhooks" (Bricolage Grotesque, ~28–30px, weight 600), subtitle `Push order events to your systems · {N} endpoint(s)` (13px muted), and a right-aligned **green primary "Add endpoint" button** (height 32px, `--brand-green`, Plus icon). On mobile the actions slot wraps below the title.
2. **Notice banner** (conditional) — full-width blue info strip (`--brand-blue-soft` bg, 3px blue left border) showing transient feedback like "Webhook deleted." / "Endpoint added — test ping sent to …".
3. **Error banner** (conditional, live mode only) — red strip (`#F5B8B8` border, `--danger-soft` bg, `#7B1C1C` text) "Failed to load webhooks." with an underlined inline **Retry** button.
4. **Two-column split** (`.webhooks-split`, `grid-template-columns: 1fr 1fr; gap:16px; align-items:start`):
   - **Left — "Endpoints" card** (`EndpointsCard`): white card, `--radius-md` (8px), 1px `--border`. A `CardHead` (webhook icon + "Endpoints" 14px/600). Then one **row per endpoint** (padding `13px 16px`, bottom hairline between rows): line 1 = monospace URL (11.5px/600, ellipsis, `max-width:240px`) + a right-aligned **status pill**; line 2 = wrapping **event chips** (mono 10px, surface-2 bg); line 3 = "Last delivery: {x}" (11px faint) + a **hover-revealed action cluster** (Edit*/Disable·Enable/Delete).
   - **Right — "Recent deliveries" card** (`DeliveriesCard`): white card with a `CardHead` (send icon + "Recent deliveries" / sub "Last 5 attempts"). On desktop a **5-column table** (Time · Event · Order · Status · Latency); on phones (≤560px) the same rows render as **stacked row-cards**.
5. **No footer / no global action bar** — the only primary action is the header "Add endpoint" button.

Spacing/type/density observations: almost everything is **inline-styled with hardcoded px + literal hex fallbacks** rather than the shared primitives — e.g. row padding `13px 16px`, card-head `14px 16px`, font sizes `11px / 11.5px / 12px / 12.5px / 13px` mixed freely. Action buttons are 27px tall (below the 44px target). Pills use the canonical ported `.pill` classes (height 21px). The two cards are forced to equal `1fr 1fr` widths even though the deliveries table is denser than the endpoint list.

### Data shown
- **Endpoints** (`WebhookRow`): `url` (→ `targetUrl`), `events` (live mode = a single `eventType` per subscription, wrapped in an array), `status` (`healthy | failing | paused`, derived), `failureCount`, `lastDelivery` (relative time from `updatedAt`).
  - **Live source:** `GET /api/integrations` via `getIntegrations()` → `IntegrationSubscription[]` (`id, platform, eventType, targetUrl, isActive, failureCount, createdAt, updatedAt`), mapped by `toRow()`. Status = `deriveWebhookStatus(isActive, failureCount)`: `!isActive → paused`; `isActive && failureCount>0 → failing`; else `healthy`.
  - **Mock source:** local `MOCK_WEBHOOKS` (`w1` erp.company.com healthy 2m, `w2` ops.company.com healthy 8m, `w3` legacy.example failing "3 retries · 1h ago").
- **Recent deliveries** (`DeliveryRow`): `time`, `event`, `po`, `status` (HTTP code), `dur` (latency), `fail?`.
  - **Mock source:** `MOCK_DELIVERIES` (5 rows; one `503 timeout` flagged `fail:true`).
  - **Live source:** **none** — `LiveWebhooksPage` passes `deliveries={null}`, so the card always shows the "No deliveries yet" empty state in production (there is no delivery-history API).
- **Backend event vocabulary** (`WEBHOOK_EVENT_TYPES`): `order.created`, `order.delivered`, `order.failed`, each with a plain-language label.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Header **"Add endpoint"** button (green primary) | `onAdd` | Clears notice, opens `WebhookPanel` in "new" mode (modal) |
| **Endpoint row** (`.wh-row`) | hover / focus-within | Reveals the per-row `.wh-actions` cluster (opacity 0→1) |
| Row **"Edit"** button *(mock only — hidden when `allowEdit:false` in live)* | `onEdit(row)` | Opens `WebhookPanel` pre-filled with that endpoint |
| Row **"Disable" / "Enable"** button (blue outline) | `onToggle(id)` | Live: `PATCH /api/integrations/{id}/toggle`, invalidates `["integrations"]`, notice "Webhook status updated." Mock: flips healthy↔failing. Shows "…" while pending (`togglingId`) |
| Row **"Delete"** button (red outline) | `window.confirm("Delete webhook for {url}?")` → `onDelete(id)` | Native confirm; on OK live `DELETE /api/integrations/{id}`, invalidate, notice "Webhook deleted." Shows "…" while pending (`deletingId`) |
| Error banner **"Retry"** link | `onRetry` | `queryClient.invalidateQueries(["integrations"])` (live only) |
| Modal **Endpoint URL** input (mono) | text entry | Sets `url`; required — empty disables the save button |
| Modal **Events** `<select>` | choose event type | Sets `eventType` (one of the 3 backend types) |
| Modal **Signing secret** input (mono, `whsec_••••` placeholder) | text entry | Sets `secret` (optional, sent on create) |
| Modal **Cancel** button | `onClose` | Closes panel, no save |
| Modal **"Add endpoint" / "Save changes"** button (green primary, Check icon) | `onSave(url, eventType, secret)` | Live new: `POST /api/integrations` (`platform:"webhook"`). Live edit: **no-op** — sets notice "Editing an existing endpoint isn't supported yet — delete and add a new one…" and closes. Mock: simulates 400ms then inserts/updates the row. Disabled while saving or when URL empty |
| Modal **× / Close** button (top-right, `aria-label="Close"`) | `onClose` | Closes the panel |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Add / Edit webhook panel** (`WebhookPanel`) | Modal dialog (fixed full-screen scrim, centered ≤540px card; bottom-sheet on mobile via `items-end sm:items-center`) | Header **"Add endpoint"** (new) OR a row **"Edit"** button (mock only) | Blue icon-chip + title ("Add/Edit webhook endpoint") + subtitle; **Endpoint URL** input (required); **Events** select; **Signing secret** input + HMAC helper text; **blue info banner** ("We'll send a test ping on save… reads as failing if recent attempts don't succeed"); footer Cancel + Add/Save | **×** button, **Cancel** button, or a successful **save** (`onClose`/`setPanel(null)`). **No Esc-to-close, no backdrop-click-to-close** — the scrim is non-interactive. Scrim `rgba(11,26,47,0.42)` + `blur(3px)`; card animates in via `modal-pop` 220ms |
| **Delete confirmation** | Native `window.confirm()` dialog | Row **"Delete"** button | Browser-native "Delete webhook for {url}?" prompt | OK (proceeds to delete) / Cancel (browser-controlled; not styled) |
| **Notice banner** | Inline panel (not an overlay) | Any successful mutation, toggle, delete, or the live edit-unsupported path | Single line of feedback text | Auto-replaced by the next action; **no dismiss control and no auto-timeout** — it persists until another notice overwrites it |
| **Native browser tooltip** | Tooltip | Hovering a truncated endpoint URL (`title={w.url}`) | Full URL string | Mouse-out (browser-controlled) |

There is **one custom modal** (the webhook panel) plus the native confirm and the inline notice. No drawers, sheets, dropdowns (the Events picker is a plain `<select>`, native popup), or toasts.

### States
- **Empty:**
  - Endpoints with no rows → `EmptyState compact` "No endpoints yet" / "Add an endpoint to start receiving order events." (centered `MarkSystem` mark, Bricolage title). Note: the "next action" (Add endpoint) lives only in the page header, **not** wired into the empty state's `action` slot.
  - Deliveries with `null`/empty → `EmptyState compact` "No deliveries yet" / "Delivery attempts will appear here once webhooks start firing." This is **always shown in live mode** (no delivery API).
- **Loading:** Endpoints card swaps to **two `SkeletonEndpointRow`s** (pulse animation `skel-pulse`) while `isLoading`. There is **no `loading.tsx`** for this route (none exists in `operations/` or anywhere under `(app)`), so the initial route transition shows no skeleton — only the in-card skeleton once mounted. Deliveries card has **no loading skeleton**.
- **Error:** Live mode `isError` → the red banner with inline Retry (above). Mutation errors (`createIntegration`/`toggle`/`delete`) surface as a **notice** ("Failed to add endpoint — {msg}", "Toggle failed — {msg}", "Delete failed — {msg}"), reusing the blue notice strip — **a failure is shown in blue, not red.**
- **Success/feedback:** Inline blue notice strip for every successful op; per-button "…" pending text on toggle/delete; modal button text changes to "Saving…".

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two equal `1fr 1fr` columns inside the 1480px shell; deliveries render as the full 5-column table; row actions hidden until hover.
- **Tablet 768:** Still two columns until the `720px` breakpoint — so at 768 it is borderline; the `1fr 1fr` split is preserved (deliveries table can feel cramped against the endpoint list at this width).
- **≤720px:** `.webhooks-split` collapses to a single column (Endpoints stacked above Recent deliveries).
- **≤560px:** Deliveries table is replaced by **stacked row-cards** (`.wh-deliv-cards`) to avoid horizontal overflow; the endpoint row meta-row stacks (`.wh-metarow` column) and actions become **always-visible, full-width, 40px tall** (`.wh-actionbtn flex:1; height:40px`). The modal becomes a **bottom sheet** (`items-end`, `rounded-t-[10px]`).
- **`@media (hover:none)`** (touch): row actions are always visible.
- Known cliff: the 720px single-column switch leaves the 720–768px band rendering two tight columns; and the 27px action buttons on desktop only grow to 44px-class size on phones, so **mid-size touch (tablet) gets the small targets**.

### Current UX issues
- **Sub-44px touch targets on desktop/tablet:** row Edit/Disable/Delete are **27px tall**; modal close is 32px; only phones (<560px) bump to 40px (still under 44px). Violates the ≥44px / visible-pressed-state bar.
- **Hover-only row actions = discoverability + a11y gap:** Edit/Disable/Delete are `opacity:0` until row hover/focus-within. On a touchpad/keyboard this hides the primary per-row operations; there's a `:focus-visible` reveal but the cluster is still invisible at rest, which reads as "nothing to do here."
- **Modal can't be dismissed by Esc or backdrop click** — only ×/Cancel. The scrim looks dismissible (blurred, dark) but isn't. Breaks the "modals have a clear escape" expectation.
- **Errors are shown in blue, not red:** mutation failures ("Toggle failed — …", "Failed to add endpoint — …") reuse the **blue info notice**, conflating failure with neutral feedback. Violates the one-status-system / red=blocking bar.
- **Notice never dismisses and never times out:** a stale "Webhook deleted." lingers until the next action; no close affordance. No real toast system.
- **Delete uses `window.confirm()`** — an unstyled native dialog, off-brand, not focus-trapped, inconsistent with the custom modal. Destructive confirm should be in-product.
- **Token drift / hardcoded literals everywhere:** the file hardcodes `--ink-faint,#5B6980` and `--brand-blue-soft,#E3EDFB` as fallbacks, but the **real tokens are `--ink-faint:#98A0AE` and `--brand-blue-soft:#EAF0F8`** — the fallbacks disagree with the design system. Dozens of inline `px`/hex values instead of the 4/8 scale (13px, 11.5px, 10.5px, 27px, 21px, etc.).
- **No tabular figures** on the deliveries table (Time, Status code, Latency) or on the "N endpoints" count — numbers are mono-font but not `font-variant-numeric: tabular-nums`, so latency/status columns can jitter.
- **Three pill states but inconsistent semantics:** "Paused" uses the **neutral** `pill-new` (grey) even though a *paused* endpoint silently drops events — and an **auto-killed-after-3-failures** endpoint also reads as the same benign grey "Paused", hiding a real problem (the very thing `deriveWebhookStatus` was written to avoid is partly re-introduced at the pill level).
- **"HTTP 200 ≠ acceptance" trap, inverted:** the deliveries log treats any non-`fail` 200 as success and the modal's info banner correctly warns about real-delivery health, but the mock data shows `order.failed` events returning `200` — fine for a webhook, yet the column header is just "Status" with no hint that 200 means "we delivered the *event*", not "the supplier accepted the *order*".
- **Cards forced to equal width:** the dense 5-column deliveries table is squeezed into the same `1fr` as the sparse endpoint list; the table wants more width.
- **No `loading.tsx`** → route-level navigation shows a blank/empty frame before the client component mounts its in-card skeleton.
- **Live "Edit" is a dead-end honesty patch:** Edit is hidden in live, and if reached it just tells the user editing isn't supported — functional gap surfaced as copy rather than a real edit/rotate-secret flow.

### Redesign recommendations (for Claude Design)
1. **Unify the feedback system into one toast/banner with semantic color + icon** — green=success, red=failure, amber=warning, neutral=info; auto-dismiss with a close button; stop rendering errors in blue. Move "Toggle failed / Delete failed / Failed to add" to the red variant. (Bars 4, 6.)
2. **Make per-row actions always visible and ≥44px** — replace the hover-reveal cluster with a persistent compact action group (or a kebab `⋮` overflow menu opening a small popover) at full size on every breakpoint; keep navy/violet focus ring. (Bars 7, 9, 10.)
3. **Replace `window.confirm` with an in-product destructive confirm dialog** — small modal, focus-trapped, red primary "Delete endpoint", clearly separated from the neutral close, naming the URL. (Destructive-action bar.)
4. **Give the modal Esc + backdrop-click close and animate-from-trigger semantics** — keep the scrim, but make it dismissible; trap focus; on save show the success in the unified toast, not a persistent banner. (Modal bar.)
5. **Fix the pill semantics so a problem never reads as benign** — keep one badge system (one shape/size/padding), but distinguish **operator-paused (neutral grey)** from **auto-deactivated after failures (red or amber "Auto-disabled — N failures")**; never let a silently-broken endpoint show grey. Add an icon/word, not color alone. (Bars 4, "never show healthy when failing".)
6. **Purge the hardcoded hex fallbacks and adopt the real tokens** — `--ink-faint:#98A0AE`, `--brand-blue-soft:#EAF0F8`, etc.; move all inline px to the 4/8 spacing scale and the canonical type scale (heading 600 / label 500 / body 400). Carry hierarchy by size+weight, not by the many faint grays currently below 4.5:1. (Bars 1, 2.)
7. **Apply tabular figures** to Time, Status, Latency, and the endpoint count so columns stop jittering and codes line up. (Bar 3.)
8. **Add a `loading.tsx` route skeleton** mirroring the two-card split, and a delivery-card loading skeleton, so navigation never shows a blank frame. (Bar 6.)
9. **Right-size the columns** — give the deliveries table more width than the endpoint list (e.g. `minmax(360px,1fr) minmax(420px,1.2fr)`), and adopt one table density (single row height, low-contrast `gray-200` gridlines, sticky header, hover) shared with other operations tables. (Bar 5.)
10. **Clarify "Status" in the deliveries log** — rename/annotate the column or add a tooltip making clear the HTTP code is the **webhook delivery** result, not supplier order acceptance; consider a green/red `.conf`-style badge with an icon. (Bar 4 + the 200≠acceptance rule.)
11. **Turn the live "edit not supported" dead-end into a real flow** — at minimum support delete-and-recreate inline ("Replace endpoint"), and a write-only **rotate signing secret** affordance (mask existing, "leave blank to keep"). Until the backend PUT exists, label the action honestly as "Replace" rather than "Edit". (Forms / write-only credentials bar.)
12. **Make the Empty state actionable** — wire the endpoints empty state's `action` slot to open the Add panel ("Add your first endpoint") instead of relying solely on the header button. (Bar 6, 7.)
