# Schema-fingerprint auto-apply — the Learn-loop moat (design + plan)

> **Status: DESIGN — needs founder sign-off before Phase 2 (auto-apply) implementation.**
> Grounded 2026-06-16. The foundation is ~70% built and production-wired; the gap is the final 30%
> (auto-*apply* a supplier's recipe on a matching layout). The risky part — silent data corruption from
> a layout collision — is why this is gated on sign-off, not auto-built.

## The idea

When a recurring supplier file lands, recognise its *shape* (column layout / structure signature) and
auto-apply that supplier's saved recipe with calibrated confidence — so a known supplier's order skips
straight to "review/preview" instead of re-resolving mappings every time. This is the compounding
Learn-loop differentiator (per the strategy memos), on top of the existing parse/transform engine.

## What already exists (the 70% — do NOT rebuild)

| Capability | Where | State |
|---|---|---|
| Format detection (magic bytes + content) | `FormatDetectorService` (Infrastructure/Services/Detection) | ✅ live |
| Column-header extraction | the parsers → `DetectedFormat.ColumnHeaders` | ✅ live |
| **Schema hash** (SHA-256 of normalised, sorted, lowercased headers) | `SchemaFingerprintHasher` (Core/Services/Detection) | ✅ live, order/case/whitespace-insensitive |
| Per-org fingerprint store (`ColumnNameHash`, `ParseSuccessCount`, `LastSeenAt`, `SampleSupplierName`, `DetectedFormat`) | `SchemaFingerprint` entity | ✅ live |
| Record-on-parse (idempotent, atomic increment) | `ParseOrderJob` → `SchemaFingerprintService.RecordParseSuccessAsync` | ✅ live |
| Lookup + confidence boost (3%/sighting, cap 15%) | `FormatDetectionController` → `LookupAsync` + `FingerprintBoost` | ✅ live, returns SeenCount |
| Supplier known BEFORE parse (supplierId path param; connection-revision pinned at ingest) | `OrderIngestionService` | ✅ live |
| Recipe storage + promotion (`PoMappingConfig`, `SupplierPoMapping`, `PromoteMappingService`) | Infrastructure | ✅ live (manual "Save mappings" trigger) |
| Per-line AI-suggestion decision log + calibration (V9) | `AiSuggestionDecision`, `IConfidenceCalibrationService` | ✅ live (line-SKU only) |

## The gap (the 30%)

Recipe reuse is **not automatic**: a new order for a supplier with a saved mapping still runs the full
parse → manual/AI map → review flow every time. There is no "detect layout → match fingerprint →
auto-apply this supplier's recipe → short-circuit" step.

## The one real risk — layout collision → silent corruption

Two unrelated suppliers can share an identical generic layout (`PO Number, Item Code, Qty`). If we
auto-apply Supplier A's recipe to a Supplier B order, codes/prices can be silently wrong and only blow
up at the receiver. **This is the reason for sign-off + the safety gates below.** The fingerprint today
stores `SampleSupplierName` (display text), NOT `SupplierId` — so it cannot answer "whose recipe?".

## Plan (phased, each shippable + test-gated)

**Phase 1 — Fingerprint→supplier binding (additive, SAFE).**
- Add `SupplierIds` (set/JSON) to `SchemaFingerprint`; capture the order's supplierId on record. Migration is additive.
- Surface "seen N times for {supplier}" on detect.
- Acceptance: a fingerprint knows which supplier(s) used it; >1 supplier on a layout is detectable.

**Phase 2 — Calibrated auto-apply (GATED on sign-off).**
- In `OrderIngestionService` after parse, before line resolution: hash layout → lookup → if it matches
  AND the current order's supplier is bound to that fingerprint AND confidence ≥ threshold AND
  `ParseSuccessCount ≥ MIN_SEEN` (start: 5) → apply that supplier's `PoMappingConfig`.
- Confidence = f(seen count, recency `LastSeenAt`, per-supplier recipe acceptance rate). Threshold ~0.75.
- **Hard safety gate: auto-applied lines are set `NeedsReview = true`** (assisted, never silent) + a
  banner "N lines auto-mapped from your saved recipe — review before sending." Delivery still blocked
  until the user confirms (the existing unresolved-line guard already enforces this).
- If >1 supplier shares the layout → do NOT auto-apply; ask which supplier.
- Acceptance: known layout auto-resolves to NeedsReview (not silent); unknown/low-confidence/collision falls through to today's flow byte-for-byte.

**Phase 3 — Decision logging + calibration.**
- `RecipeApplicationDecision` (org, order, fingerprint, applied-confidence, lines-auto-resolved,
  outcome applied|rejected, lines-manually-overridden) — mirrors `AiSuggestionDecision`.
- Feed per-supplier recipe acceptance rate back into the Phase-2 threshold.
- Acceptance: every auto-apply is logged; a systematically-wrong recipe self-disables (acceptance <70%).

**Phase 4 — UX + gradual rollout.**
- Auto-apply badge + review checkbox; telemetry (auto-apply rate, acceptance, rejection reasons);
  roll out to a small % of orgs first, watch the calibration data.

## Cleanest insertion point

`OrderIngestionService.CreateFromFileAsync` (after parse, before line resolution). The supplier is
already known and the connection revision already pinned there.

## Recommendation

Ship **Phase 1** now (additive, safe, no behaviour change). **Phases 2–4 need founder sign-off** because
auto-apply touches delivered bytes — the safety posture (NeedsReview gate + collision guard + min-seen +
calibration) makes it safe, but the founder should accept the trade explicitly before it goes live.

Effort: ~4–5 focused increments. Related: [[project-output-designer-conditional-format]], the V9
calibration memo (`docs/strategy/2026-06-09-supplier-connection-north-star.md`).
