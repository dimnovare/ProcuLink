## 25. Confirm item codes (magic-mapping preview) — `/upload/preview/[orderId]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/upload/preview/[orderId]/page.tsx`
- **Key components:**
  - `src/components/bridge/MagicMappingPreview.tsx` (the entire page body — ~1,435 lines; renders all states, rows, bulk-accept bar, commit bar, and the only overlay)
  - `src/components/bridge/layout/PageShell.tsx` (narrow 1040px page wrapper)
  - `src/components/bridge/layout/PageHeader.tsx` (canonical title + subtitle)
  - `src/components/bridge/parseStall.ts` (`isParseStalled` / `PARSE_STALL_THRESHOLD_MS = 90_000` — worker-down escalation logic)
  - `src/components/bridge/magicBulkAcceptSelection.ts` (`bulkAcceptCount` / `bulkAcceptLines` / `bulkAcceptResolutions` — shared count↔action selection)
  - `src/hooks/useOrderDirection.ts` (swaps "Supplier" ↔ "Customer" copy per org direction)
  - `src/hooks/use-mobile.ts` (`useIsMobile` — switches the row grid to stacked cards)
  - In-file sub-components: `SourceCell`, `ArrowBridge`, `MapsToAffordance`, `InfoDisclosure`, `confidencePill`, `rowReducer`
- **Capture URL (mock):** `/upload/preview/ord-002`
  - **`ord-002` is the correct id, not `ord-001`.** `ord-002` is `pending_review` with 4 lines: 2 already-resolved (green), 2 unresolved with AI suggestions (confidence 0.84 and 0.72) — it exercises the Accept/Edit/Reject controls, the bulk-accept bar, the confidence pills, and the InfoDisclosure popover. `ord-001` is `ready` (all 3 lines resolved) → renders the fully-mapped/no-action variant. Mock data: `mockGetMappingPreview()` → `mockGetOrderById()` in `src/lib/api-client.ts` (`mockOrders` array, lines 91–185).

### What it is & why it exists
This is **step 1 of a two-step review handoff** (breadcrumb: Upload › **Confirm codes** › Review & send), sitting between `Parse` and `Validate/Review` in the parse→normalize→validate→review→transform→deliver→learn loop. Right after a file is uploaded and parsed, it shows the procurement coordinator exactly how each uploaded order line maps to the supplier's item code, surfacing AI suggestions (with confidence + provenance) that must be explicitly Accepted, Edited, or Rejected before anything is committed. Its whole job is trust: "here is precisely what we'll send — nothing is sent yet — confirm the codes." On commit it routes the user to `/inbox/[orderId]` (step 2, full order review + send).

### Who uses it & the primary job
**Procurement coordinator** (the buyer-side approver). The single most important task: **resolve every unresolved order line to a valid supplier item code** — either by accepting/editing the AI suggestion, typing the code by hand, or bulk-accepting all suggestions — then click the dark primary button to commit and continue to the full order review.

### Layout & structure (current)
Wrapped in `<PageShell>` (narrow, max-width 1040px, gutter 16→24→34px, vertical padding 20→28px, page bg `#F6F7FA`). Top to bottom:

1. **Breadcrumb nav** (in page.tsx, not a component) — `text-[12px]`, three crumbs: `Upload` (green `#2E8E3A` link) › **Confirm codes** (navy `#0B1A2F`, weight 600, current) › Review & send (faint `--ink-faint #98A0AE`). Separators are literal `›` glyphs.
2. **PageHeader** — title "Confirm item codes" (Bricolage Grotesque, 28→30px, weight 600), subtitle "Confirm how your order lines map to supplier codes. Next, you'll review the full order and send it." (13px, `--ink-muted`).
3. **MagicMappingPreview card** — a single rounded container (`#F6F7FA` bg, 8px radius, 1px `#E5E8EE` border, `overflow:hidden`) containing four stacked regions:
   - **3a. Inner header** (`#FFFFFF`, 16/20/14px padding, bottom border): `<h2>` "Review your order mapping" (Bricolage Grotesque, 16px/600); a 12.5px muted descriptor "Here's exactly how we'll map your order to **your supplier**. Review and confirm — nothing is sent yet." with an inline uppercase **source-format pill** (`CSV`/`PDF`/`XLSX`/`CXML`/`EDI`, `#F1F3F7` bg, gray). Below it, the **bulk-accept + stats row**: purple primary "**Accept all AI suggestions**" button with a count badge, an optional outline "**Accept ≥85% only**" secondary with count, a `role="status"` bulk notice, and a right-side "`{resolved}/{total} mapped · {n} need attention`" counter (amber `#B36D14` for the unresolved fragment).
   - **3b. Column header strip** (`#F1F3F7`, bottom border, 8/16px padding). **Desktop**: a 5-column CSS grid `1fr 36px 120px 36px 1fr` → "SOURCE FIELD & VALUE" | (arrow gap) | "ORDER FIELD" (centered) | (arrow gap) | "{SUPPLIER} CODE", all 11px/700 uppercase gray. **Mobile**: collapses to a single "ORDER LINES" label.
   - **3c. Line rows** (scroll region: `overflowY:auto`, `overflowX:auto`, `maxHeight:480`). Each line is the same `1fr 36px 120px 36px 1fr` grid on desktop (stacked flex-column card on mobile). Columns: **Source cell** (`SourceCell` — buyer item code in mono blue `#1E66C9`/600, description 11.5px gray, then `×qty unit · unitPrice` in faint) → **ArrowBridge** (28×12 blue→green gradient SVG arrow) → **canonical-field label** ("SUPPLIER / ITEM CODE" + a tiny gradient "spine node" dot) → **ArrowBridge** → **supplier-code cell** (the interactive column; renders one of: already-resolved green chip + "✓ resolved", AI-suggestion block, accepted-code chip, inline edit input, or a dashed "+ Enter supplier code" button). Row background is semantic: resolved/accepted = `#F4FBF4` green tint w/ `#C8E6C9` border; has-AI-suggestion = `#FFFFF8` w/ `#E5E8EE`; no-suggestion-unresolved = `#FFFBF2` amber tint w/ `#F0D9A0` border.
   - **3d. Sticky commit bar** (`position:sticky; bottom:0`, `#FFFFFF`, top border, 14/20px). Left: "`{resolved} of {total} lines mapped — {n} still need a supplier code`" plus any commit error / hint / success line. Right: the **one dark primary button** ("Confirm mapping → review order" / "Continue to review (N unmapped)" / "Committing…" / "Committed ✓").

**Density/type/spacing observation:** almost everything is **inline `style={}` with hardcoded hex + non-4/8 px values** (1px, 11.5px, 12.5px, 9.5px, padding "2px 9px", border 1.5px, radius 4/6/8/10 mixed). It does NOT use the design tokens that `PageShell`/`PageHeader` use (`--ink`, `--ink-muted`, `--surface-*`). Mono font is `'JetBrains Mono'`; display is `'Bricolage Grotesque'`; body `'Inter'`.

