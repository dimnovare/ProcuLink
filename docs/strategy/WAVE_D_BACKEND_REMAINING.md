# Wave D — backend remaining (precise handoff)

**Updated:** 2026-06-07 · **Branch base:** `main` (all shipped items below are already merged).
**Golden rule:** no new bugs. Each item below is test-gated + adversarially reviewed; the
**structural four (W1/W2/W3/RLS) land on a branch for founder review, NOT a pre-launch auto-merge.**
Work in an **isolated worktree** (`git worktree add ../ProcuLink-wd -b wd2 origin/main`) — the main
checkout is shared with concurrent chips.

---

## Already SHIPPED this push (verified live on prod)

| Item | Commit | Closes |
|---|---|---|
| API security headers (HSTS/nosniff/Referrer-Policy) + Redis-ready HMAC nonce (config flag) | `0b34ff7` | W4 · P2-3 · 1.4.redis · 2.8.redesign2 · audit "API HSTS/nosniff" |
| EmailPolling indexed flag (+ migration + backfill) + AI-candidates / SFTP / S3 partial indexes | `eb24aa6` | §1.1.F · §2.3.2 · §2.3.3 · §2.4.3 · 2.8.redesign5 · 1.3.email-flag |
| DESADV upload → 501 (was misleading 202) | `d6c44ac` | A1.3 / D3 honesty |

Verified live: `/health` 200 with `Strict-Transport-Security` + `X-Content-Type-Options: nosniff`;
`/health/ready` Healthy (Wave-B migration applied on Neon). 990 tests green (one pre-existing
load-flaky HTTP-timeout test passes in isolation).

**Already done before this push (verified, do NOT redo):** R1 (tenant unify, `3c789b6`),
R2 (stuck-order requeue, `0da39cf`), R3 (billing on `IBillingService`, `3c789b6`),
P1-1..P1-5, P2-1/P2-2/P2-4, P1-3 (`ClerkTokenValidation.IsAuthorizedParty` already rejects
missing/empty `azp`; Clerk prod-instance cutover done), migrate-fail-loud, B1-B8 drift items.

---

## STRUCTURAL FOUR — branch + adversarial review + founder merge decision

### W2 — Order-status transition table (audit d-2 / W2)
- **Why:** status is a free-form string; transitions guarded ad-hoc (`DeliveryService.cs:455-464`,
  `OrderService.cs:464`); a new status silently breaks list filters (the "Failed bucket").
- **State:** `OrderStatusConstants.cs` already has the constants + `FailureBucket`. **Only the
  transition table + guard is missing.**
- **Approach (no-new-bugs critical):** add `OrderStatusTransitions` = `IReadOnlyDictionary<string, IReadOnlySet<string>>`
  + `bool IsAllowed(from, to)`. **The table MUST be a SUPERSET-or-equal of every transition the
  current code performs** (parse→pending_review/ready; resolve→ready/pending_review; transform→
  transforming→ready_to_deliver/transform_failed; deliver→delivering→delivered/delivery_failed/
  delivery_dead_letter; requeue→pending_parse/ready; reject→rejected_by_supplier; etc.). Enumerate
  EVERY `Status = "..."` assignment first (grep) and build the table from them, or you WILL reject a
  valid flow. Replace the ad-hoc `if (status is not ...)` guards with `IsAllowed`. Risk: med.
- **Test:** every current transition is allowed; a few illegal ones rejected; existing
  delivery/transform/requeue tests stay green.

### W1 — Decompose `OrderService` behind the `IOrderService` facade (audit d-3/f-2 / W1 / B1)
- **Why:** `OrderService.cs` ~1,410 LOC, 14 deps, owns ingest/parse/transform/resolve/download/
  reject/AI-accept/canonical-merge/audit. `IOrderService` (13 methods) already exists — keep it.
- **Approach:** extract `OrderIngestionService` (CreateFromFile/CreateStub*/ParseStoredFile),
  `OrderQueryService` (GetById/ListPaged/GetDownloadUrl), `OrderResolutionService` (Resolve/
  AcceptAi/MarkRejected), `OrderTransformService` (Transform). `OrderService` becomes a thin
  facade delegating to them (preserves the interface + all call sites). **Behavior-preserving** —
  move code, don't rewrite logic. The 990-test suite is the safety net (it exercises every method).
  Optionally move the orchestrator to a `Core/Application` project so the Worker stops mirroring DI
  (`Worker/Program.cs:160-176`) — **bigger; can be a second step.** Risk: high (blast radius).
- **Test:** full suite green unchanged + add per-sub-service unit tests.

### W3 — R2 + DB per-order erase (GDPR right-to-erasure) (audit d-5 / W3)
- **Why:** no "delete my data" path; R2 source files + artifacts are never purged.
- **State:** `IFileStorageService.DeleteAsync(key)` exists; `DataRetentionService` sweeps 4 DB
  tables but never calls R2 delete. Keys: `PurchaseOrderEntity.SourceFileKey`, `OutboundArtifact.FileKey`.
