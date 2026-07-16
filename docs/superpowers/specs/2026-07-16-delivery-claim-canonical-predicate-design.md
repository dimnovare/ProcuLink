# Delivery claim: one canonical predicate

**Date:** 2026-07-16
**Status:** Design approved. **Implementation BLOCKED** — see Preconditions.
**Origin:** code review, 2026-07-16. Founder call requested because this is a design change, not a bug fix.

---

## 1. Problem, restated

`DeliveryService.DispatchArtifactAsync` carries two hand-written copies of the atomic delivery claim's
"claimable status" predicate — the relational `ExecuteUpdateAsync` predicate and the EF-InMemory emulation.
They must agree; nothing enforces that they do.

The failure is silent and expensive. A status present in one predicate and absent from the other makes the
claim match 0 rows, which `DispatchArtifactAsync` treats as a benign "not claimed — skipping dispatch",
returning `DeliveryResult(true, null)`. The job logs SUCCESS having sent nothing. The order is stranded and
the operator is told it worked.

### 1.1 The correction that reframes this work

**The duplication did not cause the 52c6431 bug.** This matters, because it inverts the task's priorities.

The actual chain was:

1. `delivery_unconfirmed` was added to `OrderStatusMachine.RedeliverableFrom` — the set gating
   `OrdersController.Redeliver`'s 400-vs-202 response. The controller began returning 202 for that status.
2. The claim predicate was **not** updated, so the claim matched 0 rows.
3. 0 rows → `DeliveryResult(true, null)` → the job logged success.
4. "Send again" returned 202 and dispatched nothing. The order stayed parked.

Had the two predicates already been *one* predicate, that single predicate would **still** have lacked
`delivery_unconfirmed`, and the bug would have happened identically. Duplication made the *fix* two hand-edits
instead of one; it did not cause the *defect*.

The defect had two real roots, and neither is duplication:

- **Set incompleteness.** Nothing enforces that a status the controller accepts for redeliver is a status the
  claim can claim. The invariant `RedeliverableFrom ⊆ ClaimableForDispatchFrom` would have failed the build
  on the commit that introduced the status.
- **The silence.** The `DeliveryResult(true, null)` return is what converted a 400-shaped mistake into a
  stranded order and a lying log.

The existing test is `RedeliverableFrom_MatchesThePriorLiteralExactly` — it pins the set against a literal, so
it would not have caught this either.

Deduplicating the predicate is still worth doing: it is a real latent risk, and both copies surviving 52c6431
was luck (a human remembered to edit both). But the invariant test is the deliverable that would have
prevented the outage, and it is ranked first below.

---

## 2. Founder decisions

| # | Question | Decision |
|---|---|---|
| 1 | Base branch | **Wait for `claude/wizardly-spence-6b738a` to merge**, then build on main. |
| 2 | How to make the paths agree | **Shared `Expression` built on named sets** (approach "B on A"). |
| 3 | Scope | **Both claims** — dispatch *and* retry, all five sites. |
| 4 | `DeliveryResult(true, null)` on 0 rows | **Add an `Outcome` enum; keep control flow unchanged.** |

---

## 3. Preconditions — do not start before these hold

`delivery_unconfirmed` **does not exist on main.** Both c94c8ff (the redeliver feature) and 52c6431 (the
hand-fix) live only on `claude/wizardly-spence-6b738a`, which is unmerged, unpushed, and checked out in an
active worktree (`loving-euclid-2575e4`). On main the two dispatch predicates still agree — the drift this
spec addresses is only real on that branch.

The branches have diverged: **21 commits on main not in wizardly-spence; 20 on wizardly-spence not in main.**
Notably, main has the observer-superset structural test and `internal` visibility on
`OrderStatusTransitionObserver.AllowedTransitions`; wizardly-spence has neither.

**Order of operations:**

1. `claude/wizardly-spence-6b738a` merges main (picking up the observer test + `internal` visibility).
2. `claude/wizardly-spence-6b738a` merges to main.
3. **Then** this spec executes on main.

Starting earlier means hand-resolving conflicts in the exact lines the other session is editing. Project
memory already records the cost of rebasing two branches that edit the same method.

---

## 4. Design

### 4.1 Canonical sets — `OrderStatusMachine` (Core)

Two named sets beside `RedeliverableFrom`, in the same shape:

```csharp
/// The idle statuses DispatchArtifactAsync's atomic claim accepts.
public static readonly IReadOnlySet<string> ClaimableForDispatchFrom =
    Set(ReadyToDeliver, DeliveryFailed, DeliveryUnconfirmed);

/// RetryDeliveryAsync's automatic backoff claim. Deliberately EXCLUDES delivery_unconfirmed:
/// a parked order is re-driven only by a human "Send again", never by the backoff queue (52c6431).
public static readonly IReadOnlySet<string> ClaimableForRetryFrom =
    Set(ReadyToDeliver, DeliveryFailed);
```

