# Invisible transform strand — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `OrderTransformService.TransformAsync` must never return while the order sits in `transforming` — every failure lands in `transform_failed`, which is counted, visible, and re-claimable.

**Architecture:** Two pieces. (1) A status-guarded failure write, `FailTransformFromClaimableAsync`, that moves the order to `transform_failed` only from `OrderStatusMachine.ClaimableForTransformFrom` — safe to call from outside the claim because a row nobody could have claimed is a row it refuses to touch. (2) `TransformAsync` becomes a thin `try`/`catch` wrapper around the existing body, which moves verbatim into a new private `TransformCoreAsync`. The two `Result.Fail` returns that represent real order-level failures route through the same guarded helper.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + Moq, Hangfire.

**Spec:** [`docs/superpowers/specs/2026-08-07-transform-pre-claim-strand-design.md`](../specs/2026-08-07-transform-pre-claim-strand-design.md)

---

## Two deliberate deltas from the approved spec

Both were discovered while writing this plan. Neither removes anything the spec approved; both are supersets or simplifications of it. **Read these before starting Task 1.**

### Delta 1 — the guard region is the WHOLE method body, not lines 100-419

The spec scoped the `try` to lines 100-419 (pre-claim + the claim). Reading past line 647 shows the same unprotected shape on the far side of the transform:

- `OrderTransformService.cs:664` — `await _fileStorage.UploadAsync(...)`, a network call to Cloudflare R2.
- `OrderTransformService.cs:747` — the final `SaveChangesAsync` that commits the artifact row, the `ready_to_deliver` status and the `Transformed` audit event.

Neither is inside any `try`. A throw from either leaves the order at `transforming` — the identical invisible strand, reached by a failure mode (an R2 blip) that is far *more* likely in production than a poisoned `CanonicalJson`. Guarding only 100-419 would fix the rarer half and leave the more likely half open.

Guarding the whole body is also **simpler**, not more complex: the wrapper needs no hoisted locals at all, versus the ~24 that a mid-method `try` would force out of scope.

The widening is safe because of the guard, not in spite of it. The one dangerous case — a throw *after* the artifact commit at line 747 — cannot mis-fire: the order is then at `ready_to_deliver`, which is not in `ClaimableForTransformFrom`, so the guarded write refuses and the successful transform is left alone. This is the same argument the spec already makes for the `claimed == 0` branch, applied to the other end of the method.

### Delta 2 — the helper takes no entity parameter

The spec gave the helper a nullable tracked-entity parameter for a post-win tracker sync. That is now unnecessary and actively unwanted, because of a hazard the spec did not anticipate:

If the throw came from the `SaveChangesAsync` at line 747, the change tracker still holds an Added `OutboundArtifact` and a modified `entity.Status = ready_to_deliver`. A helper that then called `SaveChangesAsync` would **re-attempt that entire failed commit** — inserting the artifact and writing `ready_to_deliver` over the `transform_failed` we just wrote. (Same family as the known `ExecuteDelete leaves phantom tracked rows` trap.)

So the catch calls `_db.ChangeTracker.Clear()` before failing. That discards the poisoned pending state, which also means there is no tracked entity left to sync — the helper works purely from `(organisationId, orderId)`. Clearing is safe: a Postgres `SaveChanges` is all-or-nothing, so a failed one committed nothing worth keeping, and the InMemory provider persists nothing until `SaveChanges` either.

---

## Global Constraints

- **EF Core only. No raw SQL.** (`CLAUDE.md`)
- **Every EF query scoped `.Where(x => x.OrganisationId == organisationId)`** — here the column is `OrgId`. No exceptions.
- **Hangfire jobs stay idempotent.**
- **No new order status.** Reuse `OrderStatusConstants.TransformFailed`. `ProcuLink.Core/Constants/OrderStatusConstants.cs` and the frontend's `src/lib/orderStatusManifest.ts` must not change.
- **Never run `git checkout <file>` in this repository.** Undo every mutation check by editing the file back.
- **Plain-language user-facing copy.** No exception text, no stack traces, no internal jargon in any string that reaches `errorMessage`.
- Status-list membership comes from `OrderStatusMachine`, never a hand-written literal.
- Build: `dotnet build ProcuLink.slnx --configuration Release`. Full suite: `dotnet test ProcuLink.slnx --configuration Release`.

## File structure

| File | Responsibility |
|---|---|
| `ProcuLink.Api/Services/Orders/OrderTransformService.cs` | Task 1: `TransformAsync` becomes the wrapper, body moves verbatim to `TransformCoreAsync`, new `FailTransformFromClaimableAsync`. Task 2: two `Result.Fail` reroutes + one stale comment. |
| `ProcuLink.Infrastructure/Services/StuckOrderDetectionService.cs` | Task 1: comment correction only, no behaviour change. |
| `ProcuLink.Api.Tests/Services/TransformStrandNeverSilentTests.cs` | Both tasks: the whole proof. New file. |

