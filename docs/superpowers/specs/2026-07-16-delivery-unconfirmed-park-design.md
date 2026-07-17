# Design — park unknown-outcome delivery re-drives on non-idempotent channels

**Date:** 2026-07-16
**Status:** approved (founder), ready for implementation plan
**Origin:** delivery-idempotency review follow-up to A3 (`df2292f`); see
`docs/audit/2026-07-11-jobs-reliability-audit.md` A3.
**Priority:** hardening. Not urgent — no ERP/email clients at volume today.

## Problem

A process crash between the supplier accepting a delivery and the terminal
`DeliveryAttempt` committing leaves the order in `delivering` with no terminal
attempt. `StuckDeliveryDetectionService` re-drives it; `DeliveryService`
re-adopts the in-flight `dispatching` row (matched on the deterministic
idempotency key) and **re-sends**.

The per-channel idempotency key added in `df2292f` prevents a duplicate at the
supplier for:

- **SFTP / FTPS** — deterministic filename + overwrite. Genuinely idempotent.
- **HTTP** — `Idempotency-Key` + `X-Message-Id` headers, if the supplier honours them.

It does **not** for:

- **ERP** (`erp_erply`, `erp_directo`) — the key is intentionally unused; no dedupe
  signal reaches the endpoint. A re-drive posts a duplicate order.
- **Email** (Postmark, the canonical path) — only a deterministic `Message-ID`
  header. Receiving-MTA dedup on a caller-supplied `Message-ID` is best-effort and
  rarely applied. A re-drive likely sends a duplicate email.

This is not a regression (pre-`df2292f` had zero idempotency) and is consistent
with the "HTTP 200 ≠ business acceptance" posture, but it is a real duplicate-PO
risk on those two channels.

## Rejected options (and why)

### A local "delivered" marker keyed to the idempotency key — rejected, physically impossible

The originally-proposed fix was: persist a success marker under the deterministic
key before/atomically with the send, and on re-adopt check for a prior success
before re-sending.

This cannot work. The failure is a **process crash** — no transaction commits
after it. The marker would have to be written by the very transaction the crash
destroyed. Worse, the proposed check can never be true on this path: a *committed*
success moves the order to `delivered`, and `StuckDeliveryDetectionService` only
re-drives `delivering`. The local database cannot know the ACK happened. Only
evidence from **outside** the crashed process could narrow this window.

### Threading the key as an ERP order reference — rejected, no such surface

`erp_erply` and `erp_directo` are not vendor SDK integrations. Both are generic
HTTP posts to a tenant-configured URL:

- `ErplyConnector` posts raw artifact bytes + `X-ProcuLink-FileName` /
  `X-Erply-Client-Code` headers.
- `DirectoConnector` posts form-encoded `database` / `filename` / `contentType` /
  `xmldata`.

There is no ERP document model and no lookup API to thread a reference into or
query. "Threading the key" reduces to adding an `Idempotency-Key` header — the
same best-effort tier as email's `Message-ID`, not a guarantee.

### Probing the provider before re-sending — rejected for now

Only viable for email (Postmark Messages API search by metadata gives positive
evidence of a prior send). ERP endpoints expose no query surface, so ERP would
need the park regardless. One channel's worth of benefit for a live-Postmark
dependency and a new token scope. Revisit only if email volume makes it worth it.

### ERP `Idempotency-Key` headers — cut (YAGNI)

Would only help the operator-initiated "Send again" after a park, requires
changing `IErpConnector` / `ErpDeliveryRequest` plus both connectors and their
tests, and no known endpoint honours it. Adding a header nobody reads is the
offer⇔works noise the rule warns against.

### Parking HTTP too — rejected

Treating "the supplier may ignore the header" as unknown-outcome would make every
HTTP crash-recovery wait on a human — an automation regression on the most-used
channel, guarding a window the conventional header probably already covers. A
supplier integrating a webhook is the counterparty most likely to honour
`Idempotency-Key`.

## Decision

When the outcome of a send is genuinely unknown **and** the channel cannot
de-duplicate, do not guess. Do not re-send. Park the order and let a human decide.

This converts a silent automatic duplicate into an informed choice, needs no
external API dependency, and is testable without live ERP or Postmark credentials.

## Architecture

### 1. Channels declare their own re-send safety

`IDeliveryDispatcher` gains an abstract member:

```csharp
ResendSafety ResendSafety { get; }
```

