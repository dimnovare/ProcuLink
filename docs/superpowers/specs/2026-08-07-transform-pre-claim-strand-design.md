# Design — an unexpected failure early in `TransformAsync` strands the order invisibly

**Date:** 2026-08-07
**Branch:** `claude/clever-pare-f27b55`
**Status:** approved, ready for an implementation plan

---

## The defect

`OrderTransformService.TransformAsync` begins at `ProcuLink.Api/Services/Orders/OrderTransformService.cs:93`.
Its first `try` block is at line 455. Everything between those two lines runs with no exception
handling at all, and three of the statements in that region also return `Result.Fail` without ever
writing a status.

`FailTransformAsync` (line 780) is the only writer of `OrderStatusConstants.TransformFailed` and of
the `TransformFailed` audit event. It is reachable only from the catch at line 627, so nothing that
escapes before line 455 can reach it.

Both escape shapes end in the same place:

- **A thrown exception** propagates out of `TransformAsync`, through `TransformOrderJob`, into
  Hangfire, which retries and then permanently fails the job.
- **A `Result.Fail` return** is converted into a throw by `ProcuLink.Api/Jobs/TransformOrderJob.cs:71`
  (`throw new InvalidOperationException($"Transform failed: {result.Error}")`), and lands in exactly
  the same Hangfire retry-then-fail.

In both cases the order is left at `transforming`. `OrdersController.Transform` already flipped it
`ready → transforming` before enqueueing the job (`ProcuLink.Api/Controllers/OrdersController.cs:1727`),
so the order genuinely sits in that status from the moment `TransformAsync` is entered.

`ProcuLink.Infrastructure/Services/StuckOrderDetectionService.cs:143-187` then recovers a
`transforming` strand to `ready`, deliberately and explicitly never marking it failed.

**Net effect:** an order that hit a real, repeatable error is silently cycled back to `ready`. No
`transform_failed` status, no error message, no ops-health count, nothing an operator can see or act
on. It looks like nothing happened.

---

## What can actually escape

Audit of lines 99-454 as they stand on `main`:

| Lines | Operation | Escapes? | Nature |
|---|---|---|---|
| 100-105 | Load the order (`+Lines/Supplier/SourceCapture`) | **throws** | DB — transient |
| 107-108 | Order not found → `Result.Fail` | **returns Fail** | no row exists to mark |
| 111-114 | Unresolved lines → `Result.Fail` | **returns Fail** | order-level failure |
| 120-122 | `_effectiveConfig.ResolveAsync` | no | catch-all inside `EffectiveConnectionConfigResolver.ResolveAsync`; returns `Live`, rethrows cancellation |
| 127-144 | Effective output format resolution | no | `Enum.TryParse`, pure |
| 152-154 | `_cxmlResolver.ResolveAsync` | **throws** | DB read + `_encryption.Decrypt` (`ProcuLink.Infrastructure/Services/CxmlCredentialResolver.cs:54`) |
| 175 | `OrderMappingOverrideReader.Read` | **throws** | catches only `JsonException`; any other deserializer failure escapes |
| 187-226 | Precedence predicates | no | pure |
| 214 | `TryBuildRevisionOutputOverride` | no | both halves catch-all (`OrderTransformService.cs:960`, `:1036`) |
| 217 | `TryReadSupplierPromotedOutputAsync` | no | catch-all (`:1099`) |
| 259-283 | `TreeDrivesTheDocument` | no | pure + log |
| 290-292 | Transformer lookup → `Result.Fail` | **returns Fail** | order-level failure |
| 316-328 | Envelope resolution | no | pure predicates |
| 355-381 | The atomic claim (`ExecuteUpdateAsync` / `SaveChangesAsync`) | **throws** | DB — transient |
| 391-395 | `claimed == 0` existing-artifact read | **throws** | DB — transient |
| 412-419 | Change-tracker sync | no | assignment |

Two things this audit establishes:

1. The mapping-read helpers are **already** defended, each behind its own catch-all. The comment at
   `OrderTransformService.cs:960-965` names this exact hazard in so many words: *"anything that
   escapes here escapes TransformAsync itself, BEFORE the status claim, leaving no transform_failed
   row and no exception row while Hangfire retries forever."* The narrow fixes were applied one call
   at a time; the gaps between them were never closed. That is why the hole is still open.
2. The defect is not exclusively about exceptions. Two of the three `Result.Fail` returns —
   unresolved lines and no registered transformer — are genuine order-level failures with the same
   invisible outcome.

---

## Principle

> `TransformAsync` must not return while the order sits in `transforming` — unless the process died.

Everything below follows from that one sentence.

---

## Component 1 — `FailTransformFromClaimableAsync`, a status-guarded failure write

A new private helper alongside `FailTransformAsync`. It moves the order to `transform_failed`
**only from** `OrderStatusMachine.ClaimableForTransformFrom`
(`{ready, transforming, transform_failed, rejected_by_supplier}`, `ProcuLink.Core/Constants/OrderStatusMachine.cs:627`).
The audit event and the exception reconcile are written **only if the guarded update won the row**.

- Relational path: `ExecuteUpdateAsync` filtered on `Id`, `OrgId`, and
  `claimableStatuses.Contains(x.Status)` — the same shape as the claim at lines 361-368.
- InMemory path: the same status test through the change tracker, mirroring lines 370-381.
- On a win it also syncs the tracked entity's `Status` and its `OriginalValue`, for the same reason
  lines 412-419 do: an `ExecuteUpdateAsync` bypasses the change tracker, and a later write diffed
  against a stale original is silently dropped.
- It keys on `(organisationId, orderId)` for both the update and the audit row. The tracked entity
  is a separate, **nullable** parameter used only for the post-win tracker sync — the entity load
  itself is inside the protected region, so `entity` is null whenever the load is what failed, and
  the helper must do its whole job without one.

**Why guarded rather than reusing `FailTransformAsync`.** Before the claim we do not own the row. A
blind write would stomp a concurrent `billing_held` park or an `OrderMappingOverrideService` MV-1
`pending_review` reset — writing `transform_failed` over an explicit hold. The guard set is the
claim's own set, which makes the rule a single sentence: *if we could have claimed it, we may fail
it.* No new status set is introduced.

**Why the post-claim callers keep the existing unguarded `FailTransformAsync`.** They own the row,
and their write shares its `SaveChanges` with audit events already queued on the change tracker —
notably the `AcceptanceGate` override-used event at line 513, whose comment states it is *"committed
by whichever SaveChanges finishes this transform"*. Changing that write shape risks losing that row.
The four existing call sites stay byte-identical.

---

## Component 2 — one `try` spanning lines 100-419, with a broad catch

```csharp
catch (OperationCanceledException) { throw; }   // mirrors :459 — a cancelled request is not a failure
catch (Exception ex) { /* log; guarded fail; return Result.Fail(plain sentence) */ }
```

The catch is broad rather than per-operation, deliberately:

- **The defect is regional, not per-call.** Per-operation catches protect only today's statements.
  The next line added to this 250-line region reopens the hole — which is precisely the history
  here: the `TryRead*` helpers were each fixed narrowly and the gaps between them stayed open.
- **The transient→terminal hazard (the warning at `:222`) is answered by what `transform_failed`
  costs, not by narrowing the catch.** `transform_failed` is in `ClaimableForTransformFrom`,
  `TransformOrderJob` carries `[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 10, 60, 300 })]`,
  and the endpoint's `TransformableFrom` admits it. So a transient DB blip becomes: a visible
  `transform_failed` row → a Hangfire retry in 10 s → a successful re-claim → the transform
  completes. Self-healing, and visible in the meantime rather than invisible forever.