### Data shown
- **Entity:** `MappingPreview` (`{ orderId, orderStatus, sourceFormat, detectedConfidence, lines[] }`) and `MappingPreviewLine[]`.
- **Per-line fields displayed:** `lineNumber`, `buyerItemCode`, `sourceFields.{description, quantity, unit, unitPrice}`, `canonicalField` (="supplierItemCode"), `resolvedSupplierCode`, `aiSuggestedSupplierCode`, `confidence` (→ pill %), `provenance` + `reason` (→ InfoDisclosure popover), `status` (`suggested`/`resolved`/`unresolved`).
- **Header fields displayed:** `sourceFormat` (uppercased pill), derived `resolved`/`unresolved`/`total` counts.
- **Source:** `apiClient.getMappingPreview(orderId)` → `GET /api/orders/{orderId}/mapping-preview` (real) / `mockGetMappingPreview` (mock). Mutations: `apiClient.commitMappings()` → `POST` resolve with `saveMappings:true` (real `realResolvePurchaseOrder`); `apiClient.acceptAiSuggestions(orderId, minConfidence)` → `POST /api/orders/{id}/accept-ai-suggestions?minConfidence=`. TanStack Query key `["mapping-preview", orderId]` (retry 1, staleTime 60s).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Breadcrumb "Upload" link | `next/link` | Navigates to `/upload` |
| "Accept all AI suggestions" (purple, count badge) | `bulkAccept(0)` | Commits any local edits first, then accepts+saves every suggested line server-side; invalidates `mapping-preview`/`order`/`orders`; sets bulk notice |
| "Accept ≥85% only" (outline, count badge) | `bulkAccept(0.85)` | Same path at minConfidence 0.85; only shown when `highConf > 0 && highConf < suggestedUnresolved` |
| Per-row **Accept** (green) | `dispatch({type:"accept", code: aiSuggestedSupplierCode})` | Row → `accepted` state, green code chip (local only until commit) |
| Per-row **Edit** (outline) | `dispatch({type:"edit", draft: suggestion})` | Row → inline text input with the suggestion pre-filled |
| Per-row **Reject** (red) | `dispatch({type:"reject"})` | Row → rejected; shows "Rejected ·" + "+ Enter supplier code"; excluded from bulk-accept count (B11) |
| Edit input (`type=text`, mono) | `onChange` → `set_draft`; Enter → `confirm_edit`; Esc → `reset` | Live draft; Enter confirms to accepted, Esc cancels |
| Edit **Confirm** (green, disabled when blank) | `dispatch({type:"confirm_edit"})` | Row → accepted with trimmed draft |
| Edit **Cancel** (ghost) | `dispatch({type:"reset"})` | Row → idle |
| Accepted-row **Edit** (small ghost) | `dispatch({type:"edit", draft: accepted code})` | Re-opens inline editor |
| "+ Enter {supplier} code" (dashed) | `dispatch({type:"edit", draft:""})` | Opens empty inline editor for manual entry |
| **InfoDisclosure "i"** button (per suggestion) | `setOpen(!open)` | Toggles provenance/reason popover (see overlays) |
| Confidence pill | display-only | No action (green ≥80% / amber ≥50% / gray below) |
| "check system health" link (stall state only) | `next/link` | Navigates to `/operations/health` |
| **Retry** button (error state) | `refetch()` | Re-runs the preview query |
| **Primary commit button** | If `toResolve.length===0` → `onCommitted(orderId)` (advance, no POST); else `commit(toResolve)` | On success routes to `/inbox/[orderId]`; or advances "Continue to review (N unmapped)" without committing |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **AI provenance popover** (`InfoDisclosure`) | Inline absolute-positioned popover (`role="tooltip"`) | The 16×16 circular "i" button next to a suggestion (only present when `provenance` or `reason` exists) | `provenance` + `reason` joined by newline, max-width 260px, white card, 1px `#E5E8EE` border, `0 4px 14px rgba(11,26,47,0.12)` shadow | Click the "i" again (toggle) · click outside (`pointerdown` outside `wrapRef`) · **Esc** key. No backdrop/scrim. |
| Inline edit input | Inline-panel (replaces the suggestion block in the cell) | row Edit / "+ Enter code" / accepted-row Edit | mono text input + Confirm + Cancel | Confirm (→accepted) · Cancel/Esc (→idle/reset) · Enter (→confirm) |
| Bulk notice | Inline `role="status"` text (not a toast) | `bulkAccept` success/error | "N suggestions accepted and saved · N manual codes saved." or "Accept failed: …" | Persists until next bulk action / unmount (no dismiss control) |
| Commit feedback | Inline text in the commit bar | commit success/error | "✓ Mappings committed…" (green) / error (red) / "Unmapped lines will stay flagged…" hint | Replaced on next state change; navigation away |

**There are NO true modals, dialogs, drawers, sheets, dropdowns, or toasts on this page.** The only floating overlay is the `InfoDisclosure` popover (one per suggestion); everything else is inline state swapping within a row or the bars. The page **navigates in place** to `/inbox/[orderId]` on commit and to `/operations/health` from the stall link.

