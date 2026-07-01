## 23. Settings — `/settings`

- **File:** `src/app/(app)/settings/page.tsx` (1,506 lines — the whole hub plus most section components live inline in this one file)
- **Key components:**
  - `src/app/(app)/settings/page.tsx` — `SettingsPage` shell + left nav + 5 inline sections: `OrgSection`, `OrderDirectionSetting`, `EmailSettingsSection`, `ApiKeysSection`, `ConnectorsSection`, plus `IngressEndpointRow`, `SettingsRow`, `InlineConfirm`, `ToggleSwitch`, `FormField`
  - `src/components/settings/SettingsPrimitives.tsx` — shared `SettingsGroup` card shell + `primaryGreenButton` style
  - `src/components/settings/PullIngressSettings.tsx` — `SftpPullSettings` + `S3PullSettings` (the SFTP and S3/R2 tabs) and their shared `Shell`/`Field`/`Toggle`/`Notice`/`UpgradeNotice`/`SaveBar`/`LoadingShell`/`ErrorShell` helpers
  - `src/components/bridge/BillingSection.tsx` — `BillingSection` (the Billing & plan tab: plan card, usage bars, upgrade ladder, payment method)
  - `src/components/bridge/layout/PageShell.tsx`, `PageHeader.tsx` — page chrome
  - `src/components/bridge/BridgeLoader.tsx` — `BridgePageLoader` for `loading.tsx`
  - `src/lib/tab-param-sync.ts` — `useTabParamSync` deep-link `?tab=` handling
- **Capture URL (mock):** `/settings` (org tab default). Per-tab deep links: `/settings?tab=billing`, `/settings?tab=email`, `/settings?tab=sftp`, `/settings?tab=s3`, `/settings?tab=api`, `/settings?tab=connectors`

### What it is & why it exists
Settings is the configuration hub that sits OUTSIDE the per-order parse→normalize→validate→review→transform→deliver→learn loop but governs three of its endpoints: how orders get IN (email IMAP, SFTP pull, S3/R2 pull, REST ingress + API keys), how events flow OUT (webhook connectors to Zapier/Make.com/custom), and the commercial envelope around all of it (billing plan, order/supplier quotas). It also holds the workspace identity (org name) and the single direction switch that relabels "supplier" vs "customer" across the whole app. A procurement coordinator opens it to wire up an automated intake channel, mint an API key, upgrade off Pilot, or flip how parties are labelled.

### Who uses it & the primary job
Primarily the **operator / admin** (the person who owns the workspace and its billing), with an **integration expert** doing the API-key + webhook + SFTP/S3 wiring. The single most important task is **turning on a real automated intake channel** (email IMAP, SFTP, or S3/R2 — all gated on a paid plan and a default supplier) so orders flow in without manual upload; the second is **billing** (upgrade off Pilot to unlock those channels).

### Layout & structure (current)
Top to bottom:

1. **`PageHeader`** — title "Settings", subtitle `"{orgName} · {planLabel}"` (e.g. "Acme Procurement · Pilot plan"). Org name from Clerk `useOrganization()`, plan from `getBillingStatus`. Both fall back to `"…"` while loading.
2. **Two-column body** — a CSS grid, `gridTemplateColumns: "200px minmax(0,1fr)"`, `gap: 28`, `alignItems: start`:
   - **Left nav (`<nav>`, 200px)** — 7 vertical tab buttons, each `rounded-[6px]`, ~`13px`, with a Lucide icon. Active tab = white `--surface` card + `1px --border` + a `2px --brand-blue` left accent bar + blue icon + `--shadow-card` + `aria-current="page"`; inactive = transparent, `--ink-muted`, faint icon. Tabs: **Organization** (`Building`), **Billing & plan** (`Euro`), **Email intake** (`Mail`), **SFTP pull** (`HardDrive`), **S3 / R2 pull** (`Database`), **API keys** (`Key`), **Connectors** (`Plug`).
   - **Content column (`minmax(0,1fr)`)** — renders exactly one section based on `tab` state. Every section is framed by `SettingsGroup` (white card, `1px --border`, `--radius-md` = 8px, `--shadow-card`, header band with 14.5px/600 title + 12px muted sub, body padded `16px 18px`).