---

## Task 1: The guarded failure write and the wrapper

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderTransformService.cs:93-98` (split into wrapper + core), and add a new private method after `FailTransformAsync` (which ends at `:804`)
- Modify: `ProcuLink.Infrastructure/Services/StuckOrderDetectionService.cs:145-154` (comment only)
- Create: `ProcuLink.Api.Tests/Services/TransformStrandNeverSilentTests.cs`

**Interfaces:**
- Consumes: `OrderStatusMachine.ClaimableForTransformFrom` (`IReadOnlySet<string>`, = `{ready, transforming, transform_failed, rejected_by_supplier}`) from `ProcuLink.Core.Constants`; `OrderServiceShared.BuildAuditEvent(Guid orgId, Guid entityId, string action, object payload)`; `_shared.SafeReconcileExceptionsAsync(Guid, Guid, CancellationToken)`.
- Produces: `private async Task<bool> FailTransformFromClaimableAsync(Guid organisationId, Guid orderId, string error, CancellationToken ct)` — returns `true` when it won the row and wrote the audit trail, `false` when the order was not in a claimable status and was therefore left untouched. Task 2 calls this exact signature.

- [ ] **Step 1: Write the failing tests**

Create `ProcuLink.Api.Tests/Services/TransformStrandNeverSilentTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// An unexpected failure anywhere in <c>TransformAsync</c> must be VISIBLE.
///
/// <para>Before this fix, <c>TransformAsync</c> had no exception handling at all between its first
/// line and the acceptance gate's try at <c>:455</c>, and none again between the artifact generation
/// handler at <c>:647</c> and the end of the method. Anything thrown in either region unwound
/// through <c>TransformOrderJob</c> into Hangfire, which retried and then permanently failed the
/// job, leaving the order at <c>transforming</c>. <c>StuckOrderDetectionService</c> then recovered
/// that strand to <c>ready</c> — deliberately, and explicitly never marking it failed, because its
/// premise was that a job which actually RAN and failed had already written its own status.</para>
///
/// <para>The combined effect was that a real, repeatable error produced no <c>transform_failed</c>
/// status, no error message, no ops-health count, and no exception row. It looked like nothing had
/// happened. These tests fail on the pre-fix code.</para>
/// </summary>
public sealed class TransformStrandNeverSilentTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>A cXML credential resolver that throws — a real seam on the pre-claim path
    /// (<c>OrderTransformService.cs:154</c>), reached only when the effective format is cXML.</summary>
    private sealed class ThrowingCxmlResolver : ICxmlCredentialResolver
    {
        private readonly Exception _toThrow;
        public ThrowingCxmlResolver(Exception toThrow) => _toThrow = toThrow;
        public Task<CxmlCredentialConfig?> ResolveAsync(Guid organisationId, Guid supplierId, CancellationToken ct)
            => throw _toThrow;
    }

    /// <summary>
    /// OrderService wired for cXML, with an injectable cXML resolver and an injectable upload
    /// behaviour. <paramref name="uploadThrows"/> covers the far side of the method (the R2 call at
    /// <c>OrderTransformService.cs:664</c>), which was unguarded for the same reason.
    /// </summary>
    private static OrderService Build(
        ProcuLinkDbContext db,
        ICxmlCredentialResolver? cxmlResolver = null,
        Exception? uploadThrows = null,
        bool registerTransformers = true)
    {
        var fileStorage = new Mock<IFileStorageService>();
        var upload = fileStorage.Setup(s => s.UploadAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));

        if (uploadThrows is not null) upload.ThrowsAsync(uploadThrows);
        else                          upload.ReturnsAsync("artifact-key");

        var transformers = registerTransformers
            ? new ITransformService[] { new CxmlTransformService(), new XmlTransformService() }
            : Array.Empty<ITransformService>();

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            transformers,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            cxmlResolver: cxmlResolver);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId)> SeedAsync(
        ProcuLinkDbContext db, string status = OrderStatusConstants.Transforming, bool resolved = true)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-STRAND-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 8, 7),
            Currency = "EUR", Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m,
                    NeedsReview = !resolved, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    private static Task<List<AuditEvent>> TransformFailedEventsAsync(ProcuLinkDbContext db, Guid orderId) =>
        db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == orderId && a.Action == "TransformFailed")
            .ToListAsync();

    private static Task<string> StatusOfAsync(ProcuLinkDbContext db, Guid orderId) =>
        db.PurchaseOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).FirstAsync();

    // ── 1. A pre-claim throw is recorded, not swallowed ───────────────────────

    [Fact]
    public async Task APreClaimThrow_isRecordedAsTransformFailed_notLeftInTransforming()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new InvalidOperationException("resolver exploded")));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        // EVIDENCE FIRST. The audit row is what OrdersController reads to populate errorMessage and
        // what OrderExceptionService reconciles into the operator-workable exception. Asserting the
        // status string ahead of it would let a mutation that writes the status but drops the trail
        // pass — and a mutation run reports only the FIRST failure, so an assertion behind a passing
        // one is never reached.
        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);

        var error = events[0].Payload.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("resolver exploded", error);   // raw exception text never reaches the operator

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    // ── 2. The guard: a row we could not have claimed is not touched ──────────

    [Fact]
    public async Task AThrowOnAnOrderThatIsNotClaimable_leavesTheStatusAlone()
    {
        await using var db = NewDb();
        // ready_to_deliver is NOT in OrderStatusMachine.ClaimableForTransformFrom: this order has a
        // completed transform and possibly an in-flight delivery. Failing it here would overwrite a
        // good result with a false failure.
        var seed = await SeedAsync(db, status: OrderStatusConstants.ReadyToDeliver);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new InvalidOperationException("resolver exploded")));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    // ── 3. Cancellation is not a failure ──────────────────────────────────────

    [Fact]
    public async Task ACancelledTransform_propagates_andIsNotRecordedAsAFailure()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db, cxmlResolver: new ThrowingCxmlResolver(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None));

        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.Transforming, await StatusOfAsync(db, seed.OrderId));
    }

    // ── 4. The far side of the method: the artifact upload ────────────────────

    [Fact]
    public async Task AFailedArtifactUpload_isRecordedAsTransformFailed_notLeftInTransforming()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        // The R2 call at OrderTransformService.cs:664 sat outside every try for the same reason the
        // pre-claim region did, and a storage blip is the likelier of the two in production.
        var svc = Build(db, uploadThrows: new IOException("R2 unavailable"));

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);
        Assert.DoesNotContain("R2 unavailable", events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);

        // The failed transform left nothing behind that a delivery sweep could pick up.
        Assert.Empty(await db.OutboundArtifacts.AsNoTracking().Where(a => a.OrderId == seed.OrderId).ToListAsync());
    }

    // ── 5. Negative control ───────────────────────────────────────────────────

    /// <summary>
    /// Identical fixture, identical code path; the ONLY difference is that nothing throws. Without
    /// this, "the guard records real failures" and "something now fails every transform" are
    /// indistinguishable — which would make every assertion above worthless.
    /// </summary>
    [Fact]
    public async Task NegativeControl_theSameOrderTransformsCleanlyWhenNothingThrows()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db);   // ← the one difference: no throwing resolver, no throwing upload

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(await TransformFailedEventsAsync(db, seed.OrderId));
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, await StatusOfAsync(db, seed.OrderId));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~TransformStrandNeverSilentTests"`

