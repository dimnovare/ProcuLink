# Tier-D P3 Job Hygiene Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the four real Tier-D P3 job-hygiene findings — overlapping sweeps double-writing audit rows, terminal parse failures reported to Hangfire as `Succeeded`, `first_upload_parsed` double-counting on re-parse, and the SLA sweep's guard sitting in the SELECT instead of the UPDATE.

**Architecture:** Four independent, small changes. Three are additive guards (a Hangfire attribute, a status check, a count check); one restructures `DeliverySlaService.RunAsync` into a relational atomic-claim path plus a change-tracker path for the InMemory test provider. No migrations. No new dependencies.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, Hangfire 1.8.18 (OSS) + Hangfire.PostgreSql 1.20.10, xUnit + FluentAssertions + Moq, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-07-16-tier-d-job-hygiene-design.md`

## Global Constraints

- All EF queries org-scoped: `.Where(x => x.OrganisationId == organisationId)` — no exceptions.
- No raw SQL — EF Core only.
- Hangfire jobs must stay idempotent.
- On OSS Hangfire, `[DisableConcurrentExecution]` keys the lock on **type + method only, never on job arguments**. Per-argument mutexing requires paid Hangfire.Pro `[Mutex]`. Do not write comments claiming otherwise.
- `ExecuteUpdateAsync` / `ExecuteDeleteAsync` / `BeginTransactionAsync` are **Npgsql-only** — the EF InMemory provider cannot translate them. Any code path using them needs an `if (_db.Database.IsRelational())` sibling. Precedent: `ProcuLink.Infrastructure/Jobs/FireIntegrationTriggerJob.cs:277`.
- `ExecuteUpdateAsync` auto-commits its own statement unless an ambient transaction is open; it enlists in one on Npgsql.
- Windows dev, Linux CI — after pushing, check `gh run list`. Local green is not CI green.
- Items 4 and 6 of the original batch are **already fixed**. Do not touch `EmailPollOrgJob`'s catch or `FireIntegrationTriggerJob.RecordFailureAsync`.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `ProcuLink.Worker/Jobs/StuckOrderDetectionJob.cs` | Modify — add mutex attribute | 1 |
| `ProcuLink.Worker/Jobs/DeliverySlaSweepJob.cs` | Modify — add mutex attribute | 1 |
| `ProcuLink.Worker/Jobs/StuckDeliveryDetectionJob.cs` | Modify — add mutex attribute | 1 |
| `ProcuLink.Api.Tests/Jobs/SweepJobConcurrencyGuardTests.cs` | Create — reflection guard for the 3 sweeps | 1 |
| `ProcuLink.Api.Tests/Jobs/PollJobConcurrencyGuardTests.cs` | Modify — correct a false doc comment (docs only) | 1 |
| `ProcuLink.Api/Jobs/ParseOrderJob.cs` | Modify — terminal-failure throw + re-parse analytics gate | 2, 3 |
| `ProcuLink.Api.Tests/Jobs/ParseOrderJobTerminalFailureTests.cs` | Create | 2 |
| `ProcuLink.Api.Tests/Jobs/ParseOrderJobReParseAnalyticsTests.cs` | Create | 3 |
| `ProcuLink.Infrastructure/Services/DeliverySlaService.cs` | Modify — atomic claim + dual path | 4 |
| `ProcuLink.Api.Tests/Integration/DeliverySlaConcurrencyPostgresTests.cs` | Create — real-Postgres race proof | 4 |

---

### Task 1: Global mutex on the three sweep jobs

The three recurring sweeps take no arguments and run cross-tenant, so the stock per-method global lock is exactly the semantic needed. Two overlapping runs today duplicate `StuckRequeued` / `DeliverySlaBreached` audit rows and lose a `RequeueCount` read-modify-write.

`StuckDeliveryDetectionJob` is included because it **does not** currently have the attribute — only `StrandedReadyDeliveryDetectionJob`, the three poll children, and `BillingReconciliationJob` do.

**Files:**
- Modify: `ProcuLink.Worker/Jobs/StuckOrderDetectionJob.cs:31-33`
- Modify: `ProcuLink.Worker/Jobs/DeliverySlaSweepJob.cs:23-25`
- Modify: `ProcuLink.Worker/Jobs/StuckDeliveryDetectionJob.cs:35-37`
- Create: `ProcuLink.Api.Tests/Jobs/SweepJobConcurrencyGuardTests.cs`
- Modify: `ProcuLink.Api.Tests/Jobs/PollJobConcurrencyGuardTests.cs:9-15` (comment only)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Api.Tests/Jobs/SweepJobConcurrencyGuardTests.cs`:

```csharp
using System.Reflection;
using FluentAssertions;
using Hangfire;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Guards the concurrency lock on the recurring cross-tenant sweep jobs. Two overlapping runs of
/// the same sweep duplicate audit rows (StuckRequeued / DeliverySlaBreached) and lose the
/// RequeueCount read-modify-write.
///
/// <para>On OSS Hangfire, <see cref="DisableConcurrentExecutionAttribute"/> keys its distributed
/// lock on the job TYPE + METHOD ONLY — never on the job's arguments (per-argument mutexing needs
/// the paid Hangfire.Pro <c>[Mutex]</c>; see PerOrderDistributedMutexAttribute). These sweeps take
/// no arguments and are global by nature, so a per-method lock is exactly the right semantic.</para>
///
/// A distributed lock cannot be unit-tested, so this asserts the attribute is present — the same
/// approach PollJobConcurrencyGuardTests takes for the poll children.
/// </summary>
public class SweepJobConcurrencyGuardTests
{
    [Theory]
    [InlineData(typeof(StuckOrderDetectionJob))]
    [InlineData(typeof(DeliverySlaSweepJob))]
    [InlineData(typeof(StuckDeliveryDetectionJob))]
    public void ExecuteAsync_HasDisableConcurrentExecution(Type jobType)
    {
        var method = jobType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"{jobType.Name} must expose ExecuteAsync");

        var attr = method!.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        attr.Should().NotBeNull(
            $"{jobType.Name}.ExecuteAsync must be guarded by [DisableConcurrentExecution] so two " +
            "overlapping sweeps cannot double-write audit rows");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~SweepJobConcurrencyGuardTests"`

Expected: FAIL — all 3 cases fail with "must be guarded by [DisableConcurrentExecution]".

- [ ] **Step 3: Add the attribute to all three sweep jobs**

In `ProcuLink.Worker/Jobs/StuckOrderDetectionJob.cs`, replace:

```csharp
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
```

with:

```csharp
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    // DisableConcurrentExecution: two overlapping sweeps both read the same stuck order and both
    // bump RequeueCount through the change tracker — a lost update — and both append a
    // StuckRequeued audit row. On OSS Hangfire this attribute keys the lock on TYPE + METHOD only
    // (never on args; per-argument mutexing needs paid Hangfire.Pro [Mutex]), which is precisely
    // right here: this is an argument-less cross-tenant sweep that should never run twice at once.
    // Timeout < the 15-min recurrence so a hung run can't block the next tick indefinitely.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken ct)
```

In `ProcuLink.Worker/Jobs/DeliverySlaSweepJob.cs`, replace:

```csharp
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
```

with:

```csharp
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    // DisableConcurrentExecution: two overlapping sweeps would both select the same unflagged
    // overdue order and both insert a DeliverySlaBreached audit row. On OSS Hangfire this keys the
    // lock on TYPE + METHOD only (never on args) — correct for this argument-less cross-tenant
    // sweep. Defence-in-depth with the atomic claim inside DeliverySlaService.RunAsync, which also
    // covers a direct (non-Hangfire) service call. Timeout < the 15-min recurrence.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken ct)
```

In `ProcuLink.Worker/Jobs/StuckDeliveryDetectionJob.cs`, replace:

```csharp
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
```

with:

```csharp
    [Queue("background")]
    [AutomaticRetry(Attempts = 0)]
    // DisableConcurrentExecution: two overlapping sweeps would both act on the same stranded
    // 'delivering' order — double-auditing it and racing its recovery bookkeeping. On OSS Hangfire
    // this keys the lock on TYPE + METHOD only (never on args) — correct for this argument-less
    // cross-tenant sweep. Timeout < the 15-min recurrence so a hung run can't block the next tick.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken ct)
```

- [ ] **Step 4: Correct the false comment on the poll-children guard test**

The existing comment asserts behaviour Hangfire does not have. Behaviour is unchanged — this is a docs-only correction so the next reader is not misled. In `ProcuLink.Api.Tests/Jobs/PollJobConcurrencyGuardTests.cs`, replace the class doc comment:

```csharp
/// <summary>
/// Guards the per-org concurrency lock on the pull-ingress child jobs. Hangfire's
/// <see cref="DisableConcurrentExecutionAttribute"/> keys the distributed lock on the method +
/// its arguments; each child job takes <c>orgId</c> as its first argument, so the lock is
/// effectively per-organisation — two children for the SAME org can never overlap, which (with
/// the claim-first ledger insert) closes the concurrent-duplicate-import window.
/// </summary>
```

with:

```csharp
/// <summary>
/// Guards the concurrency lock on the pull-ingress child jobs: two children polling the same org
/// concurrently would both pass the check-then-act dedupe, and (with the claim-first ledger
/// insert) this closes the concurrent-duplicate-import window.
///
/// <para><b>Keying (corrected 2026-07-16):</b> on OSS Hangfire this attribute keys the distributed
/// lock on the job TYPE + METHOD ONLY — it does NOT include the job's arguments, so this is a
/// GLOBAL lock per child job type, not a per-org one. Per-argument mutexing requires the paid
/// Hangfire.Pro <c>[Mutex]</c> (see <c>PerOrderDistributedMutexAttribute</c>, which exists for
/// exactly this reason). A global lock strictly contains the per-org lock the duplicate-import
/// argument needs, so correctness is unaffected — but every org's polling serialises through one
/// lock per channel, which is a throughput ceiling as org count grows. Tracked separately.</para>
/// </summary>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ConcurrencyGuardTests"`

