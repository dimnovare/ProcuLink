# Supplier-routing matrix — handover, 2026-07-26

**Founder question:** does every incoming method route orders to the correct vendor?

**Answer:** yes, and it is now one table you can re-run.
`ProcuLink.Api.Tests/Integration/SupplierRoutingMatrixPostgresTests.cs` — 27 cells, real
Postgres (Testcontainers), **27/27 green**. Each cell drives the REAL producer for its
channel and asserts the created order's `SupplierId` + status.

## The matrix

| cell | channel | scenario | outcome | ✓ |
|---|---|---|---|---|
| 1a | manual upload | explicit `supplierId` (CSV) | routed | ✓ |
| 1b | manual upload | `supplierId = Guid.Empty` | 400, no order | ✓ |
| 1c | manual upload | explicit `supplierId` (XLSX) | routed | ✓ |
| 2a | REST ingress | `SupplierId` = GUID | routed | ✓ |
| 2b | REST ingress | `SupplierId` = name, wrong case | routed | ✓ |
| 2c | REST ingress | `SupplierId` = unknown name | 400, no order | ✓ |
| 2d | REST ingress | `SupplierId` = another org's supplier GUID | 400, no order | ✓ |
| 3a | inbound email | org default supplier set | routed **to the default** | ✓ |
| 3b | inbound email | no default, 2 suppliers | **parked `unrouted`** (200) | ✓ |
| 3c | inbound email | no supplier at all | parks `unrouted`, **200** | ✓ |
| 3d | inbound email | only a soft-deleted supplier | parks `unrouted`, 200 | ✓ |
| 3e | inbound email | prose-only body NLP, no supplier | parks `unrouted`, 200 | ✓ |
| 4a | SFTP pull | source default supplier (CSV) | routed | ✓ |
| 4b | SFTP pull | default supplier NULL | parks `unrouted` | ✓ |
| 4c | SFTP pull | default supplier soft-deleted | parks `unrouted` | ✓ |
| 4d | SFTP pull | default supplier NULL (XLSX) | parks `unrouted` | ✓ |
| 4e | S3 pull | source default supplier | routed | ✓ |
| 4f | S3 pull | default supplier NULL | parks `unrouted` | ✓ |
| 4g | S3 pull | default supplier soft-deleted | parks `unrouted` | ✓ |
| 4h | IMAP poll | source default supplier | routed | ✓ |
| 4i | IMAP poll | default supplier NULL | parks `unrouted` | ✓ |
| 4j | IMAP poll | default supplier soft-deleted | parks `unrouted` | ✓ |
| 5a | assign-supplier | unrouted + valid supplier | routed, `parsing`, revision pinned | ✓ |
| 5b | assign-supplier | order already routed (`ready`) | 409, untouched | ✓ |
| 6a | learning | assign then re-parse binds the layout | fingerprint bound | ✓ |
| 6b | learning | 2nd same-layout doc, supplier-less | **still parks `unrouted`**, no auto-bind | ✓ |
| 7a | ASN / DESADV | EDIFACT DESADV upload | 501, no order | ✓ |

Run it:

```bash
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~SupplierRoutingMatrix" --logger "trx;LogFileName=routing-matrix.trx"
```

Every assertion carries its cell id, so a red run names the cell without anyone reading the
harness. Example, from the mutation run below:

> Did not expect a value because **cell 4c [SFTP pull] default supplier soft-deleted** could
> not determine a supplier, so none may be invented, but found {540efa79-…}.

## Cell 6 — what the learning contract actually promises

The brief asked to verify before asserting, and the verification changed the cell twice.

**Nothing auto-assigns.** The single production consumer of
`SchemaFingerprint.SupplierIdsCsv` is `FormatDetectionController.cs:58` →
`ISchemaFingerprintService.LookupAsync` → `FingerprintBoost.Apply`. No ingest or parse path
reads it to pick a supplier.

**And "suggest-only" still overstates it.** `FingerprintBoost.Apply` returns
`detected with { Confidence, Reasoning, SeenCount }` — `match.SupplierIds` and
`SampleSupplierName` are dropped, and `DetectedFormat.DetectedSupplier` is never populated
from the match. The layout is recognised and confidence is boosted; **the supplier is never
offered to anyone.** (Corroborated independently by the OPS-3 pass / PR #64, which measured
the same thing live: a second document with a layout a human had routed to supplier B went
to A instead.) Say "the binding accumulates; no reader turns it into a suggestion."