```csharp
public enum ResendSafety
{
    /// Re-sending the same artifact cannot duplicate at the counterparty
    /// (deterministic overwrite). Re-drive freely.
    Safe,

    /// A dedupe signal is transmitted, but honouring it is the counterparty's
    /// choice (HTTP Idempotency-Key). Re-drive; residual documented.
    BestEffort,

    /// No dedupe signal reaches the counterparty. A re-send after an unknown
    /// outcome duplicates. Never re-drive without a human decision.
    Unsafe,
}
```

| Dispatcher | Tier |
|---|---|
| `SftpDeliveryDispatcher`, `FtpsDeliveryDispatcher` | `Safe` |
| `HttpDeliveryDispatcher` | `BestEffort` |
| `EmailApiDeliveryDispatcher`, `SmtpDeliveryDispatcher`, `ErplyDeliveryDispatcher`, `DirectoDeliveryDispatcher` | `Unsafe` |

The tier lives on the dispatcher because each channel is the only thing that knows
its own idempotency contract.

**Defaulted, not abstract** (revised 2026-07-16 during planning): the interface
supplies `ResendSafety ResendSafety => ResendSafety.Unsafe;`. The original argument
for an abstract member was "a compile error beats silent drift" (the A5 lesson from
`d4d6eac`) — but that was written before counting the implementers. There are 6
production dispatchers and **14 test doubles**; abstract means 14 files of churn to
buy a guarantee that the table test below already provides for the dispatchers that
matter. Decisively: here the default direction is **fail-safe**. A dispatcher that
forgets to declare its tier parks on crash recovery — conservative, never a
duplicate. The A5 drift was dangerous because the default was permissive; this one
is not.

Explicitness is enforced where it counts by a table test that names all six
production dispatchers and their expected tier, so a new production dispatcher
fails the suite until it is listed and considered.

`ErpDeliveryDispatcherBase` declares `Unsafe` once for both ERP dispatchers.

### 2. The decision point

`DeliveryService.OpenDispatchAttemptAsync` currently returns `DeliveryAttempt`.
It changes to return `(DeliveryAttempt Attempt, bool ReAdopted)`.

`ReAdopted == true` **is** the "we already sent this artifact, outcome unknown"
signal. No new detection is needed: the pre-send `dispatching` row committed by
A3 already carries exactly that meaning, and its absence means no send happened.

In the dispatch path, immediately after opening the attempt and **before** the send:

```
if (reAdopted && dispatcher.ResendSafety == ResendSafety.Unsafe)
    → no send occurs at all
    → in-flight row finalized: Status = DeliveryAttempt.StatusUnconfirmed
    → order.Status = OrderStatusConstants.DeliveryUnconfirmed
    → audit event "DeliveryUnconfirmed"
    → NO retry scheduled (no backoff, no dead-letter countdown)
    → return DeliveryResult(false, <the park sentence, below>)
```

Operator-facing copy is pinned here so it is not invented at implementation time
(plain-language rule: one human sentence, what happened + what to do):

> **Delivery unconfirmed.** We sent this order to {supplier} but lost the
> connection before they confirmed it, and {channel} cannot tell us whether it
> arrived. Check with {supplier}, then either send it again or mark it delivered.

The confirm dialogs must state the risk in the direction the operator is moving:

- Send again → "If {supplier} already received this order, sending again may give
  them a duplicate."
- Mark as delivered → "If {supplier} never received this order, marking it
  delivered means it will not be sent."

`Safe` and `BestEffort` re-send exactly as they do today — unchanged behaviour.
A **first** open (not re-adopted) on an `Unsafe` channel also sends normally: this
fires only on crash recovery, never on the common path.

#### The park must also leave the retry queue (found during planning)

Parking is not enough on its own. `DeliverOrderJob` and `RetryDeliveryJob` decide
whether to schedule an automatic backoff retry by branching on
`result.ResponseCode` — they bail out only for a 4xx supplier rejection. A parked
result is `Success = false` with a **null** `ResponseCode`, so it would fall
through to `ScheduleRetry`, and the retry loop would re-send the exact PO the park
just refused to re-send. Without this the feature is decorative.

`DeliveryResult` therefore gains `bool Parked = false` (defaulted, so every existing
call site compiles unchanged). Both jobs return early on it — placed **before** the
attempt-cap check too, so a parked order is never escalated to dead-letter by the
queue either. An ordinary transient failure keeps retrying exactly as before.

#### The park sentence must reach the operator (found during planning)

