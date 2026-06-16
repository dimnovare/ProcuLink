# ProcuLink bug backlog (2026-06-16) — 42 hunter findings, triaged

> From the "every-corner" bug-hunt workflow (7 hunters). Auto-verification rate-limited, so each is
> triaged by hand. **Fix waves below.** Status: ✅ fixed · 🔨 fixing · ⬜ todo · ❌ false-positive · ⏭ defer.

## CRITICAL
- ❌ Cross-tenant revision access `SupplierConnectionService.cs:137` — **FALSE POSITIVE**: `connection` is loaded org-scoped (`:106-108`), so `connection.ActiveRevisionId` is the org's own revision; no user-injected id.
- ❌ Missing org scope `SupplierConnectionService.cs:523` — **FALSE POSITIVE**: line 524 has `&& r.OrgId == orgId`.
- 🔨 **Mapping persistence lost** `useMapperModel.ts:414` (`if (!revisionId) return doc;` silently skips `updateConnectionDraft`) — the founder's "mappings not stored." Fix: never silently no-op; gate editability on an editable revision + surface "create a draft to edit."

## HIGH (founder complaints — fix wave 1)
- ✅ JSON tokenizer missing → my **T1d** (`d4641b8`).
- 🔨 **Select-all ignores `getCanSelect`** `InboxView.tsx:346-354` — confirmed live (selects nothing). Fix: custom handler over `rows.filter(r=>r.getCanSelect())`.
- 🔨 **Drag-to-connect** `MapperWireLayer.tsx:337-339` — port `onPointerMove` competes with the SVG capture. Fix: drop `onPointerMove` from port props; SVG owns movement.
- 🔨 **Designer preview stuck "UPDATING…/-"** `OrdersController.cs:793,800,805` — preview returns Uppercase format (`Csv`) + error responses lack a `format` field → FE `normalizeMappingPreview` returns undefined format → blank/stuck. Fix: lowercase the format everywhere + include `format` on error responses (also `:954,960`).
- ⬜ PDF drops numbers without NeedsReview `PdfOrderParser.cs:100` — silent data loss. Fix: null qty/price → `NeedsReview=true` + reason (like CsvOrderParser).
- ⬜ API/webhook zero-line orders `OrderIngestionService.cs:171` — placeholder line + NeedsReview so the mapper opens.
- ⏭ Non-idempotent Hangfire delivery enqueue `TransformOrderJob.cs:110` — VERIFY (audit-top10 supposedly closed this; may be false/regression).

## MEDIUM (fix wave 2 — engine/validation/UX)
- ⬜ ConformanceService throws on no-profile `ConformanceService.cs:51` → return graceful.
- ⬜ SourceCapture not persisted for CSV/XLSX upload `OrderIngestionService.cs:475,1270` — tokenize buffer + pass to UpsertSourceCapture (part of T1a).
- ⬜ CSV empty-quantity not flagged `CsvOrderParser.cs:111` → NeedsReview on null qty.
- ⬜ Email router skips .x12/.edi `InboundEmailRouter.cs:33` → add to SupportedExtensions.
- ⬜ Replay BuildRevisionOverride wrong null-coalesce `ReplayService.cs:300`.
- ⬜ Error-message clarity pass: SupplierAcceptanceService `:310`, replay note `:641`, rollback `:324`, conformance-skip `:658`, OutputFieldValidator surface Problems `:142`.
- ⬜ Stage labels inconsistent (Extract vs Transform) `BridgeDashboard.tsx:140`.
- ⬜ KPI contradiction (auto-processed % vs total) `BridgeDashboard.tsx:438-518` — always show the "based on latest 100" caveat.
- ⬜ Export button fires on 0 data `BridgeDashboard.tsx:730`.
- ⬜ Connection publish evidence gate `SupplierConnectionService.cs:275`.
- ⏭ ExecuteUpdate atomicity `OrderIngestionService.cs:784` — VERIFY (memory says fixed in ParseStoredFileAsync; likely already a single tx).
- ⏭ 5-modes precedence `OrdersController.cs:870` — hunter says working-as-designed; UI-label only.
- ⏭ canonicalJson read-convention `:716` — documented invariant, not a live bug.

## LOW (fix wave 3 — a11y + copy)
- ⬜ Columns-menu checkbox aria-disabled `InboxView.tsx:1091`.
- ⬜ LaneDrawer recent-deliveries aria-label `LaneDrawer.tsx:401`.
- ⬜ Admin Create-invoice disabled title `admin/page.tsx:254`.
- ⬜ j/k nav vs columns menu `InboxView.tsx:751`.
- ⬜ Hover-only Stripe link `admin/page.tsx:354`.
- ⬜ Empty-state noun (supplier vs customer) `BridgeDashboard.tsx:651`.
- ⬜ InvariantValidator code helper `InvariantValidator.cs:21`.
- ⬜ PDF encoding note `PdfOrderParser.cs:49`.

> **Wave 1 = the founder's literal video complaints** (select-all, drag/wires, mapping-persist, designer
> preview, PDF data-loss). These ship first. Waves 2–3 batch the engine/validation/UX/a11y/copy.
