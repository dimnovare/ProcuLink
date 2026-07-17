# Canonical Delivery-Claim Predicate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `DeliveryService`'s four hand-written claim predicates derive from one shared expression built on named status sets, and add the structural invariant that would have failed the build on the 52c6431 outage.

**Architecture:** Two named sets in `OrderStatusMachine` (mirroring the existing `RedeliverableFrom`) feed one `Expression<Func<PurchaseOrderEntity,bool>>` factory in `ProcuLink.Core`. The relational path passes it to `.Where(pred).ExecuteUpdateAsync(...)`; the EF-InMemory emulation evaluates `pred.Compile()(order)`. Drift becomes structurally impossible rather than merely tested. A `DeliveryOutcome` enum stops the job logging "success" for "did nothing" without changing any control flow.

**Tech Stack:** .NET 8, EF Core 8 (Npgsql 8.0.11), xUnit, FluentAssertions, Testcontainers (Postgres 16), Moq.

**Spec:** [`docs/superpowers/specs/2026-07-16-delivery-claim-canonical-predicate-design.md`](../specs/2026-07-16-delivery-claim-canonical-predicate-design.md) — read §1.1 before starting; it explains why the invariant test (Task 1), not the deduplication, is the deliverable that prevents the outage.

## Global Constraints

- **BLOCKED until PR #27 (`claude/wizardly-spence-6b738a`) merges to main.** `delivery_unconfirmed` does not exist on main. Do not start before it lands; see spec §3.
- **ALSO BLOCKED on `claude/priceless-pike-d2eb0a` (f078bff)** — it owns `DeliveryOutcome`. Task 5 consumes it and must not re-declare it.
- **Merge main FIRST.** `RedeliverableStatusInvariantPostgresTests` (56a82ba + 8684d17) is merged and green: per status in `RedeliverableFrom`, on real Postgres, it asserts the claim CLAIMS it and `HoldForBillingAsync` HOLDS it. It is the net under this refactor — if a repoint breaks claim semantics it tells you per status with dispatch evidence. Do not duplicate it; do not "fix" it if it goes red.
- Session `local_f5ee08ce` is **resolved, not colliding** — its work was test-only and is on main. It has no further edits planned in `DeliveryService.cs`.
- **Never assert dispatch via `result.Success`** — it is `true` for the silent-strand case. Evidence is the dispatcher call + the `DeliveryAttempt` row.
- **PRESERVE the Dispatch/Retry asymmetry.** `RetryDeliveryAsync` deliberately excludes `delivery_unconfirmed` — that asymmetry IS the park mechanism (a human may re-send a parked order; the automatic backoff queue may not). An audit over PR #27 confirmed the four other lists agree and this one differs on purpose. **Do not "fix" it into uniformity.** Task 1's `DispatchAndRetryClaimSets_DifferExactlyBy_DeliveryUnconfirmed` exists to make that deliberate.
- **Merge position: LAST.** Queue is PR #28 → funny-maxwell + priceless-pike → FE PR #19 → BE PR #27 → this. Both of this plan's blockers land ahead of it, so by execution time `delivery_unconfirmed` and `DeliveryOutcome` are both on main.
- **Run the full suite, never a narrow `--filter`** — a filtered run is not green (project rule).
- Work in an isolated git worktree (`superpowers:using-git-worktrees`). Never in the shared checkout.
- Every EF query org-scoped: `.Where(x => x.OrganisationId == organisationId)`. No exceptions.
- No raw SQL — EF Core only.
- **Line numbers in this plan are from the pre-merge tree and WILL shift.** Locate every site by content, not by line.
- Real Postgres is mandatory for Task 6. The InMemory provider does not exercise `ExecuteUpdateAsync` at all.
- `/code-review` before merge. Never skip (project rule).
- Delivery/billing/tenancy are high-care areas: extra review before merge.

---

## File Structure

| File | Responsibility |
|---|---|
| `ProcuLink.Core/Constants/OrderStatusMachine.cs` (modify) | Add the two canonical claimable sets beside `RedeliverableFrom`. |
| `ProcuLink.Core/Services/Delivery/DeliveryClaim.cs` (create) | The single claim predicate factory. No EF dependency — BCL expressions only. |
| `ProcuLink.Core/Services/Delivery/DeliveryResult.cs` (modify) | Add `DeliveryOutcome` + the derived `Outcome` init property. |
| `ProcuLink.Infrastructure/Services/DeliveryService.cs` (modify) | Repoint all four claim sites at the shared predicate. |
| `ProcuLink.Api/Jobs/DeliverOrderJob.cs` | **No change here.** The `NotAttempted` reschedule guard is owned by `claude/priceless-pike-d2eb0a` (f078bff), in both `DeliverOrderJob` and `RetryDeliveryJob`. Do not touch it. |
| `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs` (modify) | The subset invariants (pure, no DB). |
| `ProcuLink.Core.Tests` or `ProcuLink.Infrastructure.Tests/Services/DeliveryClaimTests.cs` (create) | Factory unit tests incl. the empty-set guard. |
| `ProcuLink.Api.Tests/Integration/DeliveryClaimEquivalencePostgresTests.cs` (create) | Relational-vs-compiled equivalence matrix on real Postgres. |
| `ProcuLink.Infrastructure.Tests/Services/DeliveryServiceIdempotencyTests.cs` (modify) | Age the misleading seed (spec §7). |