Section internals:
- **Organization** = two stacked `SettingsGroup` cards. Card 1 "Organization": workspace-name `<input>` (max-width 420), a `Members` row (real Clerk `membersCount`), an "About this workspace" sub-block (surface-2 box with a "FIXED" uppercase pill, showing `Default currency = EUR — Euro` and `Workspace region = EU` as a `<dl>`, non-editable), then a green "Save changes" button + inline feedback. Card 2 "How do you use ProcuLink?": a `role="radiogroup"` of two large radio cards (outbound "We send purchase orders to our suppliers" / inbound "We receive purchase orders from our customers") with arrow-key roving tabindex.
- **Billing & plan** = `BillingSection`: optional blocking `LimitBanner` (Pilot only), non-blocking `OverageNotice` (paid), a "Current plan" card containing the highlighted `PlanCard` (label, sub, big mono price, billing interval, status, "Change plan"/trial countdown) and two `UsageBar`s (Orders, Suppliers — gradient fill, mono `used / limit`), then plan-state-specific upgrade actions (Pilot: green "Upgrade to Growth" + monthly/annual `IntervalToggle` + Operations/Integration/Distributor secondary chips + contact-sales/book-demo links; Paid: "Upgrade to {nextPlan}"; Enterprise: contact-support paragraph), then a "Payment method" card (Manage in Stripe / Billing email) for paid+enterprise.
- **Email intake** = one `SettingsGroup` "Email intake": amber upgrade gate (when Pilot), an enable-toggle row ("Poll inbox for orders" + `ToggleSwitch`), an "IMAP mailbox" form (host/port/SSL grid `minmax(260px,1fr) 150px 150px`, then username/password 2-col, then folder/default-supplier 2-col), and a footer with a `ShieldCheck` security/last-poll note + validation/error/saved feedback + green "Save email".
- **SFTP pull** / **S3 / R2 pull** = `Shell` (= `SettingsGroup`) cards from `PullIngressSettings.tsx`: amber `UpgradeNotice` (when Pilot), a checkbox `Toggle`, credential fields (host/port/user/pwd/remote-dir for SFTP; bucket/prefix/region/access-key/endpoint/secret for S3), a `SupplierSelect` (default supplier, with no-suppliers empty state), inline `Notice`, and a `SaveBar` (hint + green Save).
- **API keys** = one `SettingsGroup` "API keys": an `IngressEndpointRow` (read-only ingress URL `{apiBaseUrl}/api/ingress/{slug}/orders` + Copy + `X-ProcuLink-Key` auth note), an optional green "new key created — copy now" banner, the keys table (desktop) / stacked cards (mobile), empty state, and a "Create key" button that reveals an inline create form.
- **Connectors** = one `SettingsGroup` "Connectors": two info rows ("REST API & webhooks" working path; "Native Zapier & Make.com apps" with a grey "Coming soon" pill), a "Webhook subscriptions" header with "Add webhook", an inline new-webhook form (platform/event/target-url/secret), error/loading/empty states, and a list of subscription rows.

**Spacing/type/density observations:** Almost entirely **inline `style={{}}`** with hardcoded numbers, not Tailwind utilities or tokens. Font sizes drift across half-pixel values: `13`, `13.5`, `12.5`, `11.5`, `10.5`, `14`, `14.5`, `19`, `24`, `28`. Paddings are ad-hoc (`11px 0`, `14px 16px`, `13px 15px`, `16px 18px`, `14px 18px`, `12px 14px`). Several control heights coexist: inputs `40` (page) vs `36` (PullIngress) vs `32` (connectors form); buttons `38`, `34`, `32`, `30`, `28`. Two different color systems are in use simultaneously: the page uses CSS variables (`--ink`, `--brand-green`, etc.), while `BillingSection.tsx` and `PullIngressSettings.tsx` hardcode hex literals (`#0B1A2F`, `#5E6779`, `#2E8E3A`, `#E5E8EE`, `#FFFFFF`, `#CBD5E1`).