Expected: 5 tests, **3 FAIL**, 2 pass.

- `APreClaimThrow_…` FAILS — the `InvalidOperationException` escapes `TransformAsync`, so the assertion is never reached and the test errors with `resolver exploded`.
- `AFailedArtifactUpload_…` FAILS the same way, with `R2 unavailable`.
- `AThrowOnAnOrderThatIsNotClaimable_…` FAILS — same escape.
- `ACancelledTransform_…` passes already (nothing catches it today) — it is the regression pin for Step 3, not a red test.
- `NegativeControl_…` passes already.

If `APreClaimThrow_…` does **not** fail, stop: the fixture is not reaching line 154. Confirm the seeded order is `transforming` and the format is `OutputFormat.CXml` — the resolver is called only for cXML.

- [ ] **Step 3: Write the guarded failure helper**

In `ProcuLink.Api/Services/Orders/OrderTransformService.cs`, immediately after `FailTransformAsync` (which ends at `:804`), add:

```csharp
    /// <summary>
    /// Commits a transform failure from OUTSIDE the claim: moves the order to
    /// <see cref="OrderStatusConstants.TransformFailed"/> only while it is still one of
    /// <see cref="OrderStatusMachine.ClaimableForTransformFrom"/>, and writes the audit trail ONLY
    /// when that guarded update actually won the row. Returns whether it won.
    ///
    /// <para><b>Why guarded, when <see cref="FailTransformAsync"/> is not.</b> Every caller of
    /// <c>FailTransformAsync</c> sits behind the claim and therefore owns the row. This one does
    /// not: it runs from the wrapper's catch, which can fire before the claim has been taken (or
    /// after it has been released by a completed transform). An unguarded write there would land on
    /// top of whatever else moved the order in the meantime — a <c>billing_held</c> park, an MV-1
    /// <c>pending_review</c> reset, or a <c>ready_to_deliver</c> transform that had already
    /// succeeded. The guard set is the CLAIM's own set, which makes the rule one sentence: if we
    /// could have claimed it, we may fail it.</para>
    ///
    /// <para>The set comes from <see cref="OrderStatusMachine"/> rather than a literal for the same
    /// reason the claim's does — this is the second hand-written copy of a status list, which is
    /// exactly how the five delivery-claim lists drifted apart four times.</para>
    ///
    /// <para>Idempotent: a second pass over an order already in <c>transform_failed</c> re-writes the
    /// same status (it is in the claimable set) and adds one more audit row, producing no artifact
    /// and no delivery either way.</para>
    /// </summary>
    private async Task<bool> FailTransformFromClaimableAsync(
        Guid              organisationId,
        Guid              orderId,
        string            error,
        CancellationToken ct)
    {
        var failedAt = DateTime.UtcNow;

        // Parameterised as `= ANY(@p)` rather than inlined, for the same reason the claim is: it
        // keeps the SQL text (and therefore the Postgres plan) stable whatever the set contains.
        var claimableStatuses = OrderStatusMachine.ClaimableForTransformFrom.ToArray();

        int failed;
        if (_db.Database.IsRelational())
        {
            failed = await _db.PurchaseOrders
                .Where(x => x.Id == orderId && x.OrgId == organisationId
                         && claimableStatuses.Contains(x.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status,    OrderStatusConstants.TransformFailed)
                    .SetProperty(o => o.UpdatedAt, failedAt), ct);
        }
        else
        {
            // EF InMemory cannot translate ExecuteUpdateAsync — emulate the same guarded transition
            // through the change tracker (tests are single-threaded there), mirroring the claim.
            var row = await _db.PurchaseOrders
                .Where(x => x.Id == orderId && x.OrgId == organisationId)
                .FirstOrDefaultAsync(ct);

            failed = row is not null
                  && OrderStatusMachine.ClaimableForTransformFrom.Contains(row.Status) ? 1 : 0;

            if (failed == 1)
            {
                row!.Status    = OrderStatusConstants.TransformFailed;
                row.UpdatedAt  = failedAt;
            }
        }

        if (failed == 0)
        {
            // Not ours to fail. Something else already moved the order — most often a transform that
            // SUCCEEDED (ready_to_deliver and beyond), which must never be overwritten with a
            // failure. Logged rather than silent, because "we could not record this" is itself worth
            // seeing.
            _logger.LogWarning(
                "Order {OrderId} (org {OrgId}) transform failed, but the order is no longer in a claimable "
              + "status — leaving it untouched and recording nothing. Error was: {Error}",
                orderId, organisationId, error);
            return false;
        }

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "TransformFailed", new
        {
            error,
            stage = "transform",
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogError(
            "Order {OrderId} (org {OrgId}) TRANSFORM FAILED: {Error}. The order is marked transform_failed "
          + "(visible in ops health + exceptions) and stays re-claimable, so a retry re-drives it.",
            orderId, organisationId, error);

        await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);
        return true;
    }
```

