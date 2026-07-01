## 08. Connection detail — `/connections/[connectionId]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/connections/[connectionId]/page.tsx` (thin server wrapper; awaits `params.connectionId`, renders `<ConnectionDetail>`)
- **Key components:**
  - `src/components/connections/ConnectionDetail.tsx` (page body — orchestrator)
  - `src/components/connections/useConnectionRevisions.ts` (data + every lifecycle mutation, shared with the supplier History tab)
  - `src/components/connections/HistoryDrawer.tsx` (right slide-over: `HistoryDrawer` + the extracted `HistoryContent` / `TestEvidenceSummary` / `ConfigSummary`)
  - `src/components/connections/ConnectionLifecycleUI.tsx` (`ConnectionNotice` inline banner + `ConnectionConfirmDialog` confirm modal)
  - `src/components/connections/RevisionStatusBadge.tsx` (lifecycle pill: Draft / Tested / Live / Previous)
  - `src/components/connections/ReplayPanel.tsx` (replay/impact preview, rendered inside the drawer)
  - `src/components/bridge/mapper/MapperWorkbench.tsx` (`variant="connection"` three-pane mapper, read-only over the live revision with an Edit overlay)
  - Layout chrome: `src/components/bridge/layout/PageShell.tsx` (`variant="wide"`, max-width `--container-wide`), `PageHeader.tsx`, `layout/Card.tsx`, `DSPrimitives.tsx` (`Button`)
