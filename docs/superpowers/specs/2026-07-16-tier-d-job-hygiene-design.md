# Tier-D P3 job hygiene — design

**Date:** 2026-07-16
**Source:** `docs/audit/2026-07-11-jobs-reliability-audit.md` — "Tier D — wasted work / audit-pollution / analytics"
**Severity:** all P3. Quality / observability. No correctness-critical defect. Low urgency.

## Scope

Six findings were batched for triage. Verification against the current tree (the codebase moved
during the P1/P2 fix wave) found **four real, two already closed**.

| # | Finding | Verified state |
|---|---|---|
| 1 | `[DisableConcurrentExecution]` missing on sweep jobs | REAL — **3 jobs, not 2** |
| 2 | Terminal parse failure reported as Hangfire `Succeeded` | REAL |
| 3 | `first_upload_parsed` re-fires on re-parse | REAL |
| 4 | `EmailPollOrgJob` swallows transient `DbUpdateException` | **Already fixed** — no change |
| 5 | `DeliverySlaSweep` guard in SELECT not UPDATE | REAL |
| 6 | `FireIntegrationTrigger` FailureCount lost-update | **Already fixed (0948a43)** — no change |

### Why 4 and 6 need no change

**#4** — the claim-first / resume-on-conflict rework already narrowed the catch to
`catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))`
(`ProcuLink.Worker/Jobs/EmailPollOrgJob.cs:311`). A transient `DbUpdateException` is therefore not
caught; it propagates out of `ProcessMessageAsync`, which exits the `foreach (var uid in unseen)`
loop in `ExecuteAsync` **before** `folder.AddFlagsAsync(uid, MessageFlags.Seen, …)` at `:188`. The
message stays unseen and Hangfire retries. This is exactly the requested behaviour.

**#6** — `FireIntegrationTriggerJob.RecordFailureAsync` already performs a single store-side
atomic `SetProperty(s => s.FailureCount, s => s.FailureCount + 1)`, gated on the final Hangfire
attempt, with deactivation as a second conditional atomic update. No lost update, no
double-increment.

## Key finding: how Hangfire actually keys the lock

On OSS Hangfire (1.8.18 here) `[DisableConcurrentExecution]` builds its lock resource from
**type + method only — never the job arguments**. Per-argument mutexing requires the paid
`Hangfire.Pro` `[Mutex]`. The repo already documents this in
`ProcuLink.Infrastructure/Jobs/PerOrderDistributedMutexAttribute.cs:10`, which exists precisely
because of this limitation.

Consequence for this work: the three sweep jobs take no arguments and are cross-tenant sweeps, so
a **global per-method mutex is exactly the semantic required**. The stock attribute is correct; no
custom filter is needed.

### Separate finding (report only — NOT fixed here)

`SftpPollOrgJob`, `S3PollOrgJob` and `EmailPollOrgJob` each carry a comment claiming
`[DisableConcurrentExecution]` "keys on the method + args, and this child takes orgId as its
argument, so two polls for the SAME org can never overlap". **This is false.** The lock is global
across all orgs.

- **Correctness:** unaffected, and strictly safer than claimed — a global lock contains a per-org
  lock. There is no duplicate-order risk from this.
- **Throughput:** every org's polling serialises through one lock per channel. A waiter that
  cannot acquire within the 300s timeout throws, failing the job (`AutomaticRetry(Attempts = 2)`).
  This is a scaling ceiling as org count grows.
- **Docs:** the comments are misleading and a future contributor would trust them.

Tracked for separate triage. Not in this batch.

## Design

### Item 1 — global mutex on the three sweeps

Add `[DisableConcurrentExecution(timeoutInSeconds: 300)]` to `ExecuteAsync` on:

- `ProcuLink.Worker/Jobs/StuckOrderDetectionJob.cs` — overlapping sweeps duplicate
  `StuckRequeued` audit rows and lose a `RequeueCount` update (read-modify-write on a tracked
  entity).
- `ProcuLink.Worker/Jobs/DeliverySlaSweepJob.cs` — overlapping sweeps double-insert
  `DeliverySlaBreached` audit rows.
- `ProcuLink.Worker/Jobs/StuckDeliveryDetectionJob.cs` — **gap not named in the original batch.**
  The finding assumed this job already had the attribute; it does not.

Only `StrandedReadyDeliveryDetectionJob`, the three poll children, and `BillingReconciliationJob`
currently carry it.

300s matches the `StrandedReadyDeliveryDetectionJob` precedent and sits below the 15-minute
recurrence, so a hung run cannot block the next tick indefinitely.

Each comment states the real keying semantics (global, per-method) rather than repeating the
poll-children's incorrect per-org claim.

### Item 2 — terminal parse failure surfaces to ops

**Mechanism today:** a parse failure sets `status = "failed"` and returns `Fail`;
`ParseOrderJob` throws; Hangfire retries; the retry re-enters `ParseStoredFileAsync`, whose
`status != "parsing"` re-entry guard (`ProcuLink.Api/Services/Orders/OrderIngestionService.cs:613`)
now sees `failed`, treats it as an already-processed skip and returns
`Ok(new ParsedFileOutput(entity, null, "unknown"))`. The job logs success and Hangfire records the
job **Succeeded**. The Failed queue never shows the parse failure.

**Fix:** `ParseOrderJob.ExecuteAsync` inspects the returned entity after a successful call. If the
status is terminal `failed`, throw rather than log success.

- The service contract is unchanged — a legitimate skip (order advanced to `ready` /
  `pending_review` / `unrouted`) still returns `Ok` and still no-ops.