- **This exact trade-off is already made, and argued at length, eight lines below.** The acceptance
  gate's catch at `:444-453` reasons identically: *"That is recoverable by construction:
  transform_failed is re-claimable, and TransformOrderJob's own AutomaticRetry re-drives it, so a
  transient lookup failure heals itself on the next run."* Making the same call twice with two
  different shapes is how the five delivery-claim lists drifted apart four times.
- **The filters at 552/587/610 are not a counter-example.** Their
  `when (ex is not TransformValidationException and not TransformTemplateException)` exists to let a
  *more specific* failure type pass through to the outer handler at line 627 — it is not a
  transient-vs-terminal discriminator. There is no outer handler above line 455, so there is nothing
  for a filter to pass to.

Returning `Result.Fail` (rather than rethrowing) is what the acceptance-gate catch already does, and
`TransformOrderJob.cs:71` converts it into the Hangfire retry regardless — so the retry behaviour is
identical either way, and the shape stays consistent with the handler below it.

**The region deliberately includes the claim itself (355-381) and the `claimed == 0` branch
(383-406).** The interesting case is a throw from the existing-artifact read at 391-395: `claimed`
was 0, so we do *not* own the row — some other runner does, or the transform is already complete.
The guard resolves this without a special case. An order in that situation is at
`ready_to_deliver`, `delivering`, `delivered`, or `delivery_failed`, none of which are in
`ClaimableForTransformFrom`, so the guarded write refuses and the status is left alone. The two
components compose into the correct answer with no branch that has to know about it.

---

## Component 3 — the two real `Result.Fail` returns route through the guarded helper

| Line | Case | Change |
|---|---|---|
| 108 | Order not found | **unchanged.** There is no row to mark. |
| 113-114 | `Resolve all lines before transforming. Unresolved: …` | guarded fail, message verbatim |
| 292 | `No transform service registered for format '…'` | guarded fail, message verbatim |

Both messages are already user-facing and precise, so they are passed through unaltered rather than
replaced with a generic sentence.

The accepted cost: an order with unresolved lines now moves out of `ready` into `transform_failed`
and appears in the exceptions list, where previously it sat silently. This is the intended trade —
the strand it replaces is invisible, and `transform_failed` keeps the retry door open
(`ClaimableForTransformFrom` and `TransformableFrom` both admit it).

The comment at `OrderTransformService.cs:285-289` — *"resolved up-front so a missing transformer
fails before status mutation"* — states the assumption this change overturns, and must be corrected
in the same edit. "Fails before status mutation" is exactly what makes the failure invisible.

---

## Component 4 — `StuckOrderDetectionService`: comment only, no behaviour change

Its rationale at `StuckOrderDetectionService.cs:147-150` reads:

> *"a transform job that actually RAN and failed reverts ITSELF to 'ready' (+ Hangfire
> AutomaticRetry) — so a strand this sweep still sees only ever means 'claimed but no job ran'"*

That premise is stale in two independent ways. Since `transform_failed` shipped, a transform job
that ran and failed lands in `transform_failed`, not `ready`. And it never accounted for the
pre-claim escape at all — which is the defect this design fixes.

After the fix the premise becomes true again, in a stronger form, and the two cases the brief asks
to distinguish are separated **by construction** rather than by a new signal:

| Case | Resulting status | Who handles it |
|---|---|---|
| Job ran and failed (thrown or refused) | `transform_failed` | ops health, exception row, operator retry door |
| Claimed but no job ever ran | `transforming` | the sweep — recover to `ready` |
| Process killed mid-transform | `transforming` | the sweep — recover to `ready`, **and that is correct** |

The third row is why the sweep's never-fail rule survives unchanged: a killed process is a transient
infrastructure fault, not an order-level one, so recovering it to `ready` remains the right answer.
The rule is not weakened; its premise is simply restated accurately.

**Consequences:** no new order status, no new column, no new sweep signal, no migration.
`ProcuLink.Core/Constants/OrderStatusConstants.cs` and the frontend's
`src/lib/orderStatusManifest.ts` are untouched. Only the stale comment is corrected, so the next
reader does not build on a false premise.