### Data shown
- **Organization** — `organization.name`, `organization.membersCount` (Clerk `useOrganization()`); fixed strings "EUR — Euro" / "EU". Direction from `getOrgSettings()` → `OrgSettings { direction, slug }` (mock: `{ direction:"outbound", slug:"demo-workspace" }`); saved via `updateOrgSettings(direction)`.
- **Billing** — `getBillingStatus()` → `BillingStatus`: `plan`, `accountStatus`, `ordersThisMonth`, `orderLimit`, `suppliersUsed`, `supplierLimit`, `trialStartedAt`, `trialEndsAt`, `isTrialExpired`, `isOrderLimitReached`, `isSupplierLimitReached`, `canProcessOrders`, `canAddSupplier`, `stripeCustomerId`, `stripeSubscriptionId`, `overageOrders`, `overageAmountEur`, `nearLimit`, `atLimit`, `billingInterval`. Endpoint `GET /api/billing/status`. **Mock returns Pilot** (`plan:"pilot"`, 5/20 orders, 1/1 suppliers, 9 days trial left). Checkout via `createCheckoutSession(plan, interval)` → `POST /api/billing/checkout`; portal via `createPortalSession` → Stripe portal.
- **Email** — `getEmailSettings()` → `EmailSettings { enabled, host, port, useSsl, username, folder, defaultSupplierId, hasPassword, passwordDisplay, lastPolledAt, updatedAt }`, `GET /api/settings/email`, saved via `updateEmailSettings` (`PUT`). Supplier list from `apiClient.getSuppliers()` (`GET /api/suppliers`; mock = 4 suppliers: FastParts Inc, ElectroSupply Co, GlobalComponents, PrecisionMfg).
- **SFTP** — `getSftpSettings()` → `SftpIngressSettings { enabled, host, port, username, remoteDirectory, defaultSupplierId, hasPassword, passwordDisplay, updatedAt }`, `GET/PUT /api/settings/sftp`.
- **S3/R2** — `getS3Settings()` → `S3IngressSettings { enabled, bucketName, keyPrefix, region, accessKeyId, defaultSupplierId, hasSecretKey, secretKeyDisplay, updatedAt, serviceUrl }`, `GET/PUT /api/settings/s3`.
- **API keys** — `getApiKeys()` → `ApiKey[] { id, label, keyPrefix, isActive, createdAt, lastUsedAt, expiresAt }`, `GET /api/api-keys`. Create → `createApiKey(label)` returns `{ ...ApiKey, rawKey }` (`POST`); revoke → `revokeApiKey(id)` (`DELETE`). Ingress URL built from `apiBaseUrl` + `orgSettings.slug`. **Mock seeds an EMPTY keys array.**
- **Connectors** — `getIntegrations()` → `IntegrationSubscription[] { id, platform, eventType, targetUrl, isActive, failureCount, createdAt, updatedAt }`, `GET /api/integrations`. Create `POST /api/integrations`, toggle `PATCH /api/integrations/{id}/toggle`, delete `DELETE`. **Mock seeds EMPTY.** Event labels: `order.created`, `order.delivered`, `order.failed`. Platform labels: Zapier, Make.com, Custom.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Left-nav tab buttons ×7 | `setTab(id)` | Swaps the rendered section in place; sets `aria-current`. Does NOT write `?tab=` to the URL |
| **Org:** Workspace name input | controlled `setName` | Local state; disabled until Clerk hydrates |
| **Org:** "Save changes" (green) | `organization.update({ name })` via Clerk | Inline ok/err feedback; rejects empty / no-change |
| **Org:** Direction radio cards ×2 | `choose(direction)` → `updateOrgSettings` mutation | Persists; invalidates `["org-settings"]`; relabels app; arrow-key navigable |
| **Billing:** "Change plan" / "Manage in Stripe" / "Edit" (billing email) | `portalMutation.mutate()` | `window.location.href` → Stripe customer portal (leaves app) |
| **Billing:** Monthly/Annual `IntervalToggle` | `setCheckoutInterval` | Local; feeds checkout |
| **Billing:** "Upgrade to Growth"/"Upgrade to continue" (green) | `checkoutMutation.mutate({plan:"growth", interval})` | `window.location.href` → Stripe Checkout |
| **Billing:** Operations / Integration / Distributor chips | `checkoutMutation.mutate({plan, interval})` | Stripe Checkout |
| **Billing:** "Upgrade to {nextPlan}" (paid) | `checkoutMutation.mutate` | Stripe Checkout |
| **Billing:** "Contact sales" / "Book a 15-min demo →" | mailto / external link | Opens mail client / demo URL (env-gated) |
| **Billing:** Retry (error state) | `refetch()` | Re-runs billing query |
| **Email:** "Poll inbox for orders" `ToggleSwitch` | `update("enabled", v)` | Local; disabled if Pilot or no suppliers (when off) |
| **Email:** host/port/SSL/username/password/folder inputs | controlled updaters | Local form state |
| **Email:** password "Clear" button | clears + `hasPassword=false` | Write-only credential reset |
| **Email:** Default-supplier `<select>` | `update("defaultSupplierId")` | Local; empty state links to `/library/suppliers` |
| **Email:** "Save email" (green) | client-validate → `mutation.mutate` | `PUT /api/settings/email`; inline saved/err |
| **Email:** "Upgrade to Growth (€149/mo)" link | `next/link` | `/settings?tab=billing` |
| **Email:** Retry (error) | `refetch()` | Re-run email-settings query |
| **SFTP/S3:** enable checkbox `Toggle` | `setEnabled` | Local; disabled if Pilot/no-suppliers (when off) |
| **SFTP/S3:** all credential inputs | controlled setters | Local form state |
| **SFTP/S3:** `SupplierSelect` | `setDefaultSupplierId` | Local; no-suppliers empty links to `/library/suppliers` |
| **SFTP/S3:** "Save" (green) | client-validate → `save.mutate` | `PUT /api/settings/sftp` or `/s3`; inline `Notice` |
| **SFTP/S3:** "Retry connection" (error) | `refetch()` | Re-run query |
| **API keys:** ingress "Copy" | `navigator.clipboard.writeText(endpoint)` | "Copied!" 2s; disabled if no slug |
| **API keys:** new-key banner "Copy" | clipboard | "Copied!" 2s |
| **API keys:** "I've saved it, dismiss" | `setNewKey(null)` | Hides one-time key banner |
| **API keys:** "Create key" button | `setShowCreate(true)` | Reveals inline create form |
| **API keys:** create-form label input | `setNewLabel` + Enter-to-submit | Feeds create |
| **API keys:** "Create key" (form, green) | `create.mutate(label)` | `POST /api/api-keys`; shows raw-key banner; closes form |
| **API keys:** create-form "Cancel" | `setShowCreate(false)` | Closes form |
| **API keys:** "Revoke" (row) | `InlineConfirm` → `revoke.mutate(id)` | Inline confirm pair → `DELETE`; row dims + "Revoked" pill |
| **API keys:** Retry (error) | `refetch()` | Re-run query |
| **Connectors:** "Add webhook" (header + empty-state) | `setShowForm(v=>!v)` / `true` | Reveals inline webhook form |
| **Connectors:** platform/event `<select>`, target-URL/secret inputs | controlled setters | Local form |
| **Connectors:** "Save webhook" (green) | `create.mutate()` | `POST /api/integrations`; closes form |
| **Connectors:** form "Cancel" | `setShowForm(false)` | Closes form |
| **Connectors:** row "Pause"/"Resume" | `toggle.mutate(id)` | `PATCH .../toggle`; flips status pill |
| **Connectors:** row trash icon | `InlineConfirm` → `remove.mutate(id)` | Inline confirm → `DELETE` |
| **Connectors:** Retry (error) | `refetch()` | Re-run query |

