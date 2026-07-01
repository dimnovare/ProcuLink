## 21. Connectors — `/operations/connectors`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/connectors/page.tsx`
- **Key components:** (all defined *inline* in `page.tsx` — there is no separate `ConnectorRequirementsPanel.tsx` file; the requirements panel logic was inlined as `ManifestRequirementsSection`)
  - `ConnectorsPage` (default export) — page container, grid, state
  - `ConnectorCard` — one connector tile
  - `ConnectorStatusPill` — the Connected / Available / Coming soon pill
  - `SkeletonConnectorCard` — loading skeleton tile
  - `ConnectorPanel` — the read-only configuration **modal/slideover** opened by Manage / Connect / Add connector
  - `ManifestRequirementsSection` + `ManifestFieldGroup` + `ManifestFieldRow` — the "What this connector needs" block inside the panel
  - `PanelField`, `PlugIcon`, `PlusIcon` — small primitives
  - Shared: `src/components/bridge/layout/PageShell.tsx`, `src/components/bridge/layout/PageHeader.tsx`, `src/components/bridge/EmptyState.tsx`, `src/components/bridge/BridgeLoader.tsx` (via `loading.tsx`)
  - Data: `getSuppliers`, `testFireDeliveryConfig`, `isApiMockMode` from `src/lib/api-client.ts`; `getConnectorManifest` from `src/lib/api/connectors.ts`
- **Capture URL (mock):** `/operations/connectors` (mock mode renders the 6 hard-coded `MOCK_CONNECTORS` immediately; no id/query needed)

### What it is & why it exists
This is the **deliver-side** channel inventory for the parse→…→deliver→learn loop: it answers "through which transports/ERPs can ProcuLink hand a finished PO to a supplier (and pull orders in)?" It lists integration channels (HTTP/REST, SFTP, Email IMAP/SMTP, Erply/Directo ERP) and ERP connectors (SAP Ariba, Coupa, Dynamics 365 — currently "Coming soon"). A procurement coordinator opens it to review what's wired up and to discover that **actual** delivery endpoints + credentials are configured *per supplier* (on the supplier's Delivery tab) — this page is intentionally a read-only directory/launcher, not the place credentials are entered.

### Who uses it & the primary job
**Integration expert / operator** (occasionally an admin). The single most important task: confirm a transport is available and **jump to the right place to actually configure it** — i.e. click Manage/Connect → read the requirements → "Open supplier Delivery tab", or test-fire an existing supplier's delivery config.

### Layout & structure (current)
Top-to-bottom, inside `PageShell variant="wide"` (max-width `--container-wide` = 1480px; gutter ramp 16→24→34px, vertical 20→28px):

