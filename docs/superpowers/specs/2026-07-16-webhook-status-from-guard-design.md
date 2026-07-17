# Webhook status callback: from-status guard + rejection semantics

**Date:** 2026-07-16
**Branch:** `fix/webhook-status-from-guard` (base: `main` @ db02350)
**Area:** `ProcuLink.Api/Controllers/WebhookIngressController.cs` — the `Status` endpoint
**Care level:** security-adjacent (CLAUDE.md high-care) — review required before merge

## Problem

`POST /api/webhook-ingress/{slug}/status` loads the order by id, org-scoped, with **no
from-status predicate**. The only guard is "not already delivered":

```csharp
if (status == "delivered" && order.Status != "delivered")
    order.Status = "delivered";
else if (status == "rejected" && order.Status != "delivered")
    order.Status = "delivery_failed";
```

Two defects fall out of those four lines.

### D1 — no dispatch guard (P2)

An HMAC-authenticated callback can drive **any** order in **any** state straight to a
terminal, customer-visible status — including `pending_parse`, `parsing`, `unrouted`,
`pending_review`, `ready`, and `transforming`. An order that was never sent gets marked
`delivered`: a silent lost order.

**Blast radius.** Bounded to one org (the HMAC slug is the tenant key — no cross-tenant
reach). Exploitation needs the org's webhook secret *and* a valid v4 `orderId` Guid, which
is not enumerable and which a supplier only learns by receiving the document. The realistic
trigger is therefore a buggy or mis-mapped supplier integration posting a stale/wrong
`orderId`, not an attacker. Real, worth fixing, not a P0.

### D2 — a supplier rejection is written as a transport failure (P1)

`status == "rejected"` writes **`delivery_failed`**, not `rejected_by_supplier`. That
collides with a sweeper whose correctness argument depends on the opposite:

- `StrandedFailedDeliveryDetectionService.cs:46` justifies its predicate with *"the database
  excludes the orders that legitimately rest in delivery_failed (**a supplier rejection lands
  in rejected_by_supplier**, a dead order in delivery_dead_letter — neither matches this
  status filter)"*. That premise is false today.
- `DeliveryService.RetryDeliveryAsync` (`:803`) retries from `delivery_failed`.

Chain: supplier rejects → `delivery_failed` → after the aged threshold (3h) the sweep sees
attempts remaining and no in-flight `dispatching` row → re-drives → **ProcuLink re-sends a
PO the supplier explicitly rejected**, up to the attempt cap. Duplicate delivery of rejected
goods.

### D3 — a business rejection of a delivered order is dropped (P2)