One canonical set is **wrong** — there are genuinely two, differing by exactly one status, and that delta is a
load-bearing product decision. Naming one and leaving its near-twin a literal 600 lines away hides the very
thing the next reader needs to see.

These hold **idle** statuses only. The stale-`delivering` reclaim is not status membership — it is status *plus
time* — so it composes onto the set in the predicate (§4.2), not into it.

### 4.2 One predicate — `DeliveryClaim` (Core)

`PurchaseOrderEntity` lives in `ProcuLink.Core/Entities`, and `Expression<Func<T,bool>>` is BCL, so this needs
no EF reference in Core.

```csharp
public static class DeliveryClaim
{
    /// The ONE claim predicate. Both the relational ExecuteUpdateAsync path and the InMemory
    /// emulation consume this, so they cannot disagree.
    public static Expression<Func<PurchaseOrderEntity, bool>> Claimable(
        Guid orgId, Guid orderId, IReadOnlySet<string> idleClaimable, DateTime staleBefore)
    {
        // An empty set yields `= ANY('{}')` -> matches nothing -> the claim affects 0 rows and the
        // caller reads it as "someone else claimed it". That is the exact silent-strand this file
        // exists to prevent, so fail loud instead of no-op'ing quietly.
        if (idleClaimable.Count == 0)
            throw new ArgumentException("A claim with no claimable statuses can never claim.", nameof(idleClaimable));

        // .ToArray() parameterizes the set as `= ANY(@p)`, keeping the claim SQL text INVARIANT across
        // callers. NOT because IReadOnlySet.Contains fails to translate -- it translates fine on EF8 --
        // but because it inlines the set's CONTENTS as SQL literals, minting a distinct query-cache
        // entry and Postgres plan per distinct set. Verified on Postgres 16; see §6.
        var arr = idleClaimable.ToArray();

        return x => x.Id == orderId
                 && x.OrgId == orgId
                 && (arr.Contains(x.Status)
                  || (x.Status == OrderStatusConstants.Delivering && x.UpdatedAt < staleBefore));
    }
}
```

**Stale-`delivering` is shared, not per-path** (task item 2). Both claims want identical reclaim semantics;
splitting it would duplicate the thing this spec removes. The `.ToArray()` must live inside the factory — it
is what EF captures as the closure parameter.

Org scoping lives *inside* the predicate, so the claim cannot be written un-org-scoped.

### 4.3 Consumption

```csharp
// relational
var pred = DeliveryClaim.Claimable(orgId, orderId, OrderStatusMachine.ClaimableForDispatchFrom, staleBefore);
var claimed = await _db.PurchaseOrders.Where(pred).ExecuteUpdateAsync(s => s /* ...unchanged... */, ct);

// InMemory — same predicate, evaluated against the already-loaded tracked entity
if (!DeliveryClaim.Claimable(orgId, orderId, OrderStatusMachine.ClaimableForDispatchFrom, staleBefore)
        .Compile()(order))
{ /* NotClaimed no-op */ }
```

`RetryDeliveryAsync` mirrors this with `ClaimableForRetryFrom`.

> **Sharing the predicate must NOT share the result.** The two methods have deliberately different return
> contracts on a lost claim: `DispatchArtifactAsync` returns `DeliveryResult(true, null)` (benign no-op),
> `RetryDeliveryAsync` returns `DeliveryResult(false, "Delivery for this order is already in progress.")`.
> `RetryDeliveryJob` schedules backoff on `!Success`, so collapsing these would change retry behaviour. Each
> path keeps its own return.

The `IsRelational()` fork stays. It is necessary only because InMemory cannot run `ExecuteUpdateAsync` — not
because of the predicate. It is also a house idiom (7 services, 164 InMemory test files); deleting it was
considered and rejected as disproportionate.

### 4.4 `Outcome` — honest logs, unchanged control flow

73 non-test construction sites, so a required parameter is out. A derived init property touches only the sites
whose meaning is special:

```csharp
public enum DeliveryOutcome { Dispatched, NotClaimed, SkippedAutoDeliverOff, Failed }

public record DeliveryResult(bool Success, string? ErrorMessage, int? ResponseCode = null, string? ResponseBody = null)
{
    public DeliveryOutcome Outcome { get; init; } = Success ? DeliveryOutcome.Dispatched : DeliveryOutcome.Failed;
}
```

The other ~70 sites compile untouched and derive correctly. Only the claim-lost and auto-deliver-off returns
set it explicitly. `Success` semantics are unchanged, so the benign concurrent-activation case behaves exactly
as today — `DeliverOrderJob` simply stops logging "success" for "did nothing", and ops-health gains a
countable signal.