Expected: PASS — 6 tests (3 sweep + 3 poll).

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Worker/Jobs/StuckOrderDetectionJob.cs \
        ProcuLink.Worker/Jobs/DeliverySlaSweepJob.cs \
        ProcuLink.Worker/Jobs/StuckDeliveryDetectionJob.cs \
        ProcuLink.Api.Tests/Jobs/SweepJobConcurrencyGuardTests.cs \
        ProcuLink.Api.Tests/Jobs/PollJobConcurrencyGuardTests.cs
git commit -m "fix(jobs): serialise the three recurring sweeps with DisableConcurrentExecution

StuckOrderDetection, DeliverySlaSweep and StuckDelivery had no concurrency
guard. Two overlapping runs duplicate audit rows (StuckRequeued /
DeliverySlaBreached) and lose the RequeueCount read-modify-write.

StuckDelivery was believed to already have the attribute; it did not.

Also corrects the poll-children guard test's doc comment, which claimed the
attribute keys on method + args. On OSS Hangfire it keys on type + method
only — a global lock, not per-org. Safer than claimed, so no behaviour
change here, but the comment would mislead.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Terminal parse failure surfaces as a failed Hangfire job

A parse failure sets `status='failed'` and returns `Fail`; the job throws; Hangfire retries; the retry re-enters `ParseStoredFileAsync`, whose `status != "parsing"` re-entry guard (`ProcuLink.Api/Services/Orders/OrderIngestionService.cs:613`) treats the now-`failed` order as an already-processed skip and returns `Ok`. The job logs success and Hangfire records it **Succeeded** — the Failed queue never shows the parse failure.

**Files:**
- Modify: `ProcuLink.Api/Jobs/ParseOrderJob.cs:58-60`
- Create: `ProcuLink.Api.Tests/Jobs/ParseOrderJobTerminalFailureTests.cs`

**Interfaces:**
- Consumes: `IOrderService.ParseStoredFileAsync(Guid organisationId, Guid orderId, CancellationToken ct)` returning `Result<ParsedFileOutput>`; `ParsedFileOutput(PurchaseOrderEntity Entity, IReadOnlyList<string>? ColumnHeaders, string DetectedFormat)`; `OrderStatusConstants.Failed == "failed"`.
- Produces: `ParseOrderJob.ExecuteAsync` now throws `InvalidOperationException` when the returned entity is terminally `failed`. Task 3 adds a second guard to the same method's analytics block.

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Api.Tests/Jobs/ParseOrderJobTerminalFailureTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Tier-D #2 — a TERMINAL parse failure must not be reported to Hangfire as a success.
///
/// The first attempt fails: the service sets status='failed' and returns Fail, and the job throws.
/// Hangfire then RETRIES, and the retry re-enters ParseStoredFileAsync, whose status!='parsing'
/// re-entry guard treats the now-'failed' order as an already-processed SKIP and returns Ok. The
/// job used to log success there, so the whole job landed Succeeded and every terminal parse
/// failure was invisible in the Failed queue. It must throw instead.
/// </summary>
public class ParseOrderJobTerminalFailureTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static ParseOrderJob NewJob(
        ProcuLinkDbContext db, IOrderService orders, FakeAnalyticsService analytics) =>
        new(orders,
            NullLogger<ParseOrderJob>.Instance,
            db,
            analytics,
            new Mock<ProcuLink.Core.Services.Detection.ISchemaFingerprintService>().Object);

    private static Mock<IOrderService> OrderServiceReturning(Guid orgId, Guid orderId, string status)
    {
        var mock = new Mock<IOrderService>();
        mock.Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ParsedFileOutput>.Ok(new ParsedFileOutput(
                new PurchaseOrderEntity { Id = orderId, OrgId = orgId, Status = status },
                null,
                "unknown")));
        return mock;
    }

    [Fact]
    public async Task ExecuteAsync_RetrySeesTerminallyFailedOrder_Throws()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var job = NewJob(db, OrderServiceReturning(orgId, orderId, OrderStatusConstants.Failed).Object, analytics);

        var act = async () => await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a terminally failed parse must land in Hangfire's Failed queue, not report Succeeded");
    }

    [Fact]
    public async Task ExecuteAsync_TerminallyFailedOrder_DoesNotEmitAnalytics()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var job = NewJob(db, OrderServiceReturning(orgId, orderId, OrderStatusConstants.Failed).Object, analytics);

        try { await job.ExecuteAsync(orderId, orgId, CancellationToken.None); }
        catch (InvalidOperationException) { /* expected — asserted in the sibling test */ }

        analytics.CapturedEvents.Should().BeEmpty(
            "first_upload_parsed must never fire for an order whose parse terminally failed");
    }

    [Fact]
    public async Task ExecuteAsync_NonTerminalSkip_StillSucceeds()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        var analytics = new FakeAnalyticsService();
        var job = NewJob(db, OrderServiceReturning(orgId, orderId, OrderStatusConstants.Ready).Object, analytics);

        var act = async () => await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a legitimate already-processed skip (ready / pending_review / unrouted) is not a failure");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ParseOrderJobTerminalFailureTests"`

Expected: FAIL — `ExecuteAsync_RetrySeesTerminallyFailedOrder_Throws` fails with "Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown". `ExecuteAsync_TerminallyFailedOrder_DoesNotEmitAnalytics` also fails (one captured event). `ExecuteAsync_NonTerminalSkip_StillSucceeds` passes already.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Api/Jobs/ParseOrderJob.cs`, insert immediately **before** the existing `_logger.LogInformation("ParseOrderJob completed for order {OrderId}, new status={Status}", …)` call:

```csharp
        // ── Terminal-failure guard ────────────────────────────────────────────
        // A parse failure sets status='failed' and returns Fail, and we throw above. Hangfire then
        // RETRIES, and the retry re-enters ParseStoredFileAsync, whose status!='parsing' re-entry
        // guard sees the now-'failed' order, treats it as an already-processed SKIP and returns Ok.
        // Reporting that as success marked the whole job Succeeded and hid every terminal parse
        // failure from Hangfire's Failed queue. Throw instead: the remaining retries burn out on a
        // cheap read and the job lands red where ops can see it. Attempt 1's real exception stays in
        // the job history. This also short-circuits the analytics block below, which would otherwise
        // fire first_upload_parsed for a FAILED order.
        if (result.Value!.Entity.Status == OrderStatusConstants.Failed)
        {
            _logger.LogError(
                "ParseOrderJob: order {OrderId} is in terminal status '{Status}' — surfacing as a failed job rather than reporting success.",
                orderId, result.Value!.Entity.Status);
            throw new InvalidOperationException(
                $"Parse failed: order {orderId} is in terminal status '{OrderStatusConstants.Failed}'.");
        }

```

`OrderStatusConstants` is already imported (`using ProcuLink.Core.Constants;` at line 3).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ParseOrderJobTerminalFailureTests"`

Expected: PASS — 3 tests.

Then confirm no regression in the existing analytics test:

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ParseOrderJobEmitsFirstUploadParsedTests"`

Expected: PASS — 2 tests.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Jobs/ParseOrderJob.cs \
        ProcuLink.Api.Tests/Jobs/ParseOrderJobTerminalFailureTests.cs
git commit -m "fix(jobs): terminal parse failure no longer reports Hangfire Succeeded

A failed parse set status='failed' and threw, but the Hangfire retry then hit
ParseStoredFileAsync's status!='parsing' re-entry guard, which treats an
already-processed order as a SKIP and returns Ok. The job logged success, so
the job landed Succeeded and the parse failure never appeared in the Failed
queue.

ParseOrderJob now throws when the returned order is terminally failed. The
service contract is unchanged — a legitimate skip (ready / pending_review /
unrouted) still returns Ok and still no-ops. This also stops the analytics
block firing first_upload_parsed for a failed order.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Gate `first_upload_parsed` to once per order

The existing guard asks "does any OTHER order for this org sit in a parsed state". An org whose only order is re-parsed — routing's `assign-supplier` flips `unrouted` → `parsing` and re-parses — still finds no other parsed order, so the event fires a second time. Deterministic double-count.

`ParseStoredFileAsync` writes exactly one `Parsed` audit event per parse, so `> 1` for this order means re-parse.

**Files:**
- Modify: `ProcuLink.Api/Jobs/ParseOrderJob.cs:67-75` (the `hadOtherParsedOrders` block)
- Create: `ProcuLink.Api.Tests/Jobs/ParseOrderJobReParseAnalyticsTests.cs`