1. **`PageHeader`** — title `Connectors` (Bricolage Grotesque, 28–30px, weight 600); subtitle is conditional: mock mode shows `ERP and channel integrations · {N} connected`, live mode shows just `ERP and channel integrations` (the count is suppressed in live mode because it is not derivable — see Data shown). Right-aligned **green primary** "Add connector" button (`.connectors-addbtn`, 32px tall, brand-green `#2E8E3A`, white text, `+` icon).
2. **Notice banner** (conditional) — green left-border (3px `--brand-green`) success strip; only rendered when `notice` state is set. (In current code `notice` is set to `null` on every action and never set to a message — it is effectively dead UI.)
3. **Error banner** (conditional, live mode only) — red/danger strip "Failed to load connectors. [Retry]".
4. **Body** — either: loading skeleton grid (3 skeleton cards), `EmptyState`, or the **connector card grid**.
   - Grid: inline `<style>` block — `repeat(3, 1fr)` with `gap: 16px`; collapses to `1fr 1fr` ≤1100px and `1fr` ≤640px.
   - Each card: white surface, 1px `--border` (#E2E6EE), radius `--radius-md` (8px), **padding 18px**, flex column. Top row = 40×40 grey icon tile (Lucide plug icon) + status pill. Then name (14px / 600) + description (12px muted). A 1px top-border footer (margin-top 14, padding-top 12) holds the usage count (left) + single action button (right).

Density/type/spacing observations: this page is built almost entirely with **inline `style={{}}`** and hard-coded pixel values (18px card padding, 14px gaps, 11.5/12/12.5/14/16px font sizes, `height: 27` action buttons, `height: 32` header/footer buttons) rather than Tailwind tokens — it drifts off the 4/8 scale (27px buttons, 11.5px text, gap 5/6/7/14 mixed). Numbers (the "3 suppliers" count) are not tabular-figured.

### Data shown
- **Mock mode (`isApiMockMode`):** a static array `MOCK_CONNECTORS` (6 rows) with fields: `id`, `type` (e.g. "cXML PunchOut", "ERP (REST)", "EDI (SFTP)", "Email (IMAP)", "ERP — Erply"), `name` (SAP Ariba, Coupa, Microsoft Dynamics 365, Generic SFTP, Email (IMAP), Erply), `status` ("coming_soon" | "connected" | "available"), `desc`, `docks` (supplier count), `direction` ("in" | "out"). `connectedCount` = count of `connected`/`ok` rows.
- **Live mode:** `GET /api/suppliers` via `getSuppliers` (`realGetSuppliersFn`). The endpoint returns only `{ id, name }`. The page maps **each supplier** into a connector row with `type: "API (REST)"`, `status: "available"` (always — never "connected"), `desc: "Supplier delivery endpoint"`, `docks: -1` (unknown → the usage footer line is hidden), `direction: "out"`. `connectedCount` is forced to `0` and the subtitle count is dropped — an explicit **offer⇔works honesty** decision because the list payload carries no delivery-config signal.
- **Inside `ConnectorPanel`:** `getConnectorManifest(key)` → `GET /api/connector-manifests/{key}` (`connectors.ts`). Returns `ConnectorManifest` { key, displayName, transport, authType, capabilities, docsRef, fields[] }, each field `{ name, label, type, required, secret, helpText }`. Manifest key is derived from the connector's free-text `type` via `resolveManifestKey()` (sftp/ftps/smtp/erp_erply/erp_directo/http; null = section hidden). Test-fire uses `testFireDeliveryConfig(connector.id)` → `POST /api/suppliers/{id}/delivery-config/test-fire`.

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| "Add connector" button (header, green primary) | `setSelected({ id:"new", … })` | Opens `ConnectorPanel` in **new/Add** mode |
| Connector card "Connect" button (status `available`) | `onManage(connector)` → `setSelected(c)` | Opens `ConnectorPanel` for that connector |
| Connector card "Manage" button (status `connected`, ghost text) | `onManage(connector)` → `setSelected(c)` | Opens `ConnectorPanel` for that connector |
| Connector card "Request access" link (status `coming_soon`) | `mailto:sales@proculink.eu?subject=Connector request: {name}` | Opens the OS mail client (not connectable — honest dead-end alternative) |
| Error banner "Retry" button | `queryClient.invalidateQueries(["suppliers"])` | Re-fetches the suppliers query |
| EmptyState "Add connector" button | `setSelected({ id:"new", … })` | Opens `ConnectorPanel` in Add mode |
| Panel "×" close (top-right, `aria-label="Close"`) | `onClose()` → `setSelected(null)` | Closes the panel |
| Panel "Cancel" (footer, outline) | `onClose()` → `setSelected(null)` | Closes the panel |
| Panel "Test fire" (footer) | `handleTestFire()` → `testFireDeliveryConfig(id)` (or informational message if `id==="new"`) | Shows inline test-result banner inside the panel; closes nothing |
| Panel "Open supplier Delivery tab" (footer, green primary `<Link>`) | navigates + `onClose()` | `/library/suppliers` (new) or `/library/suppliers/{id}?tab=delivery` |
| Manifest "{displayName} documentation" link (if `docsRef`) | opens `docsRef` in new tab | External (e.g. learn.erply.com, wiki.directo.ee) |
| Panel read-only inputs (Connector type / Name or endpoint / Direction / Status) | none (`readOnly`) | Display only — greyed surface-2 fields |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **ConnectorPanel** | Modal overlay (fixed full-screen scrim, centered card on desktop / bottom-sheet on mobile) | "Add connector" (header), "Connect"/"Manage" (card), "Add connector" (EmptyState) — all `setSelected(...)` | Header (plug icon tile + name + "Connector configuration"); blue-bordered info note ("Delivery endpoints … configured per supplier … This panel is read-only"); 4 read-only fields (Connector type, Name or endpoint, Direction, Status); the **ManifestRequirementsSection** (if a manifest key resolves); a test-result banner (after Test fire); footer with Cancel / Test fire / Open supplier Delivery tab | "×" button, "Cancel" button, or "Open supplier Delivery tab" (navigates + closes). **No Esc handler and no backdrop-click handler** — clicking the scrim does NOT close it |
| **ManifestRequirementsSection** | Inline panel *inside* ConnectorPanel (not separately dismissible) | Auto-renders when `resolveManifestKey()` returns non-null | "What this connector needs · {authType}"; field groups **Required / Credentials (stored encrypted) / Optional**, each row = monospace field `name` + `label` + Required/Secret/type chips; optional docs link | Closes with the parent panel only |
| **Test-result banner** | Inline status strip (`role="status"`, `aria-live="polite"`) inside the panel | "Test fire" button | success (green) / failure (danger `Failed — …`) / informational (neutral blue, for `id==="new"`) message | Replaced on next Test fire; cleared when panel closes |
| Notice banner (green) | Inline strip above the grid | `setNotice(...)` — **never actually called with a string** in current code (dead) | n/a (would be a success message) | n/a |
| Error banner (red) | Inline strip above the grid | live-mode query error | "Failed to load connectors. [Retry]" | disappears on successful refetch |

No toasts, no dropdowns, no tooltips, no popovers. There is **one** real overlay: `ConnectorPanel`.

### States
- **Empty:** handled — `EmptyState` ("No connectors configured" + sub + "Add connector" button) renders when `connectors.length === 0`. In live mode this means a real org with zero suppliers; mock mode never hits it (6 hard-coded rows). Note the EmptyState's own button is **navy** (`--navy #0B1A2F`), inconsistent with the page's green primary "Add connector".
- **Loading:** handled — skeleton **only in live mode** (`isLoading && !isApiMockMode`) renders 3 `SkeletonConnectorCard`s (pulse animation `skel-pulse`). `loading.tsx` (route-level `BridgePageLoader` "Loading connectors…") shows during the Next.js route transition before the client component mounts. Mock mode shows no loader (synchronous).
- **Error:** handled (live only) — red banner with Retry. Manifest fetch failures inside the panel **degrade silently** (`if (!manifest) return null;`) — no error shown.
- **Success/feedback:** the in-panel test-result banner (green/red/blue) is the only confirmation. The dead `notice` banner was clearly intended for "saved"/"connected" feedback but is never populated.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** grid `repeat(3,1fr)`, gap 16; panel centered at `max-w-[540px]`, radius 10, scrim `rgba(11,26,47,0.42)` + `blur(3px)`.
- **Tablet 768 (≤1100px):** grid drops to **2 columns** (`1fr 1fr`).
- **Mobile 390 (≤640px):** grid is **1 column** (cards stack — good). The panel becomes a **bottom sheet** (`items-end`, `rounded-t-[10px]`, `max-h-[92vh]`). Card action buttons get `min-height: 40px` and "Add connector" goes full-width (`flex: 1 0 100%`, height 40). The 27px desktop action height is below the 44px touch target on desktop; the mobile override only lifts it to 40px (still <44).
- Cliffs: the visible action chrome stays 27px tall on desktop/tablet (small touch target); the panel footer (Cancel / Test fire / Open Delivery tab, 3 buttons) `flex-wrap`s and can crowd at narrow widths.

### Current UX issues
- **Two competing concepts (#7 one primary action / honesty):** the page is titled/structured as a connector *config* surface, but it's actually read-only — every path funnels to "Open supplier Delivery tab." "Connect"/"Add connector" imply you can configure here; you can't. This is the biggest comprehension gap.
- **Status taxonomy isn't one system (#4):** three pill styles — `.pill-ready` (green "Connected"), `.pill-new` (neutral "Available"), and a **bespoke inline amber** "Coming soon" pill (not a shared class, different padding 2px 9px / fontSize 11.5 vs the `.pill` 21px height / 11px). Coming-soon should be a real neutral/disabled token, not a one-off.
- **Modal accessibility gaps (#9 + modal rules):** no `Esc` to close, no backdrop-click to close, no focus trap, no `role="dialog"`/`aria-modal`. The only exits are the "×", Cancel, and the navigating CTA.
- **Inline-style drift / no token discipline (#1, #2):** the whole page is hand-styled with magic pixels (27/32/34/40px heights; 5/6/7/14px gaps; 10.5/11/11.5/12/12.5/14/16px font sizes). No shared Button/Card/Pill components — diverges from the unified design system primitives.
- **Card action targets too small (#9):** 27px-tall "Connect"/"Manage"/"Request access" controls; below the 44px bar except where the ≤640px override bumps to 40px.
- **Dead "notice" banner (#6):** wired up but never populated — code path that renders nothing, signalling unfinished feedback design.
- **Inconsistent primary color (#7):** header "Add connector" = green; EmptyState "Add connector" = navy; Test fire = ghost green-text outline. No single visually-dominant primary per surface.
- **Required/Secret/type chips are dense and mono (#2):** field rows lead with monospace `field.name` (e.g. `remotePath`, `toAddresses`) — machine keys before the human label, against the "lead with the human field name" rule.
- **Usage count not tabular (#3):** "3 suppliers" / "Not in use" use the default proportional figures; live mode hides the count entirely, so cards lose a useful signal.
- **`docs/coming-soon` ERP tiles are visually identical to live ones** apart from the amber pill — at a glance SAP Ariba/Coupa look as wired as Generic SFTP.

### Redesign recommendations (for Claude Design)
1. **Reframe the page honestly as a "Channels & connectors" directory (most impactful).** Make it unmistakable that real config lives per-supplier: rename the panel to "Connector overview", keep the blue per-supplier note as the panel's lead, and make "Open supplier Delivery tab" the single dominant green primary (#1 primary-action rule). Demote "Connect"/"Manage" wording to "View" since nothing is configured here.
2. **Unify the status pill into ONE badge system** (#4): one shape/size/padding for Connected (green + check), Available (neutral + word), Coming soon (neutral/amber "soon" + clock), Failed (red) — each with icon+word, never color alone. Replace the bespoke inline amber pill with a token-driven class. Keep navy/violet brand.
3. **Fix the modal (#9 + modal rules):** add `role="dialog" aria-modal`, `Esc`-to-close, backdrop-click-to-close, focus trap, and animate-from-trigger; keep the bottom-sheet on mobile. Move the "×" to a 44px target.
4. **Replace inline styles with the shared primitives** (Card, Button, Pill, FieldRow) on the 4/8 spacing scale and the one type scale (heading 600 / label 500 / body 400). Buttons ≥44px with focus-visible ring, hover, pressed states (#1, #2, #9, #8).
5. **Lead field rows with the human label, demote the machine key** (#2/transform rule): show "Endpoint URL" prominently, `url` as a muted monospace caption — not the reverse.
6. **Standardize cards & elevation** (#8): one radius (8px), one border (`--border`), one shadow tier for cards and a separate tier for the panel; remove the ad-hoc `0 8px 24px` shadow if it doesn't match the system token.
7. **Make "Coming soon" tiles visibly distinct** (lower elevation / reduced opacity / lock affordance) so unbuilt ERPs (SAP Ariba, Coupa, Dynamics 365) never read as wired — reinforces offer⇔works.
8. **Restore a real usage signal** once the suppliers list (or a dedicated connectors endpoint) exposes delivery-config presence: show a tabular-figure count and a true Connected pill. Until then keep the honest "Available / needs setup" state but add a clear "Set up in supplier →" affordance per card.
9. **Replace the dead `notice` banner** with a real toast/inline confirmation system (success on test-fire, error on failure) so feedback isn't only buried in the panel.
10. **Add a real empty AND loading state for mock parity**: align the EmptyState button color to the page primary (green), and ensure the skeleton matches the final card geometry (it currently uses 20px padding vs the card's 18px — flag this drift, #1).
