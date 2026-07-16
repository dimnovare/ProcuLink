# ProcuLink Hangfire Jobs — Reliability Audit (2026-07-11)

24 jobs, read-only, adversarially verified (each finding 3-lens; 152 agents). **31 confirmed
defects (8 P1, 13 P2, 10 P3), 7 jobs clean. Score 68/100.** The parse→transform→deliver core is
genuinely idempotent (atomic status-guarded claims + per-order mutex + AutomaticRetry(0)); the
defects sit AROUND that core — ingress dedupe + lost-order gaps + poisoned-shared-DbContext.

## Tier A — duplicate real delivery / duplicate money (worst)
- **A1 [P1×3] Ingress dedupe ledger written AFTER the order (separate txn).** SFTP `SftpIngressService.cs:254`,
  S3 `S3IngressService.cs:303`, Email `EmailPollOrgJob.cs:271`. Retry/SIGTERM in the window → duplicate
  order → duplicate supplier delivery + duplicate €0.50 overage. FIX: claim-first — insert+commit ledger
  (unique index; 23505=already-imported→skip) BEFORE CreateStubAsync, or wrap both in one BeginTransactionAsync.
- **A2 [P2×3] No [DisableConcurrentExecution] on poll children** (Sftp/S3/EmailPollOrgJob) → overlapping
  same-org polls both pass check-then-act dedupe → duplicate order the unique index can't stop. FIX:
  [DisableConcurrentExecution] keyed on orgId; with A1 the loser hits the unique violation before creating.
- **A3 [P2] RetryDelivery double-delivers on crash after supplier ACK** (`DeliveryService.cs:292`, no supplier
  idempotency key). FIX: deterministic Idempotency-Key/message-id (orderId+attemptNumber); attempt-started row.
- **A4 [P2] Webhook double-POST — no delivery-id, send-before-persist** (`FireIntegrationTriggerJob.cs:156`).
  FIX: X-ProcuLink-Delivery-Id; only RecordFailure when the send itself failed.
- **A5 [P2] Retry path bypasses billing gate** (`RetryDeliveryJob.cs:65`; RetryDeliveryAsync never injects
  IBillingService). FIX: re-check CanProcessOrdersAsync before re-dispatch → HoldForBillingAsync when false.

## Tier B — lost order / delivery never reaches supplier
- **B1 [P1] TransformOrderJob dual-write gap** (`TransformOrderJob.cs:110`): commits ready_to_deliver, THEN
  enqueues delivery outside the unit; crash → retry claims 0 rows → Skipped without enqueue; NO sweep covers
  ready_to_deliver → stranded forever. FIX: outbox / idempotent re-enqueue on Skipped / ready_to_deliver sweep.
- **B2 [P1] Ops requeue-delivery never dispatches** (`OpsController.cs:142-144`): pre-flips to Delivering +
  UpdatedAt=now, which the claim (delivering AND UpdatedAt<now-2min) rejects → no-op → later dead-lettered
  without ever contacting supplier. FIX: don't pre-flip to a claim-rejected state; reset attempts when dispatching past cap.
- **B3 [P2] Reconciliation heal-then-release strands delivery_held** (`StripeSubscriptionReconciliationService.cs:183`):
  release is best-effort and wasReadOnly is consumed after heal commits → a failed release never retries. FIX:
  call ReleaseBillingHeldOrdersAsync on EVERY reconcile leaving org in processing status (0 when none) → self-heals.
- **B4 [P2] StuckDelivery shares RequeueCount with parse/transform, never resets** (`StuckDeliveryDetectionService.cs:73`)
  → premature dead-letter. FIX: delivery-scoped DeliveryRequeueCount or reset on entering delivering.
- **B5 [P3] RetryDelivery can lose the auto-retry** (`RetryDeliveryJob.cs:97`; schedule separate from failed-attempt
  write, no sweep for delivery_failed). FIX: schedule next attempt in same unit, or sweep delivery_failed.

## Tier C — poisoned shared DbContext (same root cause)
- **C1 [P2] ParseInvoiceJob commits parsed lines under a failed invoice** (`ParseInvoiceJob.cs:102`).
- **C2 [P2] CatalogSyncSourceJob swallows its own failed status** (`CatalogSyncSourceJob.cs:96`).
- **C3 [P2] BlobRetentionSweep one org's failed save leaks into next org's txn** (`BlobRetentionService.cs:227`).
- FIX (class): terminal-state/failure writes go through a FRESH context or ExecuteUpdate — never the tracker
  that just failed. Add ChangeTracker.Clear() per-org in a finally for loop-over-orgs jobs.