**Interfaces:**
- Consumes: Task 2's terminal-failure guard sits above this block in the same method — leave it intact. `AuditEvent { OrgId, EntityType, EntityId, Action }` (see `ProcuLink.Core/Entities`).
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Api.Tests/Jobs/ParseOrderJobReParseAnalyticsTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Tier-D #3 — first_upload_parsed is a once-per-ORDER milestone, not a per-parse event.
///
/// The org-level guard ("does any OTHER order for this org sit in a parsed state") does not stop a
/// re-parse of an org's ONLY order from firing it twice: routing's assign-supplier flips an
/// 'unrouted' order back to 'parsing' and re-parses it, and the AnyAsync still finds no other
/// parsed order. ParseStoredFileAsync writes exactly one 'Parsed' audit event per parse, so more
/// than one for this order means re-parse.
/// </summary>
public class ParseOrderJobReParseAnalyticsTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    /// <summary>Seeds the org's single order plus <paramref name="parseCount"/> 'Parsed' audit events.</summary>
    private static async Task SeedOrderWithParseHistoryAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, int parseCount)
    {
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-1",
            Currency      = "EUR",
            Status        = OrderStatusConstants.PendingReview,
            SourceFileKey = "uploads/some-file.csv",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });

        for (var i = 0; i < parseCount; i++)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id         = Guid.NewGuid(),
                OrgId      = orgId,
                UserId     = null,
                EntityType = "Order",
                EntityId   = orderId,
                Action     = "Parsed",
                Payload    = JsonDocument.Parse("""{"lineCount":1}"""),
                CreatedAt  = DateTime.UtcNow.AddMinutes(-i),
            });
        }

        await db.SaveChangesAsync();
    }

    private static ParseOrderJob NewJob(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, FakeAnalyticsService analytics)
    {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<ParsedFileOutput>.Ok(new ParsedFileOutput(
                  new PurchaseOrderEntity
                  {
                      Id = orderId, OrgId = orgId, Status = OrderStatusConstants.PendingReview,
                  },
                  null,
                  "csv")));

        return new ParseOrderJob(
            orders.Object,
            NullLogger<ParseOrderJob>.Instance,
            db,
            analytics,
            new Mock<ProcuLink.Core.Services.Detection.ISchemaFingerprintService>().Object);
    }

    [Fact]
    public async Task ExecuteAsync_ReParseOfOrgsOnlyOrder_DoesNotReEmit()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        // Two 'Parsed' events = the first parse plus this re-parse.
        await SeedOrderWithParseHistoryAsync(db, orgId, orderId, parseCount: 2);

        var analytics = new FakeAnalyticsService();
        await NewJob(db, orgId, orderId, analytics).ExecuteAsync(orderId, orgId, CancellationToken.None);

        analytics.CapturedEvents.Should().BeEmpty(
            "first_upload_parsed already fired on this order's first parse — a re-parse must not double-count it");
    }

    [Fact]
    public async Task ExecuteAsync_GenuineFirstParse_StillEmits()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();
        // Exactly one 'Parsed' event — the shape a real first parse leaves behind.
        await SeedOrderWithParseHistoryAsync(db, orgId, orderId, parseCount: 1);

        var analytics = new FakeAnalyticsService();
        await NewJob(db, orgId, orderId, analytics).ExecuteAsync(orderId, orgId, CancellationToken.None);

        analytics.CapturedEvents.Should().ContainSingle()
            .Which.EventName.Should().Be("first_upload_parsed");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ParseOrderJobReParseAnalyticsTests"`