### What opens / what closes
This page has **no true modals, drawers, sheets, popovers, dropdowns, or toasts**. Every "transient" surface is an **inline expand/collapse panel or an inline confirm swap** rendered within the content column — there is no scrim, no portal, no Escape handling, no focus trap, and no animate-from-trigger. The one-time API key reveal — which a normal app would model as a modal — is an inline banner.

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Create-API-key form | Inline panel (bordered box, `showCreate` state) | "Create key" button | Label `<input>` (autofocus, Enter-submits), "Create key" + "Cancel" buttons, create error line | "Cancel", or successful create (`onSuccess` sets `showCreate=false`). **No Esc / no backdrop** |
| New-API-key reveal banner | Inline banner (green, `newKey` state) | Successful `createApiKey` | "API key created — copy it now", warning it can't be retrieved, mono raw key, "Copy" button, "I've saved it, dismiss" link | "I've saved it, dismiss" only. **No Esc, persists until dismissed; navigating tabs while mounted keeps state** |
| Add-webhook form | Inline panel (`showForm` state) | "Add webhook" (header toggle, or empty-state CTA) | Platform `<select>`, Event `<select>`, Target URL input, Signing-secret password input, "Save webhook" + "Cancel", create error | "Cancel", header toggle again, or successful create. **No Esc / no backdrop** |
| Revoke-key confirm | Inline confirm swap (`InlineConfirm`, `role="group"`) | "Revoke" link/button in a key row | Prompt "Break any integration using it?" + red "Revoke" + "Cancel" | "Cancel" (reverts to trigger) or "Revoke" (fires + reverts). **No Esc** |
| Delete-webhook confirm | Inline confirm swap (`InlineConfirm`) | Trash icon in a subscription row | Prompt "Delete this webhook?" + red "Delete" + "Cancel" | "Cancel" or "Delete". **No Esc** |
| Tooltips | Native `title=""` only | Hover on revoke button (`title="Revoke key"`), trash (`title="Delete webhook subscription"`), truncated webhook URL (`title={targetUrl}`) | Browser-native tooltip text | Mouse-out (browser-controlled) |
| Stripe Checkout / Portal | Full navigation (NOT an overlay) | Upgrade / Change plan / Manage in Stripe | External Stripe-hosted page | Leaves the app via `window.location.href` |