`GET /api/orders/{id}` populates `errorMessage` only for a hardcoded list of
statuses (`failed`, `transform_failed`, `delivery_failed`, `rejected_by_supplier`,
`delivery_dead_letter`). A parked order matches none, so the API would return
`errorMessage: null` and the operator would see an unfamiliar status with no
explanation and no guidance. `delivery_unconfirmed` is added to that gate and to
the attempt-message fallback — the branch that carries the park sentence, since
`ParkUnconfirmedAsync` writes it to `attempt.ErrorMessage`.

### 3. New terminal attempt status

`DeliveryAttempt.StatusUnconfirmed = "unconfirmed"` — terminal, alongside
`success` / `failed`.

It **counts** toward the attempt cap: it consumed a real send. This is free —
`CountDeliveryAttemptsAsync` and the retry cap count already exclude only
`dispatching`.

Every consumer of `DeliveryAttempt.Status` must be audited for the new value
(attempt DTO, the frontend attempt list, `OrdersController`'s error-message
fallback which reads the latest attempt).

### 4. New order status + transitions

`OrderStatusConstants.DeliveryUnconfirmed = "delivery_unconfirmed"`, added to the
`All` set.

Registered in **both** maps — `OrderStatusMachine.Transitions` and
`OrderStatusTransitionObserver.AllowedTransitions`. Registering in only one is the
exact drift `d4d6eac` had to fix; a test pins both.

- `[Delivering]` gains `DeliveryUnconfirmed`.
- `[DeliveryUnconfirmed] = Set(Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, Ready, RejectedBySupplier)`
  - `Delivering` — the operator's "Send again".
  - `Delivered` — the operator's "Mark as delivered".
  - `Ready` — MV-1 sibling: a mapping edit invalidates the stored artifact, so
    `OrderMappingOverrideService` must reset an unconfirmed order to `ready`
    (same as it does for `delivery_failed` / `delivery_dead_letter`).

`OrderStatusMachine.RedeliverableFrom` gains `DeliveryUnconfirmed`, so the existing
`POST /api/orders/{id}/redeliver` covers "Send again" with **no new backend code**.

### 5. New endpoint: mark as delivered

`POST /api/orders/{id}/mark-delivered` — valid **only** from
`delivery_unconfirmed` (400 otherwise), org-scoped like every other order route.

- sets `Status = Delivered`
- clears `DeliveryDueAt = null`, `SlaBreached = false` (mirrors the success path)
- audits `DeliveryConfirmedManually` with the acting user

The attempt row **stays** `unconfirmed`. We never fabricate a success we did not
observe; the operator's assertion is recorded as its own event, distinct from an
observed supplier ACK.

### 5B. Ops health counts the park (found during planning)

`OpsHealthSummary` gains `DeliveryUnconfirmed`, populated from the existing
`GROUP BY o.Status` (no extra round-trip) and included in `TotalProblemOrders`.

The Health page renders a green "All clear" banner from these counts, so without
this it would tell an operator everything is fine while a PO sits unsent waiting on
them. A parked order belongs in `TotalProblemOrders` rather than the informational
bucket: `PendingReview`/`PendingRouting` are excluded there because they are normal
workflow backlogs, whereas a park is a fault whose PO may never have arrived.

### 6. Billing — no code

Metering is a **query**, not a counter: billable = `delivered` ∨
`rejected_by_supplier` (`StripeBillingService.ApplyMeterStatusFilter`).

Therefore:

- `delivery_unconfirmed` is automatically **non-billable** — correct and
  conservative: never charge for a delivery we cannot confirm.
- "Mark as delivered" makes the order billable through the same query, with no
  metering call and no new revenue plumbing.

If the supplier did receive the order and the operator never marks it, we
under-bill by one order. Accepted — erring in the customer's favour.

### 7. Documentation (honest residual — offer⇔works)

- Help articles: `help/dashboard-and-statuses/page.mdx` (the end-user status
  glossary — a new row for the parked state) and `help/exceptions-and-stuck-orders/page.mdx`
  (the crash/unknown-outcome case and the "Send again" vs "Mark as delivered" choice).
- **`project-proculink/src/lib/api/connectors.ts`** — the per-channel idempotency
  caveat belongs on each `ConnectorManifest` entry (one per protocol: `http`,
  `sftp`, `ftps`, `smtp`/`email`, `erp_erply`, `erp_directo`).
  **Correction (2026-07-16, during planning):** an earlier draft of this spec named
  `src/lib/standards/catalog.ts`. That file is the wrong target — it documents
  *document-format* standards (cXML, UBL, Peppol, EDIFACT, X12…), and none of its
  entries describe delivery channels. `connectors.ts` is the per-channel catalog.