**Rejected:** classifying a 0-row claim by re-reading the row (benign vs suspicious). It needs a third
drift-prone "benign statuses" list, and it solves at runtime what §4.5's invariant solves at build time.

### 4.5 Tests

**Ranked. The first is the one that would have caught 52c6431.**

1. **`RedeliverableFrom_IsSubsetOf_ClaimableForDispatchFrom`** — pure, no DB, milliseconds. Directly pins the
   regression.
2. **Sibling invariants for the other enqueue paths** (§5 maps them). Note the Ops guard set is *not* a
   subset — the invariant there must be stated over its **normalized target**, not its guard.
3. **Relational/InMemory equivalence matrix, on real Postgres.** For every status in
   `OrderStatusMachine.AllStatuses` × {fresh, stale} `UpdatedAt`, assert the relational `ExecuteUpdateAsync`
   claim and `predicate.Compile()(order)` reach the same verdict.

On item 3: under this design the two paths cannot disagree — that is true by construction, so the "structural
agreement" test the task asked for has nothing left to pin. What earns its place instead is this behavioural
matrix, which tests something the shared predicate does *not* guarantee: that **Npgsql's translation of the
expression matches C#'s evaluation of it** (null handling, collation, `= ANY` semantics). It enumerates
`AllStatuses`, so a newly added status enters the matrix automatically. This is why real Postgres is
mandatory: the InMemory provider does not exercise `ExecuteUpdateAsync` at all.

**House style to copy:** main's `OrderStatusMachineTests.Machine_Transitions_AreASupersetOf_ObserverAllowedTransitions`
— a two-sided assertion against a `KnownObserverOnlyEdges` exemption set, where the second direction ensures
exemptions cannot rot. Match that shape; do not invent a new one.

**TDD honesty note.** On the post-merge base this invariant test is **green on arrival**, because 52c6431
already hand-fixed the set. To prove it is not vacuous: drop `delivery_unconfirmed` from
`ClaimableForDispatchFrom`, watch it go red, restore. Record that; do not claim a red phase that did not
happen.

---

## 5. The invariant family (verified)

Five sites enqueue `DeliverOrderJob`. **No live subset violation exists** — 52c6431 closed the only one.

| # | Site | Guard type | From-status | Subset? |
|---|---|---|---|---|
| 1 | `OrdersController.cs:1800` | named set | `RedeliverableFrom` = {delivery_failed, ready_to_deliver, delivery_unconfirmed} | **YES** |
| 2 | `OpsController.cs:125` → `:208` | literal + normalizing write | guard {delivery_dead_letter, delivery_failed}; `:159` forces `delivery_failed` before enqueue | **guard NO / effective YES** |
| 3a | `TransformOrderJob.cs:114` | ungated | upstream invariant: `OrderTransformService.cs:489` sets `ready_to_deliver` | YES (indirect) |
| 3b | `TransformOrderJob.cs:145` → `:171` | literal | {ready_to_deliver} | **YES** |
| 4+5 | `HangfireDeliveryDispatchEnqueuer.cs:30` ← `StrandedReadyOrderDetectionService.cs:53` | WHERE clause | {ready_to_deliver} AND `UpdatedAt < cutoff` | **YES / DYNAMIC** |

**Site 2 is the fragile one.** Its guard set is *not* a subset (`delivery_dead_letter` is not claimable). It is
saved only by the normalizing write at `OpsController.cs:159`, committed before the enqueue — the exact shape
of the 52c6431 bug with a load-bearing patch holding it up. Its invariant must therefore be stated over the
normalized target. `if (tracked is not null)` at `:151` means a null `tracked` enqueues without normalizing
(benign: a nonexistent order matches 0 rows anyway).

**Corrections to the initial brief:** `HangfireDeliveryDispatchEnqueuer` is *not* the billing-reactivation
path — `ReleaseBillingHeldOrdersAsync` uses `IRetryDeliveryEnqueuer` → `RetryDeliveryJob`, a different job with
a different claim, and it resets `delivery_held` → `ready_to_deliver` and commits before enqueuing. Sites 4
and 5 are one logical path, not two. `StuckDeliveryDetectionService` also uses `RetryDeliveryJob`, so it is
out of this family.

---

## 6. Verified facts (Postgres 16, EF Core 8.0.16, Npgsql 8.0.11)

Proven by throwaway spike against a real container. Every claim below is evidence, not reasoning.

- **A pre-built `Expression` variable + `ExecuteUpdateAsync` translates and executes fine.** The feared "core
  risk" is a non-risk — EF composes the tree before translation and cannot tell it came from a variable.