- **Approach (DESTRUCTIVE — test hard):** `IDataErasureService.EraseOrderAsync(orgId, orderId)`:
  org-scoped load → delete R2 (source file + each artifact FileKey) → delete child rows EXPLICITLY
  (lines, delivery_attempts, outbound_artifacts, order_exceptions, order_validation_results,
  po_passport_events, order-scoped audit_events) → delete the order → write an erasure record to a
  SEPARATE log (not the deleted order's audit). Admin-gated endpoint (`[AdminOnly]`, e.g.
  `DELETE /api/admin/orders/{id}`). Risk: med (additive, but data-loss if mis-scoped).
- **Test:** erases ONLY the target org's target order + its R2 keys; a second org's order untouched;
  R2 DeleteAsync called with the right keys; idempotent on a missing order.
- **✅ SHIPPED** as `DELETE /api/admin/organisations/{orgId}/orders/{orderId}` (`AdminController.EraseOrder`).
  Permanent hard-erase, admin-only, org-scoped.
- **✅ SHIPPED — bulk variant** `POST /api/admin/organisations/{orgId}/orders/bulk-erase`
  (`AdminController.BulkEraseOrders` → `IDataErasureService.BulkEraseOrdersAsync`). Body is a filter
  `{ poNumberPrefix?, status?, ids?[], olderThan? }` (fields AND-combined); reuses `EraseOrderAsync`
  per matched order in ONE server-side batch and returns the erased count + summed child counts.
  Strictly org-scoped (route org id ANDed into every match — even foreign `ids` are ignored) and
  refuses an empty filter (400) so it can never mass-wipe an org.
  **OPERATIONS NOTE:** the per-order `DELETE` is rate-limited (global 300/min → 429), so purging a
  large test org one-by-one needs many paced passes. **For bulk cleanup, always use the `bulk-erase`
  endpoint** — one call, no per-row HTTP, e.g. `{ "poNumberPrefix": "TEST-" }` or
  `{ "olderThan": "2026-01-01T00:00:00Z" }`.

### RLS — Postgres Row-Level Security defence-in-depth (audit §2.5 / 2.8.redesign4)
- **Why:** tenant isolation is 100% app-code; one forgotten `.Where(OrgId==…)` leaks. History of
  cross-tenant P0s.
- **Approach:** a raw-SQL migration enabling RLS + a per-table `org_id = current_setting('app.org_id')::uuid`
  policy on the org-scoped tables; set `app.org_id` per request/connection (middleware or a
  DbConnection interceptor) AND keep app-level scoping (defence-in-depth). **Risk: high** — a wrong
  policy or unset session var silently returns zero rows or blocks writes. Hangfire/DataProtection/
  migration tables + cross-org sweeps (`StuckOrderDetectionService`) must be EXEMPT or use a
  privileged role. Validate against the full suite + a live two-org isolation test. This is the
  riskiest item; strongly consider post-launch.

---

## FLAGGED — counterproductive / blocked now (founder override required to do)

| Item | Why not now |
|---|---|
| **W6 split `api-client.ts`** | Would collide head-on with the 2 ACTIVE frontend chips (ui-parity worktree + marketing). DX-only, zero customer value. Do once the frontend settles. |
| **W5 consolidate dual retry schedulers** | Refactors currently-**correct** code (audit: "currently correct, just fragile"). Pure risk, no behavior change. Post-launch. |
| **§1.4 denormalize line_count/total_value** | Audit "at-10k-users / redesign-later"; adds another denormalization-drift surface (the BuyerName split is already a known footgun). Marginal at pilot. |
| **§1.4 partition audit/passport** | Big DB change, near-zero pilot value; the retention sweep already covers growth. |
| **Neon pooled endpoint (§1.1.A) + enable DataRetention sweep (§1.1.E)** | Founder env/Railway changes (connection-string swap + `DataRetention:Enabled=true`), not code. |
| **P1-6 Postmark signature** | Needs the Cloudflare inbound-email Worker to SIGN; token compare is already constant-time + bot-probe logs are Warning. Operational (rotate + per-tenant token). |
| **SchemaFingerprints rename (§2.7.1)** | Cosmetic; a table rename migration pre-launch is risk-for-nothing (audit: deliberate, documented). |
| **Phantom-migration cleanup (§2.7.2)** | Touching the migration bootstrap pre-launch is dangerous; migrate-fail-loud (the valuable half) is already done. |
| **jsonb ValueConverter (§2.4.4)** | Deliberate tradeoff (nothing queries inside those blobs); acceptable. |
| **Social proof on landing (C2)** | Needs a real consented logo / real "N POs processed" data — can't fabricate (offer⇔works). |
| **EDIFACT INVOIC/DESADV** | Founder said no to the EdiFabric licence. |
| **Cross-org mapping library / i18n / PEPPOL AP** | Post-launch roadmap (weeks; the moat is deliberately after ~10 customers). |