---

### Task 1: Canonical sets + the invariant that would have caught 52c6431

**Files:**
- Modify: `ProcuLink.Core/Constants/OrderStatusMachine.cs` (near `RedeliverableFrom`, ~L92)
- Test: `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs`

**Interfaces:**
- Produces: `OrderStatusMachine.ClaimableForDispatchFrom` and `OrderStatusMachine.ClaimableForRetryFrom`, both `IReadOnlySet<string>`. Tasks 2–6 consume these.

- [ ] **Step 1: Write the failing test**

Append to `OrderStatusMachineTests.cs`:

```csharp
    /// <summary>
    /// The 52c6431 regression, pinned. delivery_unconfirmed was added to RedeliverableFrom — so
    /// OrdersController.Redeliver began returning 202 for it — but not to the claim, so the claim
    /// matched 0 rows and DispatchArtifactAsync took its BENIGN no-op branch: the job logged SUCCESS
    /// having sent nothing and the order stayed parked while the operator was told it was sent.
    /// A code review caught it once. This assertion catches it every time.
    /// </summary>
    [Fact]
    public void RedeliverableFrom_IsSubsetOf_ClaimableForDispatchFrom()
        => OrderStatusMachine.RedeliverableFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForDispatchFrom,
            "every status OrdersController.Redeliver accepts (202 + enqueue DeliverOrderJob) must be a " +
            "status the dispatch claim can actually claim. A status in RedeliverableFrom but not in " +
            "ClaimableForDispatchFrom strands the order SILENTLY — 0 rows claimed reads as 'someone else " +
            "has it', so there is no error, no retry, and no exception to notice");

    /// <summary>
    /// The retry claim is deliberately the CONSERVATIVE set: it excludes delivery_unconfirmed so only a
    /// human "Send again" re-drives a parked order, never the automatic backoff queue (52c6431). This
    /// pins the direction of that asymmetry — retry may narrow the dispatch set, never widen it.
    /// </summary>
    [Fact]
    public void ClaimableForRetryFrom_IsSubsetOf_ClaimableForDispatchFrom()
        => OrderStatusMachine.ClaimableForRetryFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForDispatchFrom,
            "RetryDeliveryAsync's claim must never accept a status the dispatch claim rejects");

    /// <summary>
    /// The one-status delta is a PRODUCT decision (52c6431: only a human re-drives a parked order), not
    /// an accident. Assert it exactly, so widening the retry set is a deliberate edit to this test rather
    /// than a silent behaviour change.
    /// </summary>
    [Fact]
    public void DispatchAndRetryClaimSets_DifferExactlyBy_DeliveryUnconfirmed()
        => OrderStatusMachine.ClaimableForDispatchFrom
            .Except(OrderStatusMachine.ClaimableForRetryFrom)
            .Should().BeEquivalentTo(new[] { OrderStatusConstants.DeliveryUnconfirmed },
                "a parked (delivery_unconfirmed) order is re-driven ONLY by a human Send again — the " +
                "automatic backoff queue must never pick it up");

    /// <summary>
    /// The FOURTH list. HoldForBillingAsync sits downstream of DeliverOrderJob's billing gate, on the path
    /// "Send again" takes when the org has lapsed. Its drift is worse than the claim's: a refused status
    /// holds nothing, sends nothing, audits nothing, and never reaches delivery_held — so
    /// ReleaseBillingHeldOrdersAsync never rescues it on reactivation. Permanent strand, no self-heal.
    /// Hand-fixed once in 392b5a4; this is the assertion that stops it happening a third time.
    /// </summary>
    [Fact]
    public void RedeliverableFrom_IsSubsetOf_HoldableForBillingFrom()
        => OrderStatusMachine.RedeliverableFrom.Should().BeSubsetOf(
            OrderStatusMachine.HoldableForBillingFrom,
            "a lapsed org's Send again reaches the billing gate BEFORE the claim. A status the hold set " +
            "refuses is held nowhere, sent nowhere and audited nowhere, and never becomes delivery_held — " +
            "so the reactivation re-drive never finds it. That strand is permanent, unlike a lost claim");
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~OrderStatusMachineTests"
```

Expected: **compile error** — `ClaimableForDispatchFrom` / `ClaimableForRetryFrom` do not exist. That is a legitimate red.

- [ ] **Step 3: Add the sets**

In `OrderStatusMachine.cs`, directly beneath `RedeliverableFrom`:

```csharp
    /// <summary>
    /// The IDLE statuses DispatchArtifactAsync's atomic claim will claim. Consumed via
    /// <see cref="ProcuLink.Core.Services.Delivery.DeliveryClaim.Claimable"/> by BOTH the relational
    /// ExecuteUpdateAsync path and the InMemory emulation, so the two cannot disagree.
    ///
    /// <para>Idle statuses only. A STALE 'delivering' row is also claimable (crash recovery), but that is
    /// status PLUS time, not set membership, so the predicate composes it on — see DeliveryClaim.</para>
    ///
    /// <para><b>Invariant:</b> this must be a superset of <see cref="RedeliverableFrom"/>. A status the
    /// controller accepts for redeliver but the claim cannot claim strands the order silently (52c6431).
    /// Pinned by OrderStatusMachineTests.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForDispatchFrom =
        Set(ReadyToDeliver, DeliveryFailed, DeliveryUnconfirmed);

    /// <summary>
    /// RetryDeliveryAsync's automatic backoff claim. Deliberately EXCLUDES delivery_unconfirmed: a parked
    /// order is re-driven only by a human "Send again", never by the retry queue (52c6431). Otherwise
    /// identical to <see cref="ClaimableForDispatchFrom"/>; the delta is asserted exactly in tests so
    /// widening it is a deliberate act.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaimableForRetryFrom =
        Set(ReadyToDeliver, DeliveryFailed);

    /// <summary>
    /// HoldForBillingAsync's holdable set. This gate sits DOWNSTREAM of DeliverOrderJob's billing check, on
    /// the path "Send again" takes whenever the org has lapsed — so it must accept every status
    /// <see cref="RedeliverableFrom"/> admits.
    ///
    /// <para><b>Its drift is worse than the claim's.</b> A status this refuses holds nothing, sends nothing
    /// and audits nothing; because the order never reaches 'delivery_held', ReleaseBillingHeldOrdersAsync
    /// never re-drives it on reactivation. Permanent invisible strand with no self-heal, where a claim-drift
    /// at least leaves a row a sweep can find. Hand-fixed once already in 392b5a4 — hence this set.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> HoldableForBillingFrom =
        Set(ReadyToDeliver, DeliveryFailed, DeliveryUnconfirmed);
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~OrderStatusMachineTests"
```

Expected: PASS.

- [ ] **Step 5: Prove the invariant test is not vacuous (spec §4.5 "TDD honesty note")**

It is green on arrival because 52c6431 already hand-fixed the set. Prove it bites: temporarily remove `DeliveryUnconfirmed` from `ClaimableForDispatchFrom`, re-run, confirm `RedeliverableFrom_IsSubsetOf_ClaimableForDispatchFrom` **FAILS**, then restore it and confirm green again. Record the observed failure message in the commit body. Do not skip this — an invariant test that has never been seen red is a decoration.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Constants/OrderStatusMachine.cs ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs
git commit -m "test(delivery): pin the claim set against the redeliver set

52c6431 was found by a human reading a diff. This is the assertion that
finds it next time, before the operator does."
```

---

### Task 2: The shared predicate factory

**Files:**
- Create: `ProcuLink.Core/Services/Delivery/DeliveryClaim.cs`
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryClaimTests.cs`