- [ ] **Step 4: Split `TransformAsync` into a wrapper and a verbatim core**

In the same file, replace the signature at `:93-98`:

```csharp
    public async Task<Result<TransformResponse>> TransformAsync(
        Guid organisationId,
        Guid orderId,
        OutputFormat format,
        CancellationToken ct)
    {
        // Load with tracking — we will mutate status twice
```

with this — the wrapper, then the core's signature. **The body below `TransformCoreAsync`'s opening brace is the EXISTING body, unchanged and un-reindented.** Do not touch anything from `// Load with tracking` down to the method's closing brace at `:754`.

```csharp
    /// <summary>
    /// The single server-side transform door. A thin guard around <see cref="TransformCoreAsync"/>,
    /// which holds the whole of the previous method body verbatim.
    ///
    /// <para><b>Why the guard exists.</b> The body had no exception handling at all between its
    /// first line and the acceptance gate's try, and none again between the generation handler and
    /// the end of the method — so the entity load, the cXML credential resolve, the override read,
    /// the atomic claim, the R2 upload and the final commit could each throw straight out of this
    /// method. <c>TransformOrderJob</c> turns that into a Hangfire retry and then a permanently
    /// failed job, leaving the order at <c>transforming</c>, where
    /// <c>StuckOrderDetectionService</c> recovers it to <c>ready</c> and deliberately never marks it
    /// failed. A real, repeatable error therefore produced no status, no message, no ops-health
    /// count and no exception row: it looked like nothing had happened.</para>
    ///
    /// <para><b>Why the catch is broad rather than per-operation.</b> The defect is regional, not
    /// per-call — the mapping-read helpers below are each already defended by their own catch-all
    /// (see <c>TryReadPinnedOutputConfig</c>, whose comment names this exact hazard), and it is the
    /// gaps BETWEEN those narrow fixes that stayed open. A per-operation catch protects only today's
    /// statements; the next line added to the method reopens the hole.</para>
    ///
    /// <para><b>Why turning a transient fault terminal is acceptable here.</b> Because of what
    /// <c>transform_failed</c> costs, which is very little: it is in
    /// <see cref="OrderStatusMachine.ClaimableForTransformFrom"/>, <c>TransformOrderJob</c> carries
    /// <c>AutomaticRetry(3, [10, 60, 300])</c>, and the endpoint's <c>TransformableFrom</c> admits
    /// it. So a DB blip becomes a VISIBLE transform_failed, a retry ten seconds later, a successful
    /// re-claim, and a completed transform. This is the same trade the acceptance gate's catch
    /// already makes and argues, twenty lines into the core.</para>
    ///
    /// <para>Cancellation is rethrown untouched: a cancelled request is not a failure.</para>
    /// </summary>
    public async Task<Result<TransformResponse>> TransformAsync(
        Guid organisationId,
        Guid orderId,
        OutputFormat format,
        CancellationToken ct)
    {
        try
        {
            return await TransformCoreAsync(organisationId, orderId, format, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Order {OrderId} (org {OrgId}): the transform failed unexpectedly. Recording it as transform_failed "
              + "rather than letting it unwind into Hangfire and strand the order in 'transforming'.",
                orderId, organisationId);

            // Discard whatever the failed attempt left on the change tracker before writing the
            // failure. A throw from the final SaveChanges leaves an Added OutboundArtifact and a
            // modified Status = ready_to_deliver pending; without this, the helper's own SaveChanges
            // would re-attempt that entire failed commit and write ready_to_deliver back over the
            // transform_failed we are about to record. A Postgres SaveChanges is all-or-nothing, so a
            // failed one committed nothing worth keeping.
            _db.ChangeTracker.Clear();

            // Plain language, and deliberately NOT ex.Message: this string becomes the order's
            // errorMessage (OrdersController reads the audit payload's `error` key), and
            // "Npgsql.PostgresException: 57P01" is not a sentence an operator can act on. The
            // exception itself is in the log line above.
            const string reason =
                "Something went wrong preparing this order to send, so it wasn't sent. "
              + "Try sending it again in a moment.";

            await FailTransformFromClaimableAsync(organisationId, orderId, reason, ct);
            return Result<TransformResponse>.Fail(reason);
        }
    }

    private async Task<Result<TransformResponse>> TransformCoreAsync(
        Guid organisationId,
        Guid orderId,
        OutputFormat format,
        CancellationToken ct)
    {
        // Load with tracking — we will mutate status twice
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet build ProcuLink.slnx --configuration Release`