---

## Error message

The audit event's `error` payload key **is** the user-visible `errorMessage` — `OrdersController`
reads it to populate the order's error string (documented at `OrderTransformService.cs:767-769`).
So the unexpected-exception path writes one plain sentence:

> "Something went wrong preparing this order to send, so it wasn't sent. Try sending it again in a
> moment."

The exception itself goes to `LogError` and nowhere else. `Npgsql.PostgresException: 57P01` is not a
sentence an operator can act on, and the repo's plain-language rule forbids putting it in the UI.

The two `Result.Fail` reroutes keep their own precise sentences, which are already written for users.

---

## Testing

New file: `ProcuLink.Api.Tests/Services/Orders/TransformPreClaimFailureTests.cs`.

| # | Scenario | Asserts |
|---|---|---|
| 1 | `ICxmlCredentialResolver` throws (a real pre-line-455 seam, cXML format) | status `transform_failed`, a `TransformFailed` audit row exists, `Result` is `Fail` |
| 2 | Same throw, order at `delivered` (not claimable) | status **unchanged**, **no** `TransformFailed` audit row |
| 3 | Resolver throws `OperationCanceledException` | exception propagates; status unchanged |
| 4 | No transformer registered for the effective format | status `transform_failed`, audit row present |
| 5 | Order has unresolved lines | status `transform_failed`, audit `error` is the exact unresolved-lines sentence |

Every test asserts the audit row (or, for #2, its absence) **independently of** the status string. A
status-only assertion placed first would short-circuit the proof, and a mutation run reports only
the first failure — so the evidence assertion must not sit behind a status check.

**Mutation checks**, each restored by editing the file back (never `git checkout`):

| Mutation | Must fail |
|---|---|
| Delete the `catch (Exception ex)` body / the whole `try` | 1, 4, 5 |
| Replace `claimableStatuses.Contains(x.Status)` in the helper with an unconditional update | 2 |
| Remove the `catch (OperationCanceledException) { throw; }` | 3 |

Then the full suite: `dotnet test ProcuLink.slnx --configuration Release`.

---

## Files touched

| File | Change |
|---|---|
| `ProcuLink.Api/Services/Orders/OrderTransformService.cs` | `try` over 100-419; new `FailTransformFromClaimableAsync`; two `Result.Fail` reroutes; correct the `:285-289` comment |
| `ProcuLink.Infrastructure/Services/StuckOrderDetectionService.cs` | comment correction at `:145-154` only |
| `ProcuLink.Api.Tests/Services/Orders/TransformPreClaimFailureTests.cs` | new |

## Repo constraints honoured

- EF Core only, no raw SQL.
- Every query and every write scoped on `OrgId`.
- The Hangfire job stays idempotent: the guarded helper is a no-op on a repeat, because a second
  pass finds the order already in `transform_failed`, writes it again from a claimable status, and
  produces no artifact and no delivery either way.
- No new order status, so the backend/frontend status mirror needs no change.
- `transform_failed` is reused: already counted by `OpsHealthService`
  (`ProcuLink.Infrastructure/Services/OpsHealthService.cs:73`) and already accepted as the operator
  retry door by `OrdersController`.

## Explicitly out of scope

- Restructuring `TransformAsync` to move the claim earlier. It would protect the same region, but it
  changes the `claimed == 0` short-circuit's `effectiveFormat` fallback and reorders a heavily
  documented method for no additional coverage.
- Any change to `StuckOrderDetectionService`'s behaviour, its thresholds, or `MaxRequeues`.
- The `feat/credential-aad-binding` work itself. That branch introduces
  `CredentialUnbindableException` and its own catch at the line-154 resolver call; this design's
  regional `try` sits outside and above it, so the two compose without conflict — the narrow catch
  produces its specific message, and the broad one catches whatever the narrow one does not.
