# Spec — reset the delivery attempt cap WITHOUT erasing the dispatch evidence

Status: **SPEC ONLY. Cut is held** pending #31 and #27 landing (per the audit session's sequencing).
Ruling: option **B** (fix the premise, not the symptom) — audit session, 2026-07-17.
Branch base: main @ `52063a3`.

## The premise we are fixing

`WebhookIngressController` refuses a supplier callback it cannot prove was dispatched, where "proof"
is a per-row marker (`IdempotencyKey != null OR ArtifactSha256 != null`, PR #32). Its soundness rests
on:

> no marker on any row ⇒ this order was never dispatched

That is **not true today**, and ops punches the hole: `OpsController`'s requeue hard-deletes ALL of an
order's `DeliveryAttempt` rows (`RemoveRange(priorAttempts)`) purely to reset the attempt cap. A
genuinely-dispatched order can therefore present as never-dispatched.

**The harm that makes it worth fixing rather than documenting** (PR #33's `KNOWN_GAP` test):
refusing a `rejected` callback writes nothing, so the order stays `delivery_failed` — which is
exactly what `StrandedFailedDeliveryDetectionService` sweeps. It re-drives, and **the PO the supplier
just rejected is sent a second time**. That sweeper justifies its own predicate on rejections landing
in `rejected_by_supplier`; a refused rejection breaks that premise.

Fixing it here makes `no marker ⇒ never dispatched` **true by construction**, so the guard stops
being sound-by-hope, the refusal becomes genuinely correct, and the `NeverDispatched` erasure caveat
disappears instead of being documented.

## Correction to the original proposal — VERIFY, DO NOT ASSUME

My recommendation to the audit session said *"`DeliveryRequeueCount` already exists, so the cap need
not be count-of-terminal-rows."* **That was wrong, and its condition-3 warning caught it.** Verified:

- `DeliveryRequeueCount` has exactly ONE writer (`StuckDeliveryDetectionService:82`) and ONE reader
  (`:79`, `< MaxRequeues`), with `MaxRequeues = 2`.
- The ops requeue never touches it.
- It means *"how many times the stuck-delivery sweep re-drove this order out of a stranded
  `delivering`"* — a **different budget** from the delivery attempt cap (`MaxAttempts = 3`).

Conscripting it would overload one concept with two budgets — the exact thing CLAUDE.md forbids
("keep new connection concepts first-class, no `CanonicalJson` overloading"). **This spec does not
touch `DeliveryRequeueCount`.**

## Condition 1 — the cap predicate is copied FIVE times, not four

The countable set is `a.Status != DeliveryAttempt.StatusDispatching`, restated at:

| # | site | shape |
|---|---|---|
| 1 | `DeliveryService.CountDeliveryAttemptsAsync:978` | method — used by `DeliverOrderJob:131`, `RetryDeliveryJob:103` |
| 2 | `DeliveryService:915` (`RetryDeliveryAsync`) | inline `CountAsync` |
| 3 | `StrandedFailedDeliveryDetectionService:60` | inline, inside a correlated subquery |
| 4 | `DeliveryService.OpenDispatchAttemptAsync:378` | inline — attempt NUMBERING, same predicate |
| 5 | `OpsController` requeue | "counts" by DELETING the rows |

This is the four-list drift wearing a different hat. Redefining the countable set at some sites and
not others yields a 0-row/no-match silent strand on a money path (the 52c6431 class).

**Requirement: ONE definition; every site derives from it.** Site 4 is attempt *numbering*, not the
cap — it must be decided explicitly (see Open Question 1), not swept along by accident.

## The two questions are DIFFERENT ON PURPOSE

Same table, two predicates, and they must not be unified — the same ruling #31 made for the two
dispatch discriminators:

| question | asked by | scope |
|---|---|---|
| **"was a send ever begun for this order, ever?"** | the webhook guard's evidence check | order-scoped, epoch-agnostic, artifact-agnostic — a supplier may legitimately report against ANY past dispatch |
| **"how many attempts count against the CURRENT budget?"** | the cap sites | order-scoped, epoch-**sensitive** — an operator requeue deliberately grants a fresh budget |

The evidence predicate must **ignore** whatever the cap uses to reset. If someone "simplifies" the
guard to filter by the cap's reset marker, the erasure hole reopens with a different name.

## Design

Keep every attempt row forever; make the cap count only the rows in the **current budget generation**.

**Recommended: a supersede marker on the attempt row.**

- `DeliveryAttempt.CapSupersededAt` (`DateTime?`, null default, additive migration).
- Ops requeue REPLACES `RemoveRange(priorAttempts)` with one `ExecuteUpdateAsync` setting
  `CapSupersededAt = now` on that order's rows where it is null. Rows — and their markers — survive.
- Countable set becomes `a.Status != StatusDispatching && a.CapSupersededAt == null`.
- The webhook guard's evidence predicate is UNCHANGED and explicitly does not mention
  `CapSupersededAt`.

Rejected alternative — an `int` epoch on the order + a stamped epoch per attempt: needs a correlated
column comparison in the sweeper's subquery and a stamp at every insert site (a 6th place to forget).
The nullable marker needs neither.

Naming: NOT `Superseded` (nothing supersedes the send — it happened). The row is excluded from the
CAP by an operator's requeue, and the name should say only that.

## Condition 2 — this BREAKS the stranded-failed sweep unless it lands together

`StrandedFailedDeliveryDetectionService:60-61` matches `Count(terminal) < maxAttempts`. Today the
requeue deletes the rows, so the count drops and the sweep can re-drive. **If the rows survive and
that site is not re-based, the sweep sees the old rows, reads `count >= cap`, and skips the requeued
order FOREVER — an operator clicks Requeue and nothing happens, silently.**

Same PR. Real-Postgres test: requeue → the sweep still re-drives.

## TDD plan (RED first, real Postgres — InMemory cannot translate `ExecuteUpdate` and is more permissive)

1. RED: a genuinely-dispatched order, ops-requeued, **keeps its markers**. (Today: rows deleted → fails.)
2. RED: a refused `rejected` callback on that order no longer re-drives — i.e. the PR #33 `KNOWN_GAP`
   test now FAILS and is replaced by its inverse. **The gap test failing is the acceptance signal.**
3. RED: requeue → `StrandedFailedDeliveryDetectionService` still re-drives (condition 2's regression).
4. RED: requeue resets the cap — an order at the cap becomes deliverable again, with rows intact.
5. GREEN: `RedeliverableStatusInvariantPostgresTests` stays green throughout (condition 5 — the net;
   red there is a silent strand, not a flake).
6. Equivalence: every cap site agrees after the change. Pin it; do not eyeball five call sites.

## Open questions (do NOT guess — these decide behaviour)

1. **Attempt NUMBERING (site 4):** should `AttemptNumber` restart at 1 after a requeue (it effectively
   does today, since the rows are gone) or keep ascending? Today's behaviour is an accident of the
   delete. Numbering is supplier-visible via provenance. **Ascending is my recommendation** — the
   numbers stop lying about how many times we actually hit the supplier — but it is a visible change.
2. **Retention** (`DataRetentionService`, prunes attempt rows for terminal statuses, disabled by
   default, 180d) is the SECOND erasure window. It does not affect `delivery_failed` (not in
   `TerminalOrderStatuses`), so it cannot produce the P1 — but it can still erase evidence from a
   `delivered`/`rejected_by_supplier` order. Out of scope here; the guard's caveat shrinks to
   retention-only rather than vanishing. Say so honestly; do not claim the hole is fully closed.

## Coordination

`claude/distracted-edison-698760` (session `local_f22cd278`) owns "one canonical predicate, every gate
derives from it" for the delivery CLAIM set, and has already catalogued the traps this will hit
(Npgsql translation of a captured `Expression` inside a correlated subquery; the empty-set
`= ANY('{}')` → 0 rows → reads as "someone else claimed it" hazard; the relational/InMemory
equivalence matrix). The cap is the same class of multi-site predicate. **Their canonicalisation
mechanism, not a second one next to it.**

## Sequencing

Held until #31 (`StrandedReadyOrderDetectionService` + `TransformOrderJob`) and #27 (the park, heavy
on `DeliveryService`) land. Both move the retry/claim lines this changes; landing cap semantics into
them concurrently is how a third compile-clean conflict happens on a money path.