Expected: build succeeds.

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~TransformStrandNeverSilentTests"`

Expected: PASS, 5 tests.

- [ ] **Step 6: Correct `StuckOrderDetectionService`'s stale premise**

No behaviour change — the sweep keeps recovering a `transforming` strand to `ready` and keeps never marking it failed. Only its stated reason is wrong, and it is the sentence a future reader would build on.

In `ProcuLink.Infrastructure/Services/StuckOrderDetectionService.cs`, replace the comment block at `:145-154` (from `// ── Requeue cap exceeded` through `// so a future genuine stall gets a fresh requeue budget.`) with:

```csharp
                // ── Requeue cap exceeded, but a 'transforming' strand is NOT a genuine
                //    failure → recover to 'ready', never terminal Failed ───────────────
                // The order is already fully resolved. A transform job that actually RAN and failed
                // records ITSELF as transform_failed — OrderTransformService's wrapper catches
                // anything that escapes the whole method and routes it through the same guarded
                // write the acceptance gate and the template/mapping failures use. So a strand this
                // sweep still sees means one of exactly two things, and 'ready' is right for both:
                //   • CLAIMED BUT NO JOB EVER RAN — the rare crash window between the controller's
                //     claim commit and its synchronous enqueue.
                //   • THE PROCESS DIED MID-TRANSFORM — OOM, eviction, a hard kill. No catch runs, so
                //     nothing could have been recorded. That is a transient infrastructure fault,
                //     not an order-level one, and retrying is the correct answer to it.
                // Neither must ever become a permanent false-failure. Recover to the healthy,
                // re-sendable 'ready' state (mirrors how a stuck DELIVERY dead-letters to the
                // RECOVERABLE delivery_dead_letter, never terminal Failed). RequeueCount is reset so
                // a future genuine stall gets a fresh requeue budget.
                //
                // NOTE: the premise here used to read "a transform job that actually RAN and failed
                // reverts ITSELF to 'ready'". That was stale twice over — such a job lands in
                // transform_failed, not ready, and the pre-fix code had two unguarded regions from
                // which a job that ran could strand here with nothing recorded at all.
```

- [ ] **Step 7: Mutation-check each guarantee**

Three mutations, one at a time. **Restore each by editing the file back — never `git checkout`.** Rebuild between mutations: reverting source does not revert `bin/`.

**7a — delete the catch.** In `TransformAsync`, replace the `catch (Exception ex) { … }` block body with `{ throw; }`.

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~TransformStrandNeverSilentTests"`
Expected: `APreClaimThrow_…` and `AFailedArtifactUpload_…` FAIL. Restore by editing.

**7b — drop the guard.** In `FailTransformFromClaimableAsync`, remove `&& claimableStatuses.Contains(x.Status)` from the relational `Where`, and change the InMemory `failed` assignment to `row is not null ? 1 : 0`.

