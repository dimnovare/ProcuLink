# Spec — reset the delivery attempt cap WITHOUT erasing the dispatch evidence

Status: **SPEC ONLY. Cut is held** — #31 has landed; still waiting on #27 and on
`distracted-edison`'s canonicalisation mechanism (itself blocked on #27). Audit session's sequencing.
Ruling: option **B** (fix the premise, not the symptom) — audit session, 2026-07-17.
Branch base: main @ `91b489f` (#31 merged).

## Preamble — read this before trusting anything below, including this spec

This subsystem has produced 9+ false justifications in two days; several are mine. The class is not
"people write sloppy comments". It is: **a comment that DESCRIBES is inert, but a comment that
JUSTIFIES is a proof obligation** — and when it drifts it does not merely mislead, it *licenses* the
next person to do the wrong thing. Every Critical here was an undischarged "because".

**The class does not respect authorship, expertise, or intent.** Three data points, all from the
people best positioned to resist it:

1. This document contained **two false internal claims within an hour of being written** — a status
   line saying "pending #31" after #31 landed, and a cross-reference to an "Open Question" already
   ruled on — written by the person who had just spent a day cataloguing the class.
2. PR #32 shipped a comment claiming *"EF cannot translate a captured Expression into the claim's
   correlated subquery"*, which **licensed shipping two copies of a security-relevant predicate**. It
   was false (PR #34), and it was written *while cleaning up five other instances of the same class*.
3. That one came from an **error message** — the one kind of "because" that arrives pre-authorised
   from a third party. `.Compile()`'s failure text contains the word "Invoke", so it reads as "Invoke
   does not translate" and means "your Invoke target is a delegate". **An error message tells you WHAT
   failed, not WHY; the WHY it implies is inference.** The compiler is not a witness to its own cause.

Practical consequence for this spec: **every claim below was grepped, not remembered**, and the ones
that decide behaviour say so with a file:line. Where a justification only becomes true once narrowed,
that narrowing is written down rather than smoothed over — a special case whose rule does not factor
is usually a defect, which is exactly why option B beat option A.

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
cap — it must be decided explicitly (see **Ruling 1 × site 4** below), not swept along by accident.
Ruling 1 makes the two questions diverge, so site 4 stops sharing the cap's predicate.

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

**Both predicates carry a do-not-unify comment, CITING rather than restating each other** (the #31
ruling's own rule — a restated predicate is a second copy that drifts). The webhook guard's evidence
check must never mention `CapSupersededAt`; if someone "simplifies" the two into one, the erasure
hole reopens under a new name. The comment exists to make that refactor stop and read.

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
7. **ASSERT-THE-DIFFERENCE (mandatory).** The do-not-unify comment is necessary but NOT sufficient —
   it relies on a reviewer reading it, and this cluster's entire evidence base is that they do not.
   The assertion makes unification **fail the build**:

   > seed the post-requeue row (marker present, `CapSupersededAt` set) and assert the two
   > discriminators **DISAGREE** — evidence says "a send was begun"; the cap says "does not count
   > against the current budget". Both correct. Collapsing them turns it red.

   Same move as `DispatchAndRetryClaimSets_DifferExactlyBy_DeliveryUnconfirmed`. Keep the comment for
   the human deciding WHETHER to touch it; the assertion is what stops them.
8. **Do NOT collapse `StrandedFailedDeliveryDetectionService:62` into `!CountsAgainstCap`.** It reads
   `a.Status == StatusDispatching` — "a send is IN FLIGHT" — a different business concept that merely
   looks like the negation. Collapsing it compiles, returns an int, and passes every test. Leave it a
   literal with a comment saying why it is not the inverse.

   This is the **third** instance of one shape in this subsystem (the four claim lists, the five cap
   sites, and this). The pattern is not "people duplicate things" — it is **people write the same
   words for different reasons, and the reasons drift apart silently.** Site 4 is the fourth.

## RULED (audit session, 2026-07-17) — both were open questions; neither was guessed

### Ruling 1 — `AttemptNumber` ASCENDS. Do not preserve the restart.

Today's restart is an **accident of the delete**, not a decision. Ascending is the truthful option:
if we hit a supplier four times, "attempt 4" is true and "attempt 1" is a lie — in
**supplier-visible provenance**, the last place a number should lie.

**Condition attached to the ruling: prove no consumer assumes 1-based-per-epoch. VERIFIED — and the
result strengthens the ruling rather than merely permitting it.**

| consumer | use | ascending-safe? |
|---|---|---|
| `PassportDto:152` / `PassportService:181` | display | yes |
| `DeliveriesController:44,:59` | display DTO | yes |
| `OpsController:182` | `.OrderBy(a => a.AttemptNumber)` | **yes — and see below** |
| `DeliveryService:501` | test-fire sentinel `AttemptNumber = 0` | unaffected (`OrderId` is null; never in an order's sequence) |

No `AttemptNumber == 1` exists anywhere in the codebase.

**The ordering site makes ascending REQUIRED, not merely preferable.** `OpsController:182` orders
prior attempts by `AttemptNumber`. Today the delete keeps the surviving set at 1..N, so that is a
total order. Once rows SURVIVE, a restarting number would interleave generations — `1,1,2,2,3` — and
`OrderBy(AttemptNumber)` silently stops being a total order. So the alternative to this ruling would
have broken an existing consumer. The ruling is load-bearing, not cosmetic.

### Ruling 1 × site 4 — the numbering predicate must DECOUPLE from the cap predicate

Sites 1-3 and site 4 share the predicate `a.Status != StatusDispatching` **today only because the two
questions happen to have the same answer**. Ruling 1 makes them diverge:

- the **cap** counts attempts in the CURRENT budget → must respect `CapSupersededAt`.
- the **numbering** counts attempts EVER → must ignore `CapSupersededAt`, or numbers restart, which
  is exactly what ruling 1 forbids.

So site 4 must stop deriving from the cap predicate **in the same change**, or the two drift the
instant they diverge — and the drift is silent, because both compile and both return an int. Site 4
derives from the same countable-set-EVER question the **evidence** predicate asks, not from the cap.

### Ruling 2 — retention: say the caveat in code, do not claim the hole is shut

B shrinks the guard's caveat to **retention-only**; it does not remove it. `DataRetentionService`
prunes attempt rows for terminal statuses (disabled by default, 180d). It cannot produce THIS P1 —
`delivery_failed` is not in `TerminalOrderStatuses` — but it can still erase evidence from a
`delivered` / `rejected_by_supplier` order.

`RefusalReason.NeverDispatched` must therefore keep an erasure caveat naming retention, with the ops
requeue REMOVED from it once B lands. Claiming the hole fully closed would be this cluster's defect
one more time.

## New finding — B makes `OpsController`'s attempt-archive justification FALSE

`OpsController:178-182` archives the prior attempts into the audit log before deleting them, and its
comment justifies that: *"preserve their dead-letter evidence … rather than losing it"*.

Under B **we no longer lose it** — the rows survive. So that justification becomes false the moment B
lands. This is the cluster's defect class arriving pre-emptively: a comment that will license the next
reader to believe the archive is load-bearing when it is redundant.

### RULED: drop the row-copy, keep the audit EVENT — and the justification must name its own
### invalidation condition

The audit event stays: *an operator requeued this* is not recoverable from the rows. The row-copy
goes: under B the rows survive, so it is pure redundancy.

**But "the rows are now the record" is too flat to ship, and the narrowing is the tell** (this
spec's own corollary: if a justification only becomes true once narrowed, look harder at it).
Verified — not assumed — because this decision DELETES an evidence copy on a money path:

| fact | site |
|---|---|
| `AuditEventDays = 180` | `DataRetentionOptions.cs:21` |
| `DeliveryAttemptDays = 180` | `DataRetentionOptions.cs:30` |
| both cutoffs computed and applied | `DataRetentionService.cs:66`, `:69` |
| the whole sweep is `Enabled = false` by default, and the service honours it | `DataRetentionOptions.cs:18`, `DataRetentionService.cs:55` |

So the copy does **not** outlive the rows — but **the two windows are INDEPENDENTLY CONFIGURABLE**.
Set `DeliveryAttemptDays` below `AuditEventDays` and the rows die first, at which point the copy WOULD
have been the record and dropping it was wrong.

**Write the dependency and its invalidation condition, not the flat claim:**

> The rows are the record because `AuditEventDays` and `DeliveryAttemptDays` both default to 180 and
> are pruned by the same sweep. If `DeliveryAttemptDays` is ever configured BELOW `AuditEventDays`,
> the attempt rows die first and this decision must be revisited.

A justification with no invalidation condition is the same shrug the `KNOWN_GAP` was not allowed to
be.

## Coordination

`claude/distracted-edison-698760` (session `local_f22cd278`) owns "one canonical predicate, every gate
derives from it" for the delivery CLAIM set, and has already catalogued the traps this will hit
(Npgsql translation of a captured `Expression` inside a correlated subquery; the empty-set
`= ANY('{}')` → 0 rows → reads as "someone else claimed it" hazard; the relational/InMemory
equivalence matrix). The cap is the same class of multi-site predicate. **Their canonicalisation
mechanism, not a second one next to it.**

## Sequencing

#31 has LANDED (`91b489f`). Still held for #27 (the park — CONFLICTING, rebasing, heavy on
`DeliveryService`), which moves the retry/claim lines this changes; landing cap semantics into it
concurrently is how a third compile-clean conflict happens on a money path.

`claude/distracted-edison-698760` is itself blocked on #27, so their canonicalisation mechanism lands
after it. Order: #27 -> their mechanism -> B derives from it.