`status == "rejected" && order.Status != "delivered"` means a rejection arriving *after* our
HTTP 200 is silently ignored, and answered `200 OK`. This contradicts the product north star
("never equate HTTP 200 with supplier business acceptance", CLAUDE.md) and the observer's own
comment (`OrderStatusTransitionObserver.cs:91`: "A delivered order can still be rejected
later (supplier business ACK ≠ HTTP 200)").

## Triage: was the unguarded write intended?

Split verdict. The codebase answers it.

**Intended.** `OrderStatusTransitionObserver.AllowedTransitions` — described as "derived from
every real Status write point in the codebase (… WebhookIngressController)" and as a map that
"must stay SILENT for every legit flow" — explicitly documents each late-ACK edge:

| Edge | Observer's stated reason |
|---|---|
| `ready_to_deliver → delivered` | "supplier status webhooks (which may report a terminal state straight from ready_to_deliver)" |
| `delivery_failed → delivered` | "late supplier ACK" |
| `delivery_dead_letter → delivered` | "a late supplier ACK may still land" |
| `rejected_by_supplier → delivered` / `→ delivery_failed` | "a late positive ACK" |

`ready_to_deliver → delivered` is additionally pinned by a live test
(`WebhookIngressControllerTests.cs:209`). These are real flows, not gaps.

> **Correction (2026-07-16, during implementation).** This spec originally justified the
> `ready_to_deliver` case as a pre-send race — "`OpenDispatchAttemptAsync` commits the
> `dispatching` row before the wire send, so a supplier can call back before we commit
> `delivering`". **That is false**, and review caught it. `DispatchArtifactAsync` commits the
> D-1 atomic claim setting `Status = Delivering` (`DeliveryService.cs:205-224`) *before* the
> artifact download (`:280`), before `OpenDispatchAttemptAsync` (`:315`), and before
> `dispatcher.DispatchAsync` (`:320`). The order is never still `ready_to_deliver` when a
> supplier could ACK a live dispatch. The cited fact (`:399` precedes the send) is true in
> isolation but does not produce the claimed race.
>
> The membership decision is unchanged — `ready_to_deliver` stays in the set — on three
> reasons that do verify: (1) the behaviour is pre-existing and pinned by the test above;
> (2) `OrderStatusTransitionObserver`'s `[ReadyToDeliver]` comment already declares the
> intent; (3) there is a real path back to `ready_to_deliver` *after* a genuine dispatch — an
> MV-1 mapping edit resets a `delivered`/`delivery_failed` order to `ready`
> (`OrderMappingOverrideService.cs:87`), the next Send re-transforms it to `ready_to_deliver`,
> and a late ACK for the original dispatch can land while it sits there.

**Not intended.** The observer map lists **no** path from `pending_parse` / `parsing` /
`unrouted` / `pending_review` / `ready` / `transforming` to `delivered`. Those already log a
WARNING today. The author's own map declares them unintended.

**Conclusion.** The from-state gap (D1) is a bug. The five late-ACK edges are real flows,
mis-labelled as "reachable, but only through a gap".

## Decisions (founder, 2026-07-16)

1. **Scope:** fix D1 + D2 together. Same endpoint, same method, same tests; splitting means
   touching the file twice and leaving the P1 re-send live meanwhile.
2. **`rejected_by_supplier` is terminal for webhooks.** A supplier that rejected must not
   silently flip the order to `delivered` — a human has likely already acted on the
   rejection. A genuine retraction becomes an operator re-drive, not an automatic write.
   Keeps `IsTerminal_TrueForTerminalStates` intact.
3. **`delivery_held` is webhook-writable.** `delivery_failed → delivery_held` is a real edge
   (A5), so a held order may already have been sent. Rejecting its late ACK would mean the
   release re-drive re-sends it — a duplicate. The never-sent-held case is protected by Guid
   unguessability.
4. **Drop the `!= delivered` guard for rejections** (D3). `delivered → rejected_by_supplier`
   already exists in both maps and is the flow the north star demands.

## Design

### 1. Canonical guard set

New set in `OrderStatusMachine`, beside the existing `RedeliverableFrom`, so the controller
references one canonical set instead of a hand-written literal:

```csharp
/// <summary>
/// A supplier status callback (WebhookIngressController.Status) may report a terminal
/// outcome only for an order that was genuinely dispatched …
/// </summary>
public static readonly IReadOnlySet<string> WebhookReportableFrom =
    Set(ReadyToDeliver, Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, DeliveryHeld);
```

`rejected_by_supplier` is deliberately absent (decision 2).

### 2. Controller logic

Replaces the two `if`s:

```
target = status switch { "delivered" => Delivered, "rejected" => RejectedBySupplier, _ => null }

if target is not null:
    if order.Status == target                      -> 200, audit only      (idempotent replay)
    elif !WebhookReportableFrom.Contains(status)   -> 409, audit rejected  (integration error)
    else                                           -> write target, 200
```

- **The equality short-circuit is load-bearing.** Without it, a supplier re-posting the same
  rejection would 409 on work that already succeeded, because `rejected_by_supplier` is not
  in the from-set. Callback endpoints get retried; idempotent replay must stay a 200.
- **`received` / `in_progress` stay unguarded.** They mutate nothing; guarding them would add
  noise without preventing harm. Narrowest change.
- **409 Conflict**, not 400/422: the request is well-formed and authentic — it conflicts with
  the resource's current state. Well-behaved clients treat 4xx as permanent and stop
  retrying, which is what we want for a genuine integration error.

### 3. Rejected callbacks are audited

A 409 that nobody can see is a silent ignore with extra steps. Rejected callbacks write an
`AuditEvent` with action **`webhook_status_rejected`** (distinct from the happy-path
`webhook_status`, so ops can filter/alert on it) carrying the reported status, the order's
actual status at receipt, and the occurredAt. The endpoint is rate-limited per tenant slug,
so the write is bounded.

### 4. Map bookkeeping

**Promote to `OrderStatusMachine.Transitions` as real flows:**

| Edge | Why |
|---|---|
| `ready_to_deliver → delivered` | webhook late/racing ACK; already in observer + pinned by test |
| `delivery_failed → delivered` | late positive ACK |
| `delivery_dead_letter → delivered` | late positive ACK |
| `delivery_held → delivered` | newly reachable under decision 3; **also add to the observer** |

**Remove from `OrderStatusTransitionObserver.AllowedTransitions`** (now unreachable — the
guard forbids them, so the observer must warn if any future path performs them):

- `rejected_by_supplier → delivered`
- `rejected_by_supplier → delivery_failed`

`Transitions[RejectedBySupplier]` stays `Set()` — terminal, as `IsTerminal_TrueForTerminalStates`
asserts.

## Testing (TDD — RED first)

`WebhookIngressControllerTests` (Api.Tests):

1. RED: `Status_DeliveredCallbackForPendingParseOrder_Returns409_AndDoesNotMutate`
2. RED: `Status_DeliveredCallbackForRejectedBySupplierOrder_Returns409_AndDoesNotMutate`
3. RED: `Status_RejectedCallback_WritesRejectedBySupplier_NotDeliveryFailed` (D2)
4. RED: `Status_RejectedCallbackForDeliveredOrder_WritesRejectedBySupplier` (D3)
5. RED: `Status_DuplicateRejectedCallback_IsIdempotent200_NoConflict`
6. RED: `Status_RejectedCallback_For409Path_WritesWebhookStatusRejectedAudit`
7. GREEN-preserving: `ready_to_deliver → delivered` still 200 (existing test, must not break)
8. Theory over each allowed from-state → `delivered` returns 200 and mutates
9. Theory over each forbidden from-state → 409 and no mutation
10. `received`/`in_progress` from `pending_parse` → 200, no mutation, no 409

`OrderStatusMachineTests` (Infrastructure.Tests):

11. `WebhookReportableFrom_MatchesTheDocumentedSet` (mirrors the `RedeliverableFrom` pin)
12. Add the four promoted edges to `IsAllowed_RealTransitions_AreAllowed`
13. Pin `rejected_by_supplier → delivered` in `IsAllowed_ImpossibleTransitions_AreRejected`
14. `IsTerminal_TrueForTerminalStates` must still pass unchanged

Build: `dotnet build ProcuLink.slnx`. Suites: Api.Tests + Infrastructure.Tests.

## Coordination hazard

`KnownObserverOnlyEdges` and the two-sided superset invariant
(`Machine_Transitions_AreASupersetOf_ObserverAllowedTransitions`) **do not exist on main**.
They live only on the unmerged, diverged branch `claude/confident-elbakyan-e26059` (dff05af),
whose commit message states the five edges *"read as a missing guard rather than an intended
flow, so the machine keeps calling them impossible pending a decision"*. This spec is that
decision, and it lands differently than that branch assumed: three of the five edges are real
and get promoted; two become unreachable and leave the observer entirely.

**Sequencing:** this branch bases on `main` and does not block on dff05af (self-described
"blast radius is zero … documentation-grade"; this is a P1 re-send bug). Whoever merges
second rebases. On rebase, all five webhook entries in `KnownObserverOnlyEdges` become stale
and that branch's two-sided assertion will fail loudly and name them — which is precisely its
design intent. Its other eleven exemptions (`Failed→*`, `TransformFailed→*`, `PendingParse→*`,
`PendingReview→Failed`, `Ready→Failed`) are untouched by this work.

**Action:** notify session `confident-elbakyan-e26059` on merge.

## Out of scope

- `IsAllowed` still has no production callers; this spec does not wire it in as a hard guard.
- The `AllowedStatuses` vocabulary (`received`/`in_progress`/`rejected`/`delivered`) is unchanged.
- The observer's other documented-but-dead edges (`Failed→*`, `TransformFailed→*`).