- Retries burn out on two extra cheap DB reads; the job lands in the Failed queue.
- Attempt 1's real exception remains in job history; the retry's throw carries a generic
  "already terminally failed" message.
- **Side benefit:** this short-circuits before the analytics block, which today fires
  `first_upload_parsed` for a *failed* order on that retry.

### Item 3 — gate `first_upload_parsed` to once per order

**Mechanism today:** the guard is `hadOtherParsedOrders` — "does any OTHER order for this org sit
in a parsed state". An org whose only order is re-parsed (routing's `assign-supplier` flips
`unrouted` → `parsing` and re-parses) still finds no other parsed order, so the event fires again.
Deterministic double-count.

**Fix:** before emitting, count `Parsed` audit events for this `orderId` (org-scoped).
`ParseStoredFileAsync` writes exactly one per parse, so `> 1` means re-parse → skip.

Combined with the existing `hadOtherParsedOrders` check the event fires only on an order's first
parse, and only when no other order has parsed. No migration; reuses rows that already exist.

**Known weakness (accepted):** an audit-retention sweep could drop the `Parsed` rows, letting a
re-parse of a very old order re-fire. By then `hadOtherParsedOrders` is almost certainly true and
suppresses it anyway. Acceptable for a P3 analytics fix; the alternatives (a dedicated column, or
an org-level ledger row with a unique index) cost a migration for no proportionate gain.

### Item 5 — SLA sweep atomic claim

**Mechanism today:** `DeliverySlaService.RunAsync` filters `!o.SlaBreached` in the **SELECT**
(`ProcuLink.Infrastructure/Services/DeliverySlaService.cs:37-42`), then sets the flag in memory and
`SaveChanges`. Two overlapping sweeps both select the same unflagged order and both add an audit
event. The order write is idempotent (both set `true`); the **audit rows duplicate**.

**Fix:** move the condition into the UPDATE. Per order, issue an `ExecuteUpdateAsync` whose
predicate carries `!o.SlaBreached`; only the sweep whose claim affects 1 row writes the audit
event. A loser sees 0 rows and writes nothing.

**Transaction:** `ExecuteUpdate` auto-commits its own statement immediately, so an unwrapped claim
followed by a separate audit `SaveChanges` could crash between the two and leave a flagged order
with no audit row. The claim loop and the audit insert are wrapped in one
`BeginTransactionAsync` — `ExecuteUpdate` enlists in the ambient transaction on Npgsql (the
precedent is the persist block in `ParseStoredFileAsync`). The sweep set is small (overdue
deliveries only), so the transaction is short.

**InMemory:** `DeliverySlaServiceTests` uses `UseInMemoryDatabase`, which cannot translate
`ExecuteUpdate`. Take the `if (_db.Database.IsRelational())` dual path exactly as
`FireIntegrationTriggerJob.RecordFailureAsync` does: atomic claim on Postgres, change-tracker
emulation (today's behaviour) on InMemory. InMemory tests are single-threaded, so the atomicity
they skip is not needed there.

Item 1's mutex makes concurrent sweeps unreachable via Hangfire; this claim is defence-in-depth
covering a direct service call, and is the layer that is actually testable.

## Testing — TDD, RED first

| Item | Test | Project |
|---|---|---|
| 1 | `SweepJobConcurrencyGuardTests` — reflection, asserts the attribute on each of the 3 sweep jobs' `ExecuteAsync`. Mirrors the existing `PollJobConcurrencyGuardTests`; a distributed lock is not unit-testable. | `ProcuLink.Api.Tests/Jobs` |
| 2 | Mocked `IOrderService` returns `Ok` with a `failed` entity → assert `ExecuteAsync` throws. Assert a non-terminal skip still succeeds. Assert analytics NOT captured on the failed path. | `ProcuLink.Api.Tests/Jobs` |
| 3 | Seed 2 `Parsed` audit events for the order → assert analytics NOT captured. Existing `ParseOrderJobEmitsFirstUploadParsedTests` covers the positive first-parse case and must stay green. | `ProcuLink.Api.Tests/Jobs` |
| 5 | InMemory behaviour tests stay green (existing `DeliverySlaServiceTests`). **Plus a real Postgres concurrency test:** two `DeliverySlaService` instances on separate contexts race `RunAsync` on one overdue order → assert exactly ONE `DeliverySlaBreached` audit row. RED before the fix (two rows). | `ProcuLink.Api.Tests/Integration` |

The Postgres test is required because the InMemory path deliberately does not exercise the atomic
claim — an InMemory-only suite would pass against the bug.

## Verification

- `dotnet build` — 0 errors.
- Affected suites green: `ProcuLink.Api.Tests`, `ProcuLink.Infrastructure.Tests`.
- Docker/Postgres available for the integration test.
- Windows dev, Linux CI: check `gh run list` after push — local green is not CI green.

## Out of scope

- Items 4 and 6 — verified already fixed; no code change.
- The three Tier-D items the batch did not name: `TransformOrderJob` concurrent claim → orphan R2
  blob; `CatalogSyncSource` child gate ≠ dispatcher gate; `StuckOrder` `RequeueCount++` before
  enqueue confirmed.
- The poll-children mutex-keying misdocumentation (above) — report only.

## Coordination

Session history shows B4 (`StuckDeliveryDetectionService` RequeueCount) staged in another
worktree. That touches the **service**; item 1 touches the **job wrapper**. Collision risk is low,
but merge order matters — rebase and rebuild the combined tree before merging.