- **`arr.Contains(x.Status)` over a captured `string[]` translates to `= ANY (@param)`** — a single array
  parameter, so no parameter-count explosion and no per-cardinality plan churn.
- **`IReadOnlySet<string>.Contains` also translates** (no exception). The original justification for
  `.ToArray()` was **wrong**. It emits `IN ('ready_to_deliver','delivery_failed')` with the values baked into
  the SQL *text*.
- **The decisive experiment:** with `.ToArray()`, the SQL text is *identical* across different set contents;
  without it, the text changes with the contents. Since the factory takes the set as a parameter, the
  no-`ToArray` form pollutes the query cache on delivery's hottest write path. **Keep the hop; fix the
  comment** — a wrong comment is how the next reader deletes it.
- **Claim semantics correct:** ready_to_deliver → 1, delivery_failed → 1, delivered → 0, **fresh** delivering
  → 0, **stale** delivering → 1, wrong `orgId` → 0.
- **Empty set is a live edge case:** `= ANY('{}')` matches nothing → 0 rows → read as "someone else claimed
  it". Benign by luck, not design. Hence the explicit guard in §4.2.
- `= ANY` is Npgsql/EF8 behaviour; do not assume it survives a provider or EF major upgrade. The §4.5 matrix
  test pins it.

---

## 7. Known fallout

Making InMemory derive from the shared predicate **changes InMemory behaviour**: `DispatchArtifactAsync` starts
rejecting a fresh `delivering`, and `RetryDeliveryAsync` gains a status gate it currently lacks entirely.

**Exactly one test breaks:** `DeliveryServiceIdempotencyTests.cs:74`
(`CrashAfterSendBeforeCommit_ReDrive_ReAdoptsInFlightRow_SameKey_NoSecondDelivery`). `DispatchArtifactAsync`'s
change breaks **zero** tests — every InMemory dispatch test seeds `ready_to_deliver` or `delivery_failed`,
claimable regardless of `UpdatedAt`.

**That one test is actively misleading, and its fix is a semantic decision.** Its comment claims to "Reproduce
the EXACT post-crash state", but it seeds `UpdatedAt = now` — a *fresh* `delivering`. Production is relational,
so **this scenario has never worked in production**: the relational claim rejects a fresh `delivering`. The
test passes today *only* because the InMemory path flips unconditionally. Its own Postgres twin
(`DeliveryCrashRecoveryPostgresTests.cs:169-171`) seeds the identical scenario aged `-30 minutes`, commented
"aged well past the 2-minute reclaim window" — and a real crashed order is stale by definition
(`StuckDeliveryDetectionService` only re-drives rows stuck ~45 min).

**Fix: age the seed to `now.AddMinutes(-30)`,** which makes it pass *and* start testing the real production
path. Do **not** relax the predicate to keep it green — that preserves the fiction.

Also corrected: `RetryDeliveryAsync`'s advisory pre-check (~L803) already rejects every status outside
{delivery_failed, ready_to_deliver, delivering}, so no non-claimable status can currently reach the
unconditional InMemory flip. The only genuinely new InMemory rejection is fresh-`delivering`. This is why the
count is 1, not several.

---

## 8. Out of scope

**`OrdersController.Retry` pre-flip (P2, live on main, spawned as its own task).** `OrdersController.cs:1847-1858`
optimistically flips to `delivering` with `UpdatedAt = UtcNow` and *then* enqueues `RetryDeliveryJob`, whose
claim rejects a fresh `delivering`. The operator's "Retry now" therefore does nothing for ~30 minutes (until
the backoff fires and the row is stale), while the UI shows `delivering`. Self-healing, no attempt row written,
dead-letter cap not burned — but the button lies. The sibling `Redeliver` path was already fixed for exactly
this ("B2 (lost-order): DO NOT pre-flip to 'delivering'"); `Retry` was missed. Memory obs 4218 recorded the
class on 2026-07-11.

---

## 9. Execution order

1. Confirm §3 preconditions.
2. Add `ClaimableForDispatchFrom` / `ClaimableForRetryFrom` (§4.1).
3. Write the `RedeliverableFrom ⊆ ClaimableForDispatchFrom` invariant test (§4.5 item 1) + the red-phase
   ritual. **This is the deliverable that would have prevented the outage.**
4. Add `DeliveryClaim.Claimable` (§4.2) with the empty-set guard.
5. Repoint all four claim sites (§4.3), preserving each path's own return contract.
6. Age the `DeliveryServiceIdempotencyTests.cs:74` seed (§7).
7. Add `Outcome` (§4.4).
8. Add sibling invariants (§4.5 item 2) + the Postgres matrix (§4.5 item 3).
9. Full build + test on real Postgres. `/code-review` before merge (project rule: never skip).
