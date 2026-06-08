# Track-A — Retention & Partitioning of Append-Only Tables

**Date:** 2026-06-08
**Scope:** DESIGN + ANALYSIS only. No source/appsettings/prod changes. No build/test run.
**Track-A deliverable:** the SAFE, additive, reversible first step for bounding the growth of
append-only tables — without native Postgres table partitioning (Track-B, flagged separately).

> **Headline finding (read first):** Track-A is **already mostly shipped**. ProcuLink has a
> config-gated, idempotent, bounded-batch retention sweep (`DataRetentionService` +
> `DataRetentionSweepJob`, scheduled daily 03:30 UTC) covering the four highest-churn tables. It is
> **disabled by default** (`DataRetention:Enabled=false`). The lowest-risk, highest-value Track-A
> action is therefore **operator config** — flip `DataRetention__Enabled=true` on API + Worker — plus
> two small *additive* code hardening items (one new table on the sweep, observability). Native
> partitioning is **Track-B**, correctly deferred by the existing strategy docs to the
> 100-customer / multi-instance horizon.

This doc cross-references and does not duplicate the broader analysis in
[`docs/strategy/2026-06-08-program-design.md` § "partitioning — effort L"](2026-06-08-program-design.md)
(lines 126-187). Where they overlap, that doc is the longer treatment of Track-B; this doc is the
Track-A-focused, implementation-ready slice with the exact migration + job sketches.

---

## 1. Inventory: every append-only / unbounded-growth table

Grounded in `ProcuLink.Core/Entities/*` and `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`. The
"on sweep?" column is the current state of `DataRetentionService.RunAsync`
(`ProcuLink.Infrastructure/Services/DataRetentionService.cs:53-110`).

| Table | Entity (file) | Growth driver | Prune timestamp | Tenant col | Current indexes | On sweep today? |
|---|---|---|---|---|---|---|
| `audit_events` | `AuditEvent.cs:5` | every mutation (org/supplier/order/parse/transform/deliver) | `CreatedAt` | `org_id` | `IX_audit_events_org_id_entity_type_entity_id_created_at` (`DbContext` L639) | **Yes** (180d) |
| `po_passport_events` | `PoPassportEvent.cs:8` | upload/parse/map/transform/deliver per order, immutable | `OccurredAt` | `org_id` | `IX_po_passport_events_org_id_order_id_occurred_at` (`DbContext` L1077) | **Yes** (180d) |
| `delivery_attempts` | `DeliveryAttempt.cs:3` | every delivery + retry + test-fire; can hold up to 8 KB NACK body (`MaxResponseBodyLength`, `DeliveryAttempt.cs:36`) | `AttemptedAt` | `org_id` | `IX_delivery_attempts_org_id_order_id_attempted_at` (`DbContext` L480) | **Yes** (180d, terminal-orders + test-fire only) |
| `idempotency_keys` | `IdempotencyKey.cs:8` | one row per `Idempotency-Key` POST /upload | `CreatedAt` | `org_id` (composite PK `(OrgId, Key)`, `DbContext` L490) | PK only | **Yes** (48h) |
| `order_exceptions` | `OrderException.cs:8` | reconciled per order; rows accumulate (open/resolved/ignored never deleted) | `CreatedAt` | `org_id` | `IX_order_exceptions_org_id_state_severity_created_at`, `IX_order_exceptions_org_id_order_id` (`DbContext` L660-663) | **No — GAP** |
| `mapping_corrections` | `MappingCorrection.cs:7` | one per supplier-code overwrite; "immutable record" | `CorrectedAt` | `org_id` | `IX_mapping_corrections_org_id_mapping_id_corrected_at` (`DbContext` L1054) | **No** (low churn; KEEP — moat data) |
| `order_validation_results` | `OrderValidationResult.cs` | one per failed rule per order per validate | `DetectedAt` | `org_id` | `IX_order_validation_results_org_id_order_id` (`DbContext` L724) | **No** (medium churn; candidate) |
| `imported_sftp_files` | `ImportedSftpFile.cs:11` | one per SFTP-ingested remote file (dedupe ledger) | `ImportedAt` | `org_id` | `(OrgId, RemotePath)` unique (`DbContext` L570) | **No** (dedupe ledger — needs care, see §4) |
| `imported_s3_objects` | `ImportedS3Object.cs:12` | one per S3/R2 object polled (dedupe ledger) | `ImportedAt` | `org_id` | `(OrgId, BucketName, ObjectKey)` unique (`DbContext` L612) | **No** (dedupe ledger — needs care, see §4) |
| `overage_billing_records` | `OverageBillingRecord.cs:21` | one per org per billing period | `CreatedAt` | `org_id` | `(OrgId, BillingKey)` unique (`DbContext` L530) | **No — DO NOT PRUNE** (billing idempotency ledger) |
| `ai_usage_monthly` | `AiUsageMonthly.cs:8` | one row per org per month (upsert) | `UpdatedAt` | `org_id` (composite PK `(OrgId, Year, Month)`) | PK only | **No — bounded by design** (≤12 rows/org/yr) |
| `outbound_artifacts` | `OutboundArtifact.cs` | one per transform; points at R2 blob | `CreatedAt` | `org_id` | FK indexes only | **No** (handled by `DataErasureService`, R2 is the real cost) |

