# Polish pass 16 — broken / half-wired UI audit

Written while the founder's parallel session was running Wave 2 (engine breadth).
This audit catalogues every user-visible button or interaction in the app shell that
is **inert, mock-only, or persists to local state without backend round-trip**.

## What was fixed in this pass

1. **Orphan duplicate routes** (`/dashboard`, `/mappings`, `/suppliers`) now redirect to the canonical Bridge routes (`/bridge`, `/library/mappings`, `/library/suppliers`). Bookmarks and direct URL hits no longer land on the older `views/*` UI.
2. **Onboarding checklist v2** — clearer goal copy, "Download sample CSV" shortcut to `/demo-purchase-order.csv` so a new user can explore before uploading their own file, celebration state when complete.
3. **Sample PO CSV** committed at `project-proculink/public/demo-purchase-order.csv` (5 lines, EUR, multiple units).

## Per-page list of remaining broken buttons

Severity:
- **P0** — visibly broken / 404 / silent failure
- **P1** — opens panel but doesn't persist to backend (user thinks it saved; it didn't)
- **P2** — UX papercut (e.g., loading state missing, copy unclear)

### `/bridge` (Dashboard)

| Severity | Item | File | Fix |
|---|---|---|---|
| P2 | KPI values are still mock (1,209 orders today etc.) | `BridgeDashboard.tsx` L49-55 | Wire to `GET /api/orders?since=24h` aggregations |
| P2 | "In-transit" list is hardcoded mock orders | `BridgeDashboard.tsx` L57-63 | Use existing `useQuery` to fetch orders with status in (`parsing`, `transforming`, `delivering`) |
| P2 | "Wire topology" canvas (kept the code comment internal) buyer/supplier nodes are mock | `BridgeDashboard.tsx` | Defer — needs aggregated routes API |

### `/inbox` (filtered queue)

| Severity | Item | File | Fix |
|---|---|---|---|
| P1 | "Re-process" bulk action button does nothing | `InboxView.tsx` L311 | Wire to `POST /api/orders/{id}/redeliver` for each selected (this endpoint exists already) |
| P1 | "Discard" bulk action button does nothing | `InboxView.tsx` L314 | Add `DELETE /api/orders/{id}` (soft delete) and wire |
| P0 | Row data is still entirely from `ALL_ORDERS` mock array | `InboxView.tsx` | Replace with `useQuery(['/api/orders', filter])` — same pattern as `OrdersPage.tsx` |
| P1 | "+ New order" button does nothing | `InboxView.tsx` L338 | Either link to `/upload` or open the upload modal |
| P2 | "↻ Sync" button does nothing | `InboxView.tsx` L325 | Call `queryClient.invalidateQueries(['/api/orders'])` |

### `/upload`

| Severity | Item | File | Fix |
|---|---|---|---|
| P2 | Order detail link copy after upload says "View in inbox" but routes to `/orders/{id}` in live mode | `UploadWorkbench.tsx` | Update copy: "View order details" |
| P0 | Mock-mode and live-mode are picked by `isApiMockMode` — clear in code, opaque to user | – | Add a small badge "Demo mode" when `NEXT_PUBLIC_USE_MOCK=true` |

### `/orders` and `/orders/{id}`

| Severity | Item | File | Fix |
|---|---|---|---|
| P2 | "Save draft" notice on order detail says "verified in Group J" — exposes internal vocab | `SpineReview.tsx` L824 | Replace with "Saved locally. Server-side draft persistence coming soon." |
| P1 | "View source" and "Download" buttons exist but click handlers may be partial | `OrderDetailPage.tsx` | Wire to `GET /api/orders/{id}/artifacts/{artifactId}/download` (exists) |

### `/drafts`

