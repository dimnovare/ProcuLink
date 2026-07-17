# Canonical Delivery-Claim Predicate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `DeliveryService`'s four hand-written claim predicates derive from one shared expression built on named status sets, and add the structural invariant that would have failed the build on the 52c6431 outage.

**Architecture:** Named status sets in `OrderStatusMachine` (mirroring the existing `RedeliverableFrom`) feed one `Expression<Func<PurchaseOrderEntity,bool>>` factory in `ProcuLink.Core`. The relational path passes it to `.Where(pred).ExecuteUpdateAsync(...)`; the EF-InMemory emulation evaluates `pred.Compile()(order)`. Drift becomes structurally impossible rather than merely tested. The five hand-written status gates — two claims, the retry gate, the hold set, and the Retry admission guard — all derive from those sets. `DeliveryOutcome` is **not** part of this plan; PR #30 owns it.

**Tech Stack:** .NET 8, EF Core 8 (Npgsql 8.0.11), xUnit, FluentAssertions, Testcontainers (Postgres 16), Moq.

**Spec:** [`docs/superpowers/specs/2026-07-16-delivery-claim-canonical-predicate-design.md`](../specs/2026-07-16-delivery-claim-canonical-predicate-design.md) — read §1.1 before starting; it explains why the invariant test (Task 1), not the deduplication, is the deliverable that prevents the outage.

## Global Constraints

- **BLOCKED until PR #27 (`claude/wizardly-spence-6b738a`) merges to main — the ONLY remaining blocker,** and the last open PR in the repo (as of 2026-07-17 ~12:30). `delivery_unconfirmed` does not exist on main. Do not start before it lands; see spec §3.
- **PR #30 is MERGED** (2026-07-17 08:27) — `DeliveryOutcome { Dispatched, ClaimLost, NotRetryable }` and all three return mappings are on main, verified by content. Do not re-declare, extend, or re-map. Task 5 Step 1 verifies only.
- **To check whether a PR landed, do NOT use SHA ancestry — this repo squash-merges.** `git merge-base --is-ancestor <sha> origin/main` returns NO for merged PRs. Use `gh pr list --state merged`, or grep `origin/main` for the content. This plan's author got this wrong on #30.
- **NEVER mark a claim-lost return `NotRetryable`.** `StuckDeliveryDetectionService` stamps `UpdatedAt = now` before enqueuing the retry, so the re-driven retry meets a fresh `delivering` row, fails the staleness gate and bounces — only the rescheduled ~30-min backoff ages it enough to claim. The reschedule IS crash recovery (spec §4.4a). Marking that path never-reschedule turns "delivered 30 min late" into "never delivered". `CrashedHolderRecoveryCompositionPostgresTests` fails if you do.
- **Merge main FIRST.** `RedeliverableStatusInvariantPostgresTests` (56a82ba + 8684d17) is merged and green: per status in `RedeliverableFrom`, on real Postgres, it asserts the claim CLAIMS it and `HoldForBillingAsync` HOLDS it. It is the net under this refactor — if a repoint breaks claim semantics it tells you per status with dispatch evidence. Do not duplicate it; do not "fix" it if it goes red.
- Session `local_f5ee08ce` is **resolved, not colliding** — its work was test-only and is on main. It has no further edits planned in `DeliveryService.cs`.
- **Never assert dispatch via `result.Success`** — it is `true` for the silent-strand case. Evidence is the dispatcher call + the `DeliveryAttempt` row.
- **PRESERVE the Dispatch/Retry asymmetry.** `RetryDeliveryAsync` deliberately excludes `delivery_unconfirmed` — that asymmetry IS the park mechanism (a human may re-send a parked order; the automatic backoff queue may not). An audit over PR #27 confirmed the four other lists agree and this one differs on purpose. **Do not "fix" it into uniformity.** Task 1's `DispatchAndRetryClaimSets_DifferExactlyBy_DeliveryUnconfirmed` exists to make that deliberate.
- **Merge position: LAST.** #28–#35 have all landed; only PR #27 remains ahead of this. `DeliveryOutcome` is already on main; `delivery_unconfirmed` arrives with #27.
- **Line numbers here were measured against `origin/main` @ 560c902 (2026-07-17).** Main moved eight times the day this was written. Locate every site by content and re-verify before trusting any claim below about what the tree contains.
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
| `ProcuLink.Core/Services/Delivery/DeliveryResult.cs` | **No change here.** `DeliveryOutcome { Dispatched, ClaimLost, NotRetryable }` is owned by PR #30 (03a24c2). Do not re-declare or extend it. |
| `ProcuLink.Infrastructure/Services/DeliveryService.cs` (modify) | Repoint all four claim sites at the shared predicate; repoint the hold gate. |
| `ProcuLink.Api/Controllers/OrdersController.cs` (modify) | Repoint the Retry admission guard (the fifth list) + derive its 400 message. |
| `ProcuLink.Api/Jobs/DeliverOrderJob.cs` | **No change here.** The reschedule guard is owned by PR #30, in both `DeliverOrderJob` and `RetryDeliveryJob`. Do not touch it. |
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

    /// <summary>
    /// The FIFTH list. OrdersController.Retry admits only delivery_failed and mints a 400 for anything else;
    /// the retry claim must be able to claim whatever it admits, or the 202 is a lie in the 52c6431 shape.
    /// Passes today — this is drift prevention, not a fix.
    /// </summary>
    [Fact]
    public void RetryableFrom_IsSubsetOf_ClaimableForRetryFrom()
        => OrderStatusMachine.RetryableFrom.Should().BeSubsetOf(
            OrderStatusMachine.ClaimableForRetryFrom,
            "OrdersController.Retry returns 202 for every status in RetryableFrom and enqueues " +
            "RetryDeliveryJob; a status it admits but the retry claim rejects is the 52c6431 shape again");
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

    /// <summary>
    /// OrdersController.Retry's admission guard — <see cref="RedeliverableFrom"/>'s twin for the retry leg,
    /// and the one place a user-facing 400 is minted from a status name.
    ///
    /// <para>Correct today ({delivery_failed} is a subset of <see cref="ClaimableForRetryFrom"/>), so naming
    /// it is drift PREVENTION, not a bug fix. It is the exact shape RedeliverableFrom had before it was
    /// named — and naming that one is what made 52c6431 findable.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> RetryableFrom =
        Set(DeliveryFailed);
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