So cell 6b asserts the two things that ARE true: a repeat layout arriving supplier-less
**still parks `unrouted`** (`SupplierId` NULL), and the binding is readable at the service
(`match.IsBoundTo(supplierId)`, `SeenCount > 1`). It fails the day something starts
auto-binding — precisely when a human should be asked to review that choice.

**Reachability note (updated 2026-07-26).** This park used to need an org with no usable
supplier: inbound email fell back to the oldest active supplier, so an org holding at least one
supplier never reached it in production. That fallback was deleted (F1), and **cell 3b now parks
with two active suppliers present**. The cell still arranges a supplier-less arrival explicitly,
because what it pins is that the learned binding does not auto-route — not how the order became
unrouted.

## Proof the matrix is load-bearing

A suite written against shipped behaviour passes on day one, which proves nothing by itself.
Three mutations were applied and the matrix was re-run; **exactly the three predicted cells
failed, no collateral**, then all three were reverted:

| mutation | expected | actual |
|---|---|---|
| `InboundEmailRouter` fallback `OrderBy(CreatedAt)` → `OrderByDescending` | 3b fails | 3b failed |

That first mutation is no longer runnable as written — the fallback it perturbs was deleted on
2026-07-26. Its replacement, applied when 3b was re-pointed at `ParkedUnrouted`: restoring the
fallback makes **3b fail** (it routes instead of parking), which was observed on the sibling unit
test as `Expected orders.UnroutedCalledWith to contain 1 item(s), but found 0`.
| `SftpIngressService` resolver drops `s.DeletedAt == null` | 4c fails | 4c failed |
| 6b probe: auto-bind the 2nd order from the fingerprint | 6b fails | 6b failed |

## Fidelity boundary — read this before trusting a cell

- **What each cell proves.** The real controller / router / ingress service / job for that
  channel runs against real Postgres and decides a supplier; the leaf stub-creator is a
  recorder that persists *exactly* the supplier id the producer passed. The supplier a
  channel CHOOSES is the whole question, so that is the assertion surface.
- **Why not `CountingOrderService`.** The existing claim-first double writes
  `SupplierId = null` unconditionally (right for suites that count orders). Reusing it would
  have made every routed cell pass vacuously. `RoutingRecorder` / `RoutingStubRecorder` exist
  for that reason and for no other.
- **The park is mirrored, not re-proven.** `parsing → unrouted` is the parse job's single
  write (`OrderIngestionService`, `if (entity.SupplierId is null) newStatus = Unrouted`).
  Driving the real parse needs storage plus format parsers, so cells apply that same
  transition (`ApplyParseJobParkAsync`) and assert the row accepts it. Persistence of the
  NULL-supplier park is owned by `UnroutedOrderNullSupplierPersistencePostgresTests`.
- **Routed cells assert `!= unrouted`, not a specific terminal status.** Whether a routed
  order lands in `pending_review` or `ready` is a line-resolution outcome, not a routing one.