**Interfaces:**
- Consumes: `OrderStatusMachine.ClaimableForDispatchFrom` / `ClaimableForRetryFrom` (Task 1).
- Produces: `DeliveryClaim.Claimable(Guid orgId, Guid orderId, IReadOnlySet<string> idleClaimable, DateTime staleBefore) -> Expression<Func<PurchaseOrderEntity, bool>>`. Tasks 3, 4, 6 consume it.

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Infrastructure.Tests/Services/DeliveryClaimTests.cs`:

```csharp
using System.Linq.Expressions;
using FluentAssertions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryClaimTests
{
    private static readonly Guid OrgId   = Guid.NewGuid();
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StaleBefore = Now.AddMinutes(-2);

    private static Func<PurchaseOrderEntity, bool> Compiled(IReadOnlySet<string> set) =>
        DeliveryClaim.Claimable(OrgId, OrderId, set, StaleBefore).Compile();

    private static PurchaseOrderEntity Order(string status, DateTime updatedAt) =>
        new() { Id = OrderId, OrgId = OrgId, Status = status, UpdatedAt = updatedAt };

    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver, true)]
    [InlineData(OrderStatusConstants.DeliveryFailed, true)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, true)]
    [InlineData(OrderStatusConstants.Delivered, false)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter, false)]
    [InlineData(OrderStatusConstants.DeliveryHeld, false)]
    public void Claimable_IdleStatuses_MatchTheDispatchSet(string status, bool expected)
        => Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(Order(status, Now))
            .Should().Be(expected);

    [Fact]
    public void Claimable_FreshDelivering_IsRejected()
        => Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(
                Order(OrderStatusConstants.Delivering, Now))
            .Should().BeFalse("a just-stamped 'delivering' row belongs to the worker that stamped it — " +
                              "claiming it would double-dispatch the same PO to a real supplier");

    [Fact]
    public void Claimable_StaleDelivering_IsClaimable()
        => Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(
                Order(OrderStatusConstants.Delivering, Now.AddMinutes(-30)))
            .Should().BeTrue("a 'delivering' row older than the reclaim window is a crashed worker's " +
                             "orphan and must be recoverable");

    [Fact]
    public void Claimable_WrongOrg_IsRejected()
    {
        var foreign = Order(OrderStatusConstants.ReadyToDeliver, Now);
        foreign.OrgId = Guid.NewGuid();
        Compiled(OrderStatusMachine.ClaimableForDispatchFrom)(foreign)
            .Should().BeFalse("org scoping lives INSIDE the predicate so the claim cannot be written un-scoped");
    }

    /// <summary>
    /// An empty set compiles to `= ANY('{}')` on Postgres, which matches nothing, so the claim would
    /// affect 0 rows and the caller would read that as "someone else claimed it" — silently stranding the
    /// order. That is the exact failure this whole file exists to prevent, so fail loud instead.
    /// </summary>
    [Fact]
    public void Claimable_EmptySet_ThrowsRatherThanSilentlyMatchingNothing()
    {
        var act = () => DeliveryClaim.Claimable(
            OrgId, OrderId, new HashSet<string>(StringComparer.Ordinal), StaleBefore);

        act.Should().Throw<ArgumentException>()
           .WithParameterName("idleClaimable");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryClaimTests"
```

Expected: **compile error** — `DeliveryClaim` does not exist.

- [ ] **Step 3: Write the factory**

Create `ProcuLink.Core/Services/Delivery/DeliveryClaim.cs`:

```csharp
using System.Linq.Expressions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// The ONE delivery-claim predicate.
///
/// <para>DeliveryService claims an order by flipping it to 'delivering' in a single guarded statement.
/// That claim is written twice per method — once as a relational ExecuteUpdateAsync predicate, once as an
/// EF-InMemory emulation — because the InMemory provider cannot translate ExecuteUpdateAsync. Both consume
/// this factory, so the two cannot disagree. They previously disagreed in exactly the way that matters: a
/// status added to one and not the other makes the claim match 0 rows, which the caller reads as a benign
/// "someone else has it" and reports as SUCCESS having sent nothing.</para>
/// </summary>
public static class DeliveryClaim
{
    /// <param name="idleClaimable">
    /// The idle statuses this claim accepts — <see cref="OrderStatusMachine.ClaimableForDispatchFrom"/> or
    /// <see cref="OrderStatusMachine.ClaimableForRetryFrom"/>. Never a literal.
    /// </param>
    /// <param name="staleBefore">
    /// A 'delivering' row older than this is a crashed worker's orphan and is reclaimable. A fresher one
    /// belongs to the worker that stamped it and must NOT be claimed.
    /// </param>
    public static Expression<Func<PurchaseOrderEntity, bool>> Claimable(
        Guid orgId,
        Guid orderId,
        IReadOnlySet<string> idleClaimable,
        DateTime staleBefore)
    {
        // An empty set yields `= ANY('{}')`, which matches nothing: the claim would affect 0 rows and the
        // caller would read that as "already claimed" and skip the dispatch — a silent strand, the exact
        // bug class this type exists to close. Benign-by-luck is not a contract; fail loud.
        if (idleClaimable.Count == 0)
            throw new ArgumentException(
                "A claim with no claimable statuses can never claim; it would silently match 0 rows.",
                nameof(idleClaimable));

        // Verified on Postgres 16 / EF 8.0.16 / Npgsql 8.0.11: this parameterises the set as `= ANY(@p)`,
        // keeping the claim SQL TEXT identical no matter which set a caller passes. Do NOT "simplify" this
        // to idleClaimable.Contains(x.Status): that also translates (the array hop is not needed for
        // translation), but it inlines the set's CONTENTS as SQL literals — `IN ('ready_to_deliver', …)` —
        // minting a distinct query-cache entry and Postgres plan per distinct set, on delivery's hottest
        // write path. The array is about plan stability, not translatability.
        var arr = idleClaimable.ToArray();

        return x => x.Id == orderId
                 && x.OrgId == orgId
                 && (arr.Contains(x.Status)
                  || (x.Status == OrderStatusConstants.Delivering && x.UpdatedAt < staleBefore));
    }
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryClaimTests"
```

Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/DeliveryClaim.cs ProcuLink.Infrastructure.Tests/Services/DeliveryClaimTests.cs
git commit -m "feat(delivery): one claim predicate for both providers

The InMemory emulation and the relational claim can no longer disagree,
because there is now only one of them. The empty-set guard closes a hole
this refactor would otherwise have opened: = ANY('{}') matches nothing,
which the caller reads as 'someone else has it'."
```

---

### Task 3: Repoint `DispatchArtifactAsync`

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliveryService.cs` — relational claim (~L212-215 pre-merge) and InMemory emulation (~L256-259 pre-merge). **Locate by content.**

**Interfaces:**
- Consumes: `DeliveryClaim.Claimable` (Task 2), `OrderStatusMachine.ClaimableForDispatchFrom` (Task 1).

- [ ] **Step 1: Replace the relational predicate**

Find the `ExecuteUpdateAsync` whose `.Where` reads `x.Status == OrderStatusConstants.ReadyToDeliver || … DeliveryUnconfirmed || (Delivering && x.UpdatedAt < staleBefore)`. Replace **only the `.Where(...)`**, leaving every `SetProperty` and the surrounding transaction untouched:

```csharp
                var claimed = await _db.PurchaseOrders
                    .Where(DeliveryClaim.Claimable(
                        orgId, orderId, OrderStatusMachine.ClaimableForDispatchFrom, staleBefore))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, OrderStatusConstants.Delivering)
                        .SetProperty(o => o.DeliveryDueAt, dueAt)
                        .SetProperty(o => o.SlaBreached, false)
                        .SetProperty(o => o.UpdatedAt, dispatchStart), ct);