Inline transient feedback (not overlays): copy "Copied!" labels (2s timeout), org/email/SFTP/S3 "Saved" inline text, validation `role="alert"` lines, button label swaps ("Saving…", "Creating…", "Opening…").

### States
- **Empty:**
  - API keys: dashed-border centered empty card — `Key` icon, "No API keys yet", "Create a key to post orders to the REST API or a custom webhook integration." (shown only when not creating).
  - Connectors: dashed-border centered card — "No webhooks yet" + explanatory line + a green "Add webhook" CTA (the empty state HAS a primary action — good).
  - Default-supplier (Email/SFTP/S3 when org has zero suppliers): dashed inline box "No suppliers yet — add one first →" linking to `/library/suppliers`. (In mock mode suppliers are populated, so this empty state does NOT appear under `?tab=email`.)
  - Org "About this workspace" is a fixed info block, never empty.
- **Loading:**
  - Route-level `loading.tsx` → `BridgePageLoader label="Loading settings…"` (full-page brand loader on first navigation).
  - Email: bespoke skeleton (title bar + 3-col skeleton boxes), `role="status" aria-busy`.
  - SFTP/S3: `LoadingShell` skeleton (title + stacked field boxes).
  - Billing: bespoke skeleton (title bar + 2 pill rows).
  - API keys: 2 skeleton row-boxes, `role="status"`.
  - Connectors: a bare text line **"Loading webhooks…"** (the weakest — no skeleton).