Run the same filter.
Expected: `AThrowOnAnOrderThatIsNotClaimable_…` FAILS — the order is written to `transform_failed` and one audit row appears. Restore by editing.

**7c — drop the cancellation rethrow.** Delete the `catch (OperationCanceledException) { throw; }` clause.

Run the same filter.
Expected: `ACancelledTransform_…` FAILS — no exception is thrown, and the order is recorded as a failure. Restore by editing.

Rebuild and re-run after the final restore. Expected: PASS, 5 tests.

- [ ] **Step 8: Run the full suite**

Run: `dotnet build ProcuLink.slnx --configuration Release`
Run: `dotnet test ProcuLink.slnx --configuration Release`

Expected: PASS.

If `AcceptanceGateBlocksTransformTests` or `TransformIdempotencyPostgresTests` regress, the split in Step 4 changed the body. Diff `TransformCoreAsync` against `git show HEAD:ProcuLink.Api/Services/Orders/OrderTransformService.cs` and confirm the body is verbatim.

- [ ] **Step 9: Commit**

```bash
git add ProcuLink.Api/Services/Orders/OrderTransformService.cs \
        ProcuLink.Infrastructure/Services/StuckOrderDetectionService.cs \
        ProcuLink.Api.Tests/Services/TransformStrandNeverSilentTests.cs
git commit -m "fix: a transform that threw looked exactly like one that never ran

TransformAsync had no exception handling between its first line and the
acceptance gate's try, and none again between the generation handler and the end
of the method. The entity load, the cXML credential resolve, the override read,
the atomic claim, the R2 upload and the final commit could each throw straight
out of the method. TransformOrderJob turned that into a Hangfire retry and then a
permanently failed job, leaving the order at 'transforming' — where
StuckOrderDetectionService recovered it to 'ready' and deliberately never marked
it failed, because its premise was that a job which had actually run would have
recorded its own status.

So an order that hit a real, repeatable error was silently cycled back to
'ready': no transform_failed, no error message, no ops-health count, no exception
row. It looked like nothing had happened.

The body moves verbatim into TransformCoreAsync and TransformAsync becomes a
guard around it. The catch is broad on purpose: the mapping-read helpers are each
already defended by their own catch-all, and it was the gaps between those narrow
fixes that stayed open. Turning a transient fault terminal is cheap here —
transform_failed is claimable, the job retries three times, and the endpoint
admits it as the recovery door — which is the trade the acceptance gate's catch
already makes twenty lines further in.

The write is guarded on ClaimableForTransformFrom because the catch can fire
outside the claim, so an unguarded write could land on a billing_held park, an
MV-1 pending_review reset, or a ready_to_deliver transform that had already
succeeded. That guard is also what makes StuckOrderDetectionService correct
without changing it: ran-and-failed now lands in transform_failed, and what still
strands in 'transforming' is only a job that never ran or a process that died —
both transient, both right to recover to 'ready'. Its stale premise is corrected
in place.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 2: The two silent `Result.Fail` returns

`TransformOrderJob.cs:71` converts a `Result.Fail` into `throw new InvalidOperationException(...)`, so a `Fail` that writes no status produces the **identical** invisible strand Task 1 just closed for exceptions. Two of the three pre-claim `Fail` returns are genuine order-level failures and must go through the same guarded write.

`:108` — "Order not found" — is deliberately left alone: there is no row to mark.

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderTransformService.cs` — the unresolved-lines return (was `:111-114`), the missing-transformer return (was `:290-292`), and the stale comment above it (was `:285-289`). Line numbers shift by roughly +45 after Task 1's wrapper; find the code, not the line.
- Modify: `ProcuLink.Api.Tests/Services/TransformStrandNeverSilentTests.cs` — add two tests

**Interfaces:**
- Consumes: `FailTransformFromClaimableAsync(Guid organisationId, Guid orderId, string error, CancellationToken ct)` returning `Task<bool>`, from Task 1.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Append to `TransformStrandNeverSilentTests.cs`, inside the class:

```csharp
    // ── 6. A Fail return is as invisible as a throw ───────────────────────────

    /// <summary>
    /// TransformOrderJob turns a Fail into a throw (<c>TransformOrderJob.cs:71</c>), so a Fail that
    /// writes no status strands the order in exactly the same way an unhandled exception did.
    /// </summary>
    [Fact]
    public async Task NoRegisteredTransformer_isRecordedAsTransformFailed_notASilentFail()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db);
        var svc = Build(db, registerTransformers: false);

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);
        Assert.Contains("No transform service registered",
            events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task UnresolvedLines_areRecordedAsTransformFailed_notASilentFail()
    {
        await using var db = NewDb();
        var seed = await SeedAsync(db, resolved: false);
        var svc = Build(db);

        var result = await svc.TransformAsync(seed.OrgId, seed.OrderId, OutputFormat.CXml, CancellationToken.None);

        var events = await TransformFailedEventsAsync(db, seed.OrderId);
        Assert.Single(events);

        // The existing sentence is already written for a user and names the exact lines, so it is
        // passed through unaltered rather than replaced with the generic one.
        Assert.Equal("Resolve all lines before transforming. Unresolved: 1.",
            events[0].Payload.RootElement.GetProperty("error").GetString());

        Assert.Equal(OrderStatusConstants.TransformFailed, await StatusOfAsync(db, seed.OrderId));
        Assert.False(result.IsSuccess);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~TransformStrandNeverSilentTests"`