```

- [ ] **Step 2: Replace the InMemory emulation**

Replace the `if (order.Status is not (… or … or …))` block's condition — **keep the log line and the return exactly as they are**:

```csharp
                // Same predicate as the relational claim above, evaluated in memory because the InMemory
                // provider cannot translate ExecuteUpdateAsync. Deriving it means this path now enforces
                // the staleness gate too: it previously accepted ANY 'delivering' row, i.e. it was more
                // permissive than production.
                if (!DeliveryClaim.Claimable(
                        orgId, orderId, OrderStatusMachine.ClaimableForDispatchFrom, staleBefore)
                    .Compile()(order))
                {
                    _logger.LogInformation(
                        "Delivery {OrderId}: not claimed (status '{Status}' not claimable) — skipping dispatch.",
                        orderId, order.Status);
                    return new DeliveryResult(true, null);
                }
```

> The InMemory branch previously computed no `staleBefore`. Confirm `staleBefore` is in scope for both branches (it is declared above the `IsRelational()` fork as `var staleBefore = dispatchStart - DeliveringReclaimWindow;`). If it is inside the relational branch after the merge, hoist it.

- [ ] **Step 3: Run the delivery suites**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~Delivery"
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~Deliver"
```

Expected: PASS. Per spec §7, this change breaks **zero** tests — every InMemory dispatch test seeds `ready_to_deliver` or `delivery_failed`, claimable regardless of `UpdatedAt`. **If something else fails, stop and investigate; do not adjust the predicate to make it green.**

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryService.cs
git commit -m "refactor(delivery): dispatch claim derives from the canonical predicate

The InMemory path also stops being more permissive than production: it
now enforces the same staleness gate the relational claim always had."
```

---

### Task 4: Repoint `RetryDeliveryAsync` + fix the misleading test

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliveryService.cs` — retry relational claim (~L870-874 pre-merge) and retry InMemory branch (~L907-914 pre-merge, which today has **no status gate at all**).
- Modify: `ProcuLink.Infrastructure.Tests/Services/DeliveryServiceIdempotencyTests.cs` (~L74 + its seed helper ~L136-156).

**Interfaces:**
- Consumes: `DeliveryClaim.Claimable` (Task 2), `OrderStatusMachine.ClaimableForRetryFrom` (Task 1).

- [ ] **Step 1: Replace the retry relational predicate**

```csharp
            var claimed = await _db.PurchaseOrders
                .Where(DeliveryClaim.Claimable(
                    orgId, orderId, OrderStatusMachine.ClaimableForRetryFrom, staleBefore))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Delivering)
                    .SetProperty(o => o.UpdatedAt, claimedAt), ct);
```

- [ ] **Step 2: Add the missing InMemory gate**

The InMemory retry branch currently flips unconditionally. Gate it with the same predicate, and **return `RetryDeliveryAsync`'s own contract — `false`, not `true`**:

```csharp
            // Same predicate as the relational claim. This path previously flipped UNCONDITIONALLY,
            // so InMemory could never reproduce a lost claim.
            if (!DeliveryClaim.Claimable(
                    orgId, orderId, OrderStatusMachine.ClaimableForRetryFrom, staleBefore)
                .Compile()(order))
                return new DeliveryResult(false, "Delivery for this order is already in progress.");

            order.Status    = OrderStatusConstants.Delivering;
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            priorAttempts = await _db.DeliveryAttempts
                .CountAsync(a => a.OrderId == orderId && a.OrgId == orgId
                              && a.Status != DeliveryAttempt.StatusDispatching, ct);
```

> **Sharing the predicate must NOT share the result.** `DispatchArtifactAsync` returns `DeliveryResult(true, null)` on a lost claim; `RetryDeliveryAsync` returns `DeliveryResult(false, "…already in progress.")`. `RetryDeliveryJob` schedules backoff on `!Success`, so collapsing these changes retry behaviour. Keep each path's own return.

- [ ] **Step 3: Run — expect exactly ONE failure**

```bash
dotnet test ProcuLink.Infrastructure.Tests
```

Expected: `CrashAfterSendBeforeCommit_ReDrive_ReAdoptsInFlightRow_SameKey_NoSecondDelivery` **FAILS**. This is correct and expected (spec §7).

> **The "exactly one test breaks" figure is PRE-#27 and will be wrong when you run this.** It was measured on the tree before PR #27. The audit session reports **#27 adds ~5 more park tests carrying the same `UpdatedAt = now` trap** — a fresh `delivering` row that the relational claim rejects and only InMemory accepts. Expect several failures, not one. **Re-run the fallout analysis after merging main**, and triage each failure with the §7 question: *does this test seed a state production can actually reach?* If the seed is a fresh `delivering`, the test is asserting InMemory-only behaviour and the fix is to age the seed. Do not relax the predicate to make any of them green.
>
> Run the **full suite**, not a narrow `--filter` — a filtered run is not green (project rule; it has already missed an already-red test here).