| Severity | Item | File | Fix |
|---|---|---|---|
| P0 | Page is entirely 2 hardcoded mock rows | `app/(app)/drafts/page.tsx` | Wire to a real drafts API (doesn't exist yet) — OR hide from sidebar until Group L |

### `/library/suppliers`

| Severity | Item | File | Fix |
|---|---|---|---|
| P0 | "+ Add supplier" creates supplier via mutation — works | – | ✅ done |
| P1 | Supplier card click should open `/library/suppliers/{id}` | `SupplierDockList.tsx` | Verify; was wired in pass 8 |

### `/library/buyers`

| Severity | Item | File | Fix |
|---|---|---|---|
| P0 | All 6 buyers are hardcoded mock | `app/(app)/library/buyers/page.tsx` L5-12 | Wire to `GET /api/buyers` — endpoint doesn't exist yet; needs backend |
| P1 | "+ Add buyer" does nothing | same | Wire when API lands |

### `/library/mappings`

| Severity | Item | File | Fix |
|---|---|---|---|
| P1 | Import / Export / Add / Edit panels open + show local draft state; **do not persist** | `MappingEditor.tsx` | Wire to existing `PUT /api/suppliers/{id}/po-mapping` (built in Group D) |

### `/library/rules`

| Severity | Item | File | Fix |
|---|---|---|---|
| P1 | Rule toggle + edit panels are local-only | `ValidationRules.tsx` | No backend rules API yet — defer or build endpoint |
| P0 | Rule list is hardcoded | `ValidationRules.tsx` | Same |

### `/library/templates`

| Severity | Item | File | Fix |
|---|---|---|---|
| P1 | Template create / validate / save panels are local-only | `app/(app)/library/templates/page.tsx` | No template API yet — defer to Group L |
| P0 | Template list is hardcoded | same | Same |

### `/operations/log`

| Severity | Item | File | Fix |
|---|---|---|---|
| P0 | Event list is entirely mock | `CrossingsLog.tsx` L26-184 | Wire to `GET /api/orders/{id}/audit` aggregated across all orders (need a new `/api/audit` endpoint) |

### `/operations/connectors`

| Severity | Item | File | Fix |
|---|---|---|---|
| P1 | Add / edit / test-fire connector panels are local draft state only | `ConnectorsPage.tsx` (or similar) | Wire to `POST /api/suppliers/{id}/delivery-config/test-fire` (exists, built in D2) |

### `/operations/webhooks`

| Severity | Item | File | Fix |
|---|---|---|---|
| P1 | Webhook add / edit / test panels are local draft state only | webhook page | No webhook config API yet — needs backend |

### `/settings`

| Severity | Item | File | Fix |
|---|---|---|---|
| P0 | Billing section wires to live API | – | ✅ done (Group C2) |
| P0 | Email config wires to live API | – | ✅ done (Group H) |
| P2 | Other settings tabs (workspace, team, integrations) may have inert controls | settings page | Spot-audit |

## Recommended next polish pass priorities

If we do one more polish pass before deploying Wave 2, the highest-ROI fixes are:

1. **Wire `/inbox` to the live orders API** — most-visited route after dashboard
2. **Wire `/inbox` bulk Re-process button** — endpoint already exists (`/redeliver`)
3. **Hide `/drafts` from sidebar until the drafts API exists** — currently misleading
4. **Wire `/library/mappings` save panel to the existing `PUT /api/suppliers/{id}/po-mapping`** — biggest "I clicked save and it didn't save" complaint risk

Effort estimates:
- Items 1+2: 2 hours
- Item 3: 5 minutes
- Item 4: 3 hours

## What's NOT in this audit

- Backend route renames (`/bridge` → `/dashboard` everywhere) — would require Clerk middleware, redirects, analytics-event renames. Scoped out per founder direction.
- Component file renames (`BridgeSidebar` → `AppSidebar`, etc.) — internal vocabulary, not user-facing.
- Performance — most slowness is cold-start of the un-deployed local API; will measure on staging after first Vercel deploy.
- Wave 2 engine work (SFTP, OCR, etc.) — running in parallel session.