**Hangfire job/state tables** (`hangfire.job`, `hangfire.state`, `hangfire.jobparameter`, etc.):
these are **not** ProcuLink entities — they are owned by Hangfire.PostgreSql and self-prune via
Hangfire's own `JobExpirationCheckInterval` / per-job `ExpireAt`. They are **out of scope for the
ProcuLink retention sweep** and must not be touched by our migrations or job. (If they ever grow,
the fix is Hangfire's `JobStorage` retention config, not this design.)

**"passport" table** = `po_passport_events` (the PO Passport audit ledger). It is already on the
sweep. There is no separate `passport`-named table.

### What is NOT a candidate (and why)
- **`overage_billing_records`** — a billing idempotency ledger. The unique `(OrgId, BillingKey)`
  guarantees a replayed Stripe webhook can't double-charge (`OverageBillingRecord.cs:6-19`). Pruning
  it could allow a re-charge for an old period. **Never auto-prune.**
- **`mapping_corrections`** — explicitly "immutable record" and is the schema-fingerprint/learn-loop
  moat data; tiny per-org. Keep unless a real size problem appears.
- **`idempotency_keys`** — already swept at 48h; see §5 for why it is also **not** a partitioning
  candidate (composite PK + timestamp-less lookup).
- **`ai_usage_monthly`** — bounded by construction (one row/org/month).

---

## 2. Current state of the retention sweep (already shipped)

All grounded in code I read directly:

- **Service:** `DataRetentionService.RunAsync` (`DataRetentionService.cs:53`) — cross-tenant
  age-prune. Deletes via `protected virtual DeleteOldestBatchAsync` → bounded
  `query.Take(batchSize).ExecuteDeleteAsync` (`DataRetentionService.cs:121-124`), no entities
  materialised. `virtual` so InMemory tests override it.
- **Tables covered:** `audit_events` (CreatedAt < cutoff), `po_passport_events` (OccurredAt),
  `idempotency_keys` (CreatedAt, hours window), `delivery_attempts` (AttemptedAt) — and
  `delivery_attempts` is **conservative**: only test-fire rows (`OrderId == null`) OR orders in a
  terminal status (`TerminalOrderStatuses` = Delivered / DeliveryDeadLetter / RejectedBySupplier /
  Failed / TransformFailed, `DataRetentionService.cs:30-37`) are eligible. In-flight deliveries keep
  their audit trail regardless of age.
- **Options:** `DataRetentionOptions` (`ProcuLink.Core/Services/DataRetentionOptions.cs`) —
  `Enabled=false` default (L18); `AuditEventDays=180`, `PassportEventDays=180`,
  `IdempotencyKeyHours=48`, `DeliveryAttemptDays=180`, `BatchSize=5000`. Zero/negative windows fall
  back to the default via `PositiveOr` (L38), so a misconfig can **never** collapse to delete-all.
- **Job:** `DataRetentionSweepJob.ExecuteAsync` (`ProcuLink.Worker/Jobs/DataRetentionSweepJob.cs:29`)
  — `[AutomaticRetry(Attempts = 0)]`, `[Queue("background")]`, thin wrapper over the service.
- **Schedule:** `Worker.cs:57-60` registers recurring job `"data-retention-sweep"` daily at 03:30
  UTC. DI in `ProcuLink.Worker/Program.cs:202-209` (singleton options bound from `DataRetention`
  section + scoped service + scoped job).
- **Tests:** `ProcuLink.Infrastructure.Tests/Services/DataRetentionServiceTests.cs` — 9 tests cover
  window boundaries, hours window, conservative delivery-attempt pruning, disabled no-op,
  idempotency, batch-size remainder. They assert the **selection predicate**, agnostic to physical
  storage (so partitioning later does not break them).

**Bottom line:** the SAFE, additive, reversible-without-partitioning mechanism Track-A asks for
already exists and is tested. Track-A's remaining work is small.

---

## 3. Track-A plan (SAFE first step — do this; everything here is additive + reversible)

### A0 — Enable the sweep in prod (operator config, zero code, the 80% win)
Set on **both** the API and the Worker Railway services:

```
DataRetention__Enabled=true
```

Keep the safe 180d / 180d / 48h / 5000-batch defaults. After one nightly run, confirm the log line
`DataRetention: pruned N row(s) — …` (`DataRetentionService.cs:103`). This alone bounds the four
highest-churn tables.

- **Reversible:** set `DataRetention__Enabled=false` (or remove the var) and the sweep is a no-op on
  the next run. Nothing is scheduled-destructive between runs.
- **Why both services:** the recurring job runs in the Worker, but the options are bound in both
  hosts; enabling only one leaves the other’s view of the flag stale if the job ever moves.
- **Caveat (must verify before flipping):** this is a *destructive prune of prod data older than
  180 days*. Confirm with the founder that 180-day audit/passport retention satisfies any
  contractual/GDPR retention commitment, and confirm current prod row counts (Neon) so the first
  run’s delete volume is understood. The batch cap (5000/table/run) means a large first backlog
  drains over several nightly runs, not in one statement — but the **first enable is the one
  irreversible step in Track-A**, so it is gated on founder sign-off, not "ready to land green".

### A1 — Add `order_exceptions` to the sweep (additive code; the one real coverage GAP)
`order_exceptions` accumulates one+ rows per order forever (resolved/ignored rows are never deleted —
`OrderException.cs:8-28`) and is **not** swept today. It should be pruned conservatively: only
`resolved`/`ignored` rows past the window; **never** an `open` exception (an operator still needs to
action it). This mirrors the conservative delivery-attempt logic.

This is the additive EF/service change sketched in §6.

### A2 — Observability (additive, optional, S)
Add a Sentry breadcrumb / metric when a sweep prunes more than a threshold, and a unit test asserting
the new `order_exceptions` predicate. Pure-additive in `DataRetentionService` / `DataRetentionSweepJob`.
This lets us watch growth and confirm partitioning is even warranted before spending Track-B effort.

### A3 — (Consider) `order_validation_results` + the two import ledgers
Lower priority. `order_validation_results` is a reasonable next sweep target (medium churn, no
correctness role once an order is terminal). The `imported_sftp_files` / `imported_s3_objects`
**dedupe ledgers** can be pruned only with care — see §4. Defer both until A0+A1 are live and metrics
justify them.

---

## 4. Dedupe-ledger pruning caveat (imported_sftp_files / imported_s3_objects)

These two tables are **not** pure history — they are dedupe ledgers that stop the pollers re-importing
the same remote file (`ImportedSftpFile.cs:5-9`, `ImportedS3Object.cs:5-10`). Pruning a row whose
remote file still exists on the SFTP/S3 source would cause a **re-import** on the next poll.

Safe rule if/when we sweep them: only prune rows older than the *remote retention window of the
source* (i.e. older than the longest time a file could still sit on the server). Default off; this is
A3-tier, not Track-A core. Documented here so a future implementer doesn't naively age-prune them.

---

## 5. Track-B (native partitioning) — design-only, DEFERRED. Flagged, not built.

Native Postgres RANGE partitioning requires **recreating the table** (you cannot `ALTER … PARTITION
BY` a populated table in place). That is exactly what the Track-A/Track-B split exists to avoid. This
section is reference only; the full treatment is in
[`2026-06-08-program-design.md`](2026-06-08-program-design.md) lines 152-179.

**Why it's Track-B, not Track-A:**
- EF Core **8.0.16** + Npgsql **8.0.11** (`ProcuLink.Infrastructure.csproj:18-20`) have **no**
  declarative-partitioning fluent API. All partition DDL would be hand-written
  `migrationBuilder.Sql`, with EF kept unaware of the children → high model-drift risk.
- Migrations auto-apply on **API startup**, fire-and-forget, with a retry loop + phantom-migration
  reconciliation (`ProcuLink.Api/Program.cs:744`/`759-762`, `ReconcilePhantomMigrationsAsync` L844+).
  A long table-rewrite inside `MigrateAsync()` would block/timeout the deploy path and could
  retry-loop destructive DDL. So conversion MUST be **out-of-band**, never a normal migration `Up()`.
- The surrogate `Guid` PK must become composite `(Id, <ts>)` (Postgres requires the partition key in
  every unique constraint). Verified safe: no read path matches these tables by `Id` alone — every
  reader filters by `OrgId`/`OrderId`/`EntityId` + a timestamp (`DataErasureService.cs:48-63`;
  passport/delivery readers per program-design L141). A composite PK still permits `Where(x => x.Id
  == id)` (just less efficient), so it's a perf note, not a break.

**Eligible Track-B tables (in safe order):** `delivery_attempts` first (lowest read fan-out, prove the
FK-to-`purchase_orders` recreate on the least passport-critical table), then `po_passport_events`,
then `audit_events` last (extra `users` FK + jsonb converter + most-written).

**Explicitly NOT partitioned: `idempotency_keys`.** Verified hazard:
`IdempotencyService.TryGetExistingOrderIdAsync` looks up `WHERE k.Key == key && k.OrgId == orgId`
with **no** timestamp predicate (`IdempotencyService.cs:40-42`), and the PK is `(OrgId, Key)`.
Partitioning by `CreatedAt` would (a) force `CreatedAt` into the PK, breaking the `(OrgId, Key)`
uniqueness that prevents duplicate keys = a **correctness bug**, and (b) make every dedup lookup scan
all partitions = a perf regression. Its 48h sweep already keeps it tiny. Keep it on the sweep, never
partition it.

**Decision gate for starting Track-B:** the strategy docs (`PROD_LAUNCH_AUDIT.md:24/1287`) already
mark partition audit/passport as "redesign-later" tied to the 100-customer / multi-instance horizon.
Only start Track-B when (a) sweep deletes start causing vacuum/bloat pressure, OR (b) tables approach
tens of millions of rows, OR (c) multi-instance API is imminent. Below that, **Track-A is sufficient.**

---

## 6. Exact additive EF migration sketch (A1 — `order_exceptions` on the sweep)

> Sketch only — **no code file is written by this doc.** This is what an implementer would add. It is
> purely additive (no schema change to existing columns) and reversible.

**No new migration is strictly required** for A1 if the existing indexes suffice — but a supporting
composite index makes the prune predicate index-aligned and is the only DB change. Add an
**additive** index migration `AddOrderExceptionRetentionIndex`:

```csharp
// Up — additive, no data change, no column change. Reversible via Down (drop index).
protected override void Up(MigrationBuilder migrationBuilder)
{
    // Supports the prune predicate: WHERE org-agnostic (cross-tenant sweep) state IN
    // ('resolved','ignored') AND created_at < cutoff. Leading state lets the planner skip
    // 'open' rows; created_at second bounds the age scan. Partial filter keeps it tiny.
    migrationBuilder.Sql(
        "CREATE INDEX IF NOT EXISTS \"IX_order_exceptions_state_created_at_resolved\" " +
        "ON order_exceptions (state, created_at) " +
        "WHERE state IN ('resolved','ignored');");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_order_exceptions_state_created_at_resolved\";");
}
```

`IF NOT EXISTS` / `IF EXISTS` make it idempotent and phantom-migration-safe, matching the established
raw-SQL precedent (`20260528120230_AddTenantApiKeysAndOrgSlug.cs` uses `migrationBuilder.Sql` for an
idempotent backfill). Because it touches no existing column, the fire-and-forget startup `MigrateAsync`
applies it in milliseconds.

**Reversibility:** `Down` drops the index. The index is invisible to all readers (purely additive), so
rollback is a no-op for application behaviour.

---

## 7. Idempotent Hangfire retention-job sketch (A1 — extend the existing service)

The job already exists and is idempotent (`DataRetentionSweepJob`). A1 adds `order_exceptions` to the
**service**, not a new job. Sketch of the additive service change (no code written here):

```csharp
// DataRetentionOptions.cs — additive, safe default
public int OrderExceptionDays { get; set; } = 180;
public TimeSpan OrderExceptionWindow => TimeSpan.FromDays(PositiveOr(OrderExceptionDays, 180));

// DataRetentionResult — additive field (default 0 keeps existing callers compiling)
public sealed record DataRetentionResult(
    int AuditEvents, int PassportEvents, int IdempotencyKeys,
    int DeliveryAttempts, int OrderExceptions = 0) { /* Total += OrderExceptions */ }

// DataRetentionService.RunAsync — add, after the delivery_attempts block:
var exceptionCutoff = now - _options.OrderExceptionWindow;
var exceptionsDeleted = await DeleteOldestBatchAsync(
    _db.OrderExceptions.Where(e =>
        e.CreatedAt < exceptionCutoff
        && (e.State == "resolved" || e.State == "ignored")),  // NEVER prune 'open'
    batch, ct);
```

**Idempotency:** unchanged — a second run with nothing past the window deletes nothing
(`Take(batchSize).ExecuteDeleteAsync` over a predicate that no longer matches). The existing
`RunAsync_IsIdempotent_…` and `RunAsync_RespectsBatchSize_…` test patterns extend directly to the new
table.

**Org-scoping:** the sweep is intentionally cross-tenant but every predicate filters on a per-row
timestamp on already-tenant-scoped rows (`DataRetentionService.cs:11-14` remarks). The new predicate
follows the same shape. No org loop needed; the `org_id` column is carried on every row for the rare
GDPR per-order erase path (`DataErasureService`), not for the sweep.

**Config-gated window:** `OrderExceptionDays` is bound from the `DataRetention` section; zero/negative
falls back to 180 via `PositiveOr`, so it can never collapse to delete-all — same guard as every
existing window.

---

## 8. Risks & rollback

**Track-A risks (low):**
- **R1 — first enable is destructive on prod data > 180d.** It deletes real audit/passport rows that
  may be subject to a retention obligation. *Mitigation:* founder sign-off on the 180-day window +
  known prod row counts before flipping; the batch cap drains a backlog gradually. *Rollback:* none
  for already-deleted rows — this is the one irreversible Track-A step, hence the gate.
- **R2 — pruning an `open` exception or a dedupe-ledger row in error.** *Mitigation:* the A1 predicate
  hard-excludes `open`; the import ledgers (§4) are explicitly deferred. *Rollback:* fix the
  predicate; future runs stop; already-deleted rows are gone (so the predicate is conservative by
  design).
- **R3 — over-pruning a table that has a correctness role (overage ledger).** *Mitigation:* §1
  explicitly marks `overage_billing_records` / `mapping_corrections` / `ai_usage_monthly` as
  do-not-prune. The sweep only ever touches tables explicitly listed in `RunAsync`.

**Rollback for Track-A as a whole:**
- Disable: `DataRetention__Enabled=false` → next run is a no-op (`DataRetentionService.cs:55-59`).
- The A1 index migration `Down` drops the index (no behaviour change).
- No table is recreated, renamed, or has a column altered in Track-A → the schema is byte-identical
  to today apart from one additive index.

**Track-B risks (deferred):** live-data conversion lock/half-state, EF model drift on the composite
PK, fire-and-forget startup pipeline running destructive DDL, FK-recreate `ON DELETE` semantics,
`DataErasureService` cross-partition deletes, Neon `pg_partman`/`pg_cron` availability. Full list in
[`2026-06-08-program-design.md`](2026-06-08-program-design.md) lines 170-179.

---

## 9. Open questions (for the founder / before Track-B)
1. Is `DataRetention` already **enabled** in prod, or still safe-off? If off, A0 is the immediate
   near-zero-risk win and likely defers Track-B indefinitely at pilot scale.
2. Does 180-day audit/passport retention satisfy any contractual/GDPR commitment? (Gates A0.)
3. Current prod row counts / growth rate of `audit_events`, `po_passport_events`,
   `delivery_attempts`, `order_exceptions` on Neon? (Sets the Track-B decision gate; partitioning is
   unjustified below ~tens of millions of rows.)
4. Is multi-instance API / a second worker on the near roadmap? (The audit ties Track-B to that
   horizon.)

---

## 10. Recommendation
1. **A0 (operator, gated on Q1+Q2):** enable `DataRetention` on API + Worker — the 80% win, no code.
2. **A1 (additive code, ready to land green):** add `order_exceptions` to the sweep (conservative,
   `open`-excluded) + the one additive index migration + a predicate test.
3. **A2 (additive, optional):** prune-count observability before considering Track-B.
4. **Track-B:** leave DEFERRED behind the decision gate. Do not build now — it spends L effort for no
   pilot benefit, and the strategy docs already classify it as redesign-later.