- **Capture URL (mock):** `/connections/conn-11111111-1111-1111-1111-111111111111` (the prompt's `/connections/conn-1` is NOT a valid mock id — mock ids are `conn-<supplierGuid>`, seeded from `MOCK_SUPPLIERS.slice(0,2)`. This first one = "FastParts Inc", which has the richest state: a **published v1** (so the live summary populates) **plus a draft v2** in history.)

### What it is & why it exists
This is the single per-supplier "connection" surface: it shows exactly how one supplier's incoming orders are mapped, validated and delivered, and it holds the **version history** behind plain verbs. In the `Parse → Normalize → Validate → Review → Transform → Deliver → Learn` loop it owns the *configuration authored once per supplier* (the mapping/output/delivery/item-codes/acceptance bundle) rather than a single order. A procurement coordinator opens it to (a) see what's live for this supplier today, (b) edit the mapping safely (editing clones the live version into a draft), and (c) test, make live, restore an old version, or replay recent orders to see what would change — without ever touching a live order.

### Who uses it & the primary job
**Procurement coordinator** (with an integration-expert overlap for the mapper). The single most important task: **edit the mapping and then make it live** — the header's one primary button toggles between "Create mapping" / "Edit mapping" (when nothing is live yet vs. live exists), and once a draft is open the "Make live" action moves into the drawer's draft row. The whole revision lifecycle (draft/test/publish/archive/rollback) is deliberately disguised behind plain verbs: Edit mapping · Test · Make live · Restore this version · Discard.

### Layout & structure (current)
`PageShell variant="wide"` (centered, max-width `--container-wide`, gutter `px-4 → sm:px-6 → lg:px-[34px]`, vertical `py-5 → sm:py-7`).

1. **PageHeader** (top): title = `connection.name` (e.g. "FastParts Inc"), sub = "How this supplier's orders are mapped, validated and delivered". Right-aligned `actions` cluster, in order:
   - `← All connections` link (outline, `h-44/sm:32`, surface bg + border).
   - `↺ History & advanced ⌄` quiet outline button (only when `connection` loaded; `aria-haspopup="dialog"`, `aria-expanded`). Opens the right drawer.
   - `Create mapping` / `Edit mapping` — **green primary** `Button size="md"`; only shown when **no draft exists** (when a draft is open it hides to avoid a second-draft footgun).
2. **`ConnectionNotice`** — inline ok/err banner (left border accent green/red) rendered under the header whenever `notice` is set.
3. **Body grid** (`grid gap-4 lg:items-start`, effectively single column stacked — note the loading skeleton uses a 2-col grid but the loaded body is one column):
   - **Card "Live version"** (`edge="green"`, sub "What this supplier receives for new orders today"). When something is live: a row of `RevisionStatusBadge status="published"` (renders as "Live") + mono `v{N}`, then a **BundleSummary** definition list (7 label/value rows, see Data), then a footer with an `Open supplier editors` link (`/library/suppliers/{supplierId}`). When nothing is live: a "Nothing live yet" empty block with the same supplier-editors link.
   - **Card "Mapping"** (no edge). Sub copy is context-aware (read-only: "…Click to edit." vs. editable draft: "…Saved automatically; "Make live" to publish."). Contains the `MapperWorkbench variant="connection"`. When the live (published) revision is shown read-only, a full-bleed **semi-transparent overlay button** ("✎ Edit this mapping / You're viewing the live version. Editing opens a draft you can publish.") sits over the mapper; clicking it fires the create-draft mutation. When there is no revision at all, an empty "No mapping yet" block with a green `Start mapping` button.
4. **Overlays** (conditionally mounted at the end): `ConnectionConfirmDialog` (when `confirm` set) and `HistoryDrawer` (when `historyOpen`).

Spacing is mostly an 8px-ish rhythm but realized with **hardcoded fractional px** values throughout (`text-[12.5px]`, `text-[11.5px]`, `py-2`, `gap-px`, `h-[20px]` chips, `12px 14px` card padding inside the drawer) — it drifts from a strict 4/8 scale.

### Data shown
**Connection** (`ConnectionDetail` type, from `getConnection(connectionId)` → `GET /api/connections/{id}`; mock `mockGetConnection` over `_mockConnections`): `id`, `supplierId`, `name`, `activeRevisionId`, `revisions[]`.

**Active (live) revision bundle** (`getConnectionRevision` → `GET /api/connections/{id}/revisions/{revId}`; mock `_mockRevisionBundle`) drives the **BundleSummary** rows:
| Row label | Source field | Example value |
|---|---|---|
| Input mapping | `inputMappingJson` present? | "Configured" / amber "Default / none" |
| Output template | `outputMappingJson` present? | "Custom template" / "Fixed transformer" |
| Output format | `outputFormat` | "CSV" / "Default" |
| Delivery channel | `deliveryProtocol` + `deliveryAutoDeliver` + `hasCredentials` | "HTTP webhook · credentials set" / amber "Not configured" |
| Item mappings | `itemMappings.length` | "1 code" / amber "0 codes" |
| Acceptance rules | `acceptanceProfileId` + `acceptanceVersionNo` | "Bound · v3" / amber "Not bound" |
| Catalog | `catalogMode` | "Live (read at send time)" |

**Revision history rows** (drawer; `connection.revisions[]` = `ConnectionRevisionSummary`): `versionNo`, `status` (draft/test/published/archived), `publishedAt`, `createdAt`. Timestamps via `toLocaleString`.

**Test evidence** (drawer; from `markConnectionRevisionTest` → `POST .../test`): `passed`, `testedAt`, parsed `TestPackSummary` { `replay` {orderCount, outputErrors…}, `conformance` {skipped, passed, profile, errors, warnings}, `error` }.

**Replay** (drawer; `replayConnectionRevision` → `POST .../revisions/{rev}/replay`): per-order `ReplayOrderDiff` (poNumber, outputFormat, outputChanged, validationChanged, outputError, currentOutput/draftOutput, effectiveValueChanges[], validationFlips[]).

**Sample order for the mapper preview**: `apiClient.getOrders({ supplierId, pageSize: 1 })` → newest order id, fed to MapperWorkbench `previewOrderId`.

### Interactive elements
| Control | Action | Result / where it goes |
|---|---|---|
| `← All connections` link | navigate | `/connections` |
| `↺ History & advanced ⌄` button | `setHistoryOpen(true)` | Opens right HistoryDrawer |
| `Create mapping` / `Edit mapping` (header primary) | `createDraftMutation.mutate()` (clone-from-active) | Creates an editable draft; sets ok notice; button hides (draft now open); mapper becomes editable |
| `Open supplier editors` link (live card) | navigate | `/library/suppliers/{supplierId}` |
| `Open supplier editors` link (empty live card) | navigate | `/library/suppliers/{supplierId}` |
| Mapper read-only **Edit overlay** button | `createDraftMutation.mutate()` | Same as Edit mapping; label flips to "Opening an editable copy…" |
| `Start mapping` button (no-revision empty state) | `createDraftMutation.mutate()` | Creates first draft |
| MapperWorkbench (Mapping card) | drag-wire / inline pickers / inline edits | Authors the draft mapping (read-only when over the live revision) |
| **Drawer:** close `×` | `onClose` | Closes drawer |
| **Drawer row:** `Test` (draft/test rows) | `onTest(id)` → `testMutation` | Runs the test pack; inline `TestEvidenceSummary` appears under the row; ok/err notice |
| **Drawer row:** `Make live` (draft/test rows) | `onRequestPublish(id, v)` | Opens publish ConfirmDialog (disabled + tooltip "Run tests — checks must pass…" until tests pass) |
| **Drawer row:** `Restore this version` (archived rows) | `onRequestRollback(id, v)` | Opens rollback ConfirmDialog |
| **Drawer row:** `Discard` (draft/test rows) | `onRequestArchive(id, v)` | Opens discard ConfirmDialog |
| **Drawer replay:** "Revision to test" `<select>` | `setRevisionId` | Chooses which revision to replay |
| **Drawer replay:** "Recent orders to replay" number input | `setRecentLimit` (clamped 1–50) | Sets replay window |
| **Drawer replay:** `Run replay` / `Run again` | `replay.mutate()` | Runs non-destructive replay; renders summary + per-order diff rows |
| **Drawer replay:** an order diff row | `setOpen(toggle)` (only if it has detail) | Expands output diff / field-change table / validation flips |
| **ConfirmDialog:** `Cancel` | `setConfirm(null)` | Closes dialog |
| **ConfirmDialog:** primary (`Make live` / `Restore` / red `Discard`) | publish/rollback/archive mutation | Performs action, closes dialog, sets notice, invalidates queries |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **History & advanced drawer** | Right slide-over (`role="dialog"` `aria-modal`, `z-70`, scrim `rgba(11,26,47,0.32)`, `width: min(460px,100%)`, slide-in 200ms, focus-trapped, focus-restored to trigger) | `↺ History & advanced` header button | Version-history rows (per-revision lifecycle buttons + inline test evidence), read-only **Live configuration** summary (`ConfigSummary`), and the **Replay & impact preview** panel (`edge="blue"` Card) | `×` close button · **Esc** · click on scrim/backdrop (the panel `stopPropagation`s its own clicks) |
| **Publish confirm dialog** | Centered modal on desktop / **bottom sheet on mobile** (`role="dialog"` `aria-modal` `aria-labelledby`, `z-80`, scrim `#0B1A2F66`, `sm:max-w-[440px]`) | drawer row `Make live` → `onRequestPublish` | "Make v{N} live?" + body about new orders using it + Cancel / green **Make live** | `Cancel` · confirm (success) · (NB: no `×`, no Esc handler, **no backdrop-click close** — see issues) |
| **Restore confirm dialog** | same modal | drawer row `Restore this version` → `onRequestRollback` | "Restore v{N}?" + body + Cancel / green **Restore** | `Cancel` · confirm · (no Esc/backdrop) |
| **Discard confirm dialog** | same modal | drawer row `Discard` → `onRequestArchive` | "Discard v{N}?" + body + Cancel / **red Discard** | `Cancel` · confirm · (no Esc/backdrop) |
| **Mapper "Edit this mapping" overlay** | Inline full-bleed overlay button over the read-only mapper (`absolute inset-0 z-10`, white card, soft shadow) | shown automatically whenever the mapper is read-only (viewing the live revision) | "✎ Edit this mapping" + helper line; disabled→"Opening an editable copy…" while pending | Disappears once a draft exists (mapper becomes editable); no manual close |
| **Inline notice banner** (`ConnectionNotice`) | Inline status banner (not transient — no auto-dismiss) | every mutation success/error + create-draft | one sentence (ok green / err red, left border accent) | Replaced by the next notice or `setNotice(null)` on the next action; no dismiss control |
| **Inline test-evidence block** (`TestEvidenceSummary`) | Inline panel under a drawer revision row | `Test` succeeds for that revision | "Checks passed/failed · {time}" + replay/conformance counts + notes | Stays until another test runs / drawer reopens |
| Mapper-internal overlays (output designer, field pickers, preview popovers) | nested in MapperWorkbench | mapper interactions | (out of scope of this page; note the connection variant does **not** mount the OutputStructureDesigner — that's `variant==="order"` only) | per MapperWorkbench |

There is **no toast system** here — all feedback is the persistent inline `ConnectionNotice` banner.

### States
- **Empty (nothing live):** "Live version" card shows a "Nothing live yet" block + supplier-editors link; "Mapping" card shows "No mapping yet" + green `Start mapping`. Drawer version history shows "No versions yet. Edit the mapping to begin." Replay shows an idle hint, and "No recent orders to replay" if the supplier has no order history. Good honest empties.
- **Loading:** `isLoading` renders a **2-column grid of two 280px pulsing skeleton blocks** (`bg: var(--border)`) — note the *loaded* body is single-column, so the skeleton shape doesn't match the real layout. BundleSummary has its own 6-row pulse skeleton. Replay has a 3×56px pulse skeleton. **There is no route-level `loading.tsx`** in the `[connectionId]` folder (only `page.tsx`), so the first paint relies on the in-component skeleton once the client query starts.
- **Error:** `isError` → a centered Card "Could not load this connection" (danger text) + `Retry` button (`refetch`). `!connection` (404) → centered Card "Connection not found" + "Back to connections". Replay distinguishes 4xx (danger "Replay rejected:") from network/5xx (amber "Connection problem:") with a retry-able message. Mutation errors surface in the red `ConnectionNotice` (e.g. the backend's evidence-gate 409 "Run tests on this revision before publishing.").
- **Success/feedback:** persistent inline `ConnectionNotice` ("Live — new orders for this supplier use this version now.", "Restored — v{N} is live…", "Draft discarded.", "Checks passed…"). Per-row `loading` spinners on Test/Restore/Discard (only the acting row spins, via per-action pending ids).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** `--container-wide` centered; both cards full-width stacked. Controls render at the compact `sm:` heights (`32px`/`27px`). Drawer is a fixed 460px right slide-over. Confirm dialog is a centered 440px modal.
- **Tablet 768:** Largely the same single-column stack. The mapper is desktop-first; below `lg` it falls back to its review/triage rendering. Replay controls move from a row (`sm:flex-row`) but stay readable.
- **Mobile 390:** All buttons grow to `h-44` tap targets (header actions, links, drawer rows). Drawer becomes `width: 100%`. Confirm dialog becomes a **bottom sheet** (`items-end`, `rounded-t-[10px]`, stacked full-width buttons). The MapperWorkbench is **not** a drag canvas on mobile — review/triage. Potential cliff: the live-version card footer and header actions wrap; the read-only **Edit overlay** covers the whole mapper which is fine, but the mapper itself behind it is the desktop-first surface.

### Current UX issues
- **No single dominant primary on the loaded screen.** The header primary ("Edit mapping") *disappears* once a draft is open, and the real "Make live" action is buried inside the drawer on a revision row — the most important action in the whole workflow is two clicks deep and visually identical (`size="sm"`) to Test/Discard. Violates DESIGN BAR #7 (one dominant primary, ≥44px, green).
- **Two parallel "make a draft" affordances** (header button + full-bleed mapper overlay) doing the identical mutation; the giant overlay button competes with the header for the primary slot.
- **Confirm dialogs lack Esc + backdrop-close + an `×`.** Only Cancel closes them — inconsistent with the drawer (which has all three) and a violation of the "modals have a clear close/escape + scrim" rule.
- **Loading skeleton shape mismatches the loaded layout** (2-col skeleton → 1-col body), and there's no route `loading.tsx`, so navigation shows a blank frame before the client skeleton.
- **Status badges are inconsistent in shape/size across the page:** `RevisionStatusBadge` (rounded-full dot pill, h-5/h-6), the amber "unconfigured" summary chips (`rounded-full h-[20px]`), the replay format tag (`rounded-[4px] h-[18px]`), and the replay `Chip` (`rounded-full h-[18px]`) are four different pill systems. Violates DESIGN BAR #4 (one badge system).
- **Type scale drift:** the page is littered with fractional, ad-hoc sizes (`12.5px`, `11.5px`, `10.5px`, `13.5px`) and the drawer/header set sizes via inline `style` (18px h2, 11px labels) instead of a shared scale. Violates DESIGN BAR #2.
- **Numbers are not consistently tabular.** Version numbers use `font-mono` (good), but timestamps, replay counts ("20 orders · 3 output changes"), and item-mapping counts use the proportional body font and will jitter. Violates DESIGN BAR #3.
- **Notice banner never dismisses and has no close control** — it persists and gets stale; a long red 409 message sits above the fold indefinitely.
- **Spacing is not on a strict 4/8 grid** (e.g. `12px 14px` card padding, `gap-px`, `py-2`, `mt-2.5`, `pt-3` mixed) — DESIGN BAR #1 drift.
- **The drawer mixes three different card chromes** (its own `HISTORY_CARD_STYLE` inline cards, the design-system `Card` for ReplayPanel, and bare `<section>`s) — inconsistent elevation/radius/border (DESIGN BAR #8).
- **"History & advanced" is jargon-ish** and the `↺` / `⌄` glyphs are raw Unicode, not Lucide icons (the app standard).
- **Replay output diff `<pre>` columns** can overflow horizontally on small widths with no clear affordance, and the diff has no line numbers / legend beyond colour.
- **Icon-only-ish controls use text glyphs** (`×`, `↺`, `⌄`, `▲/▼`) rather than accessible Lucide icons with consistent sizing.

### Redesign recommendations (for Claude Design)
1. **Promote the real primary action.** Surface "Make live" (and, while a draft exists, "Test") as a single dominant green primary at the top of the page — e.g. a sticky action bar under the header that reflects draft state (Test → Make live), instead of hiding it in a drawer row. Keep navy/violet brand; green primary ≥44px (BAR #7). Demote Test/Discard to outline/ghost next to it.
2. **Collapse the duplicate draft entry points.** Keep ONE "Edit mapping" primary in the header; replace the full-bleed mapper overlay with a slimmer, non-blocking "Viewing live version — Edit" inline bar above the mapper so the live mapping stays visible.
3. **Unify the status/health pills into one badge component** (one shape, height, padding, dot/icon + green/amber/red/neutral) used by `RevisionStatusBadge`, the unconfigured summary chips, and all replay chips/format tags (BAR #4). Use a Lucide icon per state.
4. **Fix the confirm dialogs:** add `×`, Esc, and backdrop-click close to match the drawer; keep the destructive Discard visually separated (red, with the most friction) (modals-have-clear-close rule).
5. **One type scale + tabular numerals.** Replace the `12.5/11.5/10.5/13.5px` zoo with the design-system scale (heading 600 / label 500 / body 400) and apply `font-variant-numeric: tabular-nums` to all version numbers, counts, timestamps, and replay metrics (BAR #2, #3).
6. **Make the loading state match the real layout** (two stacked cards, not a 2-col grid) and add a route-level `loading.tsx` so navigation never shows a blank frame (BAR #6).
7. **Normalize spacing to a strict 4/8 grid** for card padding, gaps, and section rhythm across the page, drawer, and dialogs (BAR #1).
8. **Unify card chrome in the drawer** to the canonical `Card` (one radius/border/shadow tier) instead of three different section styles (BAR #8).
9. **Make the notice banner dismissible** (add a close `×`, optionally auto-dismiss success after a few seconds while keeping errors sticky) and consider a toast for transient successes so the persistent area is reserved for blocking errors (e.g. the publish-gate 409).
10. **Replace text glyphs with Lucide icons** (`History`, `ChevronDown`, `X`, `RotateCcw`, `ChevronUp/Down`) at a consistent size with `aria-label`s on icon-only controls (BAR #9 + aria rule).
11. **Polish the replay diff:** add a small legend (removed/added colours), make the `<pre>` columns horizontally scrollable with a visible affordance, lead field-change rows with the **human field name** (already mostly done — keep it, never raw `cbc:ID`/`BEG03`), and keep the "would start failing" danger band prominent (it's the trust payload).
12. **Lighten the "History & advanced" label** to plain language ("Versions & history") and ensure breadcrumbs/back are consistent (the `← All connections` link is good; add a breadcrumb for depth).