### States
- **Empty:** Two distinct empties. (a) **Parse-in-progress** (`orderStatus === "parsing"`): white card, spinning blue→green gradient SVG, "Parsing your order…" + "We're reading your file… the mapping preview will appear automatically." Polls every 1.5s via `refetch`. (b) **Genuinely no lines** (parse finished, `totalLines === 0`, not failed): red "No order lines were found" + "Upload a corrected file with at least one purchase-order line." — **no actionable button/link** (dead end; you must back out manually).
- **Loading:** **A bare centered spinner** ("Loading mapping preview…", 28px gradient circle) — NOT a skeleton. Same spinner reused for parsing and commit-button states. **There is no `loading.tsx`** in this route folder (none exists anywhere under `(app)`).
- **Error:** Query error / `!preview`: centered red "Could not load mapping preview" + `error.message` + a white outline **Retry** button calling `refetch()`. Good (reason + retry).
- **Parse-stalled (>90s):** While still "parsing", an amber `role="status"` banner appears: "This is taking longer than usual. Order processing may be paused — **check system health**. We'll keep checking automatically." Polling continues; never claims failure (honesty contract G9). `failed` status auto-routes to `/inbox` via `onParseFailed`.
- **Success/feedback:** Bulk-accept inline notice (green) and commit success line "✓ Mappings committed — order is ready for processing." Then `onCommitted` → `/inbox/[orderId]`. No toast system.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Identical — content is centered at max-width **1040px** (`PageShell` narrow), so it never uses the extra width; the card sits in a wide empty canvas. The 5-column `1fr 36px 120px 36px 1fr` grid renders with both arrows + canonical spine label.
- **Tablet 768:** `useIsMobile` (breakpoint ~768) is the switch. At/above it the desktop grid holds; the inner header bulk row uses `flex-wrap`. The fixed 120px center column + dual 36px arrow columns can crowd long codes/descriptions on narrower desktops — hence the `overflowX:auto` floor on the scroll region.
- **Mobile 390:** Each line becomes a **stacked card** (`flex-column`, gap 10): Source cell on top, a compact `MapsToAffordance` ("→ SUPPLIER ITEM CODE" mini-arrow) replacing the arrows + canonical column, then the full-width supplier-code control. Action buttons grow to `minHeight:40` / larger padding (touch). Column header collapses to "ORDER LINES". Commit bar wraps (`flex-wrap`).
- **Cliffs:** The hard `maxHeight:480` scroll region means long orders scroll *inside* a 480px box while the sticky commit bar floats — on small screens this double-scroll (page + inner) is awkward. The 1040px narrow shell wastes large-monitor space for what is effectively a wide mapping table.