Expected: 7 tests, **2 FAIL** (the two new ones). Both fail on `Assert.Single(events)` with 0 events — the `Fail` returns write no audit row and no status. The order is still `transforming`, which is the defect.

- [ ] **Step 3: Route both returns through the guarded write**

In `TransformCoreAsync`, replace the unresolved-lines return:

```csharp
        // Pre-flight check: all lines must be resolved
        var unresolved = entity.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolved.Count > 0)
            return Result<TransformResponse>.Fail(
                $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolved)}.");
```

with:

```csharp
        // Pre-flight check: all lines must be resolved
        //
        // Recorded, not merely returned. TransformOrderJob turns a Fail into a throw, so a Fail that
        // writes no status leaves the order at 'transforming' exactly as an unhandled exception did —
        // and the controller has ALREADY flipped ready → transforming before enqueueing this job. The
        // sentence is already written for a user and names the exact lines, so it is passed through
        // unaltered. This does move such an order out of 'ready' and into the exceptions list, which
        // is the point: the strand it replaces is invisible, and transform_failed keeps the retry
        // door open on both ClaimableForTransformFrom and TransformableFrom.
        var unresolved = entity.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolved.Count > 0)
        {
            var unresolvedError =
                $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolved)}.";
            await FailTransformFromClaimableAsync(organisationId, orderId, unresolvedError, ct);
            return Result<TransformResponse>.Fail(unresolvedError);
        }
```

Then replace the transformer-lookup block — comment included, because the comment states the assumption this change overturns:

```csharp
        // Locate the fixed transformer (Xml/Csv/Json/...). Required EXCEPT for template mode and the
        // native CSV/JSON override path; resolved up-front so a missing transformer fails before status
        // mutation. NOTE: the revision-pinned and supplier-promoted paths deliberately do NOT relax
        // this requirement — the fixed transformer must exist so the defensive fallback below is
        // always possible.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(effectiveFormat));
        if (!useOutputNode && !useTemplate && !useNativeOverride && transformer is null)
            return Result<TransformResponse>.Fail($"No transform service registered for format '{effectiveFormat}'.");
```

with:

```csharp
        // Locate the fixed transformer (Xml/Csv/Json/...). Required EXCEPT for template mode and the
        // native CSV/JSON override path. NOTE: the revision-pinned and supplier-promoted paths
        // deliberately do NOT relax this requirement — the fixed transformer must exist so the
        // defensive fallback below is always possible.
        //
        // This comment used to end "resolved up-front so a missing transformer fails before status
        // mutation", which was the bug stated as a feature: failing before the status mutation is
        // precisely what made it invisible. A missing transformer is an unambiguously terminal,
        // order-level failure — no retry of the same inputs can cure it — so it is RECORDED as one.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(effectiveFormat));
        if (!useOutputNode && !useTemplate && !useNativeOverride && transformer is null)
        {
            var noTransformerError = $"No transform service registered for format '{effectiveFormat}'.";
            await FailTransformFromClaimableAsync(organisationId, orderId, noTransformerError, ct);
            return Result<TransformResponse>.Fail(noTransformerError);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build ProcuLink.slnx --configuration Release`
Run: `dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~TransformStrandNeverSilentTests"`

Expected: PASS, 7 tests.

- [ ] **Step 5: Mutation-check both reroutes**

**5a** — delete the `await FailTransformFromClaimableAsync(...)` line from the unresolved-lines block, leaving the bare `return Result<TransformResponse>.Fail(unresolvedError);`.

Run the filter. Expected: `UnresolvedLines_…` FAILS on `Assert.Single(events)` with 0 events. Restore by editing.

**5b** — do the same to the missing-transformer block.

Run the filter. Expected: `NoRegisteredTransformer_…` FAILS the same way. Restore by editing.

Rebuild and re-run. Expected: PASS, 7 tests.

- [ ] **Step 6: Run the full suite**

Run: `dotnet build ProcuLink.slnx --configuration Release`
Run: `dotnet test ProcuLink.slnx --configuration Release`

Expected: PASS.

Watch specifically for tests that assert an order stays `ready` after a transform attempt with unresolved lines — that behaviour is intentionally changed here. If one fails, update it to expect `transform_failed` and say so in the commit body; do not revert the behaviour.

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Api/Services/Orders/OrderTransformService.cs \
        ProcuLink.Api.Tests/Services/TransformStrandNeverSilentTests.cs