- **Error:** Each data section has a dedicated "unavailable" panel (white card, `--danger-soft` border + 3px `--danger` left rule, reassuring "your data is safe", Retry button). Email = "Email settings are unavailable", API keys = "API keys unavailable", SFTP/S3 = "{title} settings are unavailable", Billing = "Billing is temporarily unavailable", Connectors = inline "Webhooks unavailable" panel. Mutation errors render inline (`role="alert"` or red `<p>`). Org-name save errors show inline next to the Save button.
- **Success/feedback:** All inline (no toast system). Org: "Workspace name updated." Direction: "Saved. Labels updated across the app." Email: "Email settings saved." (auto-clears after 4s). SFTP/S3: green `Notice` "…settings saved." Copy actions: "Copied!" (2s). New API key: persistent green banner until dismissed.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two-column layout, 200px left nav + fluid content. `PageShell` is the **narrow** variant (`--container-narrow`), so content is centered and capped — at 1920 there is wide empty gutter on both sides. Desktop API-keys uses the dense `<table>`.
- **Tablet 768:** Still desktop layout above the 767px breakpoint (the media query is `max-width:767px`), so at exactly 768 the two-column grid and the table persist. The IMAP host/port/security 3-col grid can feel cramped between ~768–900px.
- **Mobile 390 (`max-width:767px`):** Left nav transforms from a vertical list into a **2-column command grid** (surface-2 rounded box, 5px padding, centered tab labels min-height 40px, last odd item spans full width). Content stacks to 1 column (`gap:16`). All inputs forced to `16px` font (prevents iOS zoom). IMAP connection grid collapses to 1 column. Connector rows wrap and the action goes full-width. API-keys table is `hidden md:table`; mobile shows stacked row-cards with a full-width "Revoke" button. Create-key and webhook form buttons go full-width.
- **Known cliffs:** `PullIngressSettings.tsx` uses Tailwind `sm:` breakpoints (640px) for its field grids while the page nav switches at 767px — so the SFTP/S3 forms reflow to multi-column at 640px, but the page is still in single-column "mobile" nav mode between 640–767px (mismatched breakpoint systems). The Billing tab is not inside `PageShell`'s mobile-input font override scope in the same way (it's a separate component with its own hex styles), so its controls don't get the `16px` mobile bump.

### Current UX issues
- **No real overlay system.** The one-time API-key reveal — a security-critical "copy this now, it's gone forever" moment — is an inline banner with no scrim, no focus trap, and no Escape. It can be scrolled past and the page can be tab-switched while it's showing. This is the single most important thing to elevate to a proper modal (BAR: modals have scrim + clear close + animate-from-trigger). Same for the create-key and add-webhook forms, which should be modals or at least properly framed disclosures.
- **Two parallel color systems.** The page uses CSS variables; `BillingSection.tsx` and `PullIngressSettings.tsx` hardcode hex literals for the same colors (`#0B1A2F` = `--ink`, `#5E6779` = `--ink-muted`, `#E5E8EE` = `--border`, `#2E8E3A` = `--brand-green`, `#CBD5E1`/`#CBD2DE`/`#D5DAEA`/`#D5DAE5` for disabled/border greys). Disabled buttons use `#CBD5E1` in some places and `var(--ink-faint)` in others — visibly inconsistent. (BAR: one token set, no gray-on-gray drift.)
- **Spacing & type scale drift.** Font sizes span `10.5/11/11.5/12/12.5/13/13.5/14/14.5/19/24/28`; control heights span `28/30/32/34/36/38/40`; input heights differ per section (40 vs 36 vs 32). No strict 4/8 rhythm. (BAR: one spacing rhythm, one type scale.)
- **Inconsistent status pills.** Connectors uses three different pill shapes for state: a rounded-999 green dot "Active" pill, a square `radius:4` grey "Paused" pill, and a square red "{n} failures" pill. API keys uses yet another square grey "Revoked" pill. The "FIXED" org pill is a fourth shape. There is no single badge system. (BAR: one badge shape/size/padding with green/amber/red/neutral semantics + icon-or-word.)
- **Numbers aren't uniformly tabular.** Billing usage figures and prices use `'JetBrains Mono'` (good), but the API-keys table dates (`createdAt`, `lastUsedAt`) and connector counts are default proportional figures, so columns can jitter. (BAR: tabular figures for all numbers/timestamps/counts.)
- **Weak loading state on Connectors.** Bare "Loading webhooks…" text vs skeletons everywhere else. (BAR: skeleton, not bare text/spinner.)
- **Two tab systems for what's conceptually one thing.** Email / SFTP / S3 are three separate top-level tabs that are all "automated intake channels" with near-identical forms (enable toggle + credentials + default supplier + save + paid-gate). This is a lot of left-nav weight for one concept and forces the user to hunt across tabs. (BAR: consolidate; one density.)
- **No primary-action dominance per screen.** Each section has its own green Save; the left nav, Stripe links, secondary plan chips, and copy buttons compete. There's no single dominant action on the hub. (BAR: one primary action per screen, ≥44px.)
- **Tap targets below 44px.** Many controls are 28–34px tall (connector Pause/Resume = 28, "Edit" billing email = 27, revoke text button has tiny padding). Only some have `min-height: var(--tap-min)`. (BAR: ≥44px interactive + visible hover/pressed.)
- **`?tab=` is not written on manual clicks.** Switching tabs via the nav doesn't update the URL, so a chosen tab isn't shareable/bookmarkable/back-restorable unless arrived at via a deep link. Mild predictability gap.
- **Honesty caveat handled well but visually buried.** "Native Zapier & Make.com apps — Coming soon" is correct (offer⇔works), but the grey "Coming soon" pill is easy to miss; the distinction between the working REST path and the not-yet path relies on subtle grey vs white card backgrounds.
- **`PullIngressSettings` field labels are 11px uppercase grey** (`#98A0AE` faint) — below comfortable contrast for label text and inconsistent with the page's 12.5px/600 `--ink-muted` `fieldLabelStyle`.