### Current UX issues
- **Token bypass / inconsistent system:** the entire component is hardcoded inline hex (`#5E6779`, `#0B1A2F`, `#E5E8EE`, `#6F4FCE`, `#B36D14`…) and ad-hoc sizes, while the page wrapper uses CSS variables. It won't track theme/token changes and drifts from the rest of the app. (Bar 2, 8)
- **Spacing not on a 4/8 grid:** padding values like `2px 9px`, `5px 12px`, `1px 7px`, `16px 20px 14px`, borders `1.5px`/`3px`, radii `4/6/8/10` mixed. No single rhythm. (Bar 1)
- **Type scale sprawl:** font sizes 9.5, 10, 10.5, 11, 11.5, 12, 12.5, 13, 13.5, 16 px all appear. Hierarchy is carried partly by tiny size deltas + color rather than a clean size+weight scale. (Bar 2)
- **Numbers are not tabular:** quantities, unit prices, confidence %, and "{n}/{total} mapped" counts use proportional figures (no `font-variant-numeric: tabular-nums`), so the mono codes line up but the human numbers jitter. (Bar 3)
- **Two competing badge/pill vocabularies:** the source-format pill, the confidence pill (green/amber/gray with dot), the purple "AI" tag, the count badges, and the "✓ resolved" text-pill are five different shapes/paddings — not one status-badge system. (Bar 4)
- **Loading is a bare spinner, no skeleton; no `loading.tsx`.** Three different spinner treatments (page load, parsing, committing). (Bar 6)
- **"No order lines" empty state is a dead end** — red text with no button to re-upload or go back. (Bar 6)
- **Bulk notice + commit feedback are static inline strings with no dismiss and no toast** — easy to miss; "Accept failed: …" reads like body text, not an error surface. (Bar 6)
- **Primary action ambiguity:** there are arguably *two* primaries competing for the eye — the **purple** "Accept all AI suggestions" up top and the **dark navy** commit button at the bottom. Neither is green (the project's success/primary color) and neither is ≥44px tall on desktop. The most-repeated row actions (Accept/Edit/Reject) are 2px-padded ~20px-tall buttons — well under the 44px touch target on desktop. (Bar 7, 9)
- **InfoDisclosure is the only "tooltip" but provenance is critical trust copy** buried behind a 16px "i"; the green/amber `title=` attributes on the bulk buttons are native tooltips that never appear on touch. (Bar 9)
- **Reject has no confirm and is irreversible-feeling** (sits next to Accept at identical size), though it's only local state. Destructive-styled (red) action not separated. (Bar: destructive separation)
- **The canonical "ORDER FIELD" column always reads "SUPPLIER / ITEM CODE"** for every row — a constant label dressed up as data, adding visual noise without information; and it leads with a generic field name rather than the human source description. (Bar: lead with human field name)
- **Mobile inner-scroll (maxHeight 480) + sticky bar** creates nested scrolling on small screens.

### Redesign recommendations (for Claude Design)
1. **Rebuild on tokens, one type scale, one spacing rhythm.** Replace every inline hex with `--ink/--ink-muted/--ink-faint/--surface-*` and the navy `#0B1A2F` + violet brand vars; collapse the font sizes to the heading-600 / label-500 / body-400 scale; snap all padding/gap/radius to 4/8px. This alone makes the screen read "finished." (Bars 1, 2, 8)
2. **Pick ONE primary action.** Make the bottom commit button the single dominant, green, ≥44px primary ("Confirm mapping → review order"). Demote "Accept all AI suggestions" to a clearly-secondary outline/ghost action (keep violet as the AI accent, not as a second primary). Keep its count badge. (Bar 7)
3. **One status/confidence badge system.** Unify the confidence pill, "AI" tag, "resolved" marker, format pill, and count badges into a single pill primitive (one shape/size/padding; green/amber/red/neutral + icon-or-word). Confidence should never be color-only. (Bar 4)
4. **Make the line list one real table density.** Single row height, consistent cell padding, `gray-200` gridlines, sticky header (already gray strip — formalize it), tabular-nums on qty/price/%/counts, and a calmer arrow/spine treatment (the dual 36px arrow columns + constant "supplier item code" label are decorative — consider collapsing to one subtle connector). Lead each row with the human description, not the constant canonical field. (Bars 3, 5)
5. **Replace bare spinners with skeletons + add a real `loading.tsx`.** Skeleton the header + 3–4 row placeholders; keep the honest "Parsing…" and the 90s stall escalation, but give them the skeleton frame so the layout doesn't jump. (Bar 6)
6. **Give the empty "no lines" state an action** (Re-upload / Back to upload button) and the parse-failed path a visible reason, not just a silent route to `/inbox`. (Bar 6)
7. **Promote provenance/confidence trust copy.** Keep the violet "AI" treatment but surface reason inline (or in a larger, accessible popover with a clear close), since this is the trust core; ensure the "i" affordance is ≥24px hit area and keyboard/touch reachable (it already toggles + Esc-closes — keep that). Drop the native `title=` tooltips on the bulk buttons. (Bars 4, 9)
8. **Bump every interactive control to ≥44px with visible hover/pressed + focus-visible rings.** The Accept/Edit/Reject/Confirm/Cancel row buttons are the most-used controls and are currently ~20px tall on desktop. Add a confirm (or easy undo) affordance to Reject and visually separate it from Accept. (Bars 7, 9)
9. **Move feedback into the app's toast/status system** (or a fixed inline alert region) so "accepted and saved" / "Accept failed" / commit success aren't lost as inline body text. (Bar 6)
10. **Reconsider width + nested scroll.** Either use the wide (1480px) shell and let the table breathe, or keep narrow but remove the inner `maxHeight:480` so the page scrolls once with the sticky commit bar — especially on mobile where double-scroll is rough. Keep mobile as stacked cards (already correct), just on the unified token/badge system. (Bar 10)