- `ErpDeliveryDispatcherBase`'s existing honest comment updated to point at the park.

### 8. Frontend (`project-proculink`, separate PR)

- `UnifiedStatusBadge` — new status, plain-language label "Delivery unconfirmed".
- Order workshop / inbox — two actions: "Send again" (existing redeliver mutation)
  and "Mark as delivered" (new), both behind the shared `useConfirm()` dialog.
  The dialog must state the risk **both ways**: sending again may deliver a
  duplicate; marking delivered may lose a PO the supplier never received.
- The health / filter surfaces that already enumerate `delivery_dead_letter`.

## Data flow

```
send crashes after supplier ACK, before terminal commit
  → order stuck 'delivering', 'dispatching' attempt row survives (committed pre-send)
  → StuckDeliveryDetectionService re-drives (minute-scale)
  → DeliveryService re-adopts the row on the idempotency key  [ReAdopted = true]
      ├─ Safe / BestEffort  → re-send (unchanged)
      └─ Unsafe             → NO SEND
                              attempt → 'unconfirmed'
                              order   → 'delivery_unconfirmed'   (non-billable)
                              audit DeliveryUnconfirmed
                                ├─ operator "Send again"        → redeliver → delivering
                                └─ operator "Mark as delivered" → delivered (billable)
                                                                  audit DeliveryConfirmedManually
```

## Error handling

- The park path itself must never throw into the job: a failure to persist the park
  leaves the order `delivering`, which the sweep re-drives — and re-parks. Safe.
- No auto-retry is scheduled for a parked order. It waits for a human by design.
- A parked order's SLA timer keeps running and may breach while waiting. **This is
  intended** — an unconfirmed delivery should nag. Flagged explicitly because it is
  a visible behaviour change.

## Testing (TDD, red before green)

- Table test: every registered `IDeliveryDispatcher` declares its expected tier.
  Catches a new dispatcher forgetting to think about it.
- `DeliveryService`, re-adopt + `Unsafe` → dispatcher mock **never called**; order
  `delivery_unconfirmed`; attempt `unconfirmed`; no retry scheduled.
- Re-adopt + `Safe` / `BestEffort` → still sends. Regression guard on today's behaviour.
- First open (not re-adopted) + `Unsafe` → sends normally. The common path must not park.
- `DeliveryCrashRecoveryPostgresTests` extended with an ERP/email config: stale
  `delivering` → sweep → parked, not re-sent (real Postgres).
- Both transition maps carry `delivery_unconfirmed`, plus the observer silence test.
- `mark-delivered`: org-scoped, wrong-status 400, order becomes billable afterwards
  (assert through the meter query, not a mock).
- Mapping edit after a park resets to `ready` (MV-1 sibling).

## Out of scope

- Postmark Messages API probing (revisit if email volume justifies it).
- ERP `Idempotency-Key` headers (cut above).
- Any change to `Safe` / `BestEffort` channel behaviour.

## Files (expected)

Backend (`ProcuLink`):
- `ProcuLink.Core/Services/Delivery/IDeliveryDispatcher.cs` — `ResendSafety` member + enum
- `ProcuLink.Core/Entities/DeliveryAttempt.cs` — `StatusUnconfirmed`
- `ProcuLink.Core/Constants/OrderStatusConstants.cs` — `DeliveryUnconfirmed`
- `ProcuLink.Core/Constants/OrderStatusMachine.cs` — transitions + `RedeliverableFrom`
- `ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs` — mirrored map
- `ProcuLink.Infrastructure/Services/DeliveryService.cs` — `ReAdopted` + park
- `ProcuLink.Infrastructure/Services/Dispatchers/*.cs` — six tier declarations
- `ProcuLink.Infrastructure/Services/OrderMappingOverrideService.cs` — MV-1 reset set
- `ProcuLink.Api/Controllers/OrdersController.cs` — `mark-delivered`
- tests per the testing section

Frontend (`project-proculink`, separate PR):
- `src/components/bridge/UnifiedStatusBadge.tsx`
- order workshop / inbox actions
- `src/lib/standards/catalog.ts`
- delivery help article

No database migration: both new statuses are string values in existing columns.