- [ ] **Step 4: Fix the test by making it honest**

The test seeds `Status = Delivering, UpdatedAt = now` — a **fresh** `delivering` — and claims to "Reproduce the EXACT post-crash state". It passes today only because the InMemory path flips unconditionally. **Production has never behaved this way:** the relational claim rejects a fresh `delivering`. Its own Postgres twin (`DeliveryCrashRecoveryPostgresTests.cs:169-171`) seeds the same scenario aged `-30 minutes`, commented "aged well past the 2-minute reclaim window". A real crashed order is stale by definition — `StuckDeliveryDetectionService` only re-drives rows stuck ~45 min.

In the seed helper, change the `delivering` row's `UpdatedAt`:

```csharp
            // A crashed worker's orphan is STALE by definition — StuckDeliveryDetectionService only
            // re-drives rows stuck for ~45 min, and the claim's reclaim window is 2 min. Seeding
            // UtcNow described a state production never re-drives; matches the Postgres twin in
            // DeliveryCrashRecoveryPostgresTests, which ages the identical scenario by 30 minutes.
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
```

**Do NOT relax the predicate to keep the old seed green** — that preserves the fiction that this scenario works.

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryServiceIdempotencyTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryService.cs ProcuLink.Infrastructure.Tests/Services/DeliveryServiceIdempotencyTests.cs
git commit -m "refactor(delivery): retry claim derives from the canonical predicate

The InMemory retry path had no status gate at all, so it could not
reproduce a lost claim. Adding one exposed a crash-recovery test that
seeded a FRESH 'delivering' row and asserted a re-adopt production has
never performed — it passed only because InMemory was more permissive
than Postgres. Aged the seed to match its own Postgres twin."
```

---

### Task 5: Mark the non-dispatch returns `NotAttempted` + repoint the hold set

> **Scope changed 2026-07-17 — read this before writing any enum.** This task originally declared its own
> `DeliveryOutcome { Dispatched, NotClaimed, SkippedAutoDeliverOff, Failed }`. That design is **withdrawn**
> (spec §4.4). `claude/priceless-pike-d2eb0a` (f078bff) already owns `DeliveryOutcome`, its design is better,
> and two enums cannot coexist. **Consume theirs. Do NOT re-declare it, do NOT add members to it.** Adding
> members would break a guard that branches on `== NotAttempted`.

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliveryService.cs` — the two claim-lost returns, the auto-deliver-off return, and `HoldForBillingAsync`'s holdable gate (~L966)
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryClaimOutcomeTests.cs` (create)

**Interfaces:**
- Consumes (already defined on `claude/priceless-pike-d2eb0a`, must be on main first):
  `enum DeliveryOutcome { Dispatched = 0, NotAttempted = 1 }` and
  `record DeliveryResult(bool Success, string? ErrorMessage, int? ResponseCode = null, string? ResponseBody = null, DeliveryOutcome Outcome = DeliveryOutcome.Dispatched)`.
- Consumes: `OrderStatusMachine.HoldableForBillingFrom` (Task 1).

- [ ] **Step 1: Guard — confirm the enum is on main before writing anything**

```bash
git show origin/main:ProcuLink.Core/Services/Delivery/DeliveryResult.cs | grep -n "NotAttempted"
```

Expected: a match. If it returns nothing, `claude/priceless-pike-d2eb0a` has not merged — **STOP** (Global Constraints). Do not declare the enum yourself to unblock; that is the collision this task exists to avoid.

- [ ] **Step 2: Write the failing test**

Create `ProcuLink.Infrastructure.Tests/Services/DeliveryClaimOutcomeTests.cs`:

```csharp
using FluentAssertions;
using ProcuLink.Core.Services.Delivery;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DeliveryClaimOutcomeTests
{
    [Fact]
    public void LostClaim_IsSuccessful_ButMarkedNotAttempted()
    {
        var r = new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotAttempted);

        r.Success.Should().BeTrue(
            "the benign concurrent-activation case must NOT schedule a retry — another worker owns the send");
        r.Outcome.Should().Be(DeliveryOutcome.NotAttempted,
            "'we sent nothing because another worker owns it' and 'we delivered the PO' were the same value " +
            "before this, which is how a stranded order got logged as a success");
    }

    [Fact]
    public void Outcome_DefaultsToDispatched_SoAForgottenCallSiteDegradesToNoiseNotAbandonment()
        => new DeliveryResult(true, null).Outcome.Should().Be(DeliveryOutcome.Dispatched);
}
```

- [ ] **Step 3: Run to verify it fails**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryClaimOutcomeTests"
```