- **Siblings referenced, not duplicated:** `AssignSupplierPostgresTests` (404 / cross-tenant
  400 variants of cell 5), `InboundEmailUnroutedPostgresTests` (cell 3's audit trail),
  `SchemaFingerprintLearnsOnAssignPostgresTests` (cell 6's count-only guard), and the
  SFTP/S3/IMAP claim-first suites (dedupe and resume, which are not routing).

## Two things learned that cost time

1. **Real Postgres rejects the `SupplierConnection` ↔ `SupplierConnectionRevision` pair
   inserted in one `SaveChanges`** — `active_revision_id` and `connection_id` point at each
   other and EF cannot order a cycle. Cells 5a/5b failed on this first run. The fix is three
   writes: connection unpinned → revision → pin. The InMemory-based revision-authority tests
   use the single-write form and never see it. One more entry for "InMemory masks Postgres FK".
2. **One container for the whole matrix, via `IClassFixture`.** xUnit builds a fresh
   test-class instance per theory case, so the repo's usual per-class `IAsyncLifetime`
   container would have started and migrated **27** Postgres containers — exactly the Docker
   overload that makes these suites flake with `Npgsql: Timeout during reading attempt`. The
   fixture keeps it to one, and the 27 cells run in ~3 s.

## Not changed

No production code was touched. This is a test-only addition (one new file). PR only — not
merged.

## Test results — stated with their provenance

- **Matrix alone: 27/27 green**, ~3–6 s (RAN, three times: after the seeding fix, after the
  wording corrections, and once more on a quiet Docker host).
- **Full `ProcuLink.Api.Tests`: 1,661 passed / 2 failed / 0 skipped**, 15 m 24 s. All 27 matrix
  cells passed inside that run. **The 2 failures are pre-existing and are NOT this change** —
  `DeliveryClaimEquivalencePostgresTests` and `DeliveryConfigRepublishPostgresTests`, both
  `Npgsql : Exception while reading from stream` in `InitializeAsync`, zero assertion failures.
  This change adds no production code.
- **Chased rather than assumed.** Run alone with the matrix excluded from the run entirely,
  `DeliveryClaimEquivalencePostgresTests` failed **19 of 64**, and the failing theory cases
  differ between runs. It is `IAsyncLifetime` with a 64-case `MemberData` theory, so it starts
  one `postgres:16` container **per case**. That is the contention, and it is that suite's own.
  Flagged as separate work (make it class-scoped, like `AdminAccountStatusPostgresFixture` and
  this matrix) rather than fixed here.
- One intermediate matrix run showed 5 transient failures while a **sibling session was actively
  spawning containers** (13 alive, new ones every few seconds). Those containers were left alone —
  they belonged to a live run, not orphans, so the usual reap recipe did not apply. Re-run on a
  quiet host: 27/27. The `0 skipped` above matters as much as the pass count: a wedged Docker
  probe skips every Postgres test and still prints `Passed!`.

### CI settled the contention question

**PR #65, `Build + test (213 baseline)`: PASS, 13 m 45 s.** On the Linux runner
`ProcuLink.Api.Tests` reported **1,663 passed** — that is the local 1,661 *plus the exact two
that failed locally*. Both delivery suites are green on CI, which confirms those failures were
this Windows host's container contention and nothing else. All **27 matrix cells passed on CI**
(27 `Passed`, 0 `Failed` in the run log), so Testcontainers really runs there and the cells were
executed, not silently skipped. Solution totals: 1,221 (+2 env-gated skips) / 1,054 / 1,663.
## Cross-check against the OPS-3 live pass (PR #64)

`docs/qa/2026-07-fable5-push/2026-07-25-routing-matrix-live-proof.md` asked the same question on
production while this suite asked it of the code. Neither was briefed on the other's answer, so
where they agree it is corroboration rather than an echo.

- **Its F3 = this suite's fingerprint correction, reached from the opposite direction.** It
  measured that a repeat layout went to the wrong supplier; this suite read
  `FingerprintBoost.Apply` and found `SupplierIds` dropped. Same conclusion: the product cannot
  yet claim the fingerprint suggests a supplier.
- **Its F1 was the reachability caveat on cells 3c/3d/6b — now FIXED (2026-07-26).** Inbound email
  fell back to the oldest active supplier, so `unrouted` was unreachable on prod for an org holding
  at least one supplier, and **cell 3b asserted that fallback as intended behaviour**. The fallback
  is deleted: a configured Email-intake default routes the mail, anything else parks. 3b now
  asserts `ParkedUnrouted`, and the caveat on 3c/3d/6b is lifted — those cells still arrange a
  supplier-less arrival, but no longer *have* to.
- **Its F2 is a KNOWN LIMIT of cell 6a.** Cell 6a calls `RecordParseSuccessAsync` directly (as the
  sibling suite does), so it proves the recorder binds on re-entry — **not** that the real
  `ParseOrderJob` file-backed re-parse reaches it. F2 measured that it does not: phantom `Deleted`
  rows -> `DbUpdateConcurrencyException` -> swallowed at `ParseOrderJob.cs:150-153`. **Cell 6a is
  therefore green while production does not learn.** Recorded in the cell's own doc comment so the
  next reader cannot mistake it for end-to-end proof. Closing F2 is the fix; this cell is the guard
  that the recorder itself stays correct.