git commit -m "fix: two transform refusals returned Fail and recorded nothing

TransformOrderJob converts a Result.Fail into a throw, so a Fail that writes no
status produces the same invisible strand an unhandled exception did: Hangfire
retries, the job permanently fails, and the order sits at 'transforming' — which
the controller had already set before enqueueing — until StuckOrderDetection
cycles it back to 'ready' without ever marking it failed.

Unresolved lines and a missing registered transformer are both genuine
order-level failures, so both now go through the guarded write. Their existing
messages are already written for a user and name the exact lines and format, so
they pass through unaltered instead of being replaced with the generic sentence.

'Order not found' is deliberately left as a bare Fail: there is no row to mark.

The comment above the transformer lookup said the lookup was placed early 'so a
missing transformer fails before status mutation'. That was the defect stated as
a feature — failing before the status mutation is exactly what made it invisible.

An order with unresolved lines now leaves 'ready' for the exceptions list. That
is the intended trade: transform_failed is on both ClaimableForTransformFrom and
TransformableFrom, so the retry door stays open, and the strand it replaces was
invisible.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Task 3: Review and land

**Files:** none — verification only.

- [ ] **Step 1: Confirm no status constant was added**

Run: `git diff main --stat -- ProcuLink.Core/Constants/OrderStatusConstants.cs`

Expected: empty output. The frontend's `src/lib/orderStatusManifest.ts` mirror therefore needs no change and no frontend PR.

- [ ] **Step 2: Confirm the core body moved verbatim**

Run: `git diff main -- ProcuLink.Api/Services/Orders/OrderTransformService.cs`

Read it. Expected changes and nothing else: the new `TransformAsync` wrapper, the `TransformCoreAsync` signature line, the two `Result.Fail` reroutes with their comments, the corrected transformer-lookup comment, and the new `FailTransformFromClaimableAsync`. **Any other change inside the body is an accident from the Task 1 split — revert it by editing.**

- [ ] **Step 3: Run `/code-review`**

Required by `CLAUDE.md` at the end of every task group. Never skip.

- [ ] **Step 4: Push and open the PR against `main`**

```bash
git push -u origin claude/clever-pare-f27b55
```

Open the PR against `main` — never against another branch. A `pull_request` workflow filtered on `branches: [main]` gives a stacked PR zero CI, and retargeting later does not trigger a run.

- [ ] **Step 5: Verify CI actually ran**

```bash
gh pr checks --watch
```

Local green is not CI green (Windows dev, Linux CI). A brand-new PR can also sit with no run at all until the merge ref is computed — if `gh pr checks` reports nothing after a minute, poke it with `gh pr view --json mergeable` (the REST path computes it; GraphQL does not).

---

## Self-review

**Spec coverage.** Every section maps to a task:

| Spec section | Task |
|---|---|
| Component 1 — guarded failure write | 1, Step 3 |
| Component 2 — the `try` and the broad catch | 1, Step 4 (widened per Delta 1) |
| Component 3 — the two `Result.Fail` reroutes | 2, Step 3 |
| Component 4 — `StuckOrderDetectionService` comment | 1, Step 6 |
| Error message (plain sentence, exception to the log only) | 1, Step 4 — asserted by `Assert.DoesNotContain` in tests 1 and 4 |
| Tests 1-5 in the spec's table | 1 Step 1 (tests 1-3, 5) and 2 Step 1 (tests 4-5); the spec's "no transformer" and "unresolved lines" are Task 2's two |
| Mutation checks | 1 Step 7 (three), 2 Step 5 (two) |
| `:285-289` stale comment | 2, Step 3 |
| Repo constraints (EF only, org scope, idempotent, no new status) | Global Constraints + 3 Step 1 |

Two additions beyond the spec, both recorded above as deltas: the R2-upload test (test 4 in Task 1) and the negative control (test 5 in Task 1). The negative control is not in the spec's table but is required by the repo's own test convention — without it, "the guard records real failures" and "something now fails every transform" are indistinguishable.

**Placeholder scan.** No TBD, no "add error handling", no "similar to Task N". Every code step carries the literal code. Every mutation step names the exact edit and the exact test that must fail.

**Type consistency.** `FailTransformFromClaimableAsync(Guid, Guid, string, CancellationToken) → Task<bool>` is defined in Task 1 Step 3 and called with that exact shape in Task 1 Step 4 and twice in Task 2 Step 3. `TransformCoreAsync` has the same four parameters as `TransformAsync`. `ICxmlCredentialResolver.ResolveAsync(Guid, Guid, CancellationToken) → Task<CxmlCredentialConfig?>` matches `ProcuLink.Core/Services/CxmlCredentialConfig.cs:60`. The `OrderService` constructor call in the test matches `ProcuLink.Api/Services/OrderService.cs:28-49`, using the named `cxmlResolver:` argument exactly as `CxmlCredentialTransformTests.cs:70` does.