Expected: FAIL — `ExecuteAsync_ReParseOfOrgsOnlyOrder_DoesNotReEmit` fails ("Expected analytics.CapturedEvents to be empty, but found 1 item"). `ExecuteAsync_GenuineFirstParse_StillEmits` passes already.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Api/Jobs/ParseOrderJob.cs`, replace this block:

```csharp
        var hadOtherParsedOrders = await _db.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(o => o.OrgId == organisationId
                        && o.Id != orderId
                        && o.Status != OrderStatusConstants.Parsing
                        && o.Status != OrderStatusConstants.PendingParse
                        && o.Status != OrderStatusConstants.Failed, ct);

        if (!hadOtherParsedOrders)
        {
```

with:

```csharp
        var hadOtherParsedOrders = await _db.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(o => o.OrgId == organisationId
                        && o.Id != orderId
                        && o.Status != OrderStatusConstants.Parsing
                        && o.Status != OrderStatusConstants.PendingParse
                        && o.Status != OrderStatusConstants.Failed, ct);

        // Re-parse guard: the org-level check above does NOT stop an org's ONLY order from firing
        // this twice. Routing's assign-supplier flips an 'unrouted' order back to 'parsing' and
        // re-parses it; on that second parse the AnyAsync still finds no OTHER parsed order, so the
        // event re-fired — a deterministic double-count. first_upload_parsed is a once-per-order
        // milestone, not a per-parse event. ParseStoredFileAsync writes exactly one 'Parsed' audit
        // event per parse, so more than one for this order means we have been here before.
        var parseCount = await _db.AuditEvents
            .AsNoTracking()
            .CountAsync(e => e.OrgId == organisationId
                          && e.EntityType == "Order"
                          && e.EntityId == orderId
                          && e.Action == "Parsed", ct);
        var isReParse = parseCount > 1;

        if (!hadOtherParsedOrders && !isReParse)
        {
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~ParseOrderJob"`

Expected: PASS — all `ParseOrderJob*` tests green (`ReParseAnalytics` 2, `TerminalFailure` 3, `EmitsFirstUploadParsed` 2).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Jobs/ParseOrderJob.cs \
        ProcuLink.Api.Tests/Jobs/ParseOrderJobReParseAnalyticsTests.cs
git commit -m "fix(analytics): fire first_upload_parsed once per order, not per parse

The guard only asked whether any OTHER order for the org had parsed, so an org
whose single order was re-parsed (assign-supplier flips unrouted -> parsing and
re-parses) fired the event again — a deterministic double-count.

Gate on the order's own 'Parsed' audit-event count, which ParseStoredFileAsync
writes exactly once per parse. No migration; reuses rows that already exist.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: SLA sweep — move the guard into the UPDATE

`DeliverySlaService.RunAsync` filters `!o.SlaBreached` in the **SELECT**, then sets the flag in memory and `SaveChanges`. Two overlapping sweeps both select the same unflagged order and both add an audit event. The order write is idempotent (both set `true`); the audit rows duplicate.

This is the layer that is actually testable — Task 1's mutex makes concurrent sweeps unreachable *via Hangfire*, but a direct service call bypasses it.

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliverySlaService.cs:31-84` (replace `RunAsync`, add three private helpers)
- Create: `ProcuLink.Api.Tests/Integration/DeliverySlaConcurrencyPostgresTests.cs`
- Test (must stay green, unmodified): `ProcuLink.Infrastructure.Tests/Services/DeliverySlaServiceTests.cs`

**Interfaces:**
- Consumes: `IDeliverySlaService.RunAsync(CancellationToken ct)` returning `Task<int>` — signature unchanged. `DeliverySlaService(ProcuLinkDbContext db, ILogger<DeliverySlaService> logger)` — ctor unchanged.
- Produces: `RunAsync` now returns the number of orders **this** sweep claimed (a loser in a race returns 0 for that order rather than counting it).

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Api.Tests/Integration/DeliverySlaConcurrencyPostgresTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Tier-D #5 — proves two overlapping SLA sweeps cannot double-insert the DeliverySlaBreached audit
/// row on REAL Postgres, where the atomic ExecuteUpdateAsync claim actually runs (the EF InMemory
/// provider cannot translate it, so DeliverySlaServiceTests would pass against the bug).
///
/// Before the fix both sweeps SELECT the same unflagged overdue order (the !SlaBreached guard sat in
/// the SELECT), both set the flag in memory, and both append an audit row. After the fix the guard
/// is in the UPDATE, so only the sweep whose claim affects a row writes the audit event.
///
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class DeliverySlaConcurrencyPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_sla_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        // Pooling=false so each concurrent context opens its OWN physical connection — the claim race
        // is only real when two sweeps hold two connections (a pooled single connection would
        // serialise them and hide the bug the atomic claim must defend against).
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    /// <summary>Seeds org + supplier + one overdue, unflagged, still-delivering order.</summary>
    private async Task<(Guid OrgId, Guid OrderId)> SeedOverdueOrderAsync()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_sla_{orgId:N}", Name = "SLA Org",
            Slug = $"sla-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "SLA Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-SLA-CONC-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 1),
            Currency = "EUR", Status = OrderStatusConstants.Delivering,
            DeliveryDueAt = now.AddMinutes(-5), SlaBreached = false,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return (orgId, orderId);
    }

    [DockerRequiredFact]
    public async Task RunAsync_TwoOverlappingSweeps_WriteExactlyOneBreachAudit()
    {
        var (_, orderId) = await SeedOverdueOrderAsync();

        await using var dbA = NewContext();
        await using var dbB = NewContext();

        var sweepA = new DeliverySlaService(dbA, NullLogger<DeliverySlaService>.Instance);
        var sweepB = new DeliverySlaService(dbB, NullLogger<DeliverySlaService>.Instance);

        var flagged = await Task.WhenAll(
            Task.Run(() => sweepA.RunAsync(CancellationToken.None)),
            Task.Run(() => sweepB.RunAsync(CancellationToken.None)));

        await using var verify = NewContext();

        var auditCount = await verify.AuditEvents
            .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
        auditCount.Should().Be(1,
            "the guard must live in the UPDATE — an overlapping sweep that loses the claim must write no audit row");

        flagged.Sum().Should().Be(1, "exactly one sweep may claim the breach");

        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.SlaBreached.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task RunAsync_TwoSweepsOverManyOrders_EachOrderGetsExactlyOneAudit()
    {
        // Several orders in one sweep set: each claim holds a row lock until the sweep's transaction
        // commits, so this exercises the multi-row claim path the single-order test cannot reach.
        // Both sweeps walk the set in the same total order (OrderBy(o => o.Id)), so they queue on the
        // same rows in the same sequence rather than deadlocking.
        var orderIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
            orderIds.Add((await SeedOverdueOrderAsync()).OrderId);

        await using var dbA = NewContext();
        await using var dbB = NewContext();

        var flagged = await Task.WhenAll(
            Task.Run(() => new DeliverySlaService(dbA, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)),
            Task.Run(() => new DeliverySlaService(dbB, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)));

        await using var verify = NewContext();

        foreach (var orderId in orderIds)
        {
            var auditCount = await verify.AuditEvents
                .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
            auditCount.Should().Be(1, $"order {orderId} must be audited exactly once across both sweeps");
        }

        flagged.Sum().Should().Be(orderIds.Count,
            "every order is claimed exactly once, by whichever sweep won it");
    }

    [DockerRequiredFact]
    public async Task RunAsync_SecondSweepAfterFirst_IsIdempotent()
    {
        var (_, orderId) = await SeedOverdueOrderAsync();

        await using (var dbA = NewContext())
            (await new DeliverySlaService(dbA, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)).Should().Be(1);

        await using (var dbB = NewContext())
            (await new DeliverySlaService(dbB, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)).Should().Be(0, "an already-flagged order no longer matches");

        await using var verify = NewContext();
        var auditCount = await verify.AuditEvents
            .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
        auditCount.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~DeliverySlaConcurrencyPostgresTests"`

Expected: FAIL — `RunAsync_TwoOverlappingSweeps_WriteExactlyOneBreachAudit` fails with "Expected auditCount to be 1, but found 2", and `RunAsync_TwoSweepsOverManyOrders_EachOrderGetsExactlyOneAudit` fails the same way on its first order. `RunAsync_SecondSweepAfterFirst_IsIdempotent` passes already (the sweeps are sequential there, so the SELECT filter alone is enough).

If both are reported **skipped**, Docker is not reachable — start Docker Desktop and re-run. A skipped test is not a passing test; this task cannot be verified without it.

- [ ] **Step 3: Write minimal implementation**

In `ProcuLink.Infrastructure/Services/DeliverySlaService.cs`, replace the whole `RunAsync` method (lines 31-84) with:

```csharp
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Cross-tenant maintenance sweep (mirrors StuckOrderDetectionService). Tenant isolation
        // is preserved because each flagged order and its audit event carry that order's own OrgId.
        //
        // NOTE: the !SlaBreached predicate here is only a cheap pre-filter. It is NOT the guard —
        // it is advisory (TOCTOU), because an overlapping sweep can flag the same order between this
        // SELECT and the write. The real guard is the atomic claim in FlagAtomicallyAsync. Entities
        // are left TRACKED because the non-relational path below mutates them.
        //
        // OrderBy(o => o.Id) is load-bearing, not cosmetic: FlagAtomicallyAsync claims row-by-row
        // inside one transaction, so it holds each claimed row's lock until commit. Two overlapping
        // sweeps walking an UNORDERED result set could lock the same two orders in opposite order
        // and deadlock — Postgres would detect it and kill one sweep. A total order shared by every
        // sweep makes that impossible.
        var breached = await _db.PurchaseOrders
            .Where(o => o.DeliveryDueAt != null
                        && o.DeliveryDueAt < now
                        && !o.SlaBreached
                        && !ExcludedStatuses.Contains(o.Status))
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        if (breached.Count == 0)
            return 0;

        var flagged = _db.Database.IsRelational()
            ? await FlagAtomicallyAsync(breached, now, ct)
            : await FlagViaChangeTrackerAsync(breached, now, ct);

        if (flagged > 0)
            _logger.LogWarning("DeliverySla: flagged {Count} order(s) as SLA-breached.", flagged);

        return flagged;
    }

    /// <summary>
    /// Relational path — the flag flip IS the claim. Moving the !SlaBreached condition into the
    /// UPDATE means only the sweep whose statement affects a row writes the audit event; an
    /// overlapping sweep sees 0 rows and writes nothing, so the DeliverySlaBreached audit row can
    /// never be double-inserted.
    ///
    /// <para>One transaction wraps the claims and their audit rows: ExecuteUpdateAsync auto-commits
    /// its own statement, so an unwrapped claim followed by a separate SaveChanges could crash
    /// between the two and leave a flagged order with NO audit trail. ExecuteUpdate enlists in the
    /// ambient transaction on Npgsql — the same pattern as the persist block in
    /// OrderIngestionService.ParseStoredFileAsync. The sweep set is small (overdue deliveries only),
    /// so the transaction is short.</para>
    /// </summary>
    private async Task<int> FlagAtomicallyAsync(
        List<PurchaseOrderEntity> breached, DateTime now, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var flagged = 0;
        foreach (var order in breached)
        {
            var claimed = await _db.PurchaseOrders
                .Where(o => o.Id == order.Id
                         && o.OrgId == order.OrgId
                         && !o.SlaBreached)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.SlaBreached, true)
                    .SetProperty(o => o.UpdatedAt, now), ct);

            if (claimed == 0)
            {
                _logger.LogInformation(
                    "DeliverySla: order {OrderId} (org {OrgId}) was flagged by a concurrent sweep — skipping duplicate audit.",
                    order.Id, order.OrgId);
                continue;
            }

            AddBreachAudit(order, now);
            flagged++;
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return flagged;
    }

    /// <summary>
    /// Non-relational path — the EF InMemory test provider can translate neither ExecuteUpdate nor a
    /// transaction, so fall back to the original read-modify-write. InMemory tests are
    /// single-threaded, so the atomicity the relational claim guarantees is not needed here. Mirrors
    /// FireIntegrationTriggerJob.RecordFailureAsync, which splits on IsRelational() for the same reason.
    /// </summary>
    private async Task<int> FlagViaChangeTrackerAsync(
        List<PurchaseOrderEntity> breached, DateTime now, CancellationToken ct)
    {
        foreach (var order in breached)
        {
            order.SlaBreached = true;
            order.UpdatedAt = now;
            AddBreachAudit(order, now);
        }

        await _db.SaveChangesAsync(ct);
        return breached.Count;
    }

    /// <summary>Queues the DeliverySlaBreached audit row for one claimed order. Caller SaveChanges.</summary>
    private void AddBreachAudit(PurchaseOrderEntity order, DateTime now)
    {
        var dueAt = order.DeliveryDueAt!.Value;

        var payload = JsonSerializer.Serialize(new
        {
            reason = "DeliverySlaBreached",
            status = order.Status,
            dueAt,
            detectedAt = now,
            overdueMinutes = Math.Round((now - dueAt).TotalMinutes, 1),
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = order.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliverySlaBreached",
            Payload = JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        _logger.LogWarning(
            "DeliverySla: order {OrderId} (org {OrgId}) breached its SLA — due {DueAt:o}, status '{Status}'.",
            order.Id, order.OrgId, dueAt, order.Status);
    }
```

All required `using` directives are already present at the top of the file (`System.Text.Json`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.Logging`, `ProcuLink.Core.Constants`, `ProcuLink.Core.Entities`, `ProcuLink.Core.Services.Delivery`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~DeliverySlaConcurrencyPostgresTests"`

Expected: PASS — 3 tests.

Then confirm the InMemory path did not regress:

Run: `dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliverySlaServiceTests"`

Expected: PASS — all existing tests green, unmodified.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliverySlaService.cs \
        ProcuLink.Api.Tests/Integration/DeliverySlaConcurrencyPostgresTests.cs
git commit -m "fix(delivery): move the SLA-breach guard from the SELECT into the UPDATE

RunAsync filtered !SlaBreached in the SELECT, then set the flag in memory and
saved. Two overlapping sweeps both selected the same unflagged order and both
appended a DeliverySlaBreached audit row (the order write itself is idempotent
— both set true — but the audit rows duplicated).

The flag flip is now the claim: the condition lives in a per-order
ExecuteUpdateAsync, so only the sweep whose statement affects a row writes the
audit event. Claim and audit share one transaction, since ExecuteUpdate
auto-commits and a crash between the two would leave a flagged order with no
audit trail.

InMemory cannot translate ExecuteUpdate or transactions, so the original
read-modify-write is kept behind IsRelational() — same split as
FireIntegrationTriggerJob.RecordFailureAsync. The race is proven on real
Postgres, since an InMemory-only test would pass against the bug.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Full verification

**Files:** none modified.

**Interfaces:**
- Consumes: all four preceding tasks.
- Produces: the evidence needed before merge.

- [ ] **Step 1: Build clean**

Run: `dotnet build --no-incremental`

Expected: `Build succeeded. 0 Error(s)`.

`--no-incremental` is required — this is a git worktree, where stale incremental artifacts have previously masked source that never reached disk.

- [ ] **Step 2: Confirm the edits actually reached disk**

Run:

```bash
git status --short
git diff --stat HEAD~4
```

Expected: the 9 files from the File Structure table appear. If a file you edited is missing from the diff, the write did not land — rewrite it and rebuild before continuing.

- [ ] **Step 3: Run the affected suites**

Run: `dotnet test ProcuLink.Api.Tests`

Expected: PASS. `DeliverySlaConcurrencyPostgresTests` must show as **run, not skipped** — skipped means Docker is down and item 5 is unverified.

Run: `dotnet test ProcuLink.Infrastructure.Tests`

Expected: PASS.

Known pre-existing noise: `TwoConcurrentRetries…` in `ProcuLink.Api.Tests` is a documented flaky Postgres test. If it fails, re-run it alone to confirm it is the known flake and not a regression from this work.

- [ ] **Step 4: Push and check CI**

```bash
git push -u origin HEAD
gh run list --limit 3
```

Expected: the run for this branch goes green. Local green is not CI green — Windows dev, Linux CI.

---

## Out of scope

- Original batch items 4 (`EmailPollOrgJob` transient `DbUpdateException`) and 6 (`FireIntegrationTrigger` FailureCount) — verified already fixed. No code change.
- The three unnamed Tier-D items: `TransformOrderJob` concurrent claim → orphan R2 blob; `CatalogSyncSource` child gate ≠ dispatcher gate; `StuckOrder` `RequeueCount++` before enqueue confirmed.
- Changing the poll children's mutex **behaviour** — Task 1 corrects only the false comment.

## Coordination

Session history shows B4 (`StuckDeliveryDetectionService` RequeueCount) staged in another worktree. That touches the **service**; Task 1 touches the **job wrapper**. Collision risk is low, but rebase and rebuild the combined tree before merging — a prior incident came from merging two branches that edited the same method.