### Task 5: Repoint the hold and retry gates (Outcome is NOT ours — verify only)

> **REWRITTEN 2026-07-17. Do not resurrect the previous version.** This task used to mark three returns
> `NotAttempted`. That member **no longer exists**, and the mapping it prescribed would have **re-broken crash
> recovery**: it sent the claim-lost returns down the never-reschedule path, and the reschedule is the only
> thing that recovers a crashed holder (spec §4.4a). PR #30 already did all of this correctly. What remains
> here is the two gate repoints plus verification.

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliveryService.cs` — `HoldForBillingAsync`'s holdable gate (~L966)
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs:1847` — the Retry admission guard (the fifth list)
- Check: `ProcuLink.Api.Tests/Controllers/OrdersControllerErrorMessageTests.cs` — may pin the old 400 wording

**Interfaces:**
- Consumes: `OrderStatusMachine.HoldableForBillingFrom` and `OrderStatusMachine.RetryableFrom` (Task 1).
- Consumes (owned by PR #30, do NOT re-declare or extend):
  `enum DeliveryOutcome { Dispatched = 0, ClaimLost = 1, NotRetryable = 2 }`.

- [ ] **Step 1: Verify PR #30's mapping survived the merge — do not re-do it**

```bash
git show origin/main:ProcuLink.Core/Services/Delivery/DeliveryResult.cs | grep -n "ClaimLost\|NotRetryable"
git show origin/main:ProcuLink.Infrastructure/Services/DeliveryService.cs | grep -n "Outcome: DeliveryOutcome"
```

Expected: the ternary enum, and the claim-lost returns marked `ClaimLost` (**not** `NotRetryable`) with `Success` still `true`.

**If either claim-lost return says `NotRetryable`, STOP — that is the crash-recovery regression.** The stuck sweep stamps `UpdatedAt = now` before enqueuing the retry, so the re-driven retry meets a fresh `delivering` row, fails the staleness gate and bounces; only the rescheduled ~30-min backoff ages the row enough to claim. Marking that path never-reschedule turns "delivered 30 min late" into "never delivered, dead-lettered after `MaxRequeues=2`". Evidence: `CrashedHolderRecoveryCompositionPostgresTests`.

- [ ] **Step 2: Repoint `HoldForBillingAsync` to the canonical set**

Replace the hand-written holdable literal (~L966) — currently `order.Status is not (OrderStatusConstants.ReadyToDeliver or OrderStatusConstants.DeliveryFailed)`:

```csharp
        // The FOURTH list (spec §4.1). Derives from the named set so it cannot drift away from
        // RedeliverableFrom again: a status this refuses is held nowhere, sent nowhere and audited
        // nowhere, and never reaches delivery_held — so ReleaseBillingHeldOrdersAsync never rescues it
        // on reactivation. That strand is permanent, unlike a lost claim. Hand-fixed once in 392b5a4.
        if (order is null || !OrderStatusMachine.HoldableForBillingFrom.Contains(order.Status))
            return false;
```

- [ ] **Step 3: Repoint the FIFTH list — `OrdersController.Retry`'s admission guard**

`OrdersController.cs:1847` gates the retry 400-vs-202 on a bare literal and hardcodes the status name in the user-facing prose. PR #29 fixes that action's pre-flip but does **not** touch this guard (verified), so it is ours. Mirror how `Redeliver` derives both the check *and* its message from `RedeliverableFrom`:

```csharp
        if (!OrderStatusMachine.RetryableFrom.Contains(order.Status))
            return BadRequest(new
            {
                // Derived from the set, never a literal: widening RetryableFrom must not leave this
                // sentence quietly lying about which statuses are valid. Mirrors Redeliver's guard.
                error = $"Order must be in one of these statuses to retry delivery: "
                      + $"{string.Join(", ", OrderStatusMachine.RetryableFrom.OrderBy(s => s, StringComparer.Ordinal))} "
                      + $"(current: '{order.Status}')."
            });
```

> This changes a user-facing 400 message. Check `OrdersControllerErrorMessageTests` for an assertion on the old wording and update it if present — the message is now generated, so pinning the old literal would defeat it.

- [ ] **Step 4: Run the full suite**

```bash
dotnet test
```

Expected: PASS. `RedeliverableStatusInvariantPostgresTests` covers the hold repoint directly — it asserts `HoldForBillingAsync` HOLDS every `RedeliverableFrom` status on real Postgres. If it goes red, the repoint changed hold semantics; fix the repoint, never the test. `CrashedHolderRecoveryCompositionPostgresTests` and `RetryDeliveryJobBackoffTests.ExecuteAsync_ClaimLost_StillSchedulesRetry_ItIsTheCrashRecoveryNet` are the crash-recovery net — if either goes red, Step 1's warning is live.

Full suite, never a narrow `--filter` (project rule).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Controllers/OrdersController.cs ProcuLink.Infrastructure/Services/DeliveryService.cs
git commit -m "refactor(delivery): the hold and retry gates derive from the canonical sets

The hold set was the fourth hand-synced list and the worst of them: a
status it refuses never reaches delivery_held, so the reactivation re-drive
never finds it. Permanent strand, no self-heal.

The retry admission guard was the fifth, and the only one minting a
user-facing 400 from a hardcoded status name -- including in the prose,
which would have gone on naming delivery_failed after the set widened. It
now derives from the set, as Redeliver's already does."
```


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
| §4.4 `Outcome` | **Superseded and DONE** — enum + all three return mappings shipped in PR #30. Task 5 Step 1 verifies it survived the merge; nothing to build. |
| §4.4a stuck sweep freshens the row | **Not a task** — it is why Task 5 Step 1 refuses `NotRetryable` on the claim-lost returns, and why §4.5 item 3 must keep covering `fresh delivering → not claimable`. |
| §7a sweep handback protocol | **Open founder call, deliberately NOT in this plan.** |
| §4.5 item 1 (subset invariant) + red-phase ritual | Task 1 (Steps 1, 5) |
| §4.5 item 2 (sibling invariants) | Task 6 Step 1 |
| §4.5 item 3 (Postgres matrix) | Task 6 Step 2 |
| §5 Ops normalized-target subtlety | Task 6 Step 1 |
| §7 the one misleading test | Task 4 Steps 3-4 |
| §8 `OrdersController.Retry` | Out of scope — spawned as its own session (`local_9082ac44`) |

**Placeholder scan:** none — every code step carries the actual code, every command its expected output.

**Type consistency:** `DeliveryClaim.Claimable(Guid, Guid, IReadOnlySet<string>, DateTime)` is defined in Task 2 and used with that exact signature in Tasks 3, 4, 6. `ClaimableForDispatchFrom` / `ClaimableForRetryFrom` / `HoldableForBillingFrom` / `RetryableFrom` are defined in Task 1 and referenced under those exact names in Tasks 5 and 6. `DeliveryOutcome` is **not defined, extended, or mapped by this plan** — PR #30 (03a24c2) owns `{ Dispatched = 0, ClaimLost = 1, NotRetryable = 2 }` and all three return mappings; Task 5 Step 1 only verifies they survived the merge.

**Known ordering risk:** Task 1's tests reference `OrderStatusConstants.DeliveryUnconfirmed`, which exists only once PR #27 merges, and Task 5 consumes a `DeliveryOutcome` that exists only once `claude/priceless-pike-d2eb0a` merges (Global Constraints). Both are hard blocks with explicit guards; nothing here works around either. Merge main first so the merged behavioural net (`RedeliverableStatusInvariantPostgresTests`) runs underneath the whole plan.

**Sequencing note if only PR #27 lands:** Tasks 1–4 and 6 are executable without `claude/priceless-pike-d2eb0a`; only Task 5 depends on it. If the Outcome branch stalls, ship 1–4 + 6 and leave Task 5 for a follow-up — the claim dedup and every invariant stand on their own. Do **not** unblock Task 5 by declaring the enum locally.
