# P0 Trust Trio — build spec (founder-approved 2026-06-20)

> Approved from the 2026-06-20 full audit ([2026-06-20-full-audit.md](2026-06-20-full-audit.md)). These are P0 trust/data-integrity fixes that touch delivery bytes / sending behavior → **TDD, byte-parity gates, live-verify on prod before declaring done.** Build in a fresh session (the audit turn was very deep). Ship + verify ONE at a time. Order: MV-1 → F-4 → F-2.

## MV-1 — Send must never ship a stale artifact after a mapping edit (P0 CRITICAL)

**Bug:** A transformed order (`ready_to_deliver`, artifact exists) whose mapping override is then edited → `confirmSend` redelivers the PRE-EDIT artifact. The override write never resets status or invalidates artifacts.
**Evidence:** `useSendFlow.ts:85` (`artifacts.length===0 && status!=="ready_to_deliver"` is false → goes to `redeliverOrder:132`); `OrderMappingOverrideService.UpsertAsync:64-69` (no status/artifact touch); `useMapperModel.ts:472` (save only `setQueryData(["mapper-override"])`, never invalidates `["order"]`); `OrdersController:1653` Redeliver ships newest artifact; `OrderStatusMachine:67` `RedeliverableFrom` includes `ReadyToDeliver`.
**Fix (server-truth, robust):**
1. BE: in `OrderMappingOverrideService.UpsertAsync`, when the order is past `ready` (i.e. `ready_to_deliver`/`transforming`/`delivered`) AND the override content actually changed, transition status back to `ready` (so the next Send re-transforms) — wrap in one transaction. Do NOT delete artifacts (audit history), but the re-transform produces a fresh one that Redeliver/Send then ships.
2. FE: after a successful override save (`useMapperModel`), `invalidateQueries(["order", orderId])` so the workshop's `status`/send-state recompute from server truth.
3. Guard `confirmSend`: if the latest override `updatedAt` > latest artifact `createdAt`, force the transform path, never redeliver.
**Tests:**
- BE: a `ready_to_deliver` order + artifact → upsert a CHANGED override → status == `ready`; an UNCHANGED upsert → status unchanged (no needless re-transform). Then transform → delivered bytes reflect the new override, NOT the old artifact (byte assertion).
- FE: after override save the order query is invalidated; `confirmSend` chooses transform (not redeliver) when override is newer.
**Verify on prod:** transform a real order, edit a mapping, Send, confirm the delivered artifact reflects the edit (check the delivery attempt body).
**Risk:** changes sending behavior — the whole point. Gate behind the byte test + a manual prod verify before wide use.

## ⚠ F-4 CORRECTION (2026-06-20, live-verified) — DO NOT implement as written below

Live DOM probe of deployed prod (`/inbox/[orderId]`, real order): the page renders the **v3 workshop** (send-strip + "Fix these to send", no Passport/Conformance tabs) and the preview is **`MapperPreviewPane`** (a `<pre>`, "(no preview)" when unresolved) — which **already calls the real `previewMappingOverride` emitter**. The `OutputPreview.tsx` mock (fabricated `<ItemOut>` / 6-row list) is the **SpineReview/triage/mobile** path, which is **NOT the live default** on prod. The audit agent flagged F-4 from local `main` (where SpineReview is still the default) — the local↔origin divergence. **Fixing `OutputPreview` would fix a panel prod users don't see.**
**Real prod "preview won't switch format" = MV-4 (MapperPreviewPane defaults to `csv` for non-revision orders) + the order being unresolved (gated) + F-1 (can't bind arbitrary fields).** Re-scope F-4 to **MV-4 in `MapperPreviewPane`** (drive format from the supplier's delivery format, not a `?? "csv"` default) and verify on a RESOLVED prod order. Only fix `OutputPreview` if/when SpineReview is intentionally still shipped somewhere. Confirm which review screen is the live default (resolve the local↔origin FE divergence) before any preview work.

---

## F-4 (ORIGINAL, superseded by the correction above) — OutputPreview mock

**Bug:** `OutputPreview.tsx` renders a fabricated cXML scaffold (`:299-333`) / generic 6-row `po_number:…` list (`:338-362`) driven by `order.lines`+`fieldValues` — it NEVER calls the transform/emitter. `outFmt` comes from the last artifact, not the designer format. So a designed nested structure shows the flat mock; the format badge lies. This is the root of "preview won't switch format / I can't design the output."
**Fix:** Replace the mock body in `OutputPreview.tsx` with a call to the real preview path — `apiClient.previewMappingOverride(orderId, baseOverride, deliveredFormat)` (Mode-0 emitter, already correct + already what `MapperPreviewPane` uses) — and render the returned bytes verbatim (mono, format-appropriate). Drive `deliveredFormat` from the supplier's delivery format (one source of truth), not the last artifact. Remove the cXML scaffold + the 6-row generator. Keep loading/error states; surface emitter errors inline.
**Watch:** this is the most-seen panel; preview-only (no delivery bytes change). Reuse `MapperPreviewPane`'s existing call shape (memory: there are TWO preview components — `MapperPreviewPane` `<pre>` already does this correctly; `OutputPreview` is the triage/tablet/mobile mock). Consider consolidating onto `MapperPreviewPane`'s logic.
**Tests (U5):** editing a node in the designer posts the same `outputTree` the preview renders and the transform would read (single override object; preview bytes == a transform dry-run for the same override+format).
**Verify on prod:** design a nested JSON shape on a real order → the main preview shows real JSON braces (not the flat list); switch supplier format → preview changes.
**Risk:** Med (core panel), but no delivery-byte change.

## F-2 — Inferred-but-unmapped columns must show as UNBOUND, not silently deliver empty (P0 data hole)

**Bug:** `OutputNodeTemplateInferrer.RuleFor:209-215` emits `new OutputFieldRule { OutputPath = name, FixedValue = "" }` when `GuessCanonical` returns null. The designer's `usingFixed = fixedValue != null && canonicalField == null` (`OutputStructureDesigner.tsx:420`) → `bound = true` → renders a bound violet "fixed value" pill showing `""` → silently delivers an empty string forever, looking done.
**Fix:** inferrer emits `FixedValue = null` (unbound) for unmapped columns; designer treats `fixedValue == null && canonicalField == null` as **unbound** → amber "+ pick a field" prompt + counts as unresolved (and ideally blocks send until addressed or explicitly set to empty).
**Byte-parity caveat:** changing `"" → null` CAN change emitted output for EXISTING saved inferred templates that currently deliver `""`. So: (a) only change the INFERRER output (new infers), and (b) characterization-test that an explicitly user-set empty fixed value still delivers `""` (only the inferred-unmapped default changes). Gate behind the FormatMatrix/emitter parity suite.
**Tests:** infer a sample with an unmappable column (`EANCode`) → that node is unbound (amber) in the designer, NOT a bound empty pill; an explicitly-set empty fixed value remains bound + delivers `""` (unchanged).
**Verify on prod:** paste a supplier sample with an extra column → it shows "pick a field," not "done."
**Risk:** Low-Med (touches inferred-template emit) — parity-gated.

## Sequencing
MV-1 first (silent wrong-data is the worst). Then F-4 (lets you SEE real output — needed to trust everything else). Then F-2. Each: red test → fix → green → build → live-verify on prod → ship → re-verify. Do not batch all three into one deploy.