## Tier D — wasted work / audit-pollution / analytics (P2-P3, batch)
StuckOrderDetection no DisableConcurrentExecution (dup audit + lost RequeueCount); DeliverySlaSweep guard in
SELECT not UPDATE; StuckDelivery overlapping double-audit; StuckOrder RequeueCount++ before enqueue confirmed;
TransformOrderJob concurrent claim → orphan R2 blob; CatalogSyncSource child gate≠dispatcher gate; ParseOrder
first_upload_parsed re-fires on re-parse; ParseOrder failed→Succeeded hides failures; EmailPollOrg catch assumes
23505 swallows transient; FireIntegrationTrigger FailureCount lost-update + double-increment.

## STATUS — FIX WAVE COMPLETE 2026-07-16 (all P1 + all P2 merged; each TDD + adversarially reviewed)
Every fix went through a dedicated adversarial review that caught **4 additional real regressions** before
merge (2 on ingress, the delivery-held-release gap, the transition-map warning). Merged to main:
- **A1/A2 ingress duplicate-PO (3 P1)** — MERGED. Reworked to **resume-on-conflict**: claim row carries a
  pre-generated OrderId, existence verified by PurchaseOrder PK, order created via IStubOrderCreator find-or-create.
  Duplicate-safe (PK exists→skip) AND lost-order-safe (PK absent→resume). + [DisableConcurrentExecution(orgId)].
  Review caught TWO regressions before merge: (1) the first version silent-lost orders on transient stub failure;
  (2) erase-then-resurrect (erasing an SFTP/S3 order left the ledger row → next poll re-imported it) → fixed by
  tombstoning the ledger OrderId=Guid.Empty in DataErasureService. Real-Postgres tests for all 3 modes.
- **B1/B2 silent lost-order (2 P1)** — MERGED. TransformOrderJob re-enqueue-on-Skipped + StrandedReadyOrderDetection
  sweep (hardened: in-DB filter + Take(200) + DisableConcurrentExecution); Ops requeue leaves a claimable status.
- **C1/C2/C3 poisoned-DbContext (3 P2)** — MERGED. ChangeTracker.Clear() before failure/terminal writes.
- **A4/A4b webhook double-POST (P2+P3)** — MERGED. Deterministic X-ProcuLink-Delivery-Id header (HMAC-compatible),
  send-vs-persist failure split, atomic FailureCount ExecuteUpdate.
- **A3/A5 delivery double-deliver + retry billing-gate (2 P2)** — MERGED. Attempt-started `dispatching` row +
  re-adopt (no double attempt/lost delivery) + per-channel idempotency key (HTTP/SFTP/FTPS solid; ERP/email
  best-effort — residual chipped); RetryDelivery gates on billing → delivery_held for lapsed orgs.
- **B3/B4/B5 delivery/reconciliation edges (P2/P3)** — MERGED. Self-healing held-release (every processing reconcile);
  dedicated DeliveryRequeueCount (delivery budget separate from parse/transform); StrandedFailedDelivery sweep.
- **Tier-D P3 hygiene** — chipped (batch: sweep DisableConcurrentExecution, parse-failure visibility, analytics
  double-count, EmailPollOrg transient-swallow). ERP/email crash-after-ACK dedup — chipped. UI Order-Workshop h1 — open.
Migrations added (auto-apply on boot): ingress ledger order_id, delivery_attempts idempotency_key,
purchase_orders delivery_requeue_count, organisations last_stripe_event_at.

## STATUS (2026-07-11, superseded above)
- **A1/A2 (ingress claim-first): ATTEMPTED, HELD** at that time (later reworked + merged — see above). Held because
  the first version traded duplicate-order for silent lost-order (transient stub failure → 23505-skip → PO lost).
- Remaining clusters (B1/B2 lost-order, A3/A5/B3/B4/B5 delivery-idempotency, C1/C2/C3 poisoned-DbContext,
  Tier-D) not yet started — pending agent budget.

## Top 3 (risk-reduction per unit work)
1. Claim-first ingress + [DisableConcurrentExecution(orgId)] × 3 → closes 6 of 8 P1s (A1+A2); stops duplicate POs + charges.
2. TransformOrderJob dual-write gap (B1) + Ops requeue (B2) → the two silent-lost-order paths.
3. Supplier idempotency key on delivery (A3) + billing re-check on retry (A5).
Cross-cutting: 4 P2s share the poisoned-DbContext root cause — one team convention retires the class.