### Redesign recommendations (for Claude Design)
1. **Promote the one-time API-key reveal to a real modal** (navy/violet scrim, focus-trapped, Esc-closable, animate-from-trigger), with the key in a mono field, a single dominant green "Copy key" primary, and an explicit "I've saved it" to close. This is security-critical and currently the weakest pattern on the page. Model the create-key and add-webhook flows as the same modal component.
2. **Consolidate Email + SFTP + S3/R2 into a single "Intake channels" tab** (one section with three channel cards or sub-tabs sharing one form skeleton: enable toggle → credentials → default supplier → save). Shrinks the 7-tab nav, removes triplicated UI, and matches the "one density" bar. Keep API keys + Connectors as the "Outbound / API" grouping.
3. **Kill the second color system.** Refactor `BillingSection.tsx` and `PullIngressSettings.tsx` to use the same CSS variables as the page (`--ink`, `--ink-muted`, `--border`, `--brand-green`, `--surface`, disabled = one token). No raw hex. Fixes the visible disabled-button and label-grey inconsistencies.
4. **Adopt one status-badge component** for every state pill (Active / Paused / Revoked / Coming soon / Fixed / failure-count): one shape (rounded-full), one size/padding, green/amber/red/neutral semantics, always an icon + word, never color alone. Use red+warning-icon for "{n} failures" and never show a green "Active" when `failureCount > 0`.
5. **Standardize the form system:** one input height (40), one label style (13px/600 `--ink-muted` with helper text below and error below the field), write-only credential affordance ("•••• — leave blank to keep" + a Clear button) applied uniformly to Email/SFTP/S3 passwords. Enforce a strict 4/8 spacing scale for all paddings/gaps.
6. **Make the API-keys table the canonical dense table** (single row height, low-contrast `gray-200` gridlines, sticky header, tabular-figure dates, hover) and reuse that table for the webhook subscriptions list instead of bespoke flex rows.
7. **Apply tabular figures everywhere numbers appear** — API-key dates, last-used, failure counts, member count, usage bars (already mono), trial days. So columns and money never jitter.
8. **Raise every interactive control to ≥44px** with visible hover + pressed + focus-visible ring (the global `--brand-blue` ring exists; ensure it lands on all the inline-styled buttons). Fix the 27/28px Pause/Resume/Edit controls.
9. **Give each section one dominant primary action** (green Save, ≥44px, bottom-right of its card) and demote Stripe/secondary plan chips and copy buttons to outline/ghost. On the hub level, consider a sticky per-section save bar.
10. **Replace the bare "Loading webhooks…" text with a skeleton** matching the rest, and make every data surface's empty/loading/error states consistent (the dashed-empty + danger-rule-error + skeleton triad already exists in most sections — standardize it as one set of components).
11. **Write `?tab=` on manual tab clicks** (replaceState) so any settings sub-screen is shareable and back/forward works predictably; keep the existing deep-link sync. Add active-state + a breadcrumb-style indicator if the hub gains nested depth.
12. **Strengthen the offer⇔works distinction visually:** keep "Coming soon" but give the not-yet-published native connector a clearly disabled treatment (muted card + lock/clock icon + the badge) so it never reads as clickable, while the working REST/webhook path leads with the green primary.
