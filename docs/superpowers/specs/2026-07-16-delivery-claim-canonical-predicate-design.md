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

**Order of operations:**

1. ~~`claude/wizardly-spence-6b738a` merges main~~ — **DONE.** It is now 0 commits behind `origin/main`
   (which has itself advanced to 73e7e51), so it has the observer-superset structural test and `internal`
   visibility on `OrderStatusTransitionObserver.AllowedTransitions`.
2. **PR #27** (`fix(delivery): park unknown-outcome re-drives instead of duplicating the PO`) merges to main.
   **OPEN and MERGEABLE**; still open as of 2026-07-17. It carries both `delivery_unconfirmed` (52c6431) and
   the hold-set hand-fix (392b5a4). ← **blocker 1**
3. **`claude/priceless-pike-d2eb0a`** (f078bff, `fix(delivery): stop the unbounded retry loop on non-dispatch
   results`) merges to main — it owns `DeliveryOutcome` (§4.4). Unmerged, no PR seen. ← **blocker 2**
4. **Then** this spec executes on main, merging main first.

Starting before step 2 means hand-resolving conflicts in the exact lines PR #27 changes. Project memory
already records the cost of rebasing two branches that edit the same method.

> **Duplicate-work hazard — RESOLVED 2026-07-17.** Session `local_f5ee08ce` (*"Add a structural guard for
> delivery status-list drift"*, branch `claude/wizardly-khayyam-c45ffe`) turned out **not** to collide. Its
> piece is **test-only and already MERGED to main** (56a82ba + 8684d17;
> `ProcuLink.Api.Tests/Integration/RedeliverableStatusInvariantPostgresTests.cs`). It has no further edits
> planned in `DeliveryService.cs`, so the claim lines are this spec's alone.
>
> It sidestepped the blocker this spec is stuck behind by proving non-vacuity against
> **`delivery_dead_letter`** — a status that already exists on main — rather than against
> `delivery_unconfirmed`. That is why it shipped and this has not.
>
> **Its test is the safety net under this refactor.** Per status in `RedeliverableFrom`, on real Postgres, it
> asserts the claim CLAIMS it (dispatch + success attempt row + lands `delivered`) and `HoldForBillingAsync`
> HOLDS it (and dispatches 0). If the §4.3 repoint breaks claim semantics, it says so per status with dispatch
> evidence. Merge main before executing so it is running underneath.
>
> Confirmed by that session, and worth keeping straight: its test catches **neither** of this spec's two
> traps — the staleness divergence (it is relational-only and statuses-only) nor the Dispatch/Retry return
> contracts (it deliberately never asserts on the return value, because `Success` is `true` for the
> silent-strand case). §4.5 item 3's equivalence matrix is still required.

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

/// HoldForBillingAsync's holdable set — the FOURTH list (added 2026-07-17; see below).
/// Must accept every status "Send again" can traverse, because a lapsed org's redeliver
/// reaches the billing gate BEFORE the claim.
public static readonly IReadOnlySet<string> HoldableForBillingFrom =
    Set(ReadyToDeliver, DeliveryFailed, DeliveryUnconfirmed);

/// The FIFTH list (added 2026-07-17): OrdersController.Retry's admission guard, the
/// RedeliverableFrom twin for the retry leg. Must be a subset of ClaimableForRetryFrom.
public static readonly IReadOnlySet<string> RetryableFrom =
    Set(DeliveryFailed);
```

One canonical set is **wrong** — there are genuinely four, and the deltas between them are load-bearing
product decisions. Naming one and leaving its near-twins as literals hundreds of lines away hides the very
thing the next reader needs to see.

**The fifth list (added 2026-07-17, found by `local_9082ac44`, verified here on main).**
`OrdersController.Retry` (`OrdersController.cs:1847`) gates its 400-vs-202 on a bare literal:

```csharp
if (order.Status != OrderStatusConstants.DeliveryFailed)
    return BadRequest(new { error = $"Order must be in 'delivery_failed' status to retry delivery (current: '{order.Status}')." });
```

This is `RedeliverableFrom`'s twin for the retry leg — an endpoint-level admission guard, and the one place a
user-facing 400 is minted from a hardcoded status name. It is **correct today** (`{delivery_failed}` ⊆
`ClaimableForRetryFrom`), so this is drift *prevention*, not a bug fix. It is exactly the shape
`RedeliverableFrom` had before it was named — and naming that one is what made 52c6431 findable at all.

Note `RedeliverableFrom`'s error message already derives from the set, with the reason stated in-line: *"Derived
from the set, never a literal: adding a redeliverable status must not leave this sentence quietly lying about
which statuses are valid."* The Retry message hardcodes `'delivery_failed'` in its prose and must derive too —
otherwise a widened set leaves the sentence lying about which statuses are valid.

**PR #29 does not touch this literal** (verified against `origin/claude/funny-maxwell-a8fde7`: still at
`:1847`, unchanged). It fixes the pre-flip, not the guard. So this is unowned and belongs to this spec.

**The fourth list (added 2026-07-17, from `local_f5ee08ce`).** This spec originally mapped only the five
*enqueue* sites and missed `HoldForBillingAsync`'s holdable set (~L966), which sits *downstream* of the billing
gate at `DeliverOrderJob.cs:86` — a path "Send again" traverses whenever the org has lapsed. Its failure mode
is the same silent shape but **strictly worse than the one this spec was written for**: a status it refuses
holds nothing, sends nothing, and audits nothing, and because the order never becomes `delivery_held`,
`ReleaseBillingHeldOrdersAsync` never re-drives it on reactivation. **Permanent invisible strand, no
self-heal** — where the claim-drift case at least leaves an order a sweep can find. It was hand-fixed in
392b5a4, which is *inside PR #27* — i.e. the same list-by-hand pattern bit a fourth time on one branch. That
is the argument for the named set, not against it.

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

### 4.4 `Outcome` — SUPERSEDED, do not build

**This section's original design is withdrawn (2026-07-17).** A sibling session has already built
`DeliveryOutcome` on `claude/priceless-pike-d2eb0a` (f078bff, unmerged), and its design is better than the one
this spec proposed. **Consume theirs; do not introduce a competing enum.**

**Updated 2026-07-17: the enum is now TERNARY, and the binary version was wrong.** PR #30 / 03a24c2:

```csharp
public enum DeliveryOutcome { Dispatched = 0, ClaimLost = 1, NotRetryable = 2 }
```

- **`ClaimLost`** — no dispatch, no attempt row, **transient**. **MUST keep rescheduling**: it is the only path
  that recovers a crashed holder. Bounded — the next run either claims the now-stale row or finds the order
  terminal.
- **`NotRetryable`** — no dispatch, no attempt row, and no later attempt can help. Never reschedule (the
  original unbounded ~30-min loop).

The binary `{ Dispatched, NotAttempted }` collapsed transient and terminal, which turned "delivered 30 minutes
late" into "never delivered, dead-lettered once the sweep burns `MaxRequeues=2`". A real-Postgres test
(`CrashedHolderRecoveryCompositionPostgresTests`) fails if the two are collapsed — see §4.4a for why.

My orthogonality argument survives intact: `Success` stays a separate axis, `Dispatched` is still the default,
and the 0-row claim still returns `Success=true`, so no benign lost race logs a failure. My "don't split the
concept" argument does **not** apply here and I was wrong to think it might: my split (`NotClaimed` vs
`SkippedAutoDeliverOff`) was cosmetic — both terminal. This split is **behavioural** — transient vs terminal —
and a test proves it.

Why theirs wins, recorded so this is not re-litigated:

- **It is orthogonal to `Success`; mine was not.** My `{ Dispatched, NotClaimed, SkippedAutoDeliverOff, Failed }`
  made `Failed` an *outcome*, duplicating what `Success` already says. Theirs keeps the axes separate:
  `Dispatched` + `Success=false` is a real, retryable failure; `NotAttempted` is not retryable at all.
- **It solves a safety bug, not a logging nit.** Rescheduling a `NotAttempted` result is an unbounded ~30-min
  loop, not a retry: with no `DeliveryAttempt` row the count is frozen, so `attemptsMade >= maxAttempts` never
  trips and `BackoffFor` returns the same delay forever. `ResponseCode` is null, so the 4xx guard misses it
  too. My design would have logged honestly and still spun.
- **My extra members were logging detail.** `NotClaimed` and `SkippedAutoDeliverOff` are both instances of
  their `NotAttempted`. Splitting them buys nothing for the retry contract and would have broken a guard that
  branches on `== NotAttempted`.
- Their `Dispatched = 0` default is deliberate: a call site that forgets to mark itself degrades to the old
  noise, never to silently abandoning a deliverable order. Inverting that default is the dangerous direction.

**This spec now owes NOTHING here — PR #30 did all of it.** The mapping, for reference and for review:

| Return | Outcome |
|---|---|
| `DispatchArtifactAsync` relational claim-lost (~L222) | `ClaimLost` (`Success` stays `true`) |
| `DispatchArtifactAsync` InMemory claim-lost (~L253) | `ClaimLost` (`Success` stays `true`) |
| `DispatchArtifactAsync` auto-deliver-off (~L158) | `NotRetryable` |
| `RetryDeliveryAsync` claim-lost | `ClaimLost` |
| `RetryDeliveryAsync` terminal early-returns (not found / dead-letter / not-retryable status / no artifact / billing hold / cap) | `NotRetryable` |

Auto-deliver-off is `NotRetryable` — the answer to the question this spec put to the type's owner: it
dispatched nothing, and no retry can change a config decision, so it is terminal, not a lost race. It returns
`Success=true`, so control flow is unchanged either way.

Any job deciding whether to reschedule branches on `Outcome`, never on `ErrorMessage` text.

The founder's call on item 4 (keep control flow, add an outcome marker) stands and was independently reached by
three sessions. See [[project-delivery-outcome-notattempted]].

### 4.4a The stuck sweep freshens the row it is recovering (verified on main)

Found by `local_1559ce63` when the audit session made it *prove* rather than assert that stopping the retry
queue was safe. Verified here against `origin/main`. `StuckDeliveryDetectionService`:

```csharp
// Bump UpdatedAt so this order leaves the stuck window: the retry job will move it
// to a terminal status, and a duplicate sweep before then won't re-act on it.
order.UpdatedAt = now;
...
await _retryEnqueuer.EnqueueAsync(orderId, orgId, ct);
```

The sweep stamps `UpdatedAt = now` on a `delivering` row and *then* enqueues the retry. The retry's claim
requires `(Delivering && UpdatedAt < staleBefore)`. The row is now fresh, so the claim matches **0 rows** and
bounces. **The comment is a false premise: the retry job cannot move it to a terminal status**, because the
bump is what stops it claiming. Only the ~30-minute scheduled backoff ages the row enough for a later attempt
to succeed.

This is the same defect class as §1.1 and §8: **a comment asserting a guarantee the code does not deliver.** It
also means the reclaim window and the sweep are coupled in a way neither file mentions — directly relevant
here, because §4.2 makes the staleness gate shared and explicit for the first time.

**Consequence for this spec:** the `~30-min backoff` is not a wasteful fallback, it is *the* crash-recovery
mechanism. §4.2 must not "optimise" the staleness gate away, and §4.5 item 3's matrix must keep covering
`fresh delivering → not claimable`, which is the property the whole recovery path rests on.

**Rejected:** classifying a 0-row claim by re-reading the row (benign vs suspicious). It needs a third
drift-prone "benign statuses" list, and it solves at runtime what §4.5's invariant solves at build time.

### 4.5 Tests

**Ranked. The first is the one that would have caught 52c6431.**

0. **Already merged, not ours to write:** `RedeliverableStatusInvariantPostgresTests` (§3) pins the same
   invariant *behaviourally* on real Postgres, across the claim and the hold set. It is the net; do not
   duplicate it.
1. **`RedeliverableFrom_IsSubsetOf_ClaimableForDispatchFrom`** — pure, no DB, milliseconds. Complementary to
   item 0, not a substitute, and vice versa.

   > **Its validity is conditional, and the condition is this spec.** As `local_f5ee08ce` correctly argued: a
   > set-vs-set assertion pins *declaration against declaration*, and means nothing while the real gates are
   > hand-written literals that can drift underneath it — that is fake safety. It only becomes load-bearing
   > once **every** gate derives from the named set (§4.1 + §4.3). It therefore must ship in the same change
   > as the repoint, never alone or ahead of it. What it buys over item 0: build-time, milliseconds, no
   > Docker — it fails in the editor rather than in CI.

2. **Sibling invariants for the other enqueue paths** (§5 maps them), plus the **hold set** and the
   **Retry admission guard** (§4.1) — i.e. `RedeliverableFrom ⊆ HoldableForBillingFrom` and
   `RetryableFrom ⊆ ClaimableForRetryFrom`. Note the Ops guard set is *not* a subset — the invariant there
   must be stated over its **normalized target**, not its guard.
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

## 7a. OPEN DESIGN CALL — the stuck sweep's handback protocol (founder decision needed)

Handed to this spec by `local_1559ce63` as "above my scope; your predicate work is the natural home". Agreed
that it belongs here conceptually — it is entirely about claim/staleness semantics. **But it is NOT folded
into the plan**, because it is a behaviour change to crash recovery, it rewrites 4 existing tests, and this
spec is already last in a five-deep merge queue. Recorded here so it is not lost. **Founder call.**

**The question:** should `StuckDeliveryDetectionService` hand a re-driven order back in an *idle claimable*
status, instead of leaving it `delivering` with a fresh `UpdatedAt` (§4.4a)?

| Option | Effect | Cost / risk |
|---|---|---|
| **A. Leave it; fix the false comment.** *(Recommended)* | `ClaimLost` reschedules, the row ages, the ~30-min backoff delivers it. Works today. | Recovery is ~30 min, not immediate. Cheapest, zero behaviour change, and it makes the comment honest — which is the actual defect. |
| **B. Sweep sets `ready_to_deliver`.** | Retry claims immediately; recovery in seconds. The sweep's comment becomes true. | Rewrites 4 tests pinning `Status == Delivering` after re-drive. **Weakens a safety property:** an idle status is claimable *regardless of `UpdatedAt`*, so if the "stuck" holder is merely slow (a long SFTP push) rather than dead, the row is instantly claimable and the PO double-sends. The staleness gate exists precisely to make "abandoned" a *provable* property rather than a presumed one. |
| **C. Keep `delivering`, drop the bump.** | Row stays stale, so the enqueued retry claims immediately *and* the staleness gate still proves abandonment. Best of both on paper. | The bump is load-bearing for the sweep's own idempotency (`UpdatedAt` is how a duplicate sweep knows not to re-act). Dropping it needs a separate `last_swept_at` column or equivalent — a schema change. |

**Recommendation: A.** The defect that actually hurt anyone here is the *false comment*, not the 30-minute
recovery — and B trades a proven safety property (staleness ⇒ genuinely abandoned) for latency on a path that
already self-heals. C is the intellectually honest fix and is worth revisiting if the 30-minute window ever
becomes a real complaint, but it is a schema change to solve a latency problem nobody has reported.

Whichever wins, the comment must stop claiming the retry job will move the order to a terminal status.

## 8. Out of scope

**`OrdersController.Retry` pre-flip — now owned by PR #29 (`claude/funny-maxwell-a8fde7`, MERGEABLE).**
`OrdersController.cs:1847-1858` optimistically flips to `delivering` with `UpdatedAt = UtcNow` and *then*
enqueues `RetryDeliveryJob`, whose claim rejects a fresh `delivering`. The operator's "Retry now" therefore
does nothing for ~30 minutes, while the UI shows `delivering`. Self-healing, no attempt row written,
dead-letter cap not burned — but the button lies. Memory obs 4218 recorded the class on 2026-07-11.

> **Correction (2026-07-17) — two errors of mine, both now verified against `origin/main`:**
>
> 1. **The "B2 / DO NOT pre-flip" prior art is NOT in `OrdersController.Redeliver`.** This spec originally
>    called Redeliver "the already-fixed sibling". Wrong: `origin/main`'s `OrdersController.cs` contains no
>    such comment at all. The prior art is the **ops requeue/escalation** leg
>    (`OpsControllerTests.cs:197`, `RequeueDelivery_FromDeadLetter_LeavesClaimableStatus_...`). I read that
>    comment inside `Redeliver` on the **`claude/wizardly-spence-6b738a` branch** — where PR #27 adds it — and
>    wrote it up as established prior art on main. **I conflated a branch with main.** The defect and its
>    severity are unaffected; only the attribution was wrong.
> 2. **The ~30-minute rescuer is `RetryDeliveryJob`'s own backoff, not a sweep.**
>    `RetryDeliveryJob.cs:96` does `ScheduleRetry(_jobs, orderId, organisationId, _options.BackoffFor(attemptsMade))`
>    after `RetryDeliveryAsync` returns "already in progress"; by then `UpdatedAt` is stale, so the retry's own
>    next run claims successfully. `StrandedFailedDeliveryDetectionService` **cannot** be the rescuer: it
>    matches `Status == DeliveryFailed && UpdatedAt < cutoff` (3h), and the pre-flip leaves the row in
>    `delivering`. This matters if anyone tunes that sweep expecting it to cover this.
>
> The lesson is the one this thread keeps re-teaching: a fact measured on one branch and asserted about
> another is the same defect class as a stale comment. See §1.1.

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