Expected: FAIL — the returns are not yet marked. (If it errors that `Outcome` is unknown, Step 1's guard was skipped.)

- [ ] **Step 4: Mark the three non-dispatch returns in `DeliveryService.cs`**

Each of these dispatched nothing and wrote no `DeliveryAttempt` row, so the retry queue must not pick them up:

```csharp
// auto-deliver off (requireAutoDeliver && !config.AutoDeliver)
return new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotAttempted);

// relational claim: claimed == 0
return new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotAttempted);

// InMemory claim: predicate rejected
return new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotAttempted);
```

Leave `Success` alone. Rescheduling a `NotAttempted` is an unbounded ~30-min loop, not a retry: with no attempt row `CountDeliveryAttemptsAsync` is frozen, `attemptsMade >= maxAttempts` never trips, and `BackoffFor` returns the same delay forever.

- [ ] **Step 5: Repoint `HoldForBillingAsync` to the canonical set**

Replace the hand-written holdable literal (~L966) — it currently reads `order.Status is not (OrderStatusConstants.ReadyToDeliver or OrderStatusConstants.DeliveryFailed)`:

```csharp
        // The FOURTH list (spec §4.1). Derives from the named set so it cannot drift away from
        // RedeliverableFrom again: a status this refuses is held nowhere, sent nowhere and audited
        // nowhere, and never reaches delivery_held — so ReleaseBillingHeldOrdersAsync never rescues it
        // on reactivation. That strand is permanent, unlike a lost claim. Hand-fixed once in 392b5a4.
        if (order is null || !OrderStatusMachine.HoldableForBillingFrom.Contains(order.Status))
            return false;
```

- [ ] **Step 6: Run to verify it passes**

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~DeliveryClaimOutcomeTests"
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~RedeliverableStatusInvariant"
```

Expected: PASS for both. The second is the merged behavioural net (Global Constraints) — it asserts `HoldForBillingAsync` HOLDS every `RedeliverableFrom` status on real Postgres, so it covers this repoint directly. If it goes red, the repoint changed hold semantics; fix the repoint, never the test.

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryService.cs ProcuLink.Infrastructure.Tests/Services/DeliveryClaimOutcomeTests.cs
git commit -m "feat(delivery): mark non-dispatch returns NotAttempted; hold set derives

Returning bare success for 'we did nothing' is what turned a status-list
gap into a stranded order with a green log. Control flow is unchanged --
the benign lost race still must not schedule a retry -- but the result now
says which of the two happened, and the retry queue can tell.

The hold set was the fourth hand-synced list and the worst of them: a
status it refuses never reaches delivery_held, so the reactivation
re-drive never finds it. Permanent strand, no self-heal. It now derives."
```
---

### Task 6: Sibling invariants + the Postgres equivalence matrix

**Files:**
- Modify: `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs`
- Create: `ProcuLink.Api.Tests/Integration/DeliveryClaimEquivalencePostgresTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–2.

- [ ] **Step 1: Add the sibling invariants (pure, no DB)**

Per spec §5, the other four enqueue sites all reduce to `ready_to_deliver`, except Ops — whose **guard set is not a subset** and which is saved only by a normalizing write. State that invariant over the *normalized target*:

```csharp
    /// <summary>
    /// TransformOrderJob enqueues delivery straight after a successful transform, and
    /// StrandedReadyOrderDetectionService re-drives orders it finds parked there. Both rely on
    /// ready_to_deliver being claimable.
    /// </summary>
    [Fact]
    public void ReadyToDeliver_IsClaimableForDispatch()
        => OrderStatusMachine.ClaimableForDispatchFrom.Should().Contain(OrderStatusConstants.ReadyToDeliver,
            "TransformOrderJob and StrandedReadyOrderDetectionService both enqueue DeliverOrderJob for a " +
            "ready_to_deliver order; if the claim rejected it, every fresh transform would strand");

    /// <summary>
    /// OpsController's requeue guard accepts delivery_dead_letter, which is NOT claimable. It is safe only
    /// because OpsController normalizes the row to delivery_failed and commits BEFORE enqueuing. The
    /// invariant therefore holds over the NORMALIZED TARGET, not over the guard set — assert the thing that
    /// is actually true, so this test does not quietly become a lie if the normalizing write moves.
    /// </summary>
    [Fact]
    public void OpsRequeue_NormalizedTarget_IsClaimableForDispatch()
        => OrderStatusMachine.ClaimableForDispatchFrom.Should().Contain(OrderStatusConstants.DeliveryFailed,
            "OpsController.Requeue rewrites the order to delivery_failed before enqueuing DeliverOrderJob");
```

- [ ] **Step 2: Write the Postgres equivalence matrix**

Create `ProcuLink.Api.Tests/Integration/DeliveryClaimEquivalencePostgresTests.cs`. Follow the fixture pattern in `DeliveryConcurrencyPostgresTests.cs` (`[Collection]` + `PostgresContainerCollection`).

```csharp
/// <summary>
/// The shared predicate guarantees the two code paths are the same EXPRESSION. It does NOT guarantee that
/// Npgsql's TRANSLATION of that expression agrees with C#'s EVALUATION of it — null handling, collation and
/// `= ANY` semantics all live in that gap, and the InMemory provider never runs ExecuteUpdateAsync at all.
/// This matrix pins the gap for EVERY known status, so a newly added status is covered automatically.
/// </summary>
[Collection(PostgresContainerCollection.Name)]
public class DeliveryClaimEquivalencePostgresTests
{
    public static IEnumerable<object[]> StatusMatrix() =>
        from status in OrderStatusMachine.AllStatuses
        from stale in new[] { false, true }
        select new object[] { status, stale };

    [Theory]
    [MemberData(nameof(StatusMatrix))]
    public async Task RelationalClaim_AgreesWith_CompiledPredicate(string status, bool stale)
    {
        var (orgId, orderId) = await SeedOrderAsync(status, stale);

        var now         = DateTime.UtcNow;
        var staleBefore = now - TimeSpan.FromMinutes(2);
        var pred = DeliveryClaim.Claimable(
            orgId, orderId, OrderStatusMachine.ClaimableForDispatchFrom, staleBefore);

        var inMemoryVerdict = pred.Compile()(await LoadAsync(orgId, orderId));

        var rowsClaimed = await Db.PurchaseOrders
            .Where(pred)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatusConstants.Delivering)
                .SetProperty(o => o.UpdatedAt, now));

        (rowsClaimed == 1).Should().Be(inMemoryVerdict,
            $"status '{status}' (stale={stale}): Postgres and the compiled predicate must reach the same " +
            "verdict, or the InMemory tests are asserting behaviour production does not have");
    }
}
```

> Seed a **stale** row as `UpdatedAt = UtcNow.AddMinutes(-30)` and a **fresh** row as `UpdatedAt = UtcNow`. Reuse the seeding helpers already present in `DeliveryConcurrencyPostgresTests.cs` rather than writing new ones.

- [ ] **Step 3: Run against real Postgres**

```bash
docker ps                     # confirm the daemon is up first
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~DeliveryClaimEquivalencePostgresTests"
```

Expected: PASS for every status × {fresh, stale}. If Docker is unavailable, **stop** — do not report this task done. The InMemory provider cannot substitute here.

- [ ] **Step 4: Full suite + review**

```bash
dotnet build --no-incremental
dotnet test
```

Then run `/code-review`. Delivery is a high-care area (spec Global Constraints).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs ProcuLink.Api.Tests/Integration/DeliveryClaimEquivalencePostgresTests.cs
git commit -m "test(delivery): pin claim translation against C# evaluation on Postgres

One shared expression makes the two paths identical by construction, but
not identically INTERPRETED: Npgsql translates, C# evaluates, and the gap
between them is where the InMemory suite has been lying. Matrix covers
every status in the machine, so a new one is covered on arrival."
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §4.1 canonical sets (incl. `HoldableForBillingFrom`, the fourth list) | Task 1; hold-set repoint in Task 5 Step 5 |
| §4.2 predicate factory + empty-set guard | Task 2 |
| §4.3 consumption, both paths, separate return contracts | Tasks 3, 4 |
| §4.4 `Outcome` | **Superseded** — enum owned by `claude/priceless-pike-d2eb0a`. Task 5 only marks the three non-dispatch returns `NotAttempted`. |
| §4.5 item 1 (subset invariant) + red-phase ritual | Task 1 (Steps 1, 5) |
| §4.5 item 2 (sibling invariants) | Task 6 Step 1 |
| §4.5 item 3 (Postgres matrix) | Task 6 Step 2 |
| §5 Ops normalized-target subtlety | Task 6 Step 1 |
| §7 the one misleading test | Task 4 Steps 3-4 |
| §8 `OrdersController.Retry` | Out of scope — spawned as its own session (`local_9082ac44`) |

**Placeholder scan:** none — every code step carries the actual code, every command its expected output.

**Type consistency:** `DeliveryClaim.Claimable(Guid, Guid, IReadOnlySet<string>, DateTime)` is defined in Task 2 and used with that exact signature in Tasks 3, 4, 6. `ClaimableForDispatchFrom` / `ClaimableForRetryFrom` / `HoldableForBillingFrom` are defined in Task 1 and referenced under those exact names in Tasks 5 and 6. `DeliveryOutcome` is **not defined by this plan** — Task 5 consumes `{ Dispatched = 0, NotAttempted = 1 }` from `claude/priceless-pike-d2eb0a` and its Step 1 guards that it is present before writing code.

**Known ordering risk:** Task 1's tests reference `OrderStatusConstants.DeliveryUnconfirmed`, which exists only once PR #27 merges, and Task 5 consumes a `DeliveryOutcome` that exists only once `claude/priceless-pike-d2eb0a` merges (Global Constraints). Both are hard blocks with explicit guards; nothing here works around either. Merge main first so the merged behavioural net (`RedeliverableStatusInvariantPostgresTests`) runs underneath the whole plan.

**Sequencing note if only PR #27 lands:** Tasks 1–4 and 6 are executable without `claude/priceless-pike-d2eb0a`; only Task 5 depends on it. If the Outcome branch stalls, ship 1–4 + 6 and leave Task 5 for a follow-up — the claim dedup and every invariant stand on their own. Do **not** unblock Task 5 by declaring the enum locally.
