# Progress Tracker

**Update this file whenever a packet changes state.** It is the one place that answers "what is done
and what is not". Findings live in `AUDIT-2026-07-27.md`; packet specs in `01-WORK-PACKETS.md`.

**Last updated:** 2026-07-27, after fix round 1 launched.

## Status legend

`⬜ not started` · `🔵 in flight` · `🟠 built, refuted — fixing` · `🟡 built, awaiting review` ·
`🟢 merged` · `⚫ retired / no longer needed`

**Nothing has been merged to `main` in either repo. Every branch below is local and unpushed unless
marked otherwise.** BE `main` is still diverged (6 local vs 9 origin) and needs founder reconciliation.

---

## Who owns what — 2026-07-30

TWO sessions are executing this plan. Ownership below is settled; do not cross it.

**Parallel-execution session** owns twelve packets, each in its own worktree, each opening a draft PR:
WP-01 (**FE PR #40 — green, non-draft, MERGEABLE, all 5 checks SUCCESS**), WP-02, WP-04 (both repos),
WP-06, WP-07, WP-08, WP-09, WP-10, WP-11 (both repos), WP-12 (**BE PR #74, draft**), WP-14 (both
repos), WP-20.

**Plan-authoring session** owns: the design briefs (DB-1, DB-2, DB-6 — Wave 4 and WP-15/16 are blocked
without them), WP-22, WP-24, and this documentation.

Its earlier fix round for WP-01/04/12/14/20 and WP-08/09/10 was **stopped and conceded** once the
collision surfaced; all refutation findings were handed over rather than discarded.

### Writing convention for this file

Two sessions write here. **Append under your own dated subsection; do not edit shared prose.** A
concurrent write then merges trivially instead of conflicting.

### State as of 2026-07-30 — parallel-execution session

- **Merged: none.** Both repos' `main` are untouched by this plan.
- **Ready, awaiting founder: FE PR #40 (WP-01)** — non-draft, MERGEABLE/CLEAN, all five checks green
  on run `30526225189`. **The single highest-leverage merge available**: until it lands, no frontend
  "tests pass" claim in either session has actually been checked. Merging is a founder gate — FE main
  auto-deploys to Vercel.
- **Draft, mid-fix: BE PR #74 (WP-12)** — 8 defects being fixed (2 corroborated independently by both
  sessions, 6 found only by the execution session). The real-Postgres jsonb round-trip is **green on
  CI run `30527129110`** — Docker is dead on the founder's box, so CI is the only place these run.
- **Blocked, needs the founder: WP-03 check 2.** The `unrouted` test email needs the Postmark webhook
  token AND `X-Inbound-Proxy-Secret` plus authenticated production API access. Neither session will
  read live secrets into a transcript — that is the exact operation that leaked three keys on
  2026-07-27, and those three are still unrotated.

### 2026-07-30 later — first merge, and four facts that change other packets

**WP-01 IS MERGED.** `origin/main` FE is at `3b0feea`. The push-to-main run confirms
`Unit tests + lint + conformance gates => success`. **From here, frontend "tests pass" claims in either
session are actually checked.** This was the highest-leverage merge available; it is done. The founder
authorised this one specifically — everything else stays draft.

**BE PR #75 (draft, CI green, 4029 tests / 0 failures)** — Wave 1 backend retirements. Four outcomes that
other packets must respect:

1. **`RuleDefinition` was KEPT — it has live consumers.** `RuleDefinitionService` +
   `RuleDefinitionBackfillService` are DI-registered at `Program.cs:508-509` and bound into the live
   acceptance engine via `SupplierAcceptanceRule.RuleDefinitionId`. So `/library/rule-definitions` (the
   page) dies and `/api/rule-definitions` (the API) stays. **→ WP-17 must not assume the deletion was
   total when it touches the acceptance engine.**
2. **12 endpoints removed** — four `/api/templates`, five `/api/rules`, three
   `/api/webhook-ingress/{slug}/*`. Also gone with the dead callback: `OrderStatusMachine.WebhookReportableFrom`,
   `HasDispatchMarker`, the `"webhook"` rate-limit policy, and the `IDistributedCache` nonce store.
   **Outbound webhook subscriptions untouched**, protected by a new guard test
   `TheLiveNearNamesakes_AreStillPresent`. **→ WP-22 lives in `InboundEmailController`, the near-namesake
   most at risk from a future cleanup — it must not weaken that guard.**
3. **`OrderStatusMachine.Allowed` deliberately left unchanged.** The edges once justified by "a supplier
   status webhook" are still reachable via `OrderResolutionService.cs:256`, `DeliveryService.cs:771` and
   `MarkDelivered`. No order lifecycle was narrowed. **→ WP-19 rewrites that machine and must not read the
   endpoint deletion as licence to prune edges.**
4. **`/welcome` is NOT an orphan — finding resolved, not escalated.**
   `ProcuLink.Api/Services/StripeBillingService.cs:335` sets
   `SuccessUrl = "{frontendUrl}/welcome?upgraded={plan}&interval={interval}&session_id={CHECKOUT_SESSION_ID}"`.
   Every paying customer lands there after checkout — hence the `?upgraded=` param, the `noindex`, and the
   deliberate sitemap absence. Deleting it would have broken the post-payment experience.
   Docs nit: `welcome/layout.tsx:3` says "post-signup"; it is post-**checkout**. STATUS.md is right, the
   comment is wrong.

**Rate contention is real and is being managed.** The execution session held 9-12 concurrent agents; the
plan session's design round recorded 7 agent starts and 0 completions against a burst of
`API Error: 529 Overloaded`. The execution session has voluntarily paused new launches until DB-1/DB-2/DB-6
return, because those block Wave 4 and WP-15/16 and sit further down the critical path. **Lesson: 7 starts
with 0 results reads as "bad API day" from inside one session — it takes the other session saying "I am
holding 12 agents" to diagnose it.** If both sessions run wide, coordinate the ceiling.

### 2026-07-30, later still — parallel-execution session: four packets complete

Every PR below is a **draft**, CI-green, and **not merged**. WP-01 remains the only merge.

| Packet | PR | CI | State |
|---|---|---|---|
| WP-01 | FE #40 | `30526225189` | 🟢 **merged** `3b0feea` |
| WP-12 | BE #74 | `30533501611` | 🟡 all 8 refuted defects fixed — green, **fixes not re-gated** |
| WP-04 FE | FE #41 | `30531502223` | 🟡 built, refuter running |
| WP-10 | FE #42 | `30532874789` | 🟡 built, refuter running |
| Wave 1 BE (WP-06/07/09) | BE #75 | — | 🟡 4029 tests / 0 failures — **green-but-ungated** |
| WP-20 BE | BE #77 | `30533604117` | 🟡 built, ungated |
| WP-20 FE | FE #43 | `30533574000` | 🟡 built, ungated — **not optional**, see below |

Still in flight: WP-04 BE, WP-14 (both repos), Wave 1 FE, WP-11 (both repos), WP-02 (both repos).

**WP-12 is fixed, and the mutation numbers are the evidence.** Reverting *only*
`OrderTransformService.cs` to `origin/main`: before the fix round 3 of 11 tests failed; after, **7 of
11**, and **16 of 33** across all WP-12 tests. All four tests the refuter called vacuous now fail under
that mutation. The four still passing are correctly passing — promote, round-trip and the golden
no-tree path do not touch that file. Best structural outcome: the usability predicate is now
format-aware, so **promote and consumption share one predicate and cannot drift**; that closed D5 and
D4(b) together rather than as two patches that could diverge.

Still open on #74, flagged not smuggled: the field-by-field preview never threads an `EnvelopeConfig`
into the cXML/X12 transform, so a previewed `<Credential>` can differ from the delivered one on
envelope identity. **Predates the branch** — a WS-12 preview gap, not a WP-12 regression.

**WP-20 caught all 11 of its mutations, including the three the earlier attempt left green** (M4 SFTP
`canOverride:true` hardcoded, M5 `Exists` pre-check dead-coded, M6 FTPS `Overwrite` hardcoded). They
bite now because `UploadCore` was extracted behind narrow session seams, so tests drive the live upload
path instead of mocking around it. The acceptance mutation works: a new `OutputFormat` with no table
row fails with `NotSupportedException: No delivery media type is defined for output format
'EdifactDesadv'`.

**FE #43 is a hard dependency of BE #77, not a nicety.** `buildConfigObject()` returns a fixed key
whitelist and the save replaces the whole config object, so without it `overwriteExisting` is destroyed
the next time an operator saves — the backend fix would silently revert itself in production.

**Overwrite defaults ON**, reversing the packet spec. The spec was wrong: a crash-recovery re-drive must
be able to repair its own truncated file, and delivery is at-least-once. The clobber is fixed by the
filename instead. `ResendSafety` stays `Safe` and is now honest — with overwrite off a re-send
*refuses* rather than duplicating.

### ⚠️ FOUNDER GATE — WP-20 changes what real suppliers receive

SFTP/FTPS filenames become `PO-123-a1b2c3d4.xml` (was `PO-123.dat`). **Any supplier whose pickup script
globs `*.dat` or matches the PO number exactly stops seeing files.** This is a wire-visible change to
live delivery, not an internal refactor, and needs a customer conversation before BE #77 merges.

Related, and worth a human sanity check: the agent chose `.cxml` → `.xml` and made the table
authoritative, on the reasoning "no receiver we integrate with requires `.cxml`". The repo cannot fully
evidence that claim.

### Green-but-ungated is a real state, not a formality

BE #75 (4029 tests) and BE #77 have **not** been through the adversarial gate; the throttle below
stopped their refuters launching. Recorded as green-but-ungated rather than cleared. A green suite
proves nothing broke that a test already covered; it says nothing about what no test covers — which is
exactly what the gate found six times in WP-12 against a green 1723-test suite.

### Agent-ceiling throttle — in effect

The parallel-execution session is **holding all new agent launches** at the plan-authoring session's
request, until DB-1/DB-2/DB-6 return. In-flight work continues; only launches are paused. Diagnosis
correction worth keeping: "7 starts / 0 results" was 4 initial + 3 retries, not 7 failures — the
retries that died were pre-throttle, and the attempts running after it are healthy.

### 2026-07-30, merge train — parallel-execution session

**Merged this pass.** FE `#53` residency correction, `#43` delivery-config key destruction, `#45` WP-02 e2e.
BE `#83` residency ground truth, `#76` orphan guard. FE `#49` and `#54` were merged by their own chip
sessions. FE main `09ecfb7`; BE main `14c8bc9`.

**Held deliberately, each on an unresolved founder gate — not on code quality.** All are green.

| PR | Gate that holds it |
|---|---|
| BE #75 Wave-1 retirements | merging destroys `output_templates` + `validation_rules` rows irreversibly; `Down` restores shape, not data |
| BE #77 + FE #43-pair WP-20 | SFTP/FTPS filename changes on the wire; customer notice drafted, not sent |
| BE #78 + FE #44 WP-14 | changes bytes delivered to existing suppliers; blast-radius query written, not run against production |
| BE #74 WP-12 | fixed after refutation but never re-gated adversarially |
| BE #79 WP-02 | `CONFLICTING/DIRTY` — needs a rebase before anything else |
| FE #56 | went `dirty` behind #49 |

**FE #55 rebased and reduced.** It originally also widened the link crawl; PR #54 landed that same fix
independently — same two planted probes, same evidence — so both conflicted files were resolved to `main`
and the crawl half dropped as redundant. What remains is the parse-stall escalation, which nothing else
covers: `useOrderReview` was already computing `isStuck` and rendering it nowhere, and after #47 no surface
in `src/` had any escalation at all. Gates green, 122 files / 1276 tests.

### ⚠️ A started background task will cause a production outage if it lands

`task_70692e4c "Ship the deferred webhook_secret column drop"` is running in its own session. **That
migration must not merge yet.** The Wave-1 fix deliberately kept the `DropColumn` out of BE #75, because
`MigrateAsync()` applies every pending migration in one startup — so a second migration file in the same
deploy is byte-identical in effect to the bug it fixes.

Required order, and it cannot be compressed:
1. merge + deploy BE #75 — both services stop mapping the column, which still exists, so draining replicas stay safe
2. confirm **both** Railway services are on the new build: `ProcuLink` (API) **and** `aware-amazement` (Worker)
3. only then merge the drop migration, deleting `Wave1ColumnDropStaysDeferredTests` in the same PR

Skipping step 2 stops IMAP polling and every Worker billing gate with
`ERROR: column o.webhook_secret_encrypted does not exist`, for as long as the Worker deploy lags.

### Two process defects worth keeping

**A draft PR cannot be merged, and `gh pr merge` fails quietly enough to look like success.** A merge loop
that echoed its own status text reported four merges that never happened; only `git log` on `origin/main`
showed the truth. Check the merge by reading `mergedAt`, never by trusting the command's exit path.

**Local green can come from a working tree the pushed commit does not contain.** The WP-14 agent edited two
test files *after* creating its commit, pushed only the commit, and reported a green local suite — CI built
a tree that still had the old test. The tell was a test-count mismatch between local and CI (1358 vs 1346).
One `git status` before claiming would have caught it. Verify the pushed blob, not the working tree.

### New small packets found in passing — unowned

| Item | Evidence | Options |
|---|---|---|
| ~~A 404 is shipping to admins today~~ **RETRACTED 2026-07-30 — NOT a 404** | `guides.ts:263` registers it and no page exists — both true — BUT `GuideIndex.tsx:97` renders any guide whose `status !== "live"` as a `<span>` with a "Coming soon" badge, **never a `<Link>`**. It is `status: "planned"`. Nobody can click it. Same for `/help/guides/set-up-your-workspace` (`guides.ts:79`). | Write the page eventually — the write-not-delete argument stands on its own merits (capability shipped in BE #59, runbook prose exists in STATUS.md, the two-call ordering detail deserves a discoverable home) — but it is **not urgent and not a defect**. |
| `/one-pager` unreachable in-app | print collateral, published only via `sitemap.ts` | link it, or delete it — founder call |
| This is the reverse of what the guard checks | `route-reachability.test.ts` finds routes with no inbound link; this is an inbound link with no route | extend the guard to check both directions |

### TRAPS — read these before touching the named file

Not notes. Each one is a wrong inference a competent agent is likely to make, with the reason it is
wrong. Add to this list whenever a review catches one.

**TRAP 1 — "the webhook controller was deleted, so I can prune the status edges it justified."**
WRONG. BE #75 removed `/api/webhook-ingress/{slug}/*` and `OrderStatusMachine.WebhookReportableFrom`,
but it deliberately left `OrderStatusMachine.Allowed` UNCHANGED. The edges once justified by "a supplier
status webhook" are still reachable through three other live paths: `OrderResolutionService.cs:256`,
`DeliveryService.cs:771`, and `MarkDelivered`. Pruning them narrows the order lifecycle and strands
orders. **Applies to: WP-19**, which rewrites that machine.

**TRAP 2 — "the near-namesake is dead too."** Three similarly-named things exist and only one was
retired: inbound WEBHOOK ingress (gone), inbound EMAIL (live, `InboundEmailController`), and OUTBOUND
webhook subscriptions (live, a paid feature). BE #75 added a guard test
`TheLiveNearNamesakes_AreStillPresent` for exactly this. **Applies to: WP-22**, which rewrites
`InboundEmailController` and is the packet most able to weaken that guard.

**TRAP 3 — "the retirement was total."** `RuleDefinition` was KEPT. Only the page died. **Applies to:
WP-17.**

**TRAP 4 — "no inbound link means it is dead."** `/welcome` had no inbound link in `src/` and is
load-bearing — `StripeBillingService.cs:335` routes every paying customer there post-checkout. The
reachability guard cannot see a link that originates in the backend. **Applies to: any orphan
classification.** Corollary: `route-reachability.test.ts` checks routes-with-no-link but not
links-with-no-route, which is how a 404 to `/admin/guides/unfreeze-a-pilot-workspace` is shipping today.

**TRAP 5 — "a green suite means it is cleared."** BE PR #75 is green with 4029 tests and 0 failures and
has NOT been through the adversarial gate; it is recorded as **green-but-ungated**. The gate is what
caught six real regressions in WP-12 that a green 1723-test suite missed. A green suite proves nothing
was broken that a test already covered; it says nothing about what no test covers.

### Classifying an orphan — the standard, after WP-04 raised it

Because TRAP 4 exists, an allowlist reason must carry evidence, not an impression:
- **A reason:** "no reader found in EITHER repo at `origin/main` as of 2026-07-30".
- **Not a reason:** "appears unused".
Also required: consider that a store can be legitimately **write-mostly** — an audit log's consumer may
be a human answering a compliance question, not code. And check for non-EF readers: `Memberships` may be
read through Clerk rather than through the DbContext.

**The goal is not to avoid founder questions. It is to make sure the ones we raise are the ones only a
founder can answer.** Half the "needs a founder decision" items in `AUDIT-2026-07-27.md` were answerable
from the code by someone willing to go look — the revision-authority P0 died to a ten-second
`railway variables | grep`, and `/welcome` was settled by reading one line of `StripeBillingService`.

### TRAPS added 2026-07-31 (parallel-session hazards)

**TRAP 8 — "the scratchpad is mine."** It is not. The scratchpad directory is shared across every
concurrent session. An agent this round deleted another session's mutation harness believing it was
its own stale artefact. **Prefix every file you write with your packet id, and never delete a file
you did not create in this turn.**

**TRAP 9 — "the dev server on 8099 is the one I started."** An agent bound a port a sibling session
already held, measured *that* session's app, and reported a false regression that read as real.
**Check the port is free before binding, and confirm the page you measured is the build you made.**

**TRAP 10 — "my branch's green is still green."** `d1a6b9c` (WP-18) added 53 lines to
`OrderWorkshop.tsx` and pinned them with a 382-line `validationEveryBreakpoint.test.tsx`. Any packet
that branched before it and touches the same file — WP-28 compresses the chrome bands there — can
re-introduce a breakpoint-conditional mount and silently undo the fix. **Re-introducing a
`hidden lg:flex` gate around anything that participates in acceptance validation is the specific
regression.** Read `d1a6b9c` before editing that file.

**TRAP 11 — "`base.sha` says the PR is on top of main."** It does not. The GitHub API's
`pulls/N.base.sha` is the head of the **base branch right now**; it says nothing about what the PR
branch contains. `mergeable: true` is also not containment — it only means git found no textual
conflict. The main session made exactly this error this round, read two PRs as already rebased, and
was refuted by the only test that answers the question:

```
git merge-base --is-ancestor origin/main origin/<branch>   # true = branch contains main
git rev-list --count origin/<branch>..origin/main          # how far behind
```

Both PRs forked at `478b809` and were four commits behind. **Never infer containment from a PR API
field.**

**TRAP 12 — "the PR's green was computed against current main."** `pull_request` runs execute
against `refs/pull/N/merge`, computed **at event time**, and GitHub does **not** recompute it when
the base moves afterwards. Two PRs this round were created three seconds and ninety seconds after
`831fad1` landed — a race no one can call from the outside. This is protocol rule 6 with a mechanism
attached: a re-run against current main is **required** before merging a PR whose base moved,
especially one that ships a gate with a baseline (WP-30's `lint:tokens` per-file ratchet is state a
sibling merge invalidates). Pushing the rebase is the cheap way to force the fresh run.

**TRAP 13 — "a source-text assertion proves the thing is mounted."** It proves the *characters* are
in the file. `ClerkAvailabilityGate.test.tsx:196-212` reads `(app)/layout.tsx` as a string and
regexes `/<ClerkAvailabilityGate>/`. Verified: comment the JSX out and the regex **still matches
inside `{/* */}`**; or leave `<ClerkAvailabilityGate>{null}</ClerkAvailabilityGate>` beside an
ungated shell. Under the second, WP-32's refuter ran `tsc --noEmit` (exit 0), `bun run test` (1594
pass), `next lint` and `next build` — **four gates, zero detection**, with the forever-spinner fully
restored.

This is the same class as FE `4c7350a` ("a comment after a colon is still a comment"): **a regex over
source cannot tell code from a comment, and a source-text assertion is not a behavioural one.** The
technique is used in at least two places (`plain-language-copy.test.ts` names it as the pattern).
Where the question is "is it mounted", render it and assert on the DOM.

**Third instance the same day, inside a gate — and the class now has a named remedy.**
`check-vocabulary.mjs:482` takes the **first** match of a bare declaration regex, with no comment
stripping:

```js
const decl = new RegExp(`\bconst\s+${name}\b`).exec(src);
```

A packet added an explanatory comment naming `const FILTER_CHIPS` above the real declaration, and
the gate re-anchored onto the comment. Measured: rename at
`478b809` → exit 1 `registry-moved`; identical rename on the branch → **exit 0**; delete the array
outright → **exit 0**, label count silently 43→37. `FILTER_CHIPS` is the **only** one of six
`NOUN_REGISTRIES` carrying a duplicate `const NAME` token, so inspection would never find it.

**Remedy: reuse `src/test/sourceScan.ts`'s stripper. Do not write a second one.** A duplicated
stripper was deleted in the backend (`504d9cc`) for exactly this reason — two copies drift, and the
one nobody fixed is the one still shipping the bug.

**TRAP 14 — "it passed the vocabulary gate."** `scripts/check-vocabulary.mjs` is roughly half-blind,
and this is a **gate limitation, not any packet's defect**. Three verified gaps, proved by injecting
probes and watching them pass:

| Construct | Scanned? |
|---|---|
| body paragraph | caught |
| `label: value` in a data array | caught |
| `<h1>…{name}</h1>` — any interpolated heading | **missed** — `looksLikeProse`'s char class at `:299` excludes `{` and `}` |
| `name="…"` | **missed** — `name` is absent from `ATTR_RE` at `:254` |
| `detailLabel="…"` | **missed** — `\blabel` needs a word boundary that `detailLabel` does not give |

Interpolated headings are everywhere, so "checked against the vocab gate" means *some* copy was
checked. Unowned as of 2026-07-31.

**Widened 2026-07-31 (WP-28), and this is the larger half.** The scanner is **line-based** —
`check-vocabulary.mjs:436` is `readFileSync(file, "utf8").split(/
?
/)` and every rule runs per
line. So a **JSX text node that spans lines is never scanned at all**. Proof from the field:
`MapperWorkbench`'s shipped banner copy *"Shown as dashed wires"* — a retired noun — has always been
invisible, while the identical wording in a `title=` attribute was caught instantly.

State the limit as: **`lint:vocab` covers attributes and same-line prose. It does not cover JSX text
nodes or interpolated strings.** Green means "no violations in the parts it reads", and the parts it
reads are a minority of rendered body copy.


**TRAP 15 — "the number came from a harness, so it is a measurement."** A method note, not anyone's
defect. **A measurement inherits the scope conditions of its harness, and a number with a method
behind it reads as more authoritative than a number without one — which is exactly when the scope
condition goes unstated.**

WP-32's refuter reported the degraded-state card arriving at **11 153 ms on Fast 3G** and **19 694 ms
on Slow 3G**, missing the 10 s AC. Real harness: CDP `Network.emulateNetworkConditions`, timer
started before `page.goto`. But it was a **cold localhost production build with no cache**, where
206 kB of First Load JS crosses an artificial link — not a Vercel edge with a warm cache. The three
significant figures made it read as field observation.

The underlying finding survived and is worth fixing: the 8 s deadline starts in a `useEffect`, so it
is a **post-hydration** budget while the AC is **wall clock**. That is a real AC defect with a real
fix (start the clock earlier). Only the numbers were scoped wrong.

**The test before you publish a number: is this what the user experiences, or what my rig
produced?** If the second, say so in the same sentence as the number. Both sessions produced an
inference dressed as an observation on 2026-07-31 — this one, and a `useQueriesEnabled` consumer
count taken from a stale working tree. The shape recurs under parallelism because nobody re-derives
a number that already has a method attached.

**TRAP 16 — "the token gate is green, so the colours are accessible."** The two are **structurally
orthogonal** and no amount of tightening the first will imply the second. A token lint looks for raw
hex. A contrast failure between **two tokens** has no raw hex on either side — both are `var(--…)` —
so it is invisible to the gate forever. Verified live on `settings/page.tsx:675`:
`color: var(--amber)` on `background: var(--amber-soft)` at 12.5px/400 = **3.65:1**, under the 4.5:1
floor, with `lint:tokens` green.

WP-30's AC bundled the two into one sentence as though passing the hex gate implied AA. It does not.
**Contrast needs its own check, computed from resolved token values, not from source text.**

The second half of this is the retraction shape and it is the more general lesson. An earlier
contrast pair was retracted correctly for `AssignSupplierBanner` — and the retraction was applied to
the **file** without grepping the **pair**, so the same pairing still shipped elsewhere. *A fix
verified where it was applied is not verified where the defect class lives.* When a finding is about
a combination, the unit of repair is the combination, not the file it was first seen in.

**TRAP 17 — "the baseline ratchets."** `scripts/check-tokens.mjs` fails only when a file's count
**exceeds** its recorded budget (`--strict`, which `lint:tokens` does pass — growth genuinely exits
1, and that property holds). Two consequences it does not have:

- **28 → 28 with one violation swapped for another passes silently.** Only the count is compared.
- **28 → 27 prints `[STALE]` and exits 0**, by explicit design (*"fixing a file must not turn CI red"*
  — a defensible call). But the budget is not re-cut automatically, so a file swept 122 → 1 keeps
  **121 slots of permanent re-entry allowance**, up to ~729 across the ledger, until a human
  re-emits the baseline. A ratchet that never tightens itself is a ceiling.

And **"cleaned to zero" means zero by the hex regex.** `rgb()`, `rgba()`, `hsl()`, 3-digit hex,
`color-mix()`, named colours and string-concatenated hex all walk past — including seven `rgba()`
restatements of `#1E66C9` inside a page reported as cleaned. Same family as TRAP 14: **know what
your gate scans before quoting its number as coverage.**

**The near-fix is the lesson.** The first repair instruction for this was *"make `[STALE]` fail"* —
which would have turned CI red on someone for **cleaning a file**, training people to leave files
alone rather than improve them. It was caught before shipping. Generalised: **a gate that fails in
the improvement direction trains avoidance of the improvement.** When a gate is too loose, check
whether tightening it penalises the behaviour the gate exists to encourage; if it does, the fix is a
better comparison (content-hash the entries) or an honest limitation note in the header — not a new
CI failure. The remaining real gap here is only the same-count swap, and the growth property that
*does* hold is currently unpinned by any test.

**TRAP 18 — "seven mutations, all caught."** Or: the mutation was never applied and the report looks
identical. **A mutation harness that cannot distinguish "mutation applied, test survived" from
"mutation never applied" produces a report shaped exactly like a clean one.** Vacuity is the defect
we hunt; this is vacuity in the *instrument*.

It fired for real on 2026-07-31: this repo checks out **CRLF**, a harness's patterns were **LF**, and
five of seven matched nothing. Every test then "passed". It was caught only because that harness was
written to **print the pattern and exit 9 when a pattern does not match**, instead of proceeding on a
no-op.

Mechanical rules: **a mutation step must fail loudly on a non-matching pattern**, and **the
worktree's line-ending convention is part of the pattern.** Verify the mutation landed (diff it)
before trusting the run. Second instrument failure the same day — the other was `spawnSync` argv
quoting, which cost six already-recorded rows.

**The sub-class worth naming: the renderer and the verifier were the same instrument.** Two of the
day's three instrument failures share it. The CRLF harness wrote and checked through the same layer
that mismatched. And while writing TRAP 13's third instance into this file, shell escaping turned
two `\b` sequences in the quoted regex into literal **0x08 bytes** — which render as *nothing*, so
the entry looked correct in `git show`, in `grep`, and in the editor, and shipped wrong through
three commits. `cat -A` was the only view that could see it.

**A trap about exact regexes, published with its regex silently wrong.** When an artifact must be
byte-exact, verify it through a *different* layer than the one that produced it — `cat -A`,
`xxd`, a re-read from a fresh process. The `spawnSync` failure does **not** share this shape, so it
is a genuinely different mechanism and does not belong under this heading.

**TRAP 19 — "a packet edited another packet's guard to make itself pass."** Sometimes true and the
most serious thing on the board; sometimes the guard's *locator* simply broke. **Separate the two
before escalating:**

- **Assertion** — what the guard proves. Relaxing this destroys the guard. Escalate.
- **Locator** — how the test finds the subject. A label change legitimately breaks it, and a wrong
  locator **throws** (`"desktop send button not found"`) rather than silently passing.

Worked example: WP-28 (FE #72) changed `validationEveryBreakpoint.test.tsx`'s `sendControls()` helper
from `aria-label === "Send to supplier"` to `startsWith("Send to supplier")`, because WP-28 makes the
disabled control state its reason. Every acceptance-validation assertion is untouched. That is a
locator repair, not a weakened guard — **but the check that settles it is reconstructing the original
regression and confirming the guard still catches it**, which is what WP-28's refuter was tasked to
do. Read the diff before deciding; do not skip the reconstruction because the diff looks innocent.

**TRAP 20 — "the guard has a test, so the guard works."** The test may never run the guard against
the tree that matters. **A test that exercises a guard against a fixture proves the guard's
*plumbing*, not its *coverage of the repo*.**

`src/lib/vocabulary.test.ts:264` is named *"--nouns FAILS LOUDLY if a policed registry is renamed or
moved away"*. It calls `runGate(["--nouns"], root)` where `root` is a **synthetic fixture tree in
which the registry files do not exist** — so it asserts `file-not-found` and passes identically
whether the real guard works or is completely dead. The `--nouns` guard was dead on
`claude/wp29-inbox` for the life of the branch and this test stayed green throughout.

This is TRAP 14/17 one level up: there the *gate* had a false scope, here the *test certifying the
gate* does. **Ask of every meta-tested guard: does anything run this against the real tree?** A
fixture test and a real-tree smoke test answer different questions and a guard needs both.

**TRAP 21 — stating the consequence at a wider scope than you checked.** The single most common
failure of 2026-07-31, across both sessions, and the one to read first. In every instance the
underlying **fact was true**; what was wrong was the **blast radius asserted from it**.

| Fact (true) | Consequence claimed (wider than checked) | Actual |
|---|---|---|
| `mockTransformOrder` writes `delivered` directly | "every mock-mode delivered assertion in the suite has been free; earlier packets need re-reading" | **zero** such assertions exist — all five vitest files `vi.mock` the client; the two e2e hits are live-only or unrelated |
| `help/**` is `BLOCK_EXEMPT` | "so nothing was ever going to catch it" | the word is **GLOSS-tier**, the gate passes it deliberately, and `help-articles.ts` is scanned and not exempt |
| the 8s deadline starts post-hydration | "a Fast-3G user waits 11.2 s" | a cold localhost build with no cache — a synthetic worst case, not user experience |
| `base.sha` is `831fad1`, `mergeable: true` | "both PRs are already rebased onto main" | neither contained main; both were 4 commits behind |
| `useQueriesEnabled` grep returned 54 | quoted as the consumer count | 58 files / 31 call sites — the grep ran against a stale working tree |

**Narrowing the claim to what was actually verified would have cost nothing in every one of these.**
The check to run before publishing: *how far does my evidence reach, and does my sentence reach
further?* Two of the five above are the main session's, so this is not one session's habit — it is
what parallel work does to the gap between finding something and saying what it means.

**TRAP 22 — "the mock is wrong, so what it backed is unsound."** Not necessarily. **A mock can only
be wrong in a way that matters once something depends on it.** Until then the defect is inert.

`mockTransformOrder` skipped `ready_to_deliver` and `delivering` — a real divergence from the status
machine, flagged in the audit's §9 — and it invalidated **nothing**, because the transform-to-deliver
boundary had *no* coverage of any kind. Not weak coverage: none. WP-27's onboarding journey is the
first test to cross it, which is exactly why that packet had to fix the mock for its own journey to
be killable.

**So the risk points forward, not backward.** A freshly-written mirror of production behaviour,
authored in one pass, with no prior test to contradict it, is inherited by every future assertion
built on it. When a packet repairs a long-latent mock, the review question is not "what did this
break in the past" but **"what does this now silently define for everything after it"** — verify the
new edges against the real state machine, and look for real paths the new mock makes unreachable.

### TRAPS added 2026-07-30 (second pass)

**TRAP 6 — "a registry href with no page behind it is a dead link."** WRONG, and it hides the real
defect. `src/lib/guides.ts` carries `href:` literals for `status: "planned"` guides that
`GuideIndex.tsx:97` never renders as links. Those hrefs are not dead links — they are **phantom link
TARGETS** feeding the reachability guard. Proven: a refuter created a genuinely orphaned
`admin/guides/unfreeze-a-pilot-workspace/page.tsx` and the guard **passed 8/8**, because `guides.ts`
supplied a matching href. `guides.ts` is a second launch-filtered registry with exactly the
`/drafts`-in-`NAV_MAIN` shape, and it was not excluded. **Applies to: WP-04 frontend guard.**

**TRAP 7 — "enumerating `page.tsx` enumerates the routes."** It covers **52%**. Verified:
`next.config.ts:20` sets `pageExtensions: ["ts", "tsx", "mdx"]`, and `origin/main` carries **45
`page.mdx`** against **49 `page.tsx`**. A guard filtering `basename === "page.tsx"` is blind to 45 real
routes, so a synthetic orphaned `.mdx` page passes. **Applies to: WP-04 frontend guard.** Related:
comments count as links there too — `UserChipMenu.tsx:10` already credits `/` from a `//` comment.

### CORRECTION LOG — findings this plan got wrong

Kept because the shape of the mistake repeats, and naming it is cheaper than re-making it.

| Claim | Verdict | The unchecked step |
|---|---|---|
| "a 404 ships to admins at `/admin/guides/unfreeze-a-pilot-workspace`" | **RETRACTED** | Registry checked, filesystem checked, **renderer never checked**. |
| "revision authority is off in production" (audit P0) | **REFUTED** | `appsettings.Development.json` read; the deployed environment never read. |
| "Railway `europe-west4` runs on AWS" (this plan, 2026-07-30) | **RETRACTED** | Railway's docs name no cloud provider and call the regions "Metal". The AWS evidence was real but belonged to **Neon** — two providers conflated in one sentence. The unchecked step was *which subject each fact belonged to*. |
| "`/welcome` is an orphan" | **REFUTED** | Frontend links searched; the backend caller never searched. |
| "`/subprocessors` claims an OpenAI DPA we lack" | **NOT-A-FINDING** | Local tree read; it was 9 commits stale and already fixed. |

**The shape, every time: two true facts with one unchecked step between them.** Registry and filesystem
but not the renderer. Dev config but not prod. Frontend links but not backend callers. Before reporting
an inference drawn from two verified facts, name the step between them and go check it.

### 2026-07-30 — plan session claims Wave 4

WP-24, WP-25, WP-26 taken by the plan-authoring session and building now from the delivered specs
(`DESIGN-DB-6`, `DESIGN-DB-1`). The execution session explicitly declined them.

**WP-15/16 are deliberately NOT started.** They sit on WP-12 and WP-14, and WP-14 is refuted and mid-fix —
building the output designer on it now builds on moving ground. Correct call, and it came from the
execution session about its own work.

**Corrections accepted from the execution session:**
- **`/security` renders 10 subprocessors, not 7** — it is `SUBPROCESSORS.map` at `security/page.tsx:86`, so
  it structurally cannot diverge from `/subprocessors`. Their earlier "7 vs 10 contradiction" was inferred
  from copy rather than from the render, and is withdrawn. Verified; never recorded here, so nothing to fix.
- **WP-14 currently ships an item-ordering defect**: `ItemMappingService.cs:96-100` seeds the batch result
  dictionary with the case-insensitive comparer, so an order carrying `B-1` and `b-1` as *different
  products* writes line 1's supplier code onto line 2. Must be fixed before WP-14 merges. **Relevant to
  WP-17** — the acceptance engine sits downstream of that resolution.

### 2026-07-30 — WP-26 held, and two guards worth keeping

**WP-26 is BUILT BUT HELD FROM MERGE until FE #47 lands.** #47 deletes the `/drafts` entry from
`BridgeSidebar.tsx` and three tabs from `HubTabs.tsx`; WP-26 restructures both wholesale. Worktrees prevent
a working-tree collision, so the branch can be built safely — but the merge order is **FE #47 → rebase
WP-26 → re-run the reachability test**, and the reachability test is what catches the real hazard: a bad
conflict resolution silently resurrecting a route that was just retired behind a 308.

**Vocab-gate exclusions — WP-25 must not sweep these.** `scripts/check-vocabulary.mjs` scans `(app)` and
`(marketing)` render files, and it now runs in CI, so a careless jargon list turns main red for both
sessions. A word that reads as jargon is legitimate in:
- **`src/lib/standards/catalog.ts`** — it contains real EDI vocabulary and is the *conservative source of
  truth for capability claims*. A rename sweep here damages the one anti-drift mechanism that already
  works. **Do not touch it.**
- test fixtures and mock payloads — they mirror wire formats, not user-facing copy;
- quoted error strings in `docs/`;
- `"version"` in a changelog entry;
- code identifiers and comments.

**A second case of the same case-sensitivity bug class, upstream of the known one.**
`SupplierSuggestionService.cs:222-240` compares `SupplierProduct.Code` with `StringComparer.Ordinal`
against the column the catalog now folds case-insensitively. Effect: a lower-cased export **scores 0 on
supplier auto-detection while resolving fine once routed** — the order lands `unrouted` for a reason that
looks like "we do not recognise this supplier" when in fact the codes match. So the WP-14 comparer defect
has a sibling one layer up, and the class is "two comparers over one column". Both belong in the WP-14 fix.

### 2026-07-30 — design critiques, and an error in this plan's own file references

All three specs came back **buildable-with-fixes**. None is hand-to-an-engineer ready. Each critique
independently re-verified the spec's citations and recomputed its contrast ratios by hand — DB-1's nine
pairs and DB-6's fourteen all checked out to two decimals. The forensic halves are sound; the build orders
are not.

**⚠️ CORRECTION TO THIS PLAN AND TO `AUDIT-2026-07-27.md`: the desktop nav is NOT in `BridgeSidebar.tsx`.**
`(app)/layout.tsx` removed the sidebar at `md+`. Desktop nav renders from **`BridgeTopbar.tsx`** —
`useTopNav()` at `:465`, `TopNavLink` at `:410`, rendered at `:857`. `BridgeSidebar` survives only as the
`< md` drawer. Every reference in the audit and in WP-25/WP-26 that calls `BridgeSidebar.tsx:52-96` "the nav
registry" is describing the MOBILE DRAWER. Verified first-hand.
Consequence: DB-1's §11 implementation order names `HubTabs.tsx` and `BridgeSidebar.tsx` and never mentions
`BridgeTopbar.tsx`. And the topbar's second row exists *because* the code already tried the inline
arrangement and rejected it — `BridgeTopbar.tsx:845` comment: "they can't fit in the utility row above". A
spec proposing to inline it is re-proposing something already tried.

**DB-6 — buildable-with-fixes.** Diagnosis fully confirmed (D1, D2, D4, D6, D7, D8, D9 at the stated lines;
the transform gate really does link `/inbox/{id}` from inside `/inbox/{id}`; `InboxView` really has no
`useSearchParams`). But its headline fix rests on **three deep links no code reads**:
`/library/templates?supplierId=` (that page has no `useSearchParams` and the template model has no supplier
filter at all), `?details=response` (the workshop reads `?tab=`), and `&from={orderId}` (UploadWorkbench
reads only `supplierId`). It also mutates `isRedeliverable`, whose file and three test files declare it a
byte-for-byte mirror of the backend guard set; names one artifact three ways in one panel; introduces
"channel" as a **tenth noun** where shipped copy says "connection"; and stops mid-sentence at 11.10.
~70% buildable as written.

**DB-1 — buildable-with-fixes.** Best-grounded of the three. §11 stops mid-sentence at step 2, so there is
no build order for §3–§8 (supplier tabs, `WhatIsThis`, the Setup screen, the 100-row rename table). Cites an
"Open questions" section that does not exist. Copy quality high; IA thesis sound.

**DB-2 — buildable-with-fixes.**

**WP-26 built and clean.** 7 files, all nav — and it *does* edit `BridgeTopbar.tsx`, so it hit the real
target despite the spec's misdirection. Needs a rebase: it branched at `214b3f3` and `origin/main` is now
`1330cce`. Still HELD behind FE #47 per the agreed merge order.

**Merges are landing.** FE `origin/main` is at `1330cce` with #41, #42, #46, #47 and #50 merged.

### 2026-07-30 — my nav correction was half wrong, and #42 shipped refuted

**My BridgeTopbar correction does NOT reach FE #41's guard — the execution session checked and I was wrong
about the consequence.** `useTopNav()` imports `buildVisibleNav` / `isItemActive` / `hubTooltip` **from**
`BridgeSidebar` and renders `<TopNavLink href={item.href}>` off the filtered list — `href` is a *variable*,
so the topbar contributes **zero literal targets** to a scanner. The guard's `REGISTRY_FILES`
(`BridgeSidebar.tsx`, `HubTabs.tsx`, `guides.ts`, `help-articles.ts`) is the right set.

**The distinction, stated so nobody re-derives it either way:**
`BridgeSidebar.tsx` is where the nav **DATA** lives. `BridgeTopbar.tsx` is where desktop **RENDERS** it.
A restructure (WP-25/26) touches both. A reachability scan needs only the data file.

**⚠️ FE #42 SHIPPED REFUTED — wrong copy is LIVE on `/security` now.** It merged at 11:22 while its refuter
had already found the residency sentence still false. Live copy says the US subprocessor categories are
"sign-in, AI document extraction, **inbound email**, and payments". Postmark carries the **outbound purchase
order** as an attachment and is US-only by vendor policy. Also: Vercel is a fifth SCC vendor, OpenAI's
category drops "and mapping suggestions", and the region names are unevidenced with `europe-west4` not a
real Railway identifier.

**This is the plan's own warning coming true in production:** *a fix that makes a false claim more precise is
worse than the claim it replaces.* Assigned to the execution session as a **narrow stage-3 correction**
(category wording + soften the region claims to UNKNOWN, not to "probably EU"), explicitly NOT a
stage-1-first rewrite — live wrong copy is a compliance exposure and correctness outranks sequencing. Stage 1
(sourcing all eight deploy/egress cells) stays a separate packet.

### Rebase plan for WP-24/25/26 — they are SIBLINGS, not a stack

All three branched from `214b3f3`; `origin/main` is now `1330cce`. WP-25 and WP-26 both touch nav files, so
they conflict with each other as well as with FE #47 (`ded9e04`), which also edited
`navContextRowDedup.test.tsx`. A first rebase attempt hit exactly that conflict and was **aborted** rather
than pulling HEAD out from under a running refuter.

Order, once the round completes: **rebase WP-25 onto `origin/main` → rebase WP-26 onto WP-25 → re-run the
reachability guard AND the vocab gate.** Per protocol rule 6, the guard re-run is mandatory, not optional:
#47 already invalidated one allowlist and turned main red.

### 2026-07-30 — Wave 4 results, and a hole in the CI gate

**Docker recovered.** `docker ps -a` returns nothing, 0 testcontainer-labelled containers. Rule 6b stands
regardless — the containers bought no local coverage.

| Packet | Branch | Verdict |
|---|---|---|
| WP-24 | `feat/wp24-recovery-ui @ 03bfa41` | **REFUTED — blocks merge** |
| WP-25 | `feat/wp25-concept-reduction @ c13e24a` | built; **refuter DIED** (StructuredOutput retry cap) — needs re-run |
| WP-26 | `feat/wp26-nav-restructure @ a4016c0` | built clean; held behind FE #47; rebase pending |

**WP-24 has no vacuous tests — the refutation is COVERAGE, not vacuity.** All nine mutations were
independently re-applied, run and hard-restored by exit code, not string match, in a disposable worktree that
was then removed. 1265 vitest green (up 30). The gap: the contract test's guard walk does
`if (a.kind !== "post") continue`, so it **skips every link action** — and **10 of the 15 controls across the
eight states are links, 3 of them dead.**

**F1 BLOCKS MERGE, and it is the pre-#47-base hazard landing exactly where we predicted.**
`transform_failed`'s PRIMARY CTA points at `/library/templates`, which FE #47 (`ded9e04`, already on
`origin/main`) retired — the page directory is gone, 0 files match on main, and it 308s to
`/library/suppliers` with `supplierId` dropped. #47's own justification is the *opposite* of what the CTA
promises. Fix with the rebase, not separately.

**⚠️ NEW GAP — CI DOES NOT TYPECHECK.** `tsc`/`typecheck` appears **zero times** in `ci.yml` on `origin/main`.
WP-01's gate runs test + lint + pageshell + vocab, and that is all. Worse: `src/lib/seo.test.ts` **already
fails `tsc` on `origin/main`** (`Property 'card' does not exist on type 'Twitter'`), so nobody would notice
the gate was missing. Combined with `tsconfig` having `strict: false` and `strictNullChecks: false` (audit
§9, downgraded to P3 on measured impact), **a real type regression in app code passes CI today.** Those two
findings are much worse together than either was alone. Small packet, high value: add a `tsc --noEmit` step
and fix the one pre-existing failure.

**Good judgement calls in WP-24, both worth keeping:**
- It **refused to narrow `isRedeliverable`** as its spec asked, because that predicate is documented and
  tested as a byte-for-byte mirror of the backend's `RedeliverableFrom` — narrowing it would make the mirror
  lie. Added `isBulkSelectable` (`ClaimableForRetryFrom`) and pointed row selection at that: same
  user-visible outcome, no false mirror. This is the exact defect I told the execution session I would not
  ship, and the agent caught it unprompted.
- It corrected its own brief: the Worker-outage escalation is **not** "orphaned on a route nothing links to"
  — BridgeSidebar, HubTabs, CommandPalette and MagicMappingPreview all link `/operations/health`. The real
  defect was that `workerHealthy` was **read** on only that one page.

**Traps confirmed again by this round:**
- TRAP 7 (page.tsx = 52%): the two files needing a copy fix here are **`page.mdx`** —
  `help/dashboard-and-statuses` and `help/exceptions-and-stuck-orders`. The second **documented a recovery
  path WP-24 makes false**: help was telling operators the order screen could not requeue an out-of-retries
  order.
- The audit named two statuses shipping a live-but-doomed Send (`delivery_dead_letter`,
  `rejected_by_supplier`). **There were three** — `unrouted` behaved identically.
- `AssignSupplierBanner.tsx:146` still renders `--amber` as 13px/700 on `--amber-soft` = **3.65:1**. The
  audit's D5 contrast failure survives in that one file because WP-24 delegates `unrouted` to the shipped
  banner rather than restyling it. One-line token swap to `--amber-text`.

### 2026-07-30 — merge sweep: 13 open PRs, 11 not ready, reasons per PR

**Merged this pass: BE #81** (`34ce1e1`). CI on `main` in progress at time of writing — per merge-train
rule 6, nothing else merges until it is green.

**#81 was worth merging on its own merits, and it is a WP-02-class fix.**
`FireIntegrationTriggerJobReliabilityTests.TwoConcurrentFinalFailures_OnPostgres_…` proved a RELATIONAL
guarantee — the consecutive-failure count increments via a relative `ExecuteUpdateAsync` so two interleaving
failures land at base+2 rather than losing one, which EF InMemory cannot translate. It was gated on a local
dev Postgres at `:5435`; `ci.yml` has no such service, **so on CI the gate always missed and xUnit reported
`Passed` having asserted nothing.** A production guarantee that had never once been exercised. Moved to an
Integration test with a real container.
Also confirmed it does NOT touch the retired inbound webhook channel — it is the OUTBOUND subscription
failure counter, which survives BE #75. The near-namesake trap did not bite.

Also already on `main`: **BE #80** (`cd7feba`), the dead `CustomTemplates` plan flag.

**ELEVEN PRs ARE NOT READY, and "draft" is the authors' own signal — do not override it.**

| PR | State | Why it is not merging |
|---|---|---|
| FE #49 | non-draft, CLEAN | **HELD by me, not by CI.** See below — it can block six concurrent sessions from committing |
| FE #53 | draft, **UNSTABLE** | CI red. The residency stage-3 correction |
| FE #45 / BE #79 | draft, CLEAN | WP-02 no-vacuous-passes, both repos |
| FE #44 / BE #78 | draft, BE **UNSTABLE** | WP-14. Also carries a **known unfixed defect**: `ItemMappingService.cs:96-100` seeds the batch dictionary with the case-insensitive comparer, so an order carrying `B-1` and `b-1` as different products writes line 1's supplier code onto line 2 |
| FE #43 / BE #77 | draft, CLEAN | WP-20. **BE #77 is a FOUNDER GATE** — SFTP/FTPS filenames change from `PO-123.dat` to `PO-123-a1b2c3d4.xml`, so any supplier globbing `*.dat` stops seeing files. Needs a customer conversation, not an engineering sign-off. FE #43 is a HARD dependency: without it the config editor destroys `overwriteExisting` on the next save and the backend fix silently reverts in production |
| BE #83 | draft, CLEAN | Residency ground truth doc |
| BE #75 | draft, **UNSTABLE** | Wave-1 backend retirements. Held on a deploy-ordering defect, and **green-but-ungated** |
| BE #76 / BE #74 | draft, was UNKNOWN | Merge refs computed via `gh api` (rule 5 — `gh pr view` does not trigger it). Both now `clean`. WP-04 guard and WP-12 |

**FE #49 — held for a reason its author could not see from inside one worktree.** The pre-commit hook runs
`check:pageshell --strict` and `lint:vocab` **repo-wide**, not over staged files, and `core.hooksPath` is
repo-level config shared by ~13 worktrees. **WP-25 is actively renaming ~50 nouns and re-aiming the vocab
gate itself** — mid-packet that tree legitimately fails, so the hook would stop that session committing its
own work. Worse, `install-hooks.mjs` is wired to `package.json` `prepare`, so `bun install` silently
installs it — and our own protocol REQUIRES `bun install` in every fresh frontend worktree. Asked for the
gates to be scoped to staged files, which is also just more correct: a pre-commit hook should judge the
commit, not the working tree.
Its author's reasoning on the part they could see is right and worth keeping: the full vitest suite stays
CI-only because "a two-minute pre-commit hook teaches people to type `--no-verify`, and a bypassed gate
enforces nothing."

### 2026-07-30 — ~~THE ICP QUESTION IS ANSWERED~~ **RETRACTED 2026-07-30 — see the retraction below**

Resolved from the repo rather than escalated. The two real customer purchase orders say it outright:

- `real-cxml-1.1-mpn-equals-supplier-part.xml` — `<From><Identity>REDACTED-NETWORK-ID</Identity></From>`,
  `<To><Identity>REDACTED-NETWORK-ID</Identity></To>`.
- `real-cxml-1.2-ariba-punchout-mpn-differs.xml` — KSB → Markit, through an **Ariba PunchOut session**. Its own header
  comment says the buyer's `<SupplierPartID>` "resolves against" our catalog.

**Markit is the ProcuLink customer, and Markit RECEIVES these orders from its own buyers.** That is the
supplier / INBOUND side — the mirror image of this plan's documented ICP ("buyer/procurement teams sending
purchase orders OUT to many suppliers").

It also retroactively explains work that looked speculative: **MPN matching (BE #73) and supplier
auto-detect are inbound-flow problems.** A buyer sending POs out already knows which supplier each order is
for. Only a party RECEIVING orders needs to detect who sent one and match a foreign part number to its own
catalogue.

**What this does and does not change.**
- It does NOT invalidate the engine. The pipeline is direction-agnostic: ingest → normalise → map item codes
  → transform → deliver. Both directions use it unchanged.
- It DOES affect the wedge argument. `AUDIT-2026-07-27-FULL-VERDICT.md` §10 dismissed Conexiom as "the
  closest analogue but sits on the inbound side" and concluded "the buyer-side outbound slot is structurally
  unoccupied". If the real customer is inbound, then the market comparison was run against the wrong axis,
  and Conexiom is a direct competitor rather than an adjacent one.
- It DOES affect **WP-25 and WP-26**, which are about to rewrite ~50 nouns and the whole navigation around an
  outbound story ("what we send them", "Suppliers"). For an inbound customer the counterparty is a BUYER,
  not a supplier. The product already has a `counterpartyPlural` mechanism in `BridgeSidebar` — evidence the
  codebase anticipated both directions even where the plan did not.
- All marketing copy is written outbound-first throughout.

**FOUNDER DECISION REQUIRED before WP-25/WP-26 merge — this is the one thing here only you can settle:**
is Markit representative of the customers you intend to sell to, or a first customer who happens to run the
mirror flow? If the former, the vocabulary work must be direction-aware before it lands, not after.

### 2026-07-30 — RETRACTION: the ICP conclusion was wrong. Markit is the founder's EMPLOYER.

I concluded from two fixtures that Markit is the ProcuLink customer and therefore the real ICP is inbound.
**The fixture quotes were exact; the inference was wrong.** Retracted in full. Caught by the execution session.

**The evidence I did not check, every piece verified first-hand now:**
- **The founder's own Windows home directory is `C:\Users\Dmitri.REDACTED-PARTY\`** — a domain-joined account on a
  `REDACTED-PARTY` domain. Markit is his **employer**. I typed that path in every shell command in this session.
- ProcuLink's operating entity is **Diip Solutions OÜ**, registry 17527757 (`src/lib/legal-entity.ts`).
- **`example.invalid` is a live Estonian IT/electronics reseller** — the catalog fixtures carry its real product
  URLs (`https://example.invalid/ee/en/logilink-mousepad-...`, `example.invalid/images/...`).
- "Markit" appears in **test files only** — 12 of them plus `CxmlCredentialConfig.cs`. **Nowhere** as an org,
  tenant, or customer record.

Read together: these are **real documents from the founder's day job, used as realistic test data** — the
*convenience fixtures* branch of the audit's own question, which is the branch I did not take.

**What survives, and it is worth keeping:** the only two real POs obtainable are inbound-shaped, so the
realistic data available to test against is inbound. That is a genuine fact about test coverage and it does
explain why MPN matching and supplier auto-detect were worth building. It establishes **nothing** about the
paying customer's job.

**Consequence: WP-25/WP-26 must NOT rescope on this.** Rewriting ~50 nouns around an inbound story on an
inference this thin would be a large, hard-to-reverse change resting on its weakest link. The
direction-agnostic pipeline plus the existing per-org `counterpartyPlural` relabel already cover both
directions. If an inbound-first ICP is ever confirmed, the rename follows that decision — it does not arrive
as a side effect of fixture archaeology. No marketing copy changes on this either.

**The founder question stands but shrinks:** one sentence of confirmation, not a rescope.

### CORRECTION LOG — entry 5, and the pattern is now undeniable

| Claim | Verdict | The unchecked step |
|---|---|---|
| "Markit is the ProcuLink customer, so the ICP is inbound" | **RETRACTED** | Fixtures read; **who owns the receiving end never checked.** The answer was in my own shell prompt |

Five findings have now failed the same way: **two true facts with one unchecked step between them.**
Registry and filesystem but not the renderer. Dev config but not prod. Frontend links but not backend callers.
Local tree but not origin/main. And now: the document's parties, but not who employs whom.

**The sharpening this one adds:** the unchecked step was the single most familiar string in the entire
session. **Proximity is not verification.** When an inference feels obvious because the evidence is *right
there*, that is exactly where the step between the facts goes unexamined.

### FOUNDER DECISION 2026-07-30 — BUYERS FIRST. Inbound stays supported, not marketed.

Settles the ICP thread. **Sell to buyers** — procurement teams sending POs out to many suppliers. That is
the documented ICP, so **nothing in this plan rescopes.**

**Suppliers are NOT a second market to build or market to.** They are a capability that already exists and
costs nothing to keep. `src/hooks/useOrderDirection.ts` ships a per-org `OrderDirection` switch with a full
label set — counterparty Supplier/**Customer**, rail header `Buyer → Supplier` / **`Customer → You`**,
primary CTA `Send to supplier` / **`Confirm order`**, done `Sent to supplier` / **`Order confirmed`**. Its own
comment: *"the data model is direction-agnostic (orders store buyer=issuer, supplier=recipient); this hook
only swaps DISPLAY text."* Entity, route and type names stay `supplier`; colour semantics are unchanged.
A supplier who finds the product can use it. We just do not aim at them.

**BINDING CONSTRAINT ON WP-25 AND WP-26 — this is the one thing the decision changes.**
The rename must NOT hardcode outbound vocabulary. Every renamed user-facing noun routes through
`partyLabels(direction)`, never a literal. Hardcoding "supplier" into the new nav would silently delete the
inbound mode and make the decision irreversible — the exact opposite of what "buyers first, suppliers still
work" means. Cost of complying: near zero, the mechanism is already there and already tested
(`BridgeSidebar.test.tsx:278` pins the inbound relabel). Cost of not complying: the inbound capability is
gone and nobody notices until a supplier signs up.

**Marketing copy stays outbound-first.** No hedging on the website — hedged positioning is what the audit's
~50-noun problem looks like in prose.

**Why this is the right call on the evidence, recorded so it is not re-litigated:**
- The supplier/inbound market is **proven but crowded** — it is Conexiom's entire business, plus Esker, and
  Rossum/Hypatos/Nanonets sold for order intake. Funded, priced, competitive.
- The buyer/outbound slot is **structurally unoccupied** across ~20 researched vendors. Empty means untapped
  OR unviable, and this plan cannot tell which.
- Countervailing fact, weighed and accepted: our only real test documents are inbound, so the outbound path
  has **zero real customer documents**. That is a live testing gap, tracked separately — it does not change
  the positioning, but it does mean outbound quality rests on synthetic fixtures.
- And the conflict that mattered most: **Markit is the founder's employer**, an IT reseller. Selling
  supplier-side software to its peers is selling to its competitors.


### 2026-07-31 — HANDOFF SNAPSHOT. Verified against origin/main, not from memory.

**FE `origin/main` = `478b809` · BE `origin/main` = `c61fe30` · ZERO open PRs in either repo.**

**MERGED (verified by `git log origin/main`):**
WP-01 CI gate · WP-04 orphan guard · WP-06/07/09 Wave-1 retirements (`44fa058`) · WP-11 billing honesty
(`46b29fe`) · **WP-12 output-tree promotion (`d233409`) — THE WEDGE** · WP-14 backend (`7dd4261`) +
frontend (`ddced4e`) · WP-17 acceptance gate (`8a2dbc3`) · WP-19 4xx split (`b4694ad`) · WP-20
content-type/filename · WP-23-adjacent resolve recompute (`c61fe30`) · WP-24 recovery UI (`7cabd4f`) ·
WP-25+26 nine nouns / four destinations (`478b809`) · WP-34 (`3878c0c`) · plus a `tsc --noEmit` CI gate.

**Seven of the audit's top ten are closed.** Remaining from that list: #4 partially (WP-14 landed; the
designer still cannot author everything), and the residency copy.

**THE SINGLE HIGHEST-VALUE NEXT ITEM — WP-13, and it is an `S`.**
`promoteMapping()` at `src/lib/api-client.ts:1925` **still has ZERO callers**, and no mount passes
`onSaveMappings`. WP-12 merged, so the backend now carries a designed output tree to the supplier — **and
the button that triggers it does not render.** The engine works and nothing can reach it. It also makes
`/help/output-mapping-editor` true; that doc currently tells users to click a control that does not exist.

**UNPUSHED WORK THAT EXISTS — do not rebuild it.**
`fix/wp22-ingest-dedupe` carries two commits in the BE repo: `fc6876a` (claim-first dedupe on both push
channels) and `b6aa80e` (stop connection churn flaking the dedupe race tests). Built and committed, never
verified — the run was killed because it was wedging the founder's Docker. It needs a rebase, a CI run and
a PR. Nothing more.

**STILL OPEN, by wave:**
- Wave 3: WP-18 (validation does not run below 1024px), WP-21 (prove revision authority — the flag is ON,
  this is now a proof packet), WP-22 (above), WP-23.
- Wave 4: WP-27 onboarding, WP-28 workshop chrome, WP-29 inbox `ready` state, WP-30 token lint,
  WP-31 a11y, WP-32 Clerk degraded state.
- Wave 5: WP-33 auto-send (decided: automation, dry-run one week first), WP-35 replay, WP-36, WP-37.
- Wave 6: WP-38 SFTP host keys + live channel proof, WP-39 recorded production pass, WP-40, WP-41.
- Wedge: **WP-13** (above), WP-15, WP-16 — `DESIGN-DB-2` is written and critiqued *buildable-with-fixes*.

**FOUNDER ITEMS OUTSTANDING:** rotate the OpenAI, PostHog and Neon keys — leaked by an unfiltered
`railway variables` call on 2026-07-27 and still live. Nothing else is blocked on the founder.

**NOT BUILT, DELIBERATELY** — see `00-MASTER-PLAN.md §7`. Invoices, ASNs, Peppol/AS2/AS4, more ERP
connectors, EDIFACT output, PunchOut, IMAP hardening, RLS, and any new top-level noun.


> ## ⚠️ THE WAVE TABLES BELOW ARE THE 2026-07-27 AUTHORING. THEY ARE NOT MAINTAINED.
>
> They are kept as a historical record of what the audit found. **They do not describe current
> state and have not since 2026-07-30.** Several rows say ⬜ for packets that have since shipped,
> and the Wave 4 heading still reads "All ⬜ not started" when Wave 4 is complete.
>
> **Current state, verified against both `main`s on 2026-08-01:**
>
> - **Waves 0–4 are COMPLETE.** WP-01…WP-32 have all landed, with two exceptions:
>   **WP-03 check 2** (is `unrouted` reachable on prod — one test email, founder-owned) and
>   **WP-16** Designer depth II (blocked on design brief **DB-2**).
> - **Waves 5–6 are untouched.** WP-33, 35, 37, 41 have zero commits anywhere. WP-34, WP-38, WP-39
>   and WP-40 have shipped.
> - 45 packets merged on 2026-08-01 alone.
>
> **To read live state, scroll to the dated entries at the end of this file — they are appended
> newest-last and they are the truth.** Start with the session-close entry.

## Wave 0 — Ground truth & guardrails

| WP | Title | Status | Branch | Notes |
|---|---|---|---|---|
| 01 | CI runs the tests we already wrote | 🟢 **MERGED** `3b0feea` | `ci/wp01-reconciled` (in flight) | **Two sessions built this independently.** `wp01/ci-gate@c00dc39` (chip) is CI-validated and carries the `NEXT_PUBLIC_USE_MOCK:'false'` fix; `ci/run-the-tests-we-already-wrote@4728000` (mine) has the guard test but the mock-mode defect and 3 vacuous assertions. FIX-01 merges best-of-both. |
| 02 | No test may pass vacuously | ⬜ | — | Every live-transport test is an env-gated silent `return`. `Live_ImapIngress` dead since `de4ea0e`. |
| 03 | Two production truth-checks | 🟡 **half done** | — | **Check 1 DONE 2026-07-27: `Connections__RevisionAuthority = true` on BOTH Railway services — refutes a P0 and rescopes WP-21.** Check 2 (is `unrouted` reachable on prod) still open: needs one test email with the org default cleared. |
| 04 | Orphan guard | 🟠 | BE `test/orphan-guard@8e3b8c7` · FE `@93fe22e` | Correctly RED (8 BE orphans, 3 FE). Refuted on 8 counts incl. 2 vacuous meta-tests. FIX-04 also lands the **shrink-only dated allowlist** so main stays green. |
| 05 | Mock/real parity harness | ⬜ | — | Sequence after the WP-07 ruling. |

## Wave 1 — Stop lying

| WP | Title | Status | Branch | Notes |
|---|---|---|---|---|
| 06 | Retire `/library/templates` + `OutputTemplate` | ⬜ | — | **Hold until WP-12 lands** — the redirect needs a live target. |
| 07 | Retire the duplicate rules engine | ⬜ | — | **Decision 1 answered: RETIRE.** Do NOT migrate the six seeded defaults as data. Depends on FIX-04. |
| 08 | Retire the dead routes | 🔵 | `fix/wp08-retire-dead-routes` | `/drafts`, `/upload/preview/[orderId]`, `parseStall`, and `/welcome` (verify the Stripe success path before deleting). |
| 09 | Retire webhook ingress | 🔵 | `fix/wp09-retire-webhook-ingress` | **Decision 2 answered: RETIRE.** Over-deletion is the risk — inbound email and OUTBOUND webhook subscriptions must survive. |
| 10 | Marketing truth | 🔵 | `fix/wp10-marketing-truth` | `/security` EU-residency, `/customers` invented pilots, landing palette + the 2.93:1 tile, hero reduced-motion. |
| 11 | Billing gate honesty | ⬜ | — | 4 wrong error codes (3 tests pin the wrong string); 10 of 16 gates unenforced; REST ingress ungated; cancel→read-only undisclosed. |

## Wave 2 — The wedge

| WP | Title | Status | Branch | Notes |
|---|---|---|---|---|
| 12 | Carry `OutputTree` through promotion | 🟠 | `feat/wp12-output-tree-reconciled` (in flight) | **Two sessions built this too.** Mine (`@8325243`) is broader — covers the revision-snapshot path the chip's (`@09c4839`) omits; the chip's has a 567-line test suite worth porting. 5 refuted defects: preview/delivery divergence, replay divergence, invisible pinned-tree snapshots, a vacuous fallback test, an uncovered catch. The real-Postgres jsonb proof was **skipped**. |
| 13 | Wire the promote control | ⬜ | — | Depends on WP-12. `promoteMapping()` still has zero call sites. |
| 14 | Widen the canonical output row | 🟠 | BE `feat/widen-canonical-output-row@0319996` · FE `@1b32d9f` | Widening is correct. 7 refuted defects — worst: `EffectiveEntityResolver.Clone` drops the 8 new line columns on the **live** delivery path, and the case-fold fix went in read-side only so the write path now creates duplicate `ItemMapping` rows. |
| 15 | Designer depth I | ⬜ | — | Reorder, typed JSON leaves, the 8 manipulators, CSV dialect. Needs **DB-2**. |
| 16 | Designer depth II | ⬜ | — | Structured conditionals, namespace presets, validator on the tree path, the silent format rewrite. Needs **DB-2**. |

## Wave 3 — Enforcement & recovery

| WP | Title | Status | Notes |
|---|---|---|---|
| 17 | Server-side acceptance gate | ⬜ | Depends on WP-07. |
| 18 | Validation at every breakpoint | ⬜ | Nothing evaluates below 1024px today. |
| 19 | Split 4xx; end the dead end | ⬜ | 401/404/429 are currently permanent. |
| 20 | Content type and filename | 🟠 | `fix/delivery-content-type-and-filename@a8f9391` — table is correct; overwrite tests proven **vacuous** by mutation, and `DeliveryConfigEditor.tsx` destroys the new config keys on save. |
| 21 | **Prove** revision authority | ⬜ | **Rescoped `L`→`M`** — the flag is already on. Now a proof packet + doc corrections. |
| 22 | Ingest duplicate prevention | ⬜ | Postmark has no dedupe; REST ingress is check-then-create. |
| 23 | `resolve` status guard | ⬜ | Performs transitions both state maps forbid. |
| 24 | Recovery UI | ⬜ | `transform_failed` CTA links to itself; every health deep link is inert. |

## Wave 4 — Concepts & UI  ·  Wave 5 — Self-running  ·  Wave 6 — Prove it

All ⬜ not started. WP-25…WP-41 per `01-WORK-PACKETS.md`. Wave 4 needs design briefs **DB-1** and
**DB-3…DB-6** first. WP-33 (auto-send) has its ruling: **automation, dry-run one full week before a
single real order moves unattended.**

---

## Verification method — why the status column is trustworthy

Every packet passes through `build → adversarial refute`. The refuter's only job is to break the
claim, defaulting to "refuted" when uncertain, and it independently re-runs each mutation check
rather than trusting the builder's report.

**Round 1 result: 5 packets built, 5 refuted.** Two blocked merge, three needed fixes. The most
common defect was a **vacuous test** — one that still passes with its own fix reverted. That is
exactly the failure class this plan exists to remove, and it was caught before anything merged.

## Standing environment facts

- **BE `main` is diverged** — 6 unpushed local commits belonging to other sessions vs 9 on
  origin/main. Never commit to it; branch off `origin/main`.
- **The shared FE checkout has stale `node_modules`** — `remark-gfm` is missing. Run
  `bun install --frozen-lockfile` before trusting any frontend test result.
- **FE CI has a workflow-level `env` block** (`ci.yml:13-27`) setting `NEXT_PUBLIC_USE_MOCK:'true'`.
  Any new job inherits it, and the vitest suite fails under mock mode.
- **Never run `railway variables` unfiltered** — one call leaked the live OpenAI key, PostHog key and
  Neon password. Pipe through `grep`.
- Worktrees must live under `<repo>/.claude/worktrees/<short-name>` — outside the repo they lose
  `node_modules`, and a long path hits Windows MAX_PATH mid-checkout.

## Open founder actions

1. **Rotate three secrets** — OpenAI API key, PostHog project key, Neon Postgres password.
2. **Reconcile BE `main`** — `git pull --rebase origin main`; expect STATUS.md conflicts, resolve in
   favour of origin's `c315a76` corrections.
3. **Merge `docs/v1-master-plan`** — note BE main auto-deploys the API to Railway.
4. **WP-03 check 2** — one test email to confirm `unrouted` is reachable on production.
5. **The ICP question** — are the two real customer POs in the repo (both *inbound*) fixtures, or is
   the first customer's job the mirror of the documented ICP?
6. **Decide `Buyers`** — a full CRUD surface whose data reaches nothing. Same class as
   `OutputTemplate`. Retire, or wire?

### 2026-07-30 — third session (`claude/stoic-tu-b94642`): the last local-Postgres-gated test now runs on CI

**Owns: no WP packet.** This was an unowned small packet, found and closed in one pass. Recording it so
a fourth session does not re-derive it.

**BE PR #81 — open, MERGEABLE, CI green.** `FireIntegrationTriggerJobReliabilityTests.`
`TwoConcurrentFinalFailures_OnPostgres_IncrementFailureCountByExactlyTwo` was the ONLY proof of the
`FailureCount = FailureCount + 1` relational lost-update guarantee, and it was gated on
`Host=localhost;Port=5435`. `ci.yml` has no postgres service and no port 5435, so on CI the gate always
missed and the test reported **Passed having asserted nothing**. Moved to
`ProcuLink.Api.Tests/Integration/WebhookFailureCountAtomicIncrementPostgresTests.cs` on the repo's
existing Testcontainers idiom — per-class `postgres:16`, `[DockerRequiredFact]`,
`[Collection("postgres-container")]`, `MigrateAsync()`. **No `ci.yml` change, no new package
reference**, test files only, production code byte-identical to `origin/main`.

- **CI run `30534967667`** — `Passed …TwoConcurrentFinalFailures_OnPostgres_IncrementFailureCountByExactlyTwo [57 ms]`.
  Not Skipped. Api.Tests 1709 → **1710**, Infrastructure.Tests 1150 → **1149**, 0 skipped in both. The
  old fully-qualified name appears **0 times** in the log — moved, not duplicated.
- **Not vacuous, proven by mutation:** replacing the relative increment with a load-modify-save bump
  fails it with `Expected sub.FailureCount to be 3 …, but found 2`. Reverted, rebuilt, re-ran green.
- This was the **last** test in either repo gated on `localhost:5435`. Nothing now depends on the local
  dev database.

**Relation to WP-02.** WP-02 re-gated this same test as a declared skip via `[LocalPostgresRequiredFact]`,
which made the reporting honest but left the coverage gap: it then reported *skipped* on CI. That edit is
now moot — the test actually runs there. Expect a small conflict in
`FireIntegrationTriggerJobReliabilityTests.cs` whichever of #81/WP-02 lands second; resolution is to keep
#81's version (the test is deleted from that file).

**Relation to BE #75 — checked, compatible in either merge order.** #81 covers the SURVIVING outbound
webhook subscriptions (`FireIntegrationTriggerJob`, `IntegrationSubscription`), which #75's own
`TheLiveNearNamesakes_AreStillPresent` lists as LIVE. `git grep` for
`WebhookIngress|WebhookReportableFrom|HasDispatchMarker|webhook-ingress` across both #81 files returns
**zero matches**, and #81 trips none of #75's forbidden-pattern list. #75 deletes
`WebhookStatusClaimPostgresTests.cs`, which #81 used only as a pattern template, never as a dependency;
`DockerProbe` (in `EndToEndPipelineTests.cs`) and `PostgresContainerCollection.cs` both survive on
`origin/wave1/backend-retirements`. **No file is shared between #81 and #75.**

**Corroborates the standing environment fact on BE `main`, independently.** Hit it before reading this
file: a branch cut from local `main` was born `mergeable: false, mergeable_state: "dirty"` conflicting on
`STATUS.md` — a file it never touched — and therefore received **zero** CI runs. Local `main` is 9 merged
PRs behind `origin/main` AND carries 6 unpushed local docs commits (`80e04a5` back to `aa8554d`). Fix is
`git rebase --onto origin/main <stale-main-sha>`, which replays only your own commits and leaves the
unpushed docs commits where they belong. Note also: after force-pushing, the REST poke
(`gh api repos/O/R/pulls/N`) returns the **old** head sha on the first call — call it twice before
concluding anything about `mergeable`.

**One correction to the shared prose, offered not applied** (append-only convention): "Docker is dead on
this machine" was not true for this session — `docker info` returned server version 29.5.3 and local
container runs succeeded (Api.Tests 1710/0/0 locally, matching CI exactly). The operative rule still
holds and is the reason #81 exists: a locally-skipped Postgres test is not a passing test, so the CI run
id is the citation.

### 2026-07-30 — merge session: Wave 1 landed in both repos, BE #75 held on a deploy-ordering defect

**Owns: no WP packet.** This session's job was to land what other sessions had built — commit, merge,
push, in the recorded order. Founder authorised the full Wave-1 sequence. Six PRs merged; one held.

| # | PR | `main` after | Evidence |
|---|---|---|---|
| 1 | FE #42 — WP-10 marketing truth | `214b3f3` | CI success |
| 2 | FE #46 — WP-10b setup-fee waiver | `a2b25f8` | CI success |
| 3 | FE #48 — WP-11 billing (FE) | `1cea11a` | CI success (run superseded on `main` by `cancel-in-progress`) |
| 4 | FE #47 — Wave 1 FE retirements | `ded9e04` | **FE `main` CI success** |
| 5 | BE #82 — WP-11 billing (BE) | `46b29fe` | **BE `main` CI success** |
| 6 | BE #80 — retire the `CustomTemplates` flag | `cd7feba` | **BE `main` CI success** |

**Order was load-bearing, twice.** FE #47 before BE #75 per the recorded constraint — verified rather
than assumed: on the merged #47 tree, `api/templates` and `api/rules` survive only inside a comment
explaining their removal, `webhook-ingress` has zero references, and `rule-definitions` correctly
survives (TRAP 3). FE #48 before BE #82, because #48 makes the frontend derive the plan from the API
response instead of matching code literals, so it tolerates the old codes *and* the new ones; the
reverse order would have shipped a frontend that mismatches live 403s.

**WP-06's "hold until WP-12 lands" did not apply.** The constraint was recorded as "the redirect needs a
live target". `src/lib/retired-routes.ts` points `/library/templates` at `/library/suppliers`, which is
already live — not at anything WP-12 introduces. Wave 1 shipped without WP-12/BE #74.

**Verified on production, not inferred.** All five retired routes answer 308 to the right destination,
including the parameterised one preserving the id (`/upload/preview/abc123` → `/inbox/abc123`).
`/customers` no longer serves the two fabricated pilot profiles and states it has no public references.
`api.proculink.eu/health` 200 after both backend deploys.

---

#### ⚠️ BE #75 IS NOT MERGED — its column drop breaks the Worker. This is a real defect, not caution.

The founder authorised the drop after a census showed it destroys almost nothing:

| Target | Production contents |
|---|---|
| `output_templates` | **0 rows** |
| `organisations.webhook_secret_encrypted` | **0 non-null values** |
| `validation_rules` | **7 rows, 1 org** — 6 created within 73 ms of each other on 2026-05-31 (the seeded defaults this migration deliberately refuses to migrate), plus one row named `asd`. Every row has `created_at == updated_at`: never edited. |

**Row count was the wrong risk metric.** A session working `wave1/backend-retirements` concurrently found
the actual hazard and is converting the migration to expand/contract. Recording it here because it
generalises well beyond this packet:

Migrations run **only at API startup** (`ProcuLink.Api/Program.cs` — `await db.Database.MigrateAsync()`).
The Worker (Railway service `aware-amazement`) is a **separate service that deploys independently and
never migrates**. EF enumerates every mapped column for a non-projected entity query, and two hot paths
materialise the whole `Organisation`: `StripeBillingService.LoadOrgAsync` — reached from
`EmailPollOrgJob`'s `HasFeatureAsync` gate on *every* IMAP poll cycle — and
`EmailSettingsService.UpdateAsync`. So from the moment a new API migrates until the Worker redeploys, the
still-old Worker throws Npgsql `42703 — column o.webhook_secret_encrypted does not exist` on every org
load: IMAP polling and every Worker-side billing gate stop, for an unbounded window if the Worker deploy
lags. Per CLAUDE.md the Worker is mandatory.

**The two `DROP TABLE`s are exempt** — no build, old or new, queries `output_templates` or
`validation_rules` at all, so they are safe in that same window. The hazard is specific to a column still
enumerated by a mapped entity.

Required sequence: (1) unmap the column, keep it physically; (2) confirm **both** Railway services run the
new build; (3) only then a hand-written drop migration — `dotnet ef migrations add` will not generate it,
since the model already omits the property. `Wave1ColumnDropStaysDeferredTests` exists to make step 3 a
deliberate act.

**→ Proposed as TRAP 6, offered not applied** (append-only convention): *"a column with no data is safe to
drop."* WRONG. Data loss and schema/code skew are different risks. `webhook_secret_encrypted` had zero
non-null values and dropping it would still have taken IMAP polling down. Applies to any packet dropping a
mapped column while the Worker deploys separately.

---

#### FE `main` went red at `e8fff8b`, and the cause was merge order

Not the Wave-1 merges — `ded9e04` was green. FE #41 (the route-reachability guard) merged **after** #47
deleted five routes, and #41 was built against a tree where they still existed. Two failures, both in
`src/test/route-reachability.test.ts`:

- `KNOWN_DEEP_LINK_ONLY` still parked `/drafts`, `/upload/preview/[orderId]` and
  `/library/rule-definitions`, annotated "going away when the packet that owns them lands". #47 *is* that
  packet. `the allowlist cannot rot` caught it — the test working exactly as designed.
- `a launch-filtered registry entry is not a link (the /drafts shape)` pinned the only two **live**
  examples of a registry href never rendered as a link: `/drafts` (in `NAV_MAIN`, filtered out by
  `LAUNCH_CORE_HREFS`) and `/library/rule-definitions` (a `match`-only `HUB_TABS` entry). #47 deleted both
  pages *and* both registry entries, so it failed with `expected [] to include '/library/rule-definitions'`.

Fixed in **FE PR #50**: shrink the allowlist, and rewrite the second test around the invariant rather than
the two dead entries. No coverage lost — `a registry href for something never rendered as a link is a
phantom target` already pins that mechanism with a fixture, which is the form that does not decay when the
app's registries change.

**A guard that pins live data instead of a fixture has a shelf life.** #41's own self-link test says as
much in its comment ("pinned with a fixture because the real app has no self-only route"), and the two
assertions that did the opposite are exactly the two that broke.

---

#### Process notes for the next session that lands work

**A clean worktree does not mean the chip finished.** `wave1/backend-retirements` showed `0` dirty files
and `0 0` against origin at the start of this session; ~40 minutes later it had 6 modified files and a new
`Wave1ColumnDropStaysDeferredTests.cs`, most recent write 3 minutes old. Merging on the strength of the
earlier reading would have shipped the version that session was mid-way through fixing. Re-check the newest
mtime **immediately before each merge**, excluding `node_modules`, `.git`, `.next`, `obj`, `bin` — and
`tsconfig.tsbuildinfo`, which gave a false "live" reading.

**A dirty worktree can be mid-mutation-check, not mid-feature.** `wp04/route-reachability-fe` looked like
uncommitted work worth rescuing. Its tree contained `if (false) continue;` — a probe disabling the registry
exclusion — and the *uncommitted* diff was the restoration plus the two meta-tests that catch it.
Committing that snapshot would have landed a dead guard. To back out of a chip's branch without disturbing
its files: `git reset --mixed <pushed-head>` leaves the working tree untouched. Back the files up first —
`git merge --abort` cannot always reconstruct pre-existing local modifications.

**Several branches had `origin/main` as their upstream.** `feat/promote-output-tree-to-supplier`,
`feat/widen-canonical-output-row`, `fix/delivery-content-type-and-filename`, `test/orphan-guard`,
`ci/run-the-tests-we-already-wrote` — a bare `git push` from any of them pushes to **`main`** and deploys.
Always use an explicit `src:dst` refspec when pushing chip branches.

**Eight branches held commits that existed only on this disk** (unreachable from any remote), including
local `main`'s four docs commits and the 9-commit `codex/video-remake-2026-07` track. Pushed as backup refs
— CI in both repos triggers only on `main` and on PRs targeting `main`, so a backup ref costs nothing.
Local BE `main` was left diverged and untouched; its commits are preserved at
`backup/be-main-local-2026-07-30`.

**`bun install --frozen-lockfile` before trusting any frontend result** — the standing note is right, and
the failure is deceptive rather than loud: the shared checkout was missing `remark-gfm`, which fails 3
files at **collect** while the summary reports `Tests 1165 passed` and `0 failed`. After installing:
107 files / 1205 tests pass.

**BE #77 (WP-20) untouched**, per the founder gate — it changes what real suppliers receive. Its hard
dependency FE #43 is likewise unmerged, so nothing has half-shipped.

---

### 2026-07-30 — FE session (`claude/elegant-colden-a2c779`): plan-gate upsell UI, claimed and done

**Packet: wire the plan-gate helper into every surface WP-11 newly gates.** Branch
`wp11/plan-gate-wiring`, commit `8910d94`, **stacked on `wp11/billing-honesty` (FE #48, still open)** —
`src/lib/planGate.ts` does not exist on FE `main`, so this cannot merge before #48. Merge order:
**FE #48 → this**.

Five surfaces were showing a gated customer either a generic failure or the raw
`<capability>_requires_<plan>` token: `/operations/log` (audit), the supplier delivery editor, both
revision-editor paths (the mapper's draft save and the lifecycle notice), bulk mapping import, and the
supplier acceptance profile. Two of those were worse than "unhelpful" — the audit log blamed the network
("Check your connection") and offered a Retry that can only 403 again, and the acceptance tab paints its
notice in the **success green**, so a refusal read as a confirmation.

#### The wiring was not the whole fix — four api calls threw the gate away first

`getAuditLog` summarised every failure as `` `audit: ${res.status}` ``; `createConnectionDraft`,
`updateConnectionDraft` and the publish/archive lifecycle used `res.statusText`. The gate lives **only in
the body** — the status line carries none of it — so the code never reached the component and no amount of
wiring in the UI could have matched it. Those four now keep a plan-gate body **verbatim** (code *and*
`upgradeUrl`) on the Error message; every other failure keeps the message it had, including the 409s that
already read the body for their own copy.

Worth generalising: **a 403 whose meaning is in the body cannot survive a client that summarises failures
by status.** Any other call added later that does `throw new Error(\`x: ${res.status}\`)` will silently
re-break this.

#### On the "codes name the wrong plan" trap — already fixed upstream, do not re-fix in the frontend

BE #82 merged 2026-07-30T11:45Z ("stop 403s naming the wrong plan"): the server derives the plan segment
from `PlanConstants.GetMinimumPlan`. The frontend therefore **parses the plan out of the code it receives
and never writes a plan name of its own**. Reading `PlanConstants` from the frontend, or mapping codes to
tiers in a component, would rebuild the exact hardcoded ladder WP-11 deleted — and it would drift again on
the next re-tier. The shipped helper says so in its own header comment; it is a deliberate constraint, not
an oversight.

#### Unenforced gates are not a risk for this packet

Only 6 of 16 `BillingFeature` gates fire today. The banner is **response-driven** — it renders only when a
403 carrying that code actually arrives, and it never reads plan state — so a gate that does not fire
produces no upsell and makes no promise the product cannot keep. No coordination needed on that axis.

#### Details worth knowing

- One shared `PlanGateNotice` (`src/components/bridge/PlanGateNotice.tsx`) + an `isPlanGate` guard, so the
  six render sites share one shape and one copy pattern.
- The upsell link uses the **server's** `upgradeUrl`, sanitized to an in-app path — `//host`, `https://`
  and `javascript:` all fall back to `/settings`. A 403 body must never be able to turn an error banner
  into an off-site link.
- The acceptance-profile notice is now typed `ok`/`err`. It was a bare string in a green box.
- 21 new tests across 8 files, each written RED first. Gates green: `bun run test`
  (116 files / 1185 tests), `lint`, `check:pageshell --strict`, `lint:vocab`.

#### Confirming the standing `remark-gfm` note, with the precise cause

Independently hit and independently diagnosed: the shared root `node_modules` is installed from **whatever
branch the main checkout happens to sit on**, so a worktree on a newer branch can be missing a *declared*
dependency. The FE main checkout is on a `main` that predates `remark-gfm@^4.0.1`, which fails
`src/test/seo-host.test.ts` at collect on a `next.config.ts` import. Fix is `bun install` **inside the
worktree** (~90s), not at the repo root, which other sessions share and which would reinstall the stale
branch's dep set. Afterwards revert `public/mockServiceWorker.js` — msw's postinstall rewrites it and it is
tracked.

### 2026-07-31 — Wave 4 CLAIMED: WP-27, WP-28, WP-29, WP-30, WP-31, WP-32

Session `claude/agitated-chebyshev-339d7b` (frontend). All six Wave-4 UI packets claimed. Branches, one
worktree each off FE `origin/main` = `478b809`:

| Packet | Branch | Batch |
|---|---|---|
| WP-27 onboarding | `claude/wp27-onboarding` (+ BE `claude/wp27-sample`) | 1 |
| WP-29 inbox `ready` state | `claude/wp29-inbox` | 1 |
| WP-30 design-token lint | `claude/wp30-tokens` | 1 |
| WP-32 Clerk degraded state | `claude/wp32-degraded` | 1 |
| WP-28 workshop density | `claude/wp28-workshop` | 2 — sequenced after WP-24's files settle |
| WP-31 a11y | `claude/wp31-a11y` | 3 — deps WP-30; touches `OnboardingWizard` which WP-27 rewrites |

**Sequencing reasons, not arbitrary:** WP-31 owns all 17 `aria-modal` dialogs including
`OnboardingWizard.tsx`, which WP-27 is rewriting — running them together guarantees a conflict.
WP-31 also depends on WP-30's token work. WP-28 edits `workshop/OrderWorkshop.tsx`, which WP-24
(`7cabd4f`) changed by 117 lines five days ago.

**⚠️ CORRECTION TO WP-27's PREMISE — verified first-hand, recorded before the packet builds on it.**
The packet says first run "dead-ends at delivery configuration because every terminal channel needs
supplier cooperation." That is **not wholly true**.
`ProcuLink.Core/Constants/DeliveryProtocolConstants.cs` already ships `Email = "email"` — delivery via
the Postmark HTTP API, whose own docstring says *"Supplier supplies only recipient addresses; mail is
sent FROM ProcuLink's verified sender"* — plus `EmailApiDeliveryDispatcher`. `Smtp` is explicitly
RETIRED there (Railway blocks outbound SMTP ports 25/465/587) and kept only for legacy configs and
self-host opt-in. The frontend `DeliveryProtocol` union at `src/lib/api/types.ts:568` already lists
`"email"`.
So the real gaps are narrower and more tractable than the packet states: whether `email` is **offered
and defaulted** in the onboarding delivery step, whether a **`download` terminal channel that actually
transitions an order to `delivered`** exists at all (WP-34 shipped artifact download, which is not the
same thing), and whether the sample order seeds a delivery config. Same failure shape as the other
five corrections — two true facts (delivery needs an endpoint; onboarding dead-ends) with one
unchecked step between them (what `DeliveryProtocolConstants` already contains).

**Other line numbers that have already moved since the packets were written** — every agent is
instructed to re-verify rather than trust them:
- WP-28's emoji-as-icon is at `src/components/bridge/workshop/OrderWorkshop.tsx:733`, not `:746`, and
  the file is under `workshop/`, not directly under `bridge/`.
- WP-29's `InboxView.tsx` / `UnifiedStatusBadge.tsx` / `BridgeDashboard.tsx` line numbers predate
  WP-24 (`7cabd4f`, +160 lines in `InboxView`) and WP-25 (`478b809`, which rewrote
  `UnifiedStatusBadge` labels wholesale). The "Ready to send" count divergence may already be fixed;
  the cross-screen count-parity test ships either way, because the test is the deliverable.


---

## 2026-07-31 — WP-18 "Validation at every breakpoint" (FRONTEND) — shipped, PR #64

Branch `wp18/validation-every-breakpoint` off FE `origin/main` `478b809`.
PR: https://github.com/dimnovare/project-proculink/pull/64 (NOT merged).
Commits `03dd2f2` (fix + 15 tests) and `8d2fd5c` (wire correction + 4 tests).

**What was actually wrong.** The supplier acceptance answer reached the UI through one query,
`getFieldValidation` (`GET /api/orders/{id}/validation`), living inside `useMapperModel` — which only
`MapperWorkbench` builds. `OrderWorkshop` mounts that inside `hidden lg:flex`, and the derived
`blockingCount` was consumed only by the mapper's own `canDeliver` (`MapperWorkbench.tsx:546`). It
never reached `OrderWorkshop.canSend` (`:450`). The send gate was therefore computed with no
knowledge of the supplier's rules **on every surface**, and a 390/768px operator — whose only surface
is `MobileTriage` — was offered a Send that WP-17's server gate refuses, with no indication why.

**Fix.** Hoisted the query into `OrderWorkshop` above every breakpoint-conditional subtree; projected
its blocking rows onto `WorkshopIssue` and merged them into the single `issues` array that already
feeds the desktop `IssuesPanel`, the status-bar blocker chips, `MobileTriage`, and
`blockingIssues → canSend`. One merge, every breakpoint. New pure module
`src/components/bridge/workshop/acceptanceGateModel.ts`. No layout was un-hidden; the 3-column Order
Workshop is untouched (R10).

**Defer, not mirror.** WP-17 (`8a2dbc3`) also made that endpoint's `blocking` flag derive from
`ISupplierAcceptanceService.GetBlockingFailuresAsync` — the same call `IAcceptanceGate` acts on,
frozen by the set comparison in `ValidationBlockingMatchesTheGateTests`. The client re-implements no
rule; it counts rows the server already decided, so the two cannot drift. The invariant rows
(`po_number_present` etc.) stay advisory on both sides, deliberately.

**⚠️ CORRECTION TO WP-18's PREMISE — the packet's second claim is refuted.** The packet states
"`useAcceptanceValidation` is dead code — its confirm-dialog and fix-queue branches are unreachable."
It is imported at `OrderWorkshop.tsx:41` and mounted at `:164`; `validationResult` feeds
`buildFixQueue` (`:189`) and `failingRuleCount`/`isStale` feed `ConfirmDialog` (`:904-905`). The
branches are reachable. The hook was **inert, not dead**: `validate()` — the POST that populates it —
has zero callers anywhere in `src/`, so `validationResult` was permanently `null` and the confirm
dialog's failing-rule acknowledgement had never once appeared in production. Decision: **wired**, not
deleted (deleting would drop a real surface with no replacement, which R4 forbids). Same failure
shape as the other corrections: two true facts (the query is mapper-owned; the hook's outputs look
inert) with one unchecked step between them (whether the hook itself is mounted).

**Path drift.** The packet cites `src/components/bridge/review/OrderWorkshop.tsx`; the file is at
`src/components/bridge/workshop/OrderWorkshop.tsx`. The `hidden lg:flex` container is at `:789` and
`<MapperWorkbench>` at `:792`, not `:802`. `useMapperModel.ts:269-276` held exactly.

**Self-caught defect, recorded because it is instructive.** The first wire seeded `failingRuleCount`
from the BLOCKING count — but a blocking row sets `canSend=false`, so the confirm dialog can never
open and that count could never be read. The signal that actually reaches the acknowledgement is the
ADVISORY failure. Corrected in `8d2fd5c` with the R1 consumer test that was missing.

**Verification.** 19 new tests at 390/768/1440 (`validationEveryBreakpoint.test.tsx`); RED first,
verbatim output in the PR body. Three mutation checks run after committing the fix: reverting the
`issues` merge → 9 RED; neutering the hoisted `queryFn` → all 19 RED (including the negative
control); reverting the hook fallback to `0` → 3 RED. Full suite 136 files / 1601 tests green;
`tsc --noEmit`, eslint, `check:pageshell --strict`, `lint:vocab` all clean. Party nouns route through
`partyLabels`.

**Deliberately not done.** No client-side rule mirror. No gating on the query's loading state (would
flicker the primary CTA disabled on every page load). No consumption of WP-17's richer
`GET /api/orders/{id}/acceptance-gate` or its operator-override POST — `GET /validation` already
carries the gate-aligned answer and was already plumbed client-side; a second client for the same
decision is one more thing that can drift. **The operator-override flow remains unbuilt on the
frontend and wants its own packet.**

---

## 2026-07-31 — WP-21 · Prove revision authority (BE PR #98, OPEN)

**The packet's own premise was the audit's mistake, and it stayed refuted.** WP-21 was filed as a
P0 — "revision authority is off in production; the versioning subsystem is inert there" — from
reading `ProcuLink.Api/appsettings.Development.json:46` and never reading the deployed environment.
Re-verified at the start of this packet, filtered, both services:

```
railway variables --service ProcuLink       | grep -i revision  →  Connections__RevisionAuthority=true
railway variables --service aware-amazement | grep -i revision  →  Connections__RevisionAuthority=true
```

Nothing was retired. The packet proved the subsystem works, made its state readable, and corrected
the record.

**RED first (rule R2).** Six tests failed on CI run `30628425794` before any production change —
verbatim messages in the PR body. Two of them are worth repeating because they name the gap
precisely:

- `GET /health/ready must carry a top-level 'revisionAuthority' boolean — the deployed value of Connections:RevisionAuthority has to be readable without shelling into Railway.`
- `readiness must expose a 'revisionAuthority' check; found: database, migrations, storage, worker`

**Shipped.**

| Deliverable | What landed |
|---|---|
| (a1) automated proof | `PinnedOrderDoesNotRerouteAfterConfigEditPostgresTests` — real Postgres. Publish v1 (old endpoint) → pin an order → edit the live config → republish (v1 **archived**, v2 active) → dispatch through the real `DeliveryService`. Pinned goes to v1's endpoint, unpinned to the new one, and the test asserts they **differ** (R6). A companion runs it flag-off and asserts they are **identical** — so the difference is the flag and nothing else. |
| (a2) production smoke | `docs/ops/revision-authority-production-smoke.md`. **NOT EXECUTED** — production writes were not authorized. Pre-list, disposable request bins, pass/fail table, undo. §2 is two read-only commands that answer "is it on right now" in a minute. |
| (b) doc corrections | Both `Program.cs` registrations, `IEffectiveConnectionConfigResolver`, `EffectiveConnectionConfigResolver.FlagKey`, `SupplierConnectionService`, `STATUS.md`, the prelaunch-audit P3 (written conditionally on the flag being on — the condition is met, so it is live), plus in this plan: the FULL-VERDICT capability-table cell that still read `✗ flag off in prod`, its stale action item 9, and the ledger's "still unproven" clause. |
| (c) readable value | `RevisionAuthorityHealthCheck` (tag `ready`, always Healthy) + a flattened top-level `revisionAuthority` boolean on `/health/ready`; and a startup announcement of the **parsed** value on every host via `StartupConfigurationValidator`, which is the Worker's only possible surface — it serves no HTTP and it is the host that runs parse/transform/deliver. |
| (d) future hosts | `RevisionAuthorityHosts.All` + `RevisionAuthorityHostCoverageTests`: a source scan of every `Program.cs` for an `IEffectiveConnectionConfigResolver` registration, asserted equal to the roster, with the runbook required to name each entry. A third host cannot ship without someone deciding about its variable. |

**The gap that remains is observational, not behavioural.** The live production run of the runbook
is unperformed and is a founder action. Everything provable without touching production data is
proved; nothing about the deployed behaviour is asserted here on the strength of a test alone.

**What was deliberately NOT done.** `DeliveryService.TestFireAsync` still validates the LIVE
delivery config rather than the pinned revision, so a green test-fire can vet a different channel
than a pinned order will use (the P3 in `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md`). Now
that the flag is confirmed ON, that P3 is live rather than conditional — it was re-labelled, not
fixed. It wants its own packet.

---

## 2026-07-31 — POST-WAVE ADVERSARIAL AUDIT of all sixteen merged packets

Full report: `ProcuLink/docs/qa/2026-07-31-post-wave-regression-audit.md` (BE PR #105).
Audited BE `origin/main` `504d9cc` · FE `origin/main` `478b809`, zero open PRs at start.
Seven verifiers, one attack lens each. Mutation testing wherever the suite was safe to run
(`Transform.Tests`, FE vitest); analytic per-test reading where `dotnet test` is hard-barred.
Every finding in the report is labelled `ran-it` or `analytic`.

**One fix shipped, in its own PR: FE #66** (CI green, run `30629717279`). See "the guards" below.

### The four a customer hits today

1. **WP-12 changed delivered bytes, and its designer contradicts its own gate.** The format-equality
   gate is NEW at `d233409` — verified against parent `3878c0c`, which checked only cXML/X12 and never
   format. `OutputStructureDesigner.tsx:147` seeds `defaultTree("json")`, `:33-36` offers a free format
   radiogroup, `:230` previews at the TREE's format (`honorFormat` defaults false, so the preview gate
   always matches) under the in-file label at `:222` — *"exactly what will be delivered"*. Delivery then
   drops the tree with a `LogWarning`. `git grep outputFormat` over that file returns **zero hits**; its
   `<OutputSourcePicker>` mount omits the one prop that carries the honest "this connection delivers X"
   note. And any order carrying a mismatched tree **was delivering it at `3878c0c` and stopped at
   `d233409`** — silently, no migration, no notice.
2. **`rejected_by_supplier`: the backend opened the exit, the frontend pins it shut, and a test
   forecloses the fix.** `OrdersController.cs:1601` reads `TransformableFrom`, which includes the status,
   so `POST /transform` answers 202 today. `problemActions.ts` **declares itself a mirror** of
   `OrderStatusMachine` and is not one — drifted in BOTH directions (it also admits `pending_review`,
   which `TransformableFrom` excludes). `problemContract.test.ts:110-113`, titled *"the guard mirror
   itself matches the backend's sets"*, goes **RED** when the mirror is corrected (mutation, `ran-it`).
   `OrderStatusMachineTests…:713-716` calls this "the operator's one-click exit"; there is no click.
   **Found independently by two verifiers, one analytic from the BE side and one by mutation from the
   FE side** — genuine convergence with independent provenance.
3. **WP-24's rejected-order primary CTA is inert.** `problemCopy.ts:372` → `?details=response`; the
   workshop reads `?tab=`, in a `useState` initialiser. The panel is a banner already rendered at that
   route, so "See their reply" lands on the same screen. This is verbatim WP-24's own D1 defect,
   reintroduced. The contract walk missed it because `appRoutes.ts:66-70` strips `?` before matching —
   **query-blind by construction** — and `problemContract.test.ts:74` holds the broken value up as its
   example of a live destination.
4. **WP-17 refuses orders naming a remedy that has no frontend.** `IAcceptanceGate.cs:193-194` composes
   "…or record an override saying why it should go anyway", returned verbatim as the 409 body and as the
   `transform_failed` errorMessage. `OrderAcceptanceGateController.cs:74-106` exists; FE grep for the
   endpoint, `recordOverride`, `overrideReason`, `acceptanceOverride` → **0 hits**.
   `SupplierDockProfile.tsx:440` defaults a new rule to `blockOnFail: true`.
   **Relevant to WP-18 (#64, open):** wiring the gate into the send decision on every breakpoint makes
   this refusal reach *more* operators, so the override UI question gets more urgent, not less.

### Corrections to this plan

- **"WP-12's real-Postgres jsonb proof was SKIPPED" is FALSE.** CI run `30578073101` (headSha
  `d233409`) shows `DesignOnOrderA_Promote_OrderBRendersByteIdentically_ThroughRealPostgres`,
  `PromotedTree_SurvivesTheJsonbRoundTrip_WithNamespacesAndPredicateIntact` and
  `PromotedTree_ProducesAStableConfigDigestAcrossOrders` as **`Passed`** with timings. CI runs
  `dotnet test ProcuLink.slnx` on ubuntu-latest where Docker is live, so `[DockerRequiredFact]` does not
  skip. All five of WP-12's pre-merge defects are fixed at `d233409`; none merged unfixed.
- **The audit's "`getDownloadUrl` has zero callers" is refuted** — `OrderPassport.tsx:338`, rendered via
  `OrderDetailsDrawer.tsx:200`, 503 lines of tests.
- **TRAP 6 and TRAP 7 are both CLOSED as merged** — verified by planting real orphan pages (`.tsx` and
  `.mdx`) and a real phantom-registry page, not by reading regexes. Both went red.

### The guards err toward leniency more than the TRAPS say

Three new bypasses, every one run end to end against the real guard:

- **FIXED (FE #66).** `sourceScan.ts` `stripComments` used `prev !== ":"`, and `prev` is the last
  NON-WHITESPACE character — so every colon in the language exempted its line from stripping. An orphan
  page whose only referrer was `ready: // <Link href="/…">` passed reachability **18/18**. Four live
  instances on origin/main, none currently holding a link. Mutation-checked fix.
- **OPEN.** A markdown link inside an **MDX code fence** is credited as navigation — the mdx branch has
  no fence state. Latent (0 live). `check-vocabulary.mjs`'s `mdxScanner` already tracks fences; the
  precedent exists.
- **OPEN.** `extractRaw` runs its `re` patterns against **unmasked** text, so a path inside a string
  literal confers reachability. The literal masking the module header advertises applies only to anchor
  patterns.
- **OPEN (BE).** Naming a service in a **string literal**, an attribute string or a `#region` discharges
  the one-hop consumer obligation — `StripComments` preserves literals by design and the reference check
  cannot tell them apart. Proven by driving the real `OrphanDetector`; the harness reproduced exactly the
  six known `KnownWriteOnlyStores` entries as a fidelity control. **Not** introduced by `7ed0961`.
- **The FE allowlist is not shrink-only in any enforced sense** — the three hygiene tests check reason
  quality and route existence only. A planted orphan plus an entry whose reason was copied *in shape*
  from the guard's own fixture passed 18/18. The BE side's `MayOnlyEverShrink` is a two-literal edit in
  one file.

### The anti-vacuous scanner's GREEN is narrower than it reads

`VacuousTestPassScanner` (PR #79, `0184261`) is well built, but its only rule is "a valueless `return`
before an assertion". Nine shapes executed against it: the canonical offender **correctly flagged**
(control), and **eight scanned clean** while verifying nothing — `if (env) { asserts }` with no return,
vacuous `foreach`, an assertion inside a lambda defined before the guard (`AssertionPrecedes` is purely
positional and not scope-aware, unlike `BelongsDirectlyTo`), `Assert.True(true)` as a satisfying
"assertion", `goto`/`break` exits, and two non-hardcoded `Task`/`ValueTask` return spellings.
Three live `foreach` instances already in the suite (all mitigated by siblings, none currently a real
hole). **`ProcuLink.TestSupport` is compiled into two of the three test assemblies but sits outside the
`*.Tests` glob** — latent, zero `[Fact]` there today.

Read a green run as *"no bare early return before an assertion"*, not *"every test asserts something."*

### What held up under attack

`delivery_unconfirmed` **cannot** be re-sent in one click — all seven historically-drifting lists
enumerated and agreeing, and WP-19 and WP-24 added the park to **none** of them; the FE half
(`inboxSend.ts`, `bulkSendNeedsDuplicateConfirm`) mirrors it correctly. WP-14's four named pre-merge
defects are genuinely fixed and their tests are load-bearing (reverting `Clone` turns 5 tests RED;
1406/1406 restored). WP-20's media-type table mutation-proven twice; its overwrite-test vacuity was fixed
by ADDING the wiring file. WP-34 hashes the bytes actually dispatched and survives both the
"marker-not-a-row" and "re-transform blinds both rescuers" hazards. WP-19's 4xx split is sound and
`Retry-After` survives the whole path. WP-11 shrank the billing enum honestly. The Wave-1 retirement
migration drops tables only and explicitly refuses the `webhook_secret_encrypted` column drop.
`standards/catalog.ts` was not swept. The WP-25/26 inbound-mode binding constraint was respected.

### CORRECTION LOG — entries 6 through 9, all mine, all caught before they shipped

| Claim | Verdict | The unchecked step |
|---|---|---|
| "SFTP/S3 pull have no config surface, so the `live` badge is false" | **REFUTED** | `SettingsController.cs:126-180` ships GET/PUT `sftp`/`s3`, plan-gated, with a save-time SSRF pre-check. I read a month-stale QA doc; `STATUS.md:1086` already flags it as outdated. *Is the doc still true?* |
| "WP-11 left 10 of 16 billing gates unenforced" | **REFUTED** | The enum is now 10 members — WP-11 **deleted** the five that had no enforcement point. *Did the denominator change?* |
| "Cxml/WebhookDelivery/ErpConnectors have no gate — `HasFeatureAsync` is absent from `UpsertDeliveryConfig`" | **REFUTED** | `SuppliersController.cs:758` gates via a resolved `gated` object at `:776`. *Is `HasFeatureAsync` the only gate mechanism?* |
| "The three poll jobs named in `EnforcedBy` do not gate" | **REFUTED** | They live in `ProcuLink.Worker/Jobs/`, not `ProcuLink.Infrastructure/`. *Did I grep the right project?* |

The shape held in all four: two true facts, one unchecked step. Eight more claims were refuted the same
way in the report's §7, including one of this plan's own. **The ratio is the finding** — twelve refuted
against thirteen kept is what makes the thirteen worth acting on.

### Method note worth keeping

**The stale-build trap fired during this audit and was caught.** Restoring a mutated file from a `.bak`
via `Move-Item` preserves the backup's OLD mtime, so MSBuild skips recompilation and the "restored" run
reports the MUTATED result. `git status --short` showing clean is **not** sufficient — a mutation recipe
in this repo needs an explicit `touch` after restore.

---

## 2026-07-31 — Merge sweep (main session)

Swept every open PR in both repos against the merge-train rules. **Five landed, two held, one
rebased.**

| PR | Verdict |
|---|---|
| FE #66 — comment-after-colon in `stripComments` | **Merged first, deliberately.** Both link guards import that module, so it is the one change that can invalidate another PR's green. Landing it ahead of #64/#67 means their CI ran under the corrected scanner, not the leaky one. |
| FE #64 — WP-18 acceptance gate at every breakpoint | Merged (`d1a6b9c`). |
| FE #67 — WP-32 bounded sign-in wait | Merged (`3593273`). |
| FE #68 — WP-23 refusal reads as a sentence | Merged by its own session mid-sweep (`831fad1`). |
| BE #105 — post-wave regression audit report | Merged (`630922b`), docs-only onto `main`. |
| BE #109 — audit appended to this file | Was `dirty`: the plan branch had gained the WP-21 section at the same append point. Rebased onto `0faee14`, both sections kept in date order, merged as `6fb1cf5`. |
| FE #65 — WP-13 promote control · BE #97 — WP-22 dedupe | **Held.** Draft, and the wedge session was mid-turn. Not ours to land. |
| BE #100 — WP-27 sample order | **Held: RED on its own new tests.** Four failures on run `30628839145`; three of them say the same thing — no delivery-config row exists after `CreateAndEnqueueAsync` — and the fourth says `IsSample` never reaches the DTO. Handed back to the Wave 4 session with the verbatim failures. |
| BE #98/#99/#103/#106-#108/#110-#115 | Untouched. Drafts and `[DO NOT MERGE]` mutation throwaways belonging to live Wave 3 work. |

**Post-merge state.** FE `main` `831fad1`, CI run `30632392352` **green** — this is the run that
matters, because each PR's own CI had passed against a `main` that four merges later no longer
existed. BE `main` `630922b`. Both working trees clean.

**Rule that earned its keep.** Merge-order is not arbitrary when one of the PRs edits a guard.
#66 tightened what the reachability and link-crawl guards can see; had it landed last, #64 and
#67 would have carried a green that was computed under the leaky scanner.

### 2026-07-31 — WP-18 addendum: the adversarial review REFUTED the first cut

Two independent refuter runs over `wp18/validation-every-breakpoint` both returned **REFUTED**.
Both converged on the same findings. Commit `febe84b` fixes the two P1s; the third item is
recorded as an accepted, documented limitation.

**P1-a — OVER-BLOCKING (the packet's own defect, pointing the other way).** The first cut read
`GET /api/orders/{id}/validation`'s per-field `blocking` flag. That flag is the RAW
blocking-failure set. The gate's decision is that set MINUS a recorded operator override
(`AcceptanceGate.cs:62-77`), so an overridden order is `Blocked=false` with a NON-EMPTY
`blockers` array. Counting blockers refused a send the server performs — and since
`POST /acceptance-gate/override` has no frontend surface at all, there was no way out of it.
That is a strict regression against `origin/main`, where Send was enabled and the server
decided. **WP-17 shipped `GET /api/orders/{id}/acceptance-gate` for exactly this and WP-18
did not call it.** Now consumed; the client renders `decision.blocked` and derives nothing.

The shape again, exactly as the correction log predicts: two true facts — (1) WP-17 made
`/validation`'s `blocking` derive from `GetBlockingFailuresAsync`, (2) the gate acts on
`GetBlockingFailuresAsync` — with one unchecked step between them: **the gate does not act on
that set directly, it acts on that set minus the override.** I verified fact (1) in the real
controller implementation and still missed the step.

**P1-b — FAIL-OPEN.** `acceptanceQuery.isError` was consulted nowhere, so a 500/timeout left
`data` undefined → no blockers → green Send. The server refuses to transform when it cannot
evaluate the gate (`acceptance_gate_unavailable`), so unknown must gate too. Now raises a
legible "we couldn't check this order" blocker. Reads the query's STATUS (`isError ||
isPending`), not `data === undefined` — the blunt version broke three existing
`invariants.test.tsx` cases whose `useQuery` stub returns only `{data: undefined}`.

**Also fixed:** the "Where →" jump was a dead click (the server's rule code is
`{fieldPath}.{operator}`, which `resolveRowRef` cannot resolve — it splits ids on
non-alphanumerics); `failingRuleCount` was counting invariants and `output.*` rows that
`ConfirmDialog` labels "acceptance rules"; the same endpoint was being fetched twice per
desktop page under two keys with divergent `staleTime`; and the test fixtures used key shapes
the endpoint never emits, which is what concealed the dead jump.

**ACCEPTED LIMITATION, not fixed — the 390/768/1440 axis is decorative in jsdom.** Mutating
`setViewport` to a no-op leaves all 27 tests green. Nothing under `workshop/` reads
`innerWidth`/`matchMedia` and Tailwind is not applied, so the three variants execute one code
path. The tests DO pin that the gate is breakpoint-independent by construction and that BOTH
operator send controls are asserted every run (a desktop-only fix fails: 9 of 27). A genuine
per-breakpoint proof needs Playwright and was out of packet scope. **Anyone claiming
"validated at every breakpoint" from this suite alone is overclaiming — say "breakpoint-
independent by construction" instead.**

**Mutation matrix after the fix** (each verified applied before running, reverted after):
M1 revert issues merge → 13 RED; M2 neuter queryFn → 21 RED; M3 revert hook fallback → 3 RED;
M4 override-blind → 6 RED; M5 drop unavailable branch → 3 RED; M6 drop placeholderData → 1 RED;
M7 drop commitVersion from key → 1 RED; M8 canSend ignores blockingIssues → 9 RED;
M9 setViewport no-op → **27 GREEN** (the limitation above).

Suite 136 files / 1609 tests green; `tsc --noEmit`, eslint, pageshell, vocab all clean.

**Left for a follow-up packet:** the operator OVERRIDE UI. `POST /api/orders/{id}/acceptance-
gate/override` is live server-side with audit + typed refusals and has no browser surface, so
an override can be honoured but not created. Also note `useMapperModel`'s `blockingCount`
still reads the raw `/validation` flag and remains override-blind — a pre-existing
inconsistency WP-18 deliberately did not take on.

### 2026-07-31 — wedge session: WP-13 and WP-22 delivered, WP-15 started

Own packets: **WP-13, WP-22, WP-15/16**. Three PRs open, none merged (merges are a founder gate).

| Packet | PR | State | Evidence |
|---|---|---|---|
| WP-22 | BE **#97** | **ready, MERGEABLE** | CI `30628196330` pass · re-run with the new guard `30631208317` pass |
| WP-13 | FE **#65** | draft, MERGEABLE | RED 13/8 → 1654 tests green · **23/23 mutations RED** |
| WP-15 S1+S2 | FE **#71** | draft | RED 4 → green · **14/14 mutations RED** |

#### WP-22 — the work existed; what it needed was verification, and one gap CI could not see

Rebased `fc6876a`/`b6aa80e` onto `origin/main`. **The rebase reconciliation was not mechanical.**
`IngestPathBillingGateTests` (added on main by WP-11) had two `Times.Never` assertions — *"a refused
push must not create an order"* — verifying `IOrderService.CreateStubFromParsedOrderAsync`. WP-22
moves REST ingress onto `IClaimedOrderCreator`, so **both assertions would have passed with the
billing gate deleted.** Re-targeted, plus the harness now stubs `ClaimAsync` instead of the
check-then-create lookup it replaces.

**Mutation-checked on CI, one per channel** (local `dotnet test` is banned, so CI is the only place
these run):

| Mutation | Run | Result |
|---|---|---|
| A — a LIVE idempotency claim reports as new (the check-then-create failure mode) | `30629557271` | **Failed: 4**, all REST-ingress |
| B — an existing email claim never SKIPs | `30629561917` | **Failed: 4**, all Postmark |

Disjoint sets, right channel each time. Both throwaway branches closed and deleted.

**Three cells survived both mutations and the PR says so rather than claiming 8/8.** They race on
the INSERT, where the unique index is the guard; neither mutation touches that path. Falsifying it
would mean dropping a database constraint in a migration.

**The gap CI did not close, now closed (`a0ba373`).** `IngressController.ReceiveOrder` takes
`IClaimedOrderCreator` via `[FromServices]` and **every test hands it a mock** — a missing DI
registration is invisible to all of them and would surface as a 500 on every REST-ingress POST plus a
dead Postmark webhook, in production, with both dedupe channels unreachable while their tests stayed
green. `OrderServiceCompositionRootTests` does not cover it: it MIRRORS the registrations in its own
`ServiceCollection` rather than reading `Program.cs`. New `PushIngressSeamRegistrationTests` reads
both host files through the shared `OrphanDetector.StripComments` (so a commented-out registration
reads as absent) and pins that `OrderService` still implements the interface — an explicit
implementation, so dropping it compiles everywhere except the registration cast. Mutation-proven 3/3
on a filtered local run that starts no containers.

TRAP 2 respected: this PR adds to `InboundEmailController` and leaves
`TheLiveNearNamesakes_AreStillPresent` intact.

#### WP-13 — refuted on first pass, and the refuter was right twice

The control now lives on `WorkshopStatusBar`. It had to move: the mapper's own "Save mappings" button
is inside `{!hideToolbar && …}` (`MapperWorkbench.tsx:819-969`) and the workshop passes `hideToolbar`,
so **wiring the prop at that mount would still have rendered nothing.**

**F1 (blocker).** `!order?.supplierId` never fires — an unassigned counterparty comes off the wire as
**`Guid.Empty`, a non-empty string** — so the control was ENABLED on a real unrouted order and POSTed
a promote for a supplier that does not exist. The repo already ships `hasAssignedSupplier()` with two
users in that same screen. **My tests passed only because the fixture fabricated
`supplierId: undefined`** — a shape `Order.supplierId` is typed non-optional to exclude. *A fabricated
fixture is how a guard test proves nothing.*

**F2 (blocker).** `total` counted four fields; the sentence printed two. WP-12 promotes the OUTPUT
tree, so the output-side pair is routinely the only non-zero one — a ten-field promotion announced
**"Saved 0 header and 0 line mappings"**, in green, and the aggregate-only shape printed
**"undefined"**. Four mutations over that arithmetic had survived round one.

Seven more fixed: the party noun now routes through `partyLabels` in the label AND the tooltip
("counterparty" was reaching users); the notice is scoped to the same breakpoint as its trigger;
transport failures are no longer painted in raw; the `upgradeUrl` the api layer was rewritten to
preserve is now actually rendered as a link.

**Both help articles were also wrong** — `/help/output-mapping-editor` and
`/help/guides/map-supplier-po-fields` told operators to click **Save mappings for this supplier**, a
label no control had ever carried.

#### WP-15 — the design brief was verified before any code was written, and it does not hold up

`DESIGN-DB-2` audited line-by-line against `origin/main`: **14 citations stale/wrong/non-existent**
(plus 31 line-number drifts across 88), and **8 sections would produce a wrong output document**.
Two would have shipped live data-loss bugs, and both are what S1+S2 fix.

S1 — `setBinding`/`setFormatPreset` rebuilt `rule` from a **five-key object literal**, so
`OutputFieldRule.Expression` (top of the backend's resolution precedence) was deleted on every rebind
or format change, and the delivered document quietly changed. The writers now spread the previous
rule. **The test that matters is the one proving an UNKNOWN rule field survives** — that is the class.

S2 — `MANIPULATOR_TYPES` described two manipulators that do not exist. `Concat` was `["suffix"]`;
`ConcatManipulator` needs ≥2 params, reads NAMED ROW COLUMNS and ignores the incoming value, so one
param throws `ArgumentException` **at transform time**. `Fallback` was labelled a literal default;
`FallbackManipulator` treats params as COLUMN NAMES and returns **null**, so that label **silently
blanks a supplier's column**. Both re-read from `origin/main` first-hand.

**Founder rulings 2026-07-31**, recorded so the remaining slices are not re-litigated:
1. **CSV line endings** — new layouts default CRLF, existing keep LF. The dialect is nullable and
   `null` must produce today's bytes exactly, so the WP-12 byte-parity oracle stays green.
2. **`OutputFieldValidator` on the tree path (S14)** — ship as a **warning, not a block**. The only
   checks it gains are non-positive quantity and negative unit price; blocking would stop orders that
   deliver today.
3. **`Fallback`** — expose honestly ("if this is empty, use another FIELD"), no new backend
   manipulator.

Also settled by the audit: **no cXML or Peppol namespace presets** (S15 ships UBL 2.1 / Custom /
None). cXML is DTD-based and has no namespaces; a preset would teach a falsehood about the format.
The repo agrees with itself here — `OutputTreeFormats.cs:27-33` and `OutputTemplateEmitter.cs:62-65`.

**Remaining: S3–S16**, dependency-ordered: S3 (BE contract pin) → S4/S5 (reorder: pure `moveAt`, then
pointer + keyboard + live region) → S6/S7 (BE CSV dialect + typed JSON leaves) → S8/S9 (FE dialect
panel + all manipulators visible) → S10–S16 (WP-16: end the silent format rewrite, the
format-mismatch warning, the structured conditional builder, the fail-open warning, the validator
routing, namespace presets, the problems strip).

### TRAPS added 2026-07-31

**TRAP — "a fabricated fixture is a test."** WP-13's read-only guard passed against
`supplierId: undefined`, a shape the type declares impossible and the API never sends. The live shape
is `Guid.Empty`, a non-empty string, and the guard was inert against it. **Before trusting a guard
test, check that its fixture is a shape the wire can actually produce.** `Order.supplierId` being
non-optional was the tell, in the type, the whole time.

**TRAP — "a pure model with passing tests is done."** Reverting `OutputStructureDesigner` to its
inline five-key literal left `outputRuleModel.test.ts` **entirely green**. A model with the right
behaviour and no caller is the exact defect WP-13 exists to fix. Every extraction needs a mutation
that removes the CALL, not just one that breaks the callee — and one such mutation is not enough:
killing the format-preset caller left the `setBinding` caller green until a test drove that writer
too. **Mutate each call site, not each module.**

**TRAP — "no CI run means the webhook was dropped."** WRONG, and it wasted three trigger attempts
here (empty commit, close/reopen, re-push). **GitHub does not run `pull_request` workflows on a PR
whose merge commit cannot be computed.** FE #65 had silently gone `CONFLICTING` when main moved six
PRs ahead; the last run covered a head two commits behind, and citing it would have been the
"green from a tree the PR head is not" failure. `gh pr view <n> --json mergeable` answers in one call.
Check it FIRST when a run is missing.

**PROCESS — commit before mutating, and the harness is why.** `react-hooks/exhaustive-deps` flagged a
missing dep in WP-13; the fix was still uncommitted when the mutation harness ran, and the harness
reverts with `git checkout -- <file>`. It took the mutation **and the fix** together, and the tree
came back clean and green. Only re-reading the deps array showed it was gone. This is the failure the
plan already warns about by name — it still happened, so it is worth restating: the harness cannot
tell your work from its own.

### 2026-07-31, later — wedge session: all three packets through the adversarial gate

**Every one came back REFUTED. All three PRs are now green, MERGEABLE and non-draft.**

| Packet | PR | Head-run evidence |
|---|---|---|
| WP-13 | FE **#65** | `30635843072` — all checks pass |
| WP-22 | BE **#97** | `30637633696` — pass; all 6 new tests `Passed`, none skipped; Api.Tests 1995/1 |
| WP-15 S1+S2 | FE **#71** | `30637674207` — all checks pass |

#### The gate earned its keep three times, and twice on work it had already passed

**WP-13.** Two blockers. `!order?.supplierId` never fires — an unassigned counterparty is
`Guid.Empty`, a NON-EMPTY string — so the control was live on real unrouted orders. The test passed
only because the fixture faked `supplierId: undefined`, a shape the type declares impossible. And
`total` counted four fields while the sentence printed two, so a ten-field output-tree promotion
announced "Saved 0 header and 0 line mappings" in green.

**WP-22.** The PR's HEADLINE mechanism had zero coverage: `ProviderMessageId` defaults to null and
both payload builders omitted it, so all five Postmark cells proved only content-hash dedupe in one
org-wide bucket. Deleting the controller wiring is a legal compile and left the suite green. In
production the key degenerates to `(orgId, "postmark:", sha256(attachment))`, two different emails
with byte-identical attachments collide, and the second is answered 200 with an empty
`CreatedOrderIds` — a silently lost purchase order.

**WP-15.** The fix was one file short. `withManipulators` had zero callers, and the reason not to
delete it was that the identical four-key literal was LIVE in `mapper/mapperModel.ts`, destroying
`expression` and `sourceToken`. That half is worse: `sourceToken` is authorable today from the output
mapping editor, both screens edit the same override document, and dropping it left a rule that
survived the `inert` check while bound to nothing — delivering an empty column.

### TRAPS added 2026-07-31 (second pass)

**TRAP — "a test that builds the DTO proves the wiring."** It cannot. WP-22's three new dedupe cells
construct their own `InboundEmailPayload`, so they prove the ROUTER honours a message id and say
nothing about who populates it — the controller mutation came back GREEN *with the new cells in
place*. Only an assertion at the seam that does the wiring closes it. **Generalises to every
DTO-shaped test in both repos.**

**TRAP — "a source-text guard proves a registration."** WP-22's own new
`PushIngressSeamRegistrationTests` used a bare whole-file regex, so MOVING the registration below
`builder.Build()` kept it green — while the built provider never sees it, every REST-ingress POST
500s, and the webhook dies. Position is part of the contract. Assert the match offset, not just the
match.

**TRAP — "the local gates cover the UI."** jsdom applies no CSS. WP-13's longer label overflowed the
status bar by 90px at 1280 and **all 42 RTL tests stayed green**; the `zero overflow at 1280 wide
viewport` e2e caught it. Any change to a `whiteSpace: nowrap` row is invisible to the unit layer by
construction.

**PROCESS — a mutation must COMPILE to prove anything.** One WP-22 mutation removed a named argument
with a regex and broke the syntax; a compile failure is not a test failure and would have been
recorded as a kill that never happened. Build the mutation before pushing it.

### The shape that repeated across all three packets

Every blocker this round was **a mechanism that worked and a path that could not reach it, or a test
that could not see it**:

- WP-13: the promote engine worked; the button did not render (and then: the guard existed, but only
  against a fixture the wire never sends).
- WP-22: the dedupe worked; the key never arrived (and the tests built the key themselves).
- WP-15: the writers worked; one of the two modules that needed them still had its own literal.

That is the same defect WP-13 was written to fix, appearing three more times in the packets that fix
it. **When a packet extracts a mechanism, mutate the CALL SITE — every call site — not the module.**

---

## 2026-07-31 — merge sweep #2: four landed, and a clean merge that broke the build

Swept both repos against the precondition adopted earlier today: **a branch may merge only if it
contains current `main` AND has had a CI run since.** Applying it cost four extra CI cycles and
caught one defect that every "green PR" signal said was not there.

### Landed

| PR | Packet | Merged as |
|---|---|---|
| FE #74 | WP-18 follow-up (P1) — read the gate's decision, not the override-blind flag | FE `d119e91` |
| FE #76 | WP-32 follow-up to #67 — gate the sign-in route, anchor the deadline to navigation | FE `d119e91` |
| BE #98 | WP-21 — prove revision authority | BE `c8ae076` |
| BE #97 | WP-22 — one inbound document, one order (claim-first dedupe) | BE `faba3bf` |

### Held, with the reason

| PR | Reason |
|---|---|
| FE #72 WP-28 | **Red against the new main.** See TRAP 23. Owner's to fix. |
| FE #70 WP-29 · FE #75 help copy | Green and contain main, but **re-refuting** on the owner's own gate. Owner asked that they not be swept. |
| FE #65 WP-13 | `update-branch` returned **CONFLICTING** once #74/#76 landed. Wedge owner's to resolve. |
| FE #73 WP-27 + BE #100 | Coupled, both held — see the rate-cap hold below. FE #73 sends `deliverTo`; landing it without BE #100 ships a request field the API ignores, i.e. a user asks for the file by email and nothing arrives. |
| FE #69 WP-30 · FE #71 WP-15 | Behind main, refuters out. |
| BE #99 WP-23 | Updated onto `faba3bf`; CI running. |
| BE #116 | Conflicting, 27 behind. BE #119 is draft. #118/#121/#122 are throwaway mutation branches. |

---

### TRAP 23 — a clean merge is not a working merge

FE #72 was **green on its own head** and `MERGEABLE`. `gh pr update-branch` merged main into it
without a single textual conflict. The result failed immediately:

```
ReferenceError: failingAcceptanceRows is not defined
```

FE #74 **deleted** `failingAcceptanceRows` from `workshop/acceptanceGateModel.ts` — that deletion is
the substance of the packet, since the helper counted failing rows blind to overrides. FE #72
independently added a **new** call site at `OrderWorkshop.tsx:588`. Git saw a deletion in one file
and an addition in another, merged both, and produced a tree where the caller outlives the callee.

The pre-merge signals that said this was safe: PR checks green, `mergeable: MERGEABLE`, no conflict
on update. All three were true. None of them means the merged code runs.

**Rule:** `MERGEABLE` is a statement about *text*, exactly as `.base.sha` is a statement about the
base branch and not about containment (TRAP 11). When two branches touch the same symbol from
different files, only a CI run **on the merged tree** decides. Third member of that family, and the
first to reach a broken build.

### Reporting-instrument failure, self-caught

My own status helper reported `PENDING:Vercel` for #72 at a moment when its unit tests and build had
**already failed**. The reducer tested for pending checks before failing ones and returned on the
first hit, so a pending deploy preview masked two hard failures. I nearly held the sweep on a Vercel
preview while the real signal was red underneath it.

Same shape as TRAP 18's sub-class — **the instrument reporting the state was itself the broken
thing.** Precedence in a status reducer is not cosmetic: FAILURE must outrank PENDING, or the summary
lies in the one direction that matters.

---

### HOLD — WP-27 must not merge without its rate cap

BE #100 gives `POST /api/onboarding/sample-order` a caller-supplied `deliverTo`, which makes
ProcuLink's verified Postmark sender email a file to an address the caller chooses.
`SampleOrderController` carries `[Authorize]` and no named rate-limit policy.

**Not a live incident.** `Create(CancellationToken ct)` on `main` takes no body, so the surface does
not exist in production. It is created *by the packet*, which is exactly why the cap has to land
**inside** it rather than after it — order alone is the whole safety property, as with the merge train.

Three corrections to how this was first reported:

- It is **not** unlimited today. `Program.cs:331` registers a `GlobalLimiter` at 300/60s per `sub`.
  Under-limited, not unprotected. `RateLimitPolicyAppliedTests.StripeWebhook_HasNoNamedRateLimitPolicy`
  asserts the Stripe webhook must carry *no* named policy, so a missing attribute is a documented
  state in this codebase, not prima facie oversight.
- The right policy is **`support` (5/min)**, not `upload` (60/min). `SupportController.Contact` sends
  to our own fixed inbox, so bounces land where we control them. WP-27's recipient is caller-supplied,
  which is precisely the input that drives complaint rates against the verified sender. It is *worse*
  than support on the one axis the cap exists to bound.
- `RateLimitPolicyAppliedTests.Action_HasExpectedRateLimitPolicy` is a hand-written
  `[Theory]`/`[InlineData]` catalog, not a sweep. An attribute with no matching row is unpinned
  wiring — in the file whose own header records that the original defect was policies *defined but
  never applied*. The row lands in the same commit as the attribute.

Accepted by the WP-27 owner; the fix is in its round.

---

### Next batch — proposed, unowned and unblocked

Currently owned: WP-13/15/16 (wedge), WP-18/21/23 (Wave 3), WP-27–WP-32 (Wave 4), WP-23a (mid-parse).
Blocked: WP-06/07 (on WP-12), WP-17 (on WP-07), WP-05 (on the WP-07 ruling), Wave 5+.

| # | Packet | Why now | Collides with |
|---|---|---|---|
| 1 | **WP-10 remediation** — `/security` EU-residency copy | Logged at `05-PROGRESS:639`: FE #42 shipped **refuted** and the wrong copy is **live**. Marketing files only, tiny, and it is a truth defect on a trust page. | nothing |
| 2 | **WP-11** — billing gate honesty | 4 wrong error codes (3 tests pin the wrong string), 10 of 16 gates unenforced, REST ingress ungated, cancel→read-only undisclosed. `CLAUDE.md` §11.5's offer⇔works rule applies to the ladder itself. | nothing in flight |
| 3 | **WP-02** — no test may pass vacuously | Wave 0 foundation: every live-transport test is an env-gated silent `return`; `Live_ImapIngress` dead since `de4ea0e`. Touches test files only, so it is safe to run beside any product packet. | nothing |
| 4 | **WP-19 + WP-24** — split 4xx, then recovery UI | Same user journey: a failure that names its cause, then a screen that acts on it. 401/404/429 are permanent today; `transform_failed`'s CTA links to itself. WP-24 was refuted once, so it starts from a fix round, not a build. | **WP-24 touches workshop + health deep links — sequence it after FE #72 lands.** |

**WP-12 status, checked rather than inferred:** the wave table above still shows it 🟠 in flight on
`feat/wp12-output-tree-reconciled`, and it has no open PR — which invites the inference that it
stalled. It did not. `OutputTree` is on `origin/main` in `Core/Services/Mapping/OutputTreeFormats.cs`,
`IPromoteMappingService.cs`, `OrderMappingOverrideReader.cs`, `PoMappingConfig.cs`, and
`Entities/SupplierConnectionRevision.cs`. WP-12 landed; the table is stale, not the packet. Anything
downstream (WP-06, WP-13) can be planned on it.

The wave tables at lines 914–965 are the original 2026-07-27 authoring and have **not** been
maintained — they still show WP-18 and WP-21 as ⬜ after both shipped. Read the dated sections for
live state; treat the tables as history.

---

## 2026-07-31 — merge sweep #2, closed: five landed

BE #99 (WP-23, refuse a resolve issued from a status the recompute would destroy) landed as
`2c8b8f4` after `faba3bf` came back green. Final tally for the sweep:

| PR | Packet | Merged as |
|---|---|---|
| FE #74 | WP-18 follow-up (P1) | FE `d119e91` ✅ main green |
| FE #76 | WP-32 follow-up to #67 | FE `d119e91` ✅ main green |
| BE #98 | WP-21 — prove revision authority | BE `c8ae076` |
| BE #97 | WP-22 — one inbound document, one order | BE `faba3bf` ✅ main green |
| BE #99 | WP-23 — resolve status guard | BE `2c8b8f4` |

Note for anyone reading BE run history: the `c8ae076` run shows **cancelled**, not failed. #97's merge
superseded it through the concurrency group. `faba3bf` is the run that verifies both.

### TRAP 24 — green CI is not evidence that the change is covered

The precondition adopted this morning — *a branch may merge only if it contains current `main` and has
had a CI run since* — is about **staleness**. It says nothing about **coverage**, and I merged FE #74
believing it said more than it does.

`workshop/acceptanceGateModel.ts` is the module FE #74 exists to correct. It shipped with **no test
file at all**. The WP-28 owner then mutation-checked the follow-up fix and found the entire advisory
count could be forced to zero with **all 1676 tests still passing**. A packet whose whole thesis is
"read the gate's decision, not the override-blind flag" had no test that reads a decision.

The suite passing is a claim about the suite. It is not a claim about the diff. Same family as TRAP 23
(`MERGEABLE` is a claim about text) and TRAP 11 (`.base.sha` is a claim about the base branch) —
each is a true signal answering a narrower question than the one being asked of it.

**Added to the sweep precondition:** before merging, check that the diff's production files have
test-side changes alongside them. Cheap version, and it is what gated BE #99 into this sweep:

```
gh pr diff <N> --name-only          # is there a test file per production file?
gh pr diff <N> | grep -cE '^\+.*\[(Fact|Theory)\]'
```

BE #99 passed it — two production files, a matching test file for each, 12 new cases. It is a
heuristic, not a proof; a mutation check is the proof. But it would have caught `acceptanceGateModel`.

### WP-32 nav-clock concern — checked and REFUTED, do not re-open

FE #76 merged green-but-ungated; its refuter was killed mid-flight while testing whether
`performance.now()` anchoring breaks under App Router soft navigation. The worry: `performance.now()`
measures from `timeOrigin` (the *document*), which a client-side nav does not reset, so a user who
spends minutes on marketing and then clicks Sign in would arrive with the budget collapsed to its
1500 ms floor and see a false "sign-in service unavailable" card on a healthy system.

**It does not reproduce.** The claim depends on the Clerk script *not* being requested until the soft
nav. It is:

1. `src/app/layout.tsx:105` mounts `<ClerkProvider>` unconditionally around `{children}`, `(marketing)`
   included — structural.
2. Production confirms it at runtime. `curl https://proculink.eu/` (signed-out marketing landing)
   returns `src="https://clerk.proculink.eu/npm/@clerk/clerk-js@6/dist/clerk.browser.js"`. The script
   is in the marketing document's HTML.
3. `useDependencyReady.ts` computes `Math.max(MIN_WAIT_AFTER_MOUNT_MS, timeoutMs - msSinceNavigationStart())`
   — a floor, never zero.

So minutes on marketing are minutes the script genuinely had, and not resetting the clock is measuring
the real thing. Hard nav resets `timeOrigin` and re-requests; soft nav keeps the clock and the script
has had the whole time. Neither path charges the user for time the script was not loading. A false
card would require Clerk to be un-ready for minutes and then ready within 1.5s of mount.

`navigationClock.ts` argues all of this in its own docstring. That is why point 2 exists — the
docstring is the claim, not the check.

### 2026-07-31 — Wave 4 (WP-27…WP-32): all six built, five refuted or defect-confirmed, none merged by this session

Session `claude/agitated-chebyshev-339d7b`. Every packet has a PR. **A session rate limit killed six
running agents mid-flight**; what follows separates what was verified from what was interrupted.

| Packet | PR | Refutation | State |
|---|---|---|---|
| WP-27 onboarding | FE #73 + BE #100 | CONFIRMED-WITH-DEFECTS (12 findings, 2 vacuous tests) | fix round interrupted |
| WP-28 workshop | FE #72 | refuter killed before reporting | **green**, merge-break fixed |
| WP-29 inbox | FE #70 (+ #75 split) | REFUTED (4 vacuous tests) | fixed; re-refutation interrupted |
| WP-30 tokens | FE #69 | REFUTED (2 live AA failures + a regression it introduced) | fixed; re-refutation interrupted |
| WP-31 a11y | FE #77 | never run | green, **mutation table not in hand** |
| WP-32 degraded | merged `3593273` + `d119e91` | CONFIRMED-WITH-DEFECTS; follow-up refuter killed | merged twice green-but-ungated |

**Six refutations, ten vacuous tests found, zero merged by this session.** The recurring defect was not
in the code — it was **a packet claiming a scope its evidence did not reach**. WP-32 asserted an AC in
wall clock and measured it post-hydration. WP-30 asserted "zero AA failures" from a gate that
structurally cannot see a token-on-token contrast failure.

**Findings that outlive their packets:**

- **`blockBody()` in `check-vocabulary.mjs` was defeated for ALL SEVEN policed registries**, not just
  `FILTER_CHIPS`. It takes the *first* `\bconst\s+NAME\b` match and never stripped comments, so any
  comment quoting a declaration disarms `registry-moved`. Third instance of `4c7350a`'s class, in the
  gate `4c7350a` already fixed once. Fixed on #70; the stripper moved to `scripts/lib/stripComments.mjs`
  and is re-exported from `src/test/sourceScan.ts` — one copy, three consumers.
- **`vocabulary.test.ts:264` certified that guard against a synthetic fixture tree** where the registry
  files do not exist, so `file-not-found` satisfied it unconditionally. It passed identically whether the
  real guard worked or was dead — and it *was* dead. A test that exercises a guard against a fixture
  proves the guard's plumbing, not its coverage of the repo.
- **`mockTransformOrder` wrote `delivered` directly**, skipping `ready_to_deliver` and `delivering`. The
  transform→deliver boundary had **zero** coverage of any kind — so the divergence was inert, and WP-27's
  journey is the first test to cross it. Risk points forward, not back: a one-pass mirror of production
  behaviour that every future delivery assertion inherits.
- **`acceptanceGateModel` shipped with no test file** — the module #74 centres on. Forcing
  `OrderWorkshop`'s entire advisory count to zero passed all 1676 tests. Now pinned by nine cases,
  both halves mutation-checked by exit code.
- **`POST /api/onboarding/sample-order`** carries no named rate-limit policy and WP-27 turns it into a
  mail sender with a caller-supplied recipient. Not escalated: the surface does not exist on `main`
  (`Create(CancellationToken ct)` takes no body), so WP-27 creates it and the cap ships in the same
  packet. `support`'s 5/min is the right precedent, **not** `upload`'s 60/min — and
  `RateLimitPolicyAppliedTests` is a hand-written `[InlineData]` catalog, so the row must land in the
  same commit as the attribute or the wiring is unpinned.

**FE #72 was jointly red and is fixed (`b8d9eae`).** Merging `main` in left a call to
`failingAcceptanceRows`, which #74 removed — build, vitest, e2e and Vercel all failed. The replacement is
not a rename: `failingAcceptanceCount` returns `blockers.length` unconditionally while `acceptanceIssues`
returns `[]` unless `blocked`, so the subtraction nets 0 for a blocked order and every blocker for an
overridden one. That is exactly the override case #74 exists to model. Rule 6 again, from a third
direction.

**Four instrument failures in one day**, each producing a report shaped identically to a clean one:
`spawnSync(args[], {shell:true})` joining argv unquoted so `-t "a name"` became file filters; LF mutation
patterns matching nothing in a CRLF worktree; MSYS rewriting a leading-slash CLI argument into a Git
install path; and a swallowed `UnicodeDecodeError` disabling a harness's cleanliness check. Three were
caught only because the harness was built to **fail loudly on a non-matching pattern** rather than proceed.

**What is NOT verified, stated plainly:**
- **FE #77 (WP-31) has no mutation table.** Its agent was killed running final gates. Every test passes;
  none has been shown to fail with its fix reverted. It has also had no refutation.
- WP-27's and WP-29's/WP-30's fix rounds were interrupted mid-verification; #73's F1 party-noun fix and
  F5 rate limit may be incomplete. WIP is committed locally in the worktrees, **unpushed**, clearly marked.
- **WP-32 merged twice without its refuter finishing.** Its first refutation then found `/sign-in` hanging
  blank for the most common signed-out path. The second refuter was checking whether `performance.now()`
  anchoring breaks under App Router **soft navigation** — it measures since the *document*, which a
  client-side nav does not reset, collapsing the budget to its 1500 ms floor. **Unverified.** Now on main.

---

## 2026-07-31 — sweep #2 final: six landed, both mains verified green

FE #72 (WP-28) landed as `f9ab894` once its owner fixed the semantic break and re-checked containment.
Final state of both repos:

| Repo | `main` | CI |
|---|---|---|
| FE `project-proculink` | `f9ab894` | ✅ success |
| BE `ProcuLink` | `2c8b8f4` | ✅ success |

Six packets merged: **FE #74** (WP-18 follow-up), **FE #76** (WP-32 follow-up), **FE #72** (WP-28),
**BE #98** (WP-21), **BE #97** (WP-22), **BE #99** (WP-23).

### FE #72 closed the gap that FE #74's merge exposed

`src/components/bridge/workshop/acceptanceGateModel.test.ts` **now exists.** Its absence is the whole
of TRAP 24 — the module FE #74 was built to correct had no direct coverage, and the follow-up fix was
itself unpinned until its owner mutation-checked it (forcing the advisory count to zero passed all
1676 tests). `b8d9eae` carries the fix and its guard together: 36 new cases across 10 test files, both
halves of the asymmetry mutation-checked by exit code.

The packet that broke on the merge is the packet that closed the gap the merge revealed. Worth
recording because the sequence is not a coincidence: `update-branch` forced a build of the merged
program, the build failed, and fixing it required someone to finally read what the model actually
promised.

### TRAP 23, sharpened

The original clause — "`MERGEABLE` is a claim about text" — is true but understates it. The sharper
statement, from the WP-28 owner:

> All three signals were scoped to one side each. Green CI tested the **branch**. `MERGEABLE` tested
> the **textual merge**. Nothing tested the merged **program**. A symbol deleted in one file and
> called from another is invisible to all three by construction.

That also explains why `gh pr update-branch` is what caught it: it is the only step in the sequence
that builds the merge result. **Practical consequence — update branches early, not at merge time.**
The defect surfaced within minutes of the update instead of after a merge to `main`.

### HOLD — FE #77 (WP-31) is green and must not be swept on that

Its owner reports the packet's agent died running final gates, so the mutation checks never ran.
Green CI is precisely the signal TRAP 24 says cannot speak to coverage, and this is a live instance
rather than a hypothetical: do not merge #77 until its owner confirms the gates ran.

### Still held at sweep close

FE #65 (CONFLICTING after #74/#76), FE #69 / #71 (behind main), FE #70 / #75 (green, re-refuting),
FE #77 (above), FE #73 + BE #100 (the WP-27 rate cap), BE #116 (conflicting), BE #119 (draft),
BE #118 / #121 / #122 (throwaway mutation branches).

### Resolved in passing: the `stripComments` move is justified

The relocation from `src/test/sourceScan.ts` to `scripts/lib/stripComments.mjs` (re-exported from its
old home) rests on a premise that has now been checked rather than assumed: `.github/workflows/ci.yml`
has **no `setup-node` step at all**, so `lint:vocab` runs `scripts/check-vocabulary.mjs` under bare
`node` on an unpinned `ubuntu-latest`. A `.ts` import from there would depend on
`--experimental-strip-types` being available in a runtime nobody pinned. Plain ESM under `scripts/` is
the correct resolution; `4c7350a`'s fix is intact and there is still exactly one copy for three
consumers.

---

## 2026-07-31 — next batch dispatched, and the chip ledger

Sweep #2 closed at six merged, FE main `f9ab894` and BE main `2c8b8f4` both green. Four new packets
dispatched as chips, chosen because they are unowned, unblocked, and touch files nobody else holds:

| Packet | Why now |
|---|---|
| **WP-10 remediation** | FE #42 shipped refuted — wrong `/security` EU-residency copy is **live**. Truth defect on a trust page. |
| **WP-11** billing gate honesty | 10 of 16 gates unenforced, 4 wrong error codes (3 tests pin the wrong string), REST ingress ungated, cancel→read-only undisclosed. |
| **WP-02** no vacuous tests | Wave 0 foundation. Every live-transport test is an env-gated silent `return`; the suites this plan's verification method rests on cannot currently fail. |
| **WP-19 + WP-24** | Dispatched as one packet — a failure that names its cause, then a screen that acts on it. Unblocked now that FE #72 landed and the workshop files are clear. WP-24 starts from its earlier refutation, not from scratch. |

Each chip was given the mutation-check and adversarial-refutation requirements explicitly, since
TRAP 24 established that green CI is not evidence the diff is covered.

### Chip ledger

| Chip | State | Archivable |
|---|---|---|
| Close Wave 3 (WP-18, WP-21, WP-23) | scope complete — all three merged (`d1a6b9c`, `c8ae076`, `2c8b8f4`) | **yes** |
| Wave 4 UI | running; owns FE #70/#75/#77 + BE #100 | no |
| Finish the wedge | idle; owns FE #65/#71 + BE #124/#125 (WP-15 still in flight) | no |
| Mid-parse correction (WP-23a) | idle; owns BE #119 draft + mutation branches #121/#122 | no |

Note: the wedge chip's session metadata still shows PR #97 as OPEN. It merged as `faba3bf`. Session
`prState` lags; do not read it as the packet's state.

## 2026-07-31 — WP-23 · The `resolve` status guard (BE PR #99, OPEN)

**Part of the packet was already done, and it was done the right way.** `c61fe30` reconciled the
resolve recompute into both status maps — 11 edges into `OrderStatusMachine.Transitions`, 13 into
`OrderStatusTransitionObserver.AllowedTransitions` — deliberately choosing reconcile over gate,
because the write was real and the maps were wrong. **Nothing about that was undone here.** What it
left open, naming each one "wants an endpoint guard, which is a product decision", was the two
from-states where the same write *destroys* something. Those two were this packet.

| Hole | Consequence before this PR |
|---|---|
| `unrouted` | An unrouted order has lines, so a resolve — or a **header-only** one, which the endpoint accepts — recomputed it to `ready` with `SupplierId` still null. `OrdersController.AssignSupplier` claims atomically on `Status == Unrouted` (`:682`) and answers 409 otherwise, so the recompute **permanently** locked the operator out of the one action the routing hold exists for. |
| `delivering` | A row *sits* in `delivering` (`StuckDeliveryDetectionService` exists because dispatches strand there), so a resolve wrote over a live dispatch claim whose outcome write then landed on top of the correction. |

A resolve reached **14** from-states before; it now reaches **12**.

**Shipped.** One canonical declaration, `OrderStatusMachine.ResolveHeldFrom = {unrouted, delivering}`,
plus `ResolveHoldMessage(status)`. One consumer in production —
`OrdersController.RefusedByResolveHoldAsync` — called by **both** recompute endpoints. 409 with a
plain-language sentence; no status token, no party noun, an instruction cue. No transition edge was
touched.

**`accept-ai-suggestions` is guarded too, and that was the load-bearing call.** It shares the writer
(`OrderResolutionService` `:242` and `:393` end with the identical recompute) and its recompute runs
even when nothing is accepted — the file's own comment at `:358` already made that argument for the
terminal guard. Guarding `/resolve` alone would have relocated hole 1, not closed it. Mutation **B**
proves it: reverting only that call site turns exactly the two accept-AI rows red and nothing else.

**Derivation, not enumeration.** No status name is hand-written in the new production code or in any
theory data. Refusals derive from `ResolveHeldFrom`; the 28 positive controls derive from
`AllStatuses` minus it. A status added to the machine tomorrow gains "must NOT be refused" rows
automatically; a status added to the hold gains "must be refused, with its own actionable sentence"
rows automatically.

**Eight CI runs, seven of them mutations.** RED `30628462957` (9 failures, verbatim in the PR body) →
GREEN `30629301937` → mutations **A** `30630169044`, **B** `30630324709`, **C** `30630369823`,
**D+E** `30630471634`, **F** `30631231892`, **G** `30631245138`, **H** `30631264515`. **41 of 42 test
cases are killed by at least one mutation.**

**Two findings worth carrying forward, both stated rather than hidden.**

- *Mutation C did not kill the controller theories.* Because the theory data derives from the same set
  production reads, mutating the **set** moves the tests with it. That is the intended property and
  simultaneously a limit on what a set-mutation can prove. Mutations that alter the **guard** (A, B, F)
  are what prove those tests non-vacuous. Worth knowing before designing the next derived suite.
- *The one test no mutation kills* is the pre-existing
  `EveryStatusAResolveCanBeIssuedFrom_HasBothRecomputeEdges`, which gained `ResolveHeldFrom` as a third
  exclusion — what that test's own closing message asked the next packet to do. The change is
  behaviourally inert (the edges are present either way); it is a premise correction that stops the
  test's stated claim becoming false, and the coverage it sheds is re-asserted by the new
  `EveryResolveHeldStatus_KeepsBothRecomputeEdges`. Mutation **H** shows the swap is real: pruning
  c61fe30's `unrouted` edges leaves the old test green and turns the new one red.

**Drift.** `OrdersController.Resolve` is at **`:554`**, not `:483`. The file has not changed since
`c61fe30` (`git diff c61fe30 504d9cc` on it is empty), so the citation was already stale in that
commit's message, in `OrderStatusMachine.cs:39` and in `OrderStatusMachineTests.cs:764`. The
substance is right — `Resolve` does validate the body only, at `:559-626`. Left uncorrected: fixing
stale citations across three files would bury this diff.

**What was deliberately NOT done.**

- `failed` stays where it is: refused a layer down by `OrderResolutionService.IsFinished` with a
  **400** and a permanent-verdict sentence. This guard is a temporary verdict, so it keeps a different
  code and a different sentence; the two sets are asserted disjoint.
- `parsing` is **not** guarded. A resolve during parse races the parse job's own write — arguably a
  third hole, but a different failure mode (a race, not a permanent lock-out), not one `c61fe30`
  named, and widening the set without that argument is exactly what
  `ResolveHeldFrom_IsExactlyTheTwoHoles…` exists to prevent. Named, not added.
- The guard is at the **endpoint**, so a **TOCTOU window** remains between its status read and the
  service's write: an order entering `delivering` in between is still overwritten. Closing that needs
  an atomic claim in `OrderResolutionService` — a larger change, and it does not affect hole 1 at all.
- **The frontend renders this 409 as raw JSON.** `project-proculink` `src/lib/api-client.ts:739`
  unwraps the `{error}` body **only for status 400**; a 409 falls through to
  `` throw new Error(`Resolution failed: ${t}`) `` at `:748`. The same file already handles this
  correctly for `assign-supplier` (`:917-930`, via `ApiHttpError`). **The plain-language message is on
  the wire; it is not yet on the screen.** Different repo, filed as a follow-up.

### 2026-07-31 — Wave 3 closure session: WP-18, WP-21, WP-23 dispatched in parallel

**All three packets were REFUTED by their own refuter.** That is the mechanism working, not failing.
Between them the refuters caught one P1 regression that had already merged, two dead assertions, an
R1 violation, and a vacuous test axis. None of it would have surfaced from a green suite (TRAP 5).

**Outcome:**

| Packet | Landed | CI | Refuter |
|---|---|---|---|
| WP-18 | FE `d119e91` — `#64` merged mid-review, P1 fix `#74` merged after | green | REFUTED x2 -> 6 fixed, 5 disclosed |
| WP-21 | BE `c8ae076` (`#98`) | `30637291250` success | REFUTED -> 9 fixed, 3 disclosed |
| WP-23 | BE `2c8b8f4` (`#99`) | `30645617088` success | REFUTED -> 4 fixed, 3 standing |

All three are on green mains: BE `2c8b8f4`, FE `f9ab894`. WP-23's final green ran on a head carrying
`main` merged in twice, so it is green *including* WP-21 and WP-22 — not a stale green against an
older base.

**Correction to the 2026-07-31 handoff snapshot.** It pinned BE `origin/main` at `c61fe30`. At dispatch
it was `504d9cc` — `49dd828` and `504d9cc` had landed in between. The packets were cut from `504d9cc`.
Anyone reading that snapshot as current should re-derive the SHA, not trust it.

**WP-23 was rescoped by `c61fe30` before it started.** That commit chose *reconcile, not gate*: it
declared the resolve recompute's edges legal and added 11 to `OrderStatusMachine.Transitions` and 13 to
the observer. So WP-23 was never "block those transitions" — it is the two holes `c61fe30` named and
deliberately left open (`unrouted`, `delivering`), each of which it labelled a product decision. The
guard is an **endpoint admission rule**, kept explicitly separate from the transition rule, and
`EveryResolveHeldStatus_KeepsBothRecomputeEdges` exists so a future session cannot prune those edges on
TRAP-1 reasoning ("no writer performs it any more").

**WP-21's CI was unblocked by a rebase, and the throttling diagnosis is not established.** The head of
`#98` had zero check-suites after seven trigger attempts across two agents (force-push, fresh branch,
`ready_for_review`, two new PRs, close+reopen, empty commit). Both agents concluded actor-level
throttling. The branch was also **CONFLICTING against current `main`**, which nobody checked. A rebase
onto `origin/main` plus a force-push produced a run within seconds, and the same worked for
`wp21/mutb`. The causal claim is not proven either way — but the correction-log shape is present: two
true facts (no runs here; runs on other branches) with an unchecked step between them (whether this
branch was current against `main`). **Try the rebase before diagnosing the platform.**

**Mutation set B is no longer missing.** WP-21 reported three tests with zero mutation evidence because
Actions would not run `wp21/mutb`. That branch was also cut from the **pre-refutation** commit
`0034c27`, so it would have evidenced code that never shipped. Re-pointed at the final head and run:
CI `30637394173`, **failure**, killing all three —
`PinnedOrderDoesNotRerouteAfterConfigEditPostgresTests.RevisionAuthorityOff_BothOrdersRouteToTheSameEditedEndpoint_SoTheDifferenceIsTheFlag`,
`RevisionAuthorityStartupAnnouncementTests.IsEnabled_IsTheSingleStrictReaderOfTheFlag` (all five theory
rows), and `RevisionAuthorityHostCoverageTests.EveryDeclaredHost_NamesItsDeployedServiceAndItsConfigFile`.
It also kills 17 further flag-off tests across parse, transform, validation, delivery, preview, replay,
conformance and republish. **That breadth is itself the finding:** forcing `IsEnabled` true breaks every
flag-off path in the codebase, which is the direct evidence that it really is the single reader — the
claim WP-21 made when it deleted `SupplierConnectionService`'s duplicate `bool.TryParse`. PR `#118`
closed with the evidence recorded; branch `wp21/mutb` left in place so the mutation stays reproducible.

**A P1 shipped to production because a PR merged while its review was still running.** `#64` went to FE
`main` at 12:52 with the pre-refutation code. It counted the validation endpoint's **raw** blocking-failure
set, but the gate's real decision subtracts a recorded operator override (`AcceptanceGate.cs:62-77`) — an
overridden order is `Blocked=false` with a **non-empty** blockers array. The UI therefore refused a send
the server performs, and `POST /acceptance-gate/override` has no browser surface, so there was no way
out of it. Against pre-`#64` `main`, where Send was enabled and the server decided, that is a strict
regression. WP-17 had shipped `GET /api/orders/{id}/acceptance-gate` for exactly this and `#64` never
called it. Fixed in `#74`. **The lesson is the merge gate, not the code:** M4.3 exists precisely to
catch this class, and merging before it reports skips it.

**Standing, and named rather than smoothed over:**

- **WP-18's viewport axis is VACUOUS.** Mutation M9 — make `setViewport` a no-op — leaves 27/27 green.
  Nothing under `workshop/` reads `innerWidth` or `matchMedia`, and jsdom applies no Tailwind, so all
  three viewports execute one code path. The gate is breakpoint-independent *by construction* and both
  send controls are asserted every run, but **the AC as written ("identical at 390/768/1440") is not
  measured.** Closing it needs Playwright.
- **WP-23's `delivering` hole is narrowed, not closed.** The guard is check-then-act and
  `PurchaseOrderEntity` carries no concurrency token, so an order entering `delivering` between the
  guard's read and `OrderResolutionService`'s `SaveChangesAsync` is still overwritten. The ordinary
  operator path is shut; the race is not. `unrouted` has no equivalent gap.
- **`parsing` is a verified third hole of the same shape** — both parse-persist paths claim on
  `Status == Parsing` and return `Fail` *before* inserting lines, so a mid-parse resolve discards the
  entire parse result. Not added: refusing it removes an operator control, which is a product decision.
  Draft BE `#119` carries the RED. `transforming` is an unruled-out fourth.
- **No operator override UI.** `POST /acceptance-gate/override` is live server-side with audit and typed
  refusals, and now that `#74` honours it, an override can be **honoured but not created**. Own packet.

**Founder decisions this session surfaced and did not take:**

1. The party-noun ban vs shipped UI copy — the backend refusal says "Route it first… the routing queue"
   while the UI says "Assign a supplier", and the UI's own wording carries the banned noun. One gives.
2. Whether a header-only correction should be refused on an `unrouted` order. WP-23 removes that control
   on the strength of `c61fe30`'s naming of the hole; there is no sign-off on the header-only case.
3. Whether `parsing` and `transforming` join `ResolveHeldFrom` (BE `#119`).
4. **The WP-21 production smoke is still owed.** Runbook checked in at
   `docs/ops/revision-authority-production-smoke.md` with a pre-list, a disposable-request-bin
   constraint (never a real supplier), a pass/fail/inconclusive table and a full undo. Production writes
   were not authorized this session (R8), so the live observation remains UNPERFORMED. Its section 2 is
   two read-only commands that answer "is the flag on right now" without touching data.
5. **The three leaked keys are still unrotated** — OpenAI, PostHog, Neon, from the unfiltered
   `railway variables` call on 2026-07-27. Every `railway` call this session was filtered through `grep`.
---

## 2026-07-31 — TRAP 25: two packets, both correct, one false claim

The WP-27 rate-cap hold is **lifted**. Verified at source on `origin/claude/wp27-sample`, not taken
from the owner's report: a dedicated `sample-order` policy at `permit: 5, seconds: 60`
(`Program.cs:338`), the attribute on the action (`SampleOrderController.cs:39`), the `[InlineData]`
catalog row (`RateLimitPolicyAppliedTests.cs:56`), and a **live 429 probe**
(`SampleOrder_Returns429_AfterExceedingPolicyLimit`, `:165`). The cap is proven by behaviour rather
than by the attribute's presence, and the dedicated partition is better than reusing `support` —
a burst on one cannot eat the other's budget.

WP-27 remains held: BE #100 does not contain current `main` (`2c8b8f4`, WP-23 landed after it was
built), and its owner wants a re-refutation after the fix round.

### TRAP 25 — a merge can be textually and semantically sound and still ship a lie

Distinct from TRAP 23, and worse, because nothing mechanical catches it.

WP-28 deleted the practice-order band — one of the seven chrome bands that packet exists to remove —
and moved its copy into a chip plus a `PracticeNote` in the Issues column. That note read:

> "Sending stops at 'delivery not set up' — that's expected for a practice run."

True when written. WP-27 then seeds an email delivery for the practice order, which is the entire
point of the packet: the dead end goes away. Git conflicted on the text and the conflict was resolved
correctly. **Underneath it sat a claim that the other packet had just made false.**

Neither author erred. WP-28 could not know delivery would be seeded; WP-27 could not know the band
would be gone. Both packets were right in isolation, and the merged product would have shipped a
sentence contradicting its own behaviour with every test green.

**Why no signal catches it:** TRAP 23's failure was a symbol — a deleted export and a surviving
caller, which the first build of the merged tree reports as a `ReferenceError`. This is a *claim*.
Copy asserting something about behaviour is not type-checked against that behaviour, so CI,
`MERGEABLE`, and building the merge result are all silent. The only thing that catches it is reading
what the merged copy now promises.

**Practical rule:** when two packets in flight touch the same screen, and one of them changes what the
product *does* while the other changes what the product *says about it*, the merge needs a copy read,
not just a green build. Resolution here: keep WP-28's structure, carry WP-27's three-branch
`practiceDelivers` copy into its new home.

### Related, from the same fix round — a vacuous test wearing a passing test's clothes

`practiceFraming`'s `MapperWorkbench` mock **dropped `issuesSlot`**, so six assertions read as "the
note does not render" when the note was never handed anywhere to render. `invariants.test.tsx:209`
and `validationEveryBreakpoint.test.tsx:140` already mock it correctly — the pattern existed and this
file predated it. Same family as WP-31's vacuous 24px assertion: the test ran, reported confidently,
and was scoped to something narrower than its name implied.

---

## 2026-07-31 — TRAP 25 amended: the detection was luck, so the rule has to change

The WP-27 owner sharpened TRAP 25 and the amendment matters more than the original entry.

My framing was "neither packet could have known", which is true and **not actionable**. Theirs is the
actionable half: **what surfaced the contradiction was the textual conflict**, and resolving it forced
someone to read both sides of the copy. Had WP-28 moved the band to a different file, or had WP-27
touched only `PracticeOrderPrompt`, git would have merged silently and the false claim would have
shipped green.

So the detection was **luck of file layout, not process**. The rule cannot be "resolve conflicts
carefully" — conflicts are exactly the case that already works. It has to be closer to: *when two
packets touch the same user-visible claim, someone reads the merged copy even when git does not ask.*

### A mechanical trigger, since a mechanical detector does not look possible

Detecting the contradiction automatically would mean type-checking prose against behaviour. But the
**condition under which one is possible** is cheaply computable, and that is enough to route a human
or an agent read:

> For each pair of in-flight branches, intersect the copy-bearing files each touches (component and
> content files, excluding tests). A non-empty intersection at the *screen* level — not the file level
> — means both packets are editing what one screen says. Flag the pair for a copy read at merge time,
> whether or not git conflicts.

This detects nothing about truth. It detects that two packets are both writing claims about the same
surface, which is precisely the state that file-layout luck otherwise hides. Component-directory
proximity is a serviceable proxy for "same screen" in this repo's structure.

Not built. Recorded because the next instance will not conflict textually, and then there is no
second chance.

### WP-23 × WP-27 interaction: checked, and clean

The owner took the BE merge early rather than at landing time. `claude/wp27-sample` now contains BE
main `2c8b8f4`; head `fa051e3`. `dotnet build ProcuLink.slnx` clean — but a build answers the symbol
question, and the open question was behavioural, since WP-23 gates `resolve` and the practice loop
calls it:

```
OrderStatusMachine.cs:337   ResolveHeldFrom = Set(Unrouted, Delivering)
SampleOrderService.cs:160   Status = "parsing" → parse job → pending_review
```

The practice order seeds its own `__sample__` supplier, so it routes and never reaches `unrouted`;
the deliberate 2-of-3 fixture gap lands it in `pending_review`, which the guard admits. The single
resolve the practice loop issues is not refused. **No interaction — as a checked fact, not a green
build.**

### A docstring worth copying

WP-23's guard documents its own limits precisely: `delivering` is **narrowed, not closed**, because
the check is check-then-act and `PurchaseOrderEntity` carries no concurrency token, so an order
entering `delivering` between the read and `SaveChangesAsync` is still last-writer-wins. It shuts the
operator path and says plainly that it does not shut the race.

That is the standard `navigationClock.ts`'s docstring was reaching for and did not quite hit — it
asserted its premise (the script is document-requested) rather than stating what it had and had not
closed. Point at `OrderStatusMachine.cs:337` next time a claim goes into a comment.

---

## 2026-07-31 — the TRAP 25 trigger, run for real

Seven merged today. BE #125 (WP-15 S6+S7, CSV dialect and typed JSON leaves) landed as `25cb8b7`:
contained main, green, `MERGEABLE`, and — the TRAP 24 check — `CsvDialectTests.cs` and
`JsonTypedLeafTests.cs` beside three production files.

Then I ran the overlap trigger proposed in the TRAP 25 amendment against every open branch in both
repos, rather than leaving it as a note. Method: per branch,
`git diff --name-only $(git merge-base origin/main $B) $B`, pairwise-intersect the non-test `src/`
files. It cost one command and found things no session could see from inside itself.

### FE overlap matrix

| Pair | Files | Note |
|---|---|---|
| **#77 × #69** | **48** | WP-31 a11y × WP-30 tokens, spanning `src/app/**`, `bridge/{mapper,review,workshop}`, `components/help`, marketing. |
| #77 × #73 | 4 | `OnboardingWizard`, `InboxView`, `SupplierDockList`, `UploadWorkbench` — the plan predicted this one. |
| #70 × #69 · #73 × #70 · #73 × #69 | 2 each | `InboxView.tsx` appears in **four** branches. |
| **#73 × #65** | 2 | `workshop/OrderWorkshop.tsx`, `api-client.ts` — **cross-chip** (Wave 4 × wedge). |
| #70 × #65 | 1 | `api-client.ts`, also cross-chip. Three branches, two owners, one file. |

`#77 × #69` at 48 files is the expensive one and the plan already sequences WP-31 after WP-30 — so
land #69 first and rebase #77 onto it, or pay the 48-file rebase twice.

### The trigger caught its own case

**`src/lib/help-articles.ts` is touched by three branches: #75, #69 and #77.** #75 *is* the help-copy
packet — its whole job is making the glossary teach the labels the product renders, derived from
`STATUS_META` rather than word-patched. A token or a11y edit that rewords a label in that file
desynchronises it again, and nothing textual necessarily conflicts.

That is the TRAP 25 condition exactly: two packets writing claims about one surface, where detection
would otherwise be luck of file layout. The trigger does not know the claims disagree — it only says
*read this merged file even if git does not ask*. That is the whole intended value and it fired on its
first real run.

### Merge state after the pass

Only **FE #73** contains current FE main (`f9ab894`); every other FE branch is behind. BE #100
contains BE main and passes CI but stays held on its owner's re-refutation gate. So the FE side is
blocked on staleness, not on judgement — which is the cheap kind of blocked.

### Chip ledger

| Chip | State | Archivable |
|---|---|---|
| Close Wave 3 | complete — WP-18, WP-21, WP-23 all merged; notified | **yes** |
| Wave 4 UI | running; owns FE #70/#73/#75/#77 + BE #100 | no |
| Finish the wedge | idle; owns FE #65/#71 + BE #124/#116 | no |
| Mid-parse (WP-23a) | idle; owns BE #119 draft + #121/#122 mutation branches | no |

Both owners informed of the collisions above, including the cross-chip pair neither could see alone.

---

## 2026-07-31 — F7 fixed and shipped as BE #126, ahead of WP-27

WP-27's refutation produced twelve findings; six were silently dropped by the fix round and appear in
neither PR body (F6, **F7**, F8, F9, F11, F12). Its owner recorded them rather than carrying them
quietly and named F7 as the one they would not let ship silently. Verified and fixed here, because it
is unowned and it gates WP-27 rather than following it.

### The defect

`DashboardController` carried **zero** `IsSample` references. Seven other controllers and services
honour the exclusion, and `OnboardingController` states the invariant for the whole product:

> Every flag/count EXCLUDES sample data (IsSample suppliers/orders): running the sample order must
> never "complete" onboarding with zero real data.

The dashboard was the one place that did not. Every KPI, the sidebar badge, the notifications bell and
the wire topology counted the practice order as real work — and since the practice order seeds its own
`__sample__` supplier, the landing page also drew a wire for a supplier the user never added.

**It was latent until WP-27.** The practice order could not previously reach `delivered`; WP-27 closes
the practice delivery loop, which makes it reachable. A brand-new account that runs the practice flow
would read **"1 delivered"**. Same class as the Group J2 fabricated-data purge — staged content
rendering as real for real users — and the reason it ships *in front of* WP-27, not behind it.

### Scope and verification

Eight query sites across three actions: `GetStats` (four counts), `GetSummary` (the status `GROUP BY`
behind the sidebar badge and notifications bell), and `GetTopology` (supplier health, wires, and the
legacy `canonical_json` fallback — three separate query paths, each needing its own guard).

Each test pins **one** site, so reverting a single guard turns exactly the matching test red.
Mutation-checked by exit code: all eight guards removed → **6/6 fail**; restored → 6/6 pass.
`--filter Dashboard` → 12/12 with the pre-existing suite unaffected. `GetStats` had **no test at all**
before this.

### A note on the mutation procedure itself

Restoring the mutation with `git checkout <file>` reverted the file to `HEAD` — which discarded the
**fix** along with the mutation, since the fix was uncommitted. The tests then passed against
unguarded code for the wrong reason. Caught because the restore step printed the guard count and it
read `0`.

**Commit the fix before mutating it, or mutate a copy.** A mutation harness that restores from source
control assumes the thing under test is already in source control. Fifth instrument failure recorded
today, and the same shape as the others: the step that verifies the harness was itself the broken one.

### The other five dropped findings — unowned, not fixed

F6 (three `SupplierDeliveryConfigs` writers, so the docstring's "cannot drift apart" invariant is now
false), F8, F9, F11, F12. Recorded here so they survive the fix round that dropped them.
### 2026-07-31 later — Wave 4 after the rate limit: WP-27 green, and a correction I owe the record

**Correcting my own earlier entry.** I reported FE #73 as "green except Playwright in flight." It was
not in flight — it had **FAILED**, and I read `IN_PROGRESS` twice without going back for the terminal
state. #73 is now genuinely green on all five checks at `792938b`.

**The failure was the sharpest instance of this plan's own trap that I have produced.**
`first-run-to-delivered.spec.ts:93` matched `/doesn'?t count against your plan/i` against copy that
renders **U+2019** after the WP-28 merge. `'?` makes the straight quote *optional*; it does not match a
different codepoint. **The same merge commit fixed this exact defect in `practiceFraming.test.tsx` and
`sample-order-happy-path.spec.ts` and missed the flagship** — because that commit's gate list omitted
`test:e2e`. So the packet's headline journey, the one proving its entire AC, was red while every other
check on the PR read green. *A fix verified where it was applied is not verified where the defect class
lives* — quoted at two agents that same hour, in the commit that committed it.

**F2 is closed after surviving two refutations.** The journey could not distinguish "transform stopped
at `ready_to_deliver` and a dispatch delivered it" from "transform teleported and nothing was sent" —
`useSendFlow` short-circuits on an already-delivered order, so every assertion was satisfied by a mock
that never dispatched. It now asserts the in-flight notice `useSendFlow` sets **only** on the dispatch
path. Mutation-checked: `mockTransformOrder` writing `delivered` now **exits 1**; it exited 0 through
both prior passes.

**A default is indistinguishable from a correct value.** `nounLower` was optional with a `"supplier"`
default, so deleting it from one call site silently reproduced the exact regression it was added to
close — 1709 tests green, `tsc` blind. Now required. Worth generalising: an optional prop with a
plausible default cannot be pinned by any test that does not enumerate the call sites.

**WP-31 (#77): CONFIRMED-WITH-DEFECTS, six vacuous tests.** The missing mutation table was hiding them.
Two block: the AC *"zero controls below either floor"* is **false** — the floors are scoped to
`(pointer: coarse), (max-width: 639px)` (`globals.css:996`) and the spec measures only at 390×844, so
the claim is never tested at the scope it is stated; at 1280×900 both surfaces the packet names by name
fail. And the popover conformance test is `src.includes("modal: false")` over the whole file, so a
comment defeats it — third instance that day of a source-text assertion standing in for a behavioural
one. Credit: the dialog contract is genuinely pinned (11 of 13 mutations red across all 8 rendered
dialogs), the registry guard does catch a new conventionally-written dialog, and the hero-toggle
correction was itself found by mutation testing.

**Six WP-27 findings the fix round silently dropped**, recorded rather than carried: F6, **F7**, F8, F9,
F11, F12. F7 is the one that should not ship quietly — `DashboardController` has zero `IsSample`
references, so a brand-new account reads **"1 delivered"**, contradicting `OnboardingController.cs:40`'s
stated invariant, and it is newly reachable *because* WP-27 makes the practice order deliverable.

**On the branch-overlap trigger: `diff $(merge-base) $B` cannot distinguish "both packets edited this"
from "one packet contains the other."** #77 × #69 reported 48 overlapping files; #77 *contains* #69
(verified by `merge-base --is-ancestor`), so every file WP-30 touched counted twice. Check
ancestry between each pair first, or every stacked branch reports maximal overlap with its own base —
the loudest possible signal for the least interesting case. The genuine hit was `src/lib/help-articles.ts`
across #69 and #75; reading the merged content took ninety seconds and returned a definite negative
(#69 edits a hex-value comment, #75 edits `blurb`/`keywords`). **Record negatives** — otherwise the next
sweeper re-reads them, or learns to skip the trigger because it "always comes back clean."

**Live cross-branch claim conflict, found by that trigger in my own branches:** `api-client.ts` is edited
by #73 and #70. #70 routes transform errors through `parseApiErrorBody` so a raw JSON body never reaches
the user; #73's `realRunSampleOrder` throws `new Error(await res.text())` and prints it verbatim — the
same defect #70 fixes one function over. Different functions, so git merges them silently.

---

## 2026-07-31 — the overlap trigger corrected: my 48-file collision was an artifact

**I reported `#77 × #69` as a 48-file collision and recommended landing #69 first. Both wrong.
#77 *contains* #69** — `merge-base --is-ancestor origin/claude/wp30-tokens origin/claude/wp31-a11y`
→ YES. Its owner merged the token branch in before opening #77, because WP-31's changes must pass
WP-30's `lint:tokens`. The sequencing I "recommended" was already done, and there is no rebase waiting
in either direction.

### The method has three distinct inflation modes, and I hit all three

| Attempt | Method | Inflation |
|---|---|---|
| 1 | `diff $(merge-base main B) B`, pairwise-intersect | A **stacked** branch reports maximal overlap with its own base — the loudest possible signal for the least interesting case. |
| 2 | `diff $(merge-base A B)` for each of A and B | Fixes stacking, but if one branch contains recent `main` and the other is behind, their common ancestor is old, so **main's own commits** count as shared. Packet-vs-main staleness reported as packet-vs-packet. |
| 3 | diff each branch from its *true* base (newest of main + any contained sibling) | Correct in principle. My selection loop required `main` to be an ancestor of the candidate — but #69 does **not** contain main, so #77 fell back to main and `#69 × #77` stayed inflated. |

The general lesson is not about git. **A pairwise-difference metric over a set of objects that can
contain one another needs a containment pre-pass, or it reports the containment as similarity.**
The cheap guard is `merge-base --is-ancestor` between every pair before measuring anything.

### What survives — the hotspot table, which is the useful output anyway

Discounting the `#77 ⊃ #69` double-count, files edited by three or more independent packets:

| File | Branches |
|---|---|
| `workshop/OrderWorkshop.tsx` | #65 #69 #70 #71 #73 #75 |
| `InboxView.tsx` | #69 #70 #71 #73 #75 |
| `workshop/WorkshopStatusBar.tsx` | #65 #69 #70 #71 #75 |
| `lib/api-client.ts` | #65 #69 #70 #73 |
| `mapper/MapperWorkbench.tsx`, `workshop/{WorkshopLinesView,MobileTriage,sendBarLabel}`, `mapper/MapperPreviewPane.tsx`, `help/HelpArticleShell.tsx` | #69 #70 #71 #75 |

`OrderWorkshop.tsx` is the file TRAP 23 already fired in. Six independent packets are editing it.

### The trigger's first true positive — and it is a real claim conflict

`api-client.ts` sits in four branches across two owners. Its owner read the merged content and found
that **#73 and #70 disagree about a claim right now**: `realRunSampleOrder` (#73) throws
`new Error(await res.text())` and prints the raw body verbatim, which is **exactly the defect #70
fixes one function over** — #70's entire point is that a raw JSON body must never reach the user.
Different functions in the same file, so git will merge them silently and cleanly, and the product
will contradict itself.

That is the TRAP 25 shape, surfaced by the trigger, in two branches held by the same owner who could
not see it from inside either one.

### Record the negatives too

`help-articles.ts` fired and came back **clean**: #69 edits a comment listing hex values, #75 edits
`blurb`/`keywords` — the user-facing content. Different lines, different concerns, no shared claim.
Ninety seconds to read, definite negative.

Its owner's addition to the rule is the right one: **write the negative down.** A coarse
over-inclusive trigger whose negatives go unrecorded gets re-read by the next sweeper, or worse,
learns a reputation for "always comes back clean" and stops being run. A trigger that only ever fired
on true positives would have to be a detector, and we established a detector is not available here.

---

## 2026-07-31 — F7 landed as `a7437a3`, and the rule for where a latent defect belongs

BE #126 merged. **Eight packets landed today**: FE #74, #76, #72; BE #98, #97, #99, #125, #126.
FE main `f9ab894`, BE main `a7437a3`, both verified green at every intermediate point
(`c8ae076` cancelled-by-supersede, `faba3bf` ✅, `2c8b8f4` ✅, `25cb8b7` ✅).

### The ordering principle — worth keeping, because the obvious version is wrong

F7 shipped **ahead of** WP-27 rather than inside it. The instinct is the opposite: a defect *my*
packet surfaced belongs *in* my packet. WP-27's owner argued the correct version and it generalises:

> **When a packet makes a latent defect reachable, the defect is not the packet's to carry.**

`DashboardController` never honoured `IsSample`, and every other consumer already did. WP-27 only gave
a practice order a path to `delivered` for the first time, which made the existing violation
*reachable*. Bundling the fix would have done two bad things: written a pre-existing invariant
violation into a packet's history as though that packet caused it, and held a correct standalone fix
behind a re-refutation gate with nothing to do with it.

Same shape as TRAP 22's mock-divergence finding — inert until something crossed a boundary. **The fix
belongs where the defect lives, not where it surfaced.**

### Verification discipline worth copying, from the #100 rebase

Its owner re-checked `origin/main` by `rev-parse` and subject line rather than trusting the SHA passed
in a message, and confirmed `merge-base --is-ancestor` read NO before the merge and YES after —
i.e. verified the *transition*, not just the end state. Then ran `ProcuLink.Transform.Tests` locally
(1449 passed / 0 failed / 2 skipped) because that project is Docker-free, so #125's CSV-dialect and
typed-JSON-leaf work is proven **on the merged tree** rather than only on the branch where it was
written.

That is TRAP 23's lesson applied without being prompted: green on the branch and green on the merged
program are different claims.

---

## 2026-07-31 — TRAP 26: the fixture that pins a ban re-emits the banned thing

FE #69 (WP-30) was **refuted a second time**, five blocking. Notably *not* for weak tests — all 15
mutations reproduced, 14/14 contrast numbers agree to 4 decimal places, the ratchet narrowing is
correct. The tests are sound and the AC is still false.

The headline finding is a shape neither session had written down.

### The mechanism, verified independently

```
tailwind.config.ts:25            "./src/**/*.{ts,tsx}"        ← no test exclusion
src/test/check-tokens.test.ts:189  writeFileSync(outside,
    `export const ring = "focus-visible:ring-[#28C55E]";\n`)  ← the emerald-ban fixture
```

`bun run build` succeeds, and the built CSS still contains
`.focus-visible\:ring-\[\#28C55E\]:focus-visible{--tw-ring-color:rgb(40 197 94/…)}`.

Tailwind's content scanner is a **regex over file text**. It does not parse, and it has no idea the
string is a fixture being written to a temp file by a test. It sees a class name in a scanned file and
emits the rule. So **the test written to pin the emerald ban is what puts emerald back into production
CSS.**

Both new token guards skip `*.test.tsx?` by design. Tailwind does not. The author verified "built CSS
free of `28C55E`" and it was not — because **the verification and the emission ran through different
scanners.**

### The generalisable form

> When two tools scan the same file set with **different exclusion rules**, an exclusion in one is not
> an exclusion in the other. A fixture invisible to the guard can still be visible to the compiler.

This is the instrument class inverted. Every previous instance was a check that failed to *see* a
defect — `looksLikeProse`'s char class, the source-text popover assertion, my status reducer putting
PENDING ahead of FAILURE. This is a check that **creates** the defect it exists to prevent. One-line
fix (concatenate the literal in the fixture, or exclude tests from `content`), but the shape is worth
more than the fix.

### The other four, recorded so they are not re-derived

- **F1** — third "verified where applied, not where the defect class lives" instance this round.
  `ghostTierColor`'s return is the fill of a 7px/800 SVG `<text>` numeral: `#B36D14` on white is
  4.1061:1, the `ok` tier 4.1613:1, both under 4.5. The same commit **rewrote that function's doc
  comment asserting it is non-text**, and fixed the byte-identical construction at
  `WireTopology.tsx:365` one file over.
- **F5** — the AC's escape clause does not cover the failures. It defers to "the 798 ledgered
  violations", but the ledger is `src/app/**` only, and all nine live sub-4.5:1 text pairs found this
  round are in `src/components/**` — including `OnboardingChecklist:451` at 3.65:1, the same pair the
  packet claims to have swept.
- **F3** — the new rule is repo-wide in *file* scope but narrow in *syntax* scope: it matches
  `color:`/`fg:` followed by a quoted literal **on one line**. Eight genuine spellings evade (variable
  indirection, non-`fg` map key, a ternary's else branch, `className="text-amber"`, an arbitrary
  class, an SVG `fill=`, a template literal, and every `.css` file — the regex requires a quote before
  `var(`, and CSS never quotes).
- `#28C55E` was **10** live uses, not 9; `RETIRED_RE` is hex-only so `rgb(40,197,94)` walks past; and
  `COLOR_FN_RE` lacks the `i` flag so `RGBA(` evades.

Genuinely good in the same diff: the codemod made **zero wrong-direction moves across 52 sites**, the
growth test restores byte-for-byte in a `finally`, and the gate prints a "WHAT THIS GATE DOES NOT DO"
header on every run — which the refuter singled out as the best thing in it, and which is the same
virtue as `OrderStatusMachine.cs:337` documenting what it did *not* close.

### 2026-07-31 — Wave 4 second-pass refutations: #69 REFUTED again, #70/#75 confirmed-with-defects

**FE #69 (WP-30) — REFUTED, 5 blocking, zero vacuous tests.** All 15 mutations reproduced, the ratchet
narrowing is exactly as instructed, 14/14 contrast figures agree to 4dp. The tests are sound; the AC is
still false.

- **The fixture that pins the ban re-emits it.** `bun run build` succeeds and the production CSS still
  ships `.focus-visible\:ring-\[\#28C55E\]`. `tailwind.config.ts` scans `./src/**/*.{ts,tsx}` with no
  test exclusion, so Tailwind's content scanner reads the banned class name out of
  `src/test/check-tokens.test.ts:189` — **the fixture written to prove the emerald is gone** — and emits
  the rule. Both new guards skip `*.test.tsx?` by design; Tailwind does not. Not a check that fails to
  see a defect: **a check that creates the defect it exists to prevent.** One-line fix (concatenate the
  class name, or exclude tests from `content`), but a new trap shape.
- **`ghostTierColor` is the fill of a 7px/800 SVG `<text>`** — `#B36D14` on white = 4.1061:1, `ok` tier
  4.1613:1. The same commit **rewrote that function's comment asserting it is non-text** and correctly
  fixed the byte-identical construction at `WireTopology.tsx:365`. Verified where applied, not where the
  class lives — third instance this round.
- **The AC's escape clause names the wrong region.** It defers to "the 798 ledgered violations", but the
  ledger is `src/app/**` only and **all nine live failures are in `src/components/**`** — reachable from
  `/bridge`, `/inbox/[orderId]`, `/operations/*`. Includes `OnboardingChecklist:451` at 3.65:1, the same
  pair the packet claims to have swept.
- The new repo-wide rule is repo-wide in FILE scope, narrow in SYNTAX scope: it matches `color:`/`fg:`
  plus a **quoted literal on one line**. Eight spellings evade, each isolated with a passing control.

**FE #70 + #75 (WP-29) — CONFIRMED-WITH-DEFECTS, 88 mutations reproduced, all four pass-1 vacuities
dead.** R4/R6/R8/R10 all exit 1 now; the count-parity test was not weakened (R1/R2/R3 still exit 1); the
CI-enforced deferral is **real** — merging #75 into #70 fails with the exact missing chip row named.

- **G1, and it is the same trap one layer down.** The `blockBody` fix strips comments, but
  `stripComments` **preserves string and template literals by design** (the link guards need them), so
  the same `const NAME` token in a **string literal, template literal or JSX text node** still disarms
  `registry-moved` on all seven blocks. Reproduced on the real tree using the file's own guard-error
  idiom: `checked 37 navigation label(s) … OK`, exit 0 — **43 → 37, six chip labels silently unpoliced.**
  The fix is precise and already in the repo: **`maskLiterals` is exported at `sourceScan.ts:99`, is
  offset-preserving, and is used two lines away in `extractRaw` for exactly this reason.** Anchor on
  `maskLiterals(stripComments(src))`. Also wants a per-block floor — a captured-but-empty body
  contributes zero labels and raises no offence.
- **G4: a THIRD indexed help article still teaches the pre-rename failure vocabulary**
  (`help/exceptions-and-stuck-orders/page.mdx:20-24`), linked twice from the two #75 rewrote. #75's guard
  reads only the two articles it fixed. **Do not silently align it** — `healthTiles.ts:39`,
  `ExceptionDetail.tsx:48/50`, `BridgeTopbar.tsx:260` and `problemCopy.ts:260` still render the old names,
  so the PRODUCT carries two failure vocabularies and #75 aligned the articles with one of them. The
  reconciliation is its own packet.
- G2: the stripper move to untyped `.mjs` dropped the `SourceSyntax` union from a shared guard's public
  type — `stripComments("x","bogus")` now typechecks. G3: the meta-test's own registry list is a
  source-text regex over a `>= 6` floor on a list of exactly 6, so a seventh entry written multi-line is
  silently uncovered. G10: both branches sit on `d119e91`, not `f9ab894`; WP-28 also edits `InboxView.tsx`.

**Handoff:** #69 needs a third pass. #70/#75 need G1 (cheap, mechanism identified above) and a rebase.
#73 green at `792938b`, BE #100 rebased at `6555d41`, #77 green at `6147a76` with both blockers closed.

---

## 2026-07-31 — Wave 4 handed back: what its owner left open, and where it went

The Wave 4 session closed after eight adversarial passes, ~20 vacuous tests found, two packets landed,
and **nothing merged on green CI alone**. Its parting state:

| PR | State |
|---|---|
| FE #72, #76 | merged (`f9ab894`, `d119e91`) |
| FE #73 · BE #100 | green `792938b` · rebased `6555d41`, contains `a7437a3` |
| FE #77 | green `6147a76`, both blockers closed, PR body rewritten |
| FE #69 | **refuted twice** — needs a third pass |
| FE #70 · #75 | confirmed-with-defects — G1 + rebase |

Three unowned items came back with it. All three are now dispatched as chips rather than left in a
report.

### G1 — TRAP 13's third instance is still open, one layer down

The `blockBody` fix strips comments. But **`stripComments` preserves string and template literals by
design**, because the link guards need them. So the same `const NAME` token inside a string literal,
template literal or JSX text node still disarms `registry-moved` — **on all seven policed blocks**.
Reproduced on the real tree: `checked 37 navigation label(s) … OK`, exit 0, where it should see 43.
**Six chip labels silently unpoliced.**

The fix is already in the repo: `maskLiterals` is exported at `sourceScan.ts:99`, is offset-preserving
by construction, and sits two lines from `extractRaw` which uses it for exactly this reason. Anchor on
`maskLiterals(stripComments(src))`, plus a per-block floor — a captured-but-empty body currently
contributes zero labels and raises no offence at all.

Its owner **declined to do it and said why**, which is the right call recorded the right way: it means
moving `maskLiterals` and `readLiteral` across the same TS→ESM boundary `stripComments` just crossed,
updating the re-export, and re-verifying three consumer guards plus the gate — on a branch #77 is
stacked on. "Recording the mechanism precisely is worth more than a half-verified refactor of the file
`4c7350a` already fixed once." Dispatched as its own chip, with FE #70/#75 named as blocked on it.

### G4 — the product carries two failure vocabularies, and one packet aligned half of them

`help/exceptions-and-stuck-orders/page.mdx:20-24` still teaches **Parse failed / Transform failed /
Delivery failed / Rejected by supplier**, and is linked twice from the two articles #75 rewrote. But
`healthTiles.ts:39`, `ExceptionDetail.tsx:48/50`, `BridgeTopbar.tsx:260` and `problemCopy.ts:260`
**still render those old names**.

So aligning the third article with the rewritten two would make it contradict the exceptions screen
the user is looking at. **Do not quick-fix this.** It is TRAP 25's shape as a standing condition
rather than a merge event: two vocabularies, no mechanical check, and the obvious repair makes it
worse. Dispatched as its own packet, instructed to derive the inventory from `STATUS_META` rather than
grep the words that look wrong — the technique that found five extra wrong labels last time.

### G2 and G10 — two for the merge preconditions

- **G2:** the stripper's move to untyped `.mjs` dropped the `SourceSyntax` union from a shared guard's
  public type. `stripComments("x", "bogus")` now typechecks where it did not at `d119e91`. Folded into
  the G1 chip, since it is the same file and the same boundary.
- **G10:** #70 and #75 sit on `d119e91`, not `f9ab894`, and **WP-28 also edits `InboxView.tsx`**, where
  #70's diff is 508 lines. That merge wants **reading**, not just CI — TRAP 23 and TRAP 25 both apply
  to the same file at once.

### Dispatched this session

Seven chips total, all unowned and file-disjoint from anything in flight: WP-10 remediation, WP-11
billing gates, WP-02 vacuous tests, WP-19+WP-24 recovery, **G1+G2 guard fix**, **G4 vocabulary
reconciliation**, **WP-30 third pass**.

---

## 2026-07-31 — nine landed; seven sessions now in flight; a lookout posted

FE #71 (WP-15 S1+S2+S4+S5 — stop deleting rule fields, describe the real manipulators, make node order
changeable) merged as **`d733c10`**. Ninth packet today. Its wedge owner had rebased it *and* resolved
#65's conflict since the last sweep, so both came back sweepable without anyone asking.

#71 met every precondition and was **fully disjoint** from the other two candidates — it touches
`OutputStructureDesigner`, `mapper/mapperModel`, `outputNamespaceModel`, `outputRuleModel` and
`api/types.ts`; no `OrderWorkshop.tsx`, no `api-client.ts`. 61 new test cases. FE #65 is updated onto
`d733c10` and re-running.

### FE #73 (WP-27) held — G6 verified at source, not taken on report

```
src/lib/api-client.ts:1593   const t = await res.text().catch(() => "");
                     :1594   throw new Error(t || `sample-order: ${res.status}`);
```

`realRunSampleOrder` throws the **raw response body** as the user-facing message. That is exactly the
defect FE #70 (WP-29) exists to fix one function over — its whole point is that a raw JSON body must
never reach the user. Merging #73 now would ship the thing another in-flight packet is fixing, in the
same file.

This is the claim collision the overlap trigger surfaced, and it is worth noting that **it is now the
reason a merge is held**, not merely an observation. The trigger paid for itself twice: once by
finding it, once by making it actionable at merge time.

It routes naturally to the **WP-19+WP-24** session, whose scope is exactly 4xx error shaping — so G6
does not need a packet of its own.

### Seven sessions in flight — the collision surface is now the risk

WP-10, WP-11, WP-02, WP-19+WP-24, G1+G2, G4, WP-30 third pass. Known danger points from the file map:

| Risk | Detail |
|---|---|
| **WP-30 vs everyone** | its token sweep touches ~69 files app-wide, including the marketing pages WP-10 edits and the pricing page WP-11 edits |
| **G4 × WP-19+24** | both reach `ExceptionDetail.tsx`, `BridgeTopbar.tsx`, `healthTiles.ts`, `problemCopy.ts`. G4 changes what those screens **say**; WP-19+24 changes what they **do**. TRAP 25's exact shape, and no build catches it |
| **G1+G2 × WP-30** | adjacent in `src/test/` and `scripts/`; WP-30's fix may exclude tests from `tailwind.config.ts` `content`, which G1 does not touch |
| **WP-02 × WP-30/G1** | all three touch test files, though WP-02 is BE-weighted |

A read-only **collision lookout** is posted with a strict methodology brief: containment pre-pass
before any pairwise diff (the failure that produced two wrong answers earlier today), hotspot
detection at 3+ independent branches, and explicit instruction to quote both sides wherever two
branches change a user-visible claim in one file.

### Sweep state

| | |
|---|---|
| FE main | `d733c10` |
| BE main | `a7437a3` |
| Held | FE #73 (G6, above) · #69 (refuted twice) · #70/#75 (blocked on G1 + rebase) · #77 (stacked on #69) · BE #100 (pairs with #73) · BE #124 (behind main) · #116 (behind, conflicting) · #119/#121/#122 (drafts/throwaways) |
| Re-running | FE #65 |

---

## 2026-07-31 — ten landed, and TRAP 27: I dispatched two packets into work that already existed

FE #65 (WP-13, the promote control) merged as **`1d5e86d`** — tenth packet today. BE main moved to
**`3c75daf`** when the mid-parse session landed WP-23a (#119). A read-only collision lookout was posted
over the seven concurrent sessions and returned findings that change what happens next.

### TRAP 27 — dispatching against `main` when the prerequisite lives in an unmerged PR

I dispatched two chips whose work already existed in open, green, unmerged pull requests. Neither chip
could have known; **both briefs were mine.**

| Dispatched | Already existed in | Result |
|---|---|---|
| **G4** — reconcile the failure vocabulary | **FE #75** rewrote the same `dashboard-and-statuses/page.mdx` from the identical base blob `ff7a528` | A real 3-way merge yields **4 conflict hunks**, plus two competing tests for one invariant (`help-status-labels.test.ts` vs `statusVocabulary.test.ts`) |
| **G1+G2** — close the gate's literal blind spot | **FE #70** already extracted the scanner to `scripts/lib/stripComments.mjs` | G1 is building `scripts/lib/sourceScan.mjs` — same extraction, different filename, into a directory **neither base has**, so the two cannot merge |

The mechanism: I wrote each brief against `origin/main`, because that is where a new packet should
start. But the prerequisite for both was sitting in a PR that had not landed — so from `main`'s point
of view the work looked undone, and each chip correctly set about doing it.

> **Before dispatching, check the open PR set, not just `main`. A packet's prerequisite may be
> written, reviewed and green, and still invisible from `main`.**

This is the dispatch-time twin of TRAP 24. There, green CI was mistaken for coverage; here, `main` was
mistaken for the sum of the work. Both are a narrower thing standing in for a wider one.

Corrected both sessions in flight: G4 told to read #75 first and either adopt or explicitly supersede
it (its genuinely unique half — the four render sites `healthTiles.ts:37-41`, `ExceptionDetail.tsx`,
`BridgeTopbar.tsx`, `problemCopy.ts` — is untouched by #75 and is the real packet); G1 told to base on
`origin/claude/wp29-inbox` and **add** `maskLiterals` to #70's module rather than fork it. G1's actual
finding stands and is not duplicated: #70 closed comments, G1 found literals.

### C4 — main already contradicts itself, and nothing in flight fixes it

- **BE #89** (WP-19, merged 07-30) made `rejected_by_supplier` **recoverable**:
  `OrderStatusMachine[RejectedBySupplier]` went from `Set()` to `Set(PendingReview, Ready, Transforming)`.
- **FE #61** (WP-24, merged 07-31, *a day later*) shipped `problemActions.ts` asserting it is
  **terminal**: `transformOrder: new Set(["ready","pending_review","transform_failed"])`.

Both on main. **The frontend refuses a recovery the backend accepts.** Two merged packets, a day
apart, and the contradiction belongs to neither — TRAP 25's shape across a *merge boundary in time*
rather than across two branches. Routed to the WP-19+WP-24 session, whose scope it is.

### The lookout's methodology worked where mine failed

Its containment pre-pass correctly identified `wp30-tokens ⊂ wp31-a11y` and excluded the pair — the
step whose absence gave me two wrong overlap answers earlier today. WP-31's own diff is **33 files,
not 78**.

It also found what no ref-based audit can: **six of the seven "in-flight" packets have zero pushed
commits.** The real work is uncommitted worktree state. If a session is interrupted, that work is
unrecoverable — worth knowing before anyone reorders anything.

### Two blockers on the merge order

- **FE #77 (WP-31)**: only **Vercel** has reported. Build, unit and e2e checks have **never run**. It
  is also stacked on a `wp30-tokens` head that has since diverged from its own session's third pass
  (57 files apart). Must not merge on the strength of a green Vercel badge.
- **BE #116**: targets `docs/v1-master-plan`, not `main`, and is **CONFLICTING** with no checks. It
  cannot merge as configured.

### Ordering that is now fixed, not advisory

**Behaviour lands before the prose that describes it.** WP-19+24 rewrites the actions inside
`problemCopy.ts`'s `rejected_by_supplier` entry; G4 renames the badges in the same file on different
lines. Git auto-merges, TypeScript compiles, and nothing checks that the prose still describes the
behaviour. Both sessions have been told the order and the reason.

Likewise the vocabulary gate: G1 owns it, and G4's `check-vocabulary.mjs` copy edit lands after.

---

## 2026-08-01 overnight — WP-11 landed as a pair, enforcement first

Running an unattended merge watch until 10:00. Merges gated on: contains current `main`, a CI run
*since* that, and test-side changes beside the production files.

| Merged | Packet | Result |
|---|---|---|
| BE #124 | WP-15 S3 — pin the manipulator contract from the C# side | BE `a138ce4` |
| BE #127 | WP-11 follow-through — prove the gates against compiled code | BE `e4d4ac5` |
| FE #78 | WP-11 follow-through — the tier a capability is sold on must be the tier that gates it | FE `0e3c445` |

**Thirteen packets today.**

### The WP-11 halves were deliberately ordered, and the rule is the same one G4 got

FE #78 went green and merge-ready *before* BE #127 and was **held anyway**. #78 is the claims half —
marketing copy, `plans.ts`, the billing FAQ. #127 is the enforcement half. Landing the claims first
would have published tier promises that nothing enforced yet, which is precisely the defect WP-11
exists to close, so the packet would have briefly *caused* its own bug.

**Behaviour lands before the prose that describes it.** Same ordering given to G4 vs WP-19+24 on
`problemCopy.ts`, and it is now applied twice from two different directions.

BE #127 also earns a note on instrument quality: it proves the gates with an **IL scanner**
(`BillingGateIlScanner.cs`) reading compiled code, not a source-text grep. Three source-text
assertions were caught standing in for behavioural ones today — this is the opposite, and the right
shape.

### Still held overnight, each for a stated reason

- **FE #77** (WP-31) — only Vercel has ever reported. Build, unit and e2e have **never run**.
- **FE #73** (WP-27) — `api-client.ts:1593-94` throws the raw response body to the user (G6). Routed to
  the WP-19+24 session.
- **FE #69** — refuted twice; its third pass is in flight.
- **FE #70 / #75** — #70 owns the vocabulary gate that G1+G2 must build on; #75's glossary overlaps G4.
- **BE #100** — updated onto `e4d4ac5`, re-running.
- **BE #116** — targets `docs/v1-master-plan`, conflicting, no checks. Cannot merge as configured.

---

## 2026-08-01 overnight — seventeen landed, and two orphaned PRs rescued by hand

| Merged | Packet | Result |
|---|---|---|
| BE #100 | WP-27 backend — practice delivery loop + `IsSample` on the DTO | BE `a4e78f6` |
| FE #79 | G4 — one failure vocabulary at the render sites, derived from `STATUS_META` | FE `e0e1be5` |
| FE #81 | G1+G2 — a string literal naming a registry no longer disarms the gate | FE `dc34947` |
| FE #70 | WP-29 — make the valuable state visible in the inbox | FE `dc34425` |

BE #100 landed **before** FE #73, so the `sample-order` rate cap exists before anything can call it.

### G4 reconciled instead of duplicating, and the dispatch error cost nothing

Told that FE #75 already owned the glossary, the G4 session **dropped**
`dashboard-and-statuses/page.mdx` — the file with 4 conflict hunks — and kept only the half #75 does
not touch: the four render sites. TRAP 27 was caught early enough that the duplicate work was never
written.

### Two orphaned PRs rescued — their owner's session had ended

**FE #70** had gone `CONFLICTING`. Two conflicts, neither resolvable by picking a side:

- `BridgeDashboard.tsx` — **main's own comment documented the defect** as "unfixed and deliberately
  named rather than papered over": `countReady` sums `ready` AND `ready_to_deliver`, so both its
  labels under-describe the number. #70 fixes it by splitting the segment into "Ready to send" and
  "Queued to send" — which is what main's comment says the fix would be. Took #70.
- `api-client.ts` — **combined.** #70 replaced a raw `throw new Error(res.text())` with
  `ApiHttpError` + `parseApiErrorBody`, so the operator sees the backend's sentence and a 403's
  `upgradeUrl` survives for the banner. G4 renamed the copy off "Transform failed" but still
  interpolated the raw body. Kept #70's structure, took G4's wording.

Then #81 landed and made #70's `scripts/lib/stripComments.mjs` redundant, so the gate files went to
main wholesale and that module was deleted rather than carried.

**The "ONE COPY, THREE CONSUMERS" guard earned itself on its first real merge.** It exists to turn CI
red when a branch carries its own copy of the shared scanner — precisely what #70 was about to do. Its
comment named `stripComments.mjs` by filename, which is what dated it; reworded to name the hazard.

**FE #75** failed on something better designed than the failure it caused. G4 deferred two help
articles to #75 rather than rewrite them twice, and guarded the deferral with a test asserting each
deferred file is **still dirty** — *"deliberately not a silent skip. An exemption that outlives its
reason is a lie."* Merging main carried that guard onto #75's branch, where the file is already
rewritten, so the exemption became false and the test named it. Removed; 97/97.

> **A deferral that verifies its own necessity is worth the six lines.** Both halves of this worked:
> G4 skipped work it would have duplicated, and the skip announced its own expiry instead of rotting.

### Ordering held all night

Enforcement before claims (BE #127 → FE #78), backend before frontend (BE #100 → FE #73), gate owner
before gate consumer (#81 → #70). No merge went in on green CI alone.

---

## 2026-08-01 overnight — nineteen landed; every Wave 4 orphan is home

FE #75 (`docs(help)`) merged as **`9278aec`**. With #70 and #81 already in, all three PRs orphaned by
the Wave 4 session's end are on main. BE #116 also landed, so the Wave 3 session's closure record is
preserved on the plan branch alongside the sweep log.

**Nineteen packets in the day:** FE #74, #76, #72, #71, #65, #78, #79, #81, #70, #75 · BE #98, #97,
#99, #125, #126, #124, #127, #100, #116.

### Guards that caught real drift tonight, all three written by the packets they later blocked

1. **The self-retiring deferral.** G4 skipped two help articles it would have duplicated and asserted
   each skipped file is *still dirty*. On #75's branch one was already rewritten, so the exemption
   became false and the test named it. *"An exemption that outlives its reason is a lie."*
2. **ONE COPY, THREE CONSUMERS.** Turned red on a branch carrying its own copy of the shared scanner
   — exactly what #70 was about to do once #81 landed the superset.
3. **The chip table binds to the rendered list.** WP-29 split the queue's `ready` chip into "Ready to
   send" and "Queued to send"; the toolbar went to six chips and `inbox-basics` documented five.
   #75's own guard — *"the chip table lists exactly the chips the toolbar renders, in order"* —
   failed by name.

The third is the one worth generalising. TRAP 25 says copy is not type-checked against behaviour and
nothing mechanical catches prose drift. **That is true by default and false where someone binds the
prose to the source of truth.** All three of tonight's catches were packets that wrote the binding
themselves, and each one blocked a *later* packet — not its own author. That is the shape to copy: a
guard is worth most when it fails for someone who was not in the room when it was written.

### FE #82 (WP-19+24) — red, diagnosed, handed back

Three e2e specs assert `/order not found/i`. The gate is **intact and improved**: WP-19 replaced
main's flat `order === null ? "Order not found" : "Failed to load order"` with branched 4xx copy —
`"We can't find this order"`, `"You've been signed out"`, `"This order isn't yours to open"`. So the
phrase was deliberately retired and three pre-existing specs still pin it.

I flagged uncertainty about whether the gate had vanished, then resolved it rather than leaving the
doubt with its owner. Advised pinning by *meaning* — the "Back to inbox" action and the absence of
Retry — rather than by sentence, since an exact-phrase assertion breaks on the next reword.

---

## 2026-08-01 overnight — twenty-two landed; WP-27 complete end to end

| Merged | Packet | Result |
|---|---|---|
| BE #128 | WP-02 round 2 — the vacuous-pass guard enforced one rule, not five | BE `429ee7f` |
| FE #82 | WP-19 + WP-24 — a failure that names its cause, and a screen that acts on it | FE `756e44c` |
| FE #73 | WP-27 — onboarding that completes in one sitting | FE `8fca74a` |

**All four PRs orphaned by the Wave 4 session are now on main** (#70, #75, #81, #73), and WP-27 is
complete end to end: BE #100's practice-delivery loop, its `sample-order` rate cap, and the frontend
that drives them — landed in that order, so the cap existed before anything could call it.

### BE #128 changes CI, and it tightens rather than loosens

Worth recording because a workflow change is the one diff that can quietly disable everything else:

- adds a **skip census** naming every skipped test *and its reason* in the job summary
- **fails on a skip with no reason** — "Skipped" with an empty message is indistinguishable from a
  test that quietly stopped existing
- **fails when no `.trx` is produced at all** — *"the skip census cannot read the run, so it must not
  report a pass"*
- the artifact upload moved from `if: failure()` to `if: always()`, which the census needs

That last pair is the day's "silence is not success" rule written into CI. `Live_ImapIngress`
reported Passed for two and a half weeks; this is what makes that impossible to repeat quietly.
Verified BE main green on `429ee7f` **after** the workflow change, not just the PR.

### FE #82 cleared both of #73's blockers in one packet

- **G6 fixed** — `realRunSampleOrder` now goes through `parseApiErrorBody` + `ApiHttpError` instead
  of throwing the raw response body at the operator.
- The 404 specs were stale, not broken: WP-19 replaced main's flat
  `order === null ? "Order not found" : "Failed to load order"` with branched 4xx copy. I resolved
  that ambiguity rather than leaving it with its owner, and it came back the good way.

### The #73 rescue — a third conflict where neither side was right

`OrderWorkshop.tsx`:

- **WP-27** replaced `?sample=1` with `order.isSample`, because the param was only ever appended by
  `useSampleOrder` — so a practice order opened from a bookmark, the back button, or an inbox row
  rendered as **real work**, and a real order with the param pasted on rendered as practice.
- **WP-24** (already on main) kept the old param read but added `useTabParamSync`, fixing a separate
  real bug: "See their reply" points at `/inbox/{id}?tab=response`, a same-route navigation that does
  not remount, so the initialiser never re-ran and **the button did nothing at all**.

Kept both. Third time tonight that a conflict resolved to a combination rather than a side.

**And a fourth guard fired.** `routeQueryParams` asserts every declared param is really read, and
named the exact ambiguity: *"either the reader was removed (every link carrying it is now inert) or
the contract names the wrong file."* It was the former — nothing appends `?sample=1` any more — so
the registry entry went rather than the reader coming back.

That is now four guards in one night, each written by one packet and each catching a **different,
later** packet: the self-retiring deferral, ONE COPY / THREE CONSUMERS, the chip table bound to the
rendered list, and the route-param registry. None of them caught its own author.

---

## 2026-08-01 — WP-30 third pass: TRAP 26 closed, and a new way for a guard to go dark

FE #69 landed after a **third** pass. Round 2 was refuted on five blocking findings, and the split
that mattered was: the tests were sound (15/15 mutations reproduced, 14/14 contrast figures to 4dp,
zero wrong-direction codemod moves across 52 sites) and **the acceptance criteria were false**. Those
are different failures. Round 3 kept the tests and rewrote the AC.

### TRAP 26, closed, and verified by the scanner that emits

The fix is three-layered, in increasing order of durability: `tailwind.config.ts` excludes tests
(measured — removes **exactly one** emitted rule, 1088 → 1087, the emerald ring and nothing else); the
fixture **assembles** its literal so the mistake is not one config edit away; and
`scripts/check-emitted-css.mjs` asks the real compiler what it emits, wired into CI.

The verification is the part worth copying. The defect was reproduced in the real build first —
`.next/static/css/d13993a82d7b9159.css` shipping
`.focus-visible\:ring-\[\#28C55E\]{--tw-ring-color:rgb(40 197 94/…)}` — and then confirmed gone by
**reading the built stylesheets**, not by re-running the guard. Re-running the guard could never have
shown either state, because the guard is not the scanner that emits.

The new gate matches **decimal as well as hex**, and that is load-bearing rather than thorough:
Tailwind compiles the class to `rgb(40 197 94 / …)`, so the hex survives only in the escaped
selector. Rename the class and a hex-only grep sees nothing while the banned declaration still ships.
The same hole existed one rule down — `RETIRED_RE` was hex-only, so `rgb(40,197,94)` walked past the
retired-colour rule, which is exactly the defect the `color-fn` rule beside it exists to close.

### The new shape: an anchor longer than the report goes dark, silently

Round 3's exemption lists are matched by **line content, not line number**, so an exemption whose site
has changed goes stale and turns the suite red instead of forgiving whatever replaced it.

It was tested for real within the hour. WP-27 (#73) landed mid-flight, touching 24 of the same files,
and reworded two `BridgeDashboard` stat rows — added an `href`, added a `queued` entry. Both
exemptions went stale and the suite went red, exactly as intended. Re-verified before re-anchoring:
those `color` fields are still consumed only as `background:` on a dot, a bar segment and a legend
square.

But the same event exposed a hole in the mechanism, and this is the reusable part:

> An exemption anchor is matched against the **truncated** report line (120 chars). #73 pushed both
> rows past that. An over-long anchor **can never match a hit** — so it silently stops forgiving its
> site, while the stale check, which reads the whole file, still reports it as fine. The failure then
> reads as a brand-new violation and sends the reader after a phantom.

Same family as TRAP 26 and as everything else this packet was refuted for: **a check that looks green
while doing nothing**, because two parts of it disagree about what they are reading. `REPORT_LINE_CHARS`
is now exported and named as load-bearing, the anchors are shortened to distinctive fragments (which
survives cosmetic reflow anyway), and `assertAnchorsFitTheReport()` runs in both blocks —
mutation-checked by restoring the 137-character anchor.

**That is a fifth guard catching a later packet.** The self-retiring deferral, ONE COPY / THREE
CONSUMERS, the chip table bound to the rendered list, the route-param registry — and now the
content-anchored exemption. Still none of them caught its own author.

**A sixth, an hour later.** WP-10 (#83) landed on main and added one more link to
`(marketing)/privacy/page.tsx` in exactly the style the file's seven existing links use —
`color: "#1E6D29"` — taking the file 17 → 18 and failing `lint:tokens`. #83 landed legitimately:
the gate is not on main yet, so main is green and only the merged tree is red.

CI on that merge commit failed on exactly two steps — `Design-token gate` and the vitest case that
runs it — so this was not a local-only artefact; the branch was red on GitHub until it was fixed.

The interesting part is the fix that was NOT taken. Bumping the row to 18 would have made CI green in
one character. **A per-file count that rises whenever something trips it is not a ratchet, it is a
log.** All eight spellings went to `var(--brand-green-deep)` — same value, same render, and the file
stops being seven-of-one-and-one-of-the-other — and the row was re-cut 17 → **10**.

### A shared branch makes a rebase a hostile act

Worth writing down because it nearly went the other way. This session rebased `claude/wp30-tokens`
onto main and went to force-push. **`--force-with-lease` refused it** — `! [rejected] (stale info)` —
because ANOTHER session had meanwhile pushed `Merge branch 'main' into claude/wp30-tokens` to the
same branch, pulling in #83.

A plain `--force` would have silently deleted that merge commit. The lease is what turned a
destructive operation into a question. The resolution was to abandon the local rebase entirely
(`git branch -f wp30-local-rebase` as a safety ref first), `reset --hard` onto the remote tip, and
**cherry-pick only the one new commit** on top — which pushes as a fast-forward and destroys nothing.

> On a branch more than one agent can push to, prefer integrating onto THEIR tip over replaying your
> history over it. `--force-with-lease` is not optional there, and a rejected lease is information,
> not an obstacle to route around.

### One vacuous test, found in round 3's own work

The decimal case used `text-[#28C55E]` and asserted the report contained `40 197 94` — but the report
prints a ±60-character **context window** around each hit, so the decimal sat inside the window of the
*hex* hit. Deleting the decimal branch from `retiredRegex()` entirely left all seven cases green. It
now names the colour in the fixture's `theme.extend.colors`, so the selector is `.text-retired-probe`
and the only trace of the banned value in the compiled CSS is the declaration, in decimal.

Note the shape: the assertion was reading the **diagnostic output** rather than the **matched value**.
A report designed to be readable had made the test unfalsifiable.

### A mutation-procedure defect, recorded so it is not re-derived

A `node -e` string replace written with `\n` **silently no-op'd** against this repo's CRLF files, and
the run then looked like "the mutant survived" — indistinguishable from "the test is vacuous". It sent
one investigation down the wrong path before being caught.

**Rule: a mutation is only evidence if the edit is confirmed to have landed.** Either assert the
replacement changed the file (`if (s === before) process.exit(3)`) or observe a behaviour change that
could only follow from it. Every mutation recorded in this round was confirmed that way after the
false reading.

Second, smaller one: the emitted-CSS cases each spawn the Tailwind compiler (1.5–4.5s), and vitest's
5s default turned that into flake — a first mutation run reported five failures where **one** was real
and four were timeouts wearing an assertion's clothes. A timeout that reads as a failed assertion
makes a mutation check unreadable, and a mutation check is the one tool that says whether any of the
rest is load-bearing.

### The AC clause that was wrong, and why the shape recurs

Round 2 deferred its un-audited remainder to *"the 798 ledgered violations"*. The ledger is
`src/app/**` only **and it counts raw hex, not contrast** — two different debts treated as one. It
could not absolve `src/components/**`, which has never had a row in it, and could not absolve anything
of a contrast failure even inside src/app. A full audit of `src/components/**` returned a table of
**27** sub-4.5:1 text pairs against the nine the refutation had named — including the pair round 2
claimed to have swept — and working through them surfaced roughly fifteen more that the first pass
had not reached, so the honest figure is "the nine were a floor, not a count".

`check-tokens.mjs`'s "WHAT THIS GATE DOES NOT DO" header — which a refuter called the best thing in
the diff — gained two items in its own voice: a ledger is only an alibi for the files it lists and the
kind of debt it counts, and a source-text gate is not an emission check.

Two systemic causes, not 27 bugs: `--brand-green` and `--amber` are each the **non-text member** of
their family being used in `color:` and `fill=`. `PoMappingEditor` is the one to remember — it
declared `const BLUE = "#2E8E3A"` alongside `BLUE_DEEP`/`BLUE_SOFT`, all three **byte-identical
duplicates of the `GREEN` trio four lines below**. `color: BLUE` reads as obviously fine in review,
which is precisely why it survived every earlier sweep. A constant that lies about its own value is
the defect; fixing only the contrast would have left the trap armed.

### Scope stated rather than implied

The green regression rule covers `src/components/**` plus `src/mdx-components.tsx` — the region
audited — and **not** `src/app/**`, where a probe found unmeasured green text sites. It is a ratchet:
the swept region cannot regress, the unswept region is named out loud. Also left open and said so:
`WireTopology`'s `isHealthy >= 80` vs `healthColor < 90` mismatch, which put amber copy on the green
pill for health 80–89 — contrast fixed, thresholds not, because collapsing them changes what
"healthy" means on screen and also reaches `BridgeDashboard.healthColor`.
## 2026-08-01 — WP-02 round 2: the guard enforced one rule, not five

**BE `#128` MERGED (`429ee7f`, post-merge CI green). FE `#80` open.** WP-02 moves from
🟢-with-a-caveat to closed on the backend.

`#79` was right about the headline: `Live_ImapIngress` is repaired and every live-transport test
is a declared skip. What its refutation found — *"the anti-vacuous scanner's GREEN is narrower
than it reads"* — was the actual packet. `VacuousTestPassScanner` enforced **one** rule, so its
green meant "no bare early return precedes an assertion" while every reader took it to mean
"every test asserts something".

Now five: `early-exit-before-assert`, `no-assertion-at-all`, `every-assertion-is-conditional`,
`tautological-assertion`, `swallowed-assertion-failure`. **37 vacuous tests fixed** — 27 backend,
10 frontend.

### Two shapes nobody had named

- **A deconstructing `foreach (var (a, b) in xs)` is `ForEachVariableStatementSyntax`**, a
  different Roslyn node from the plain form with identical meaning. Matching only the plain one
  let two allowlist sweeps in `ItemCodeComparerGuardTests` scan clean — and those allowlists are
  *designed to shrink to empty*, which is exactly the state in which they stop asserting.
- **Helper methods were never scanned.** Moving a `try` one call deeper walked past the swallow
  rule. A helper's assertions are the test's assertions.

### The allowed side needed mutation-proving as much as the caught side

A guard people learn to wave through stops being a guard. Two real tests were flagged by an early
draft and are **correct**: `f(x).Should().Be(f(x))` is a **determinism** check — textually
identical to a tautology, semantically its opposite (`ApiKeyHasherTests`,
`DeliveryServiceIdempotencyTests`). Likewise `var cases = new[] { … }` one statement above a loop
is the table-driven spelling this repo actually uses. Both now have allowed fixtures, and both
fixtures were proved by mutating the exemption and watching them go red.

**16 mutations, 19 guard tests red, all restored green.** Plus four production mutations —
dropping a `ShippingAddressKeys` entry, the Worker host, `delivery_unconfirmed`, PostHog's
api-key guard — each turning its fixed test red. **None would have failed before the fix.**

### FE: the gate that gated nothing

`package.json` ran `next lint` with **no `--dir`**, so it linted `src` only. Any Playwright rule
added to `eslint.config.js` would have reported nothing forever while the check stayed green —
WP-01's shape exactly. And there was no `expect-expect` rule to be inert in the first place:
`eslint-plugin-vitest`, `-jest` and `-playwright` were all absent from `package.json`.

18 Playwright tests asserted only the **absence** of something on a page never checked to have
rendered — a 500, a branded 404 or a `/sign-in` bounce produces no CSP violation and contains no
mock-residue literal, so both suites called those routes clean.

### The new rules immediately caught newly-merged code

Rebasing FE #80 across seven PRs merged the same day, the guard found **one** real offender in
them — `first-run-to-delivered.spec.ts` (WP-27 `#73`) dismissing the consent banner with
`.click().catch(() => {})` — and one helper needing declaration (`bothScopes`, WP-10 `#83`, which
does assert twice). **Fifth instance of the pattern**: a guard written by one packet catching a
different, later packet, never its own author.

### Skips are legible now

BE CI printed `Skipped: 14` and nothing else — not which, not why. A typo'd variable name in a
gating attribute would skip its test forever, silently; that is `Live_ImapIngress` one level up.
A census step now names every skipped test and its reason in the job summary and **fails only on
a skip with no reason**. First live run: 14 skipped, 0 reasonless, and the first name in the
report is `Live_ImapIngress_RealPollImportsCsvAttachment`. FE gained list/json/html reporters and
an always-on report upload; Playwright counts `skipped` as passing, so 31 of 111 were invisible.

### Two collisions, resolved by NOT taking the fix

- I fixed the absolute-`.claude` path bug in `RevisionAuthorityHostCoverageTests`, then found
  `claude/beautiful-torvalds-04a9a5` had fixed it first with a synthetic-tree regression test mine
  lacked. **Reverted mine**; left a comment naming that branch at the call site. As of `a4e78f6`
  main still has the bug — that branch is worth merging.
- The `docs/ops/wp14-*.md` test-writes-a-checked-in-file finding was independently fixed in
  `#130` while this packet ran.

### Process notes worth keeping

- **`Copy-Item` restore defeats the rebuild.** It carries the *backup's* timestamp, so MSBuild
  prints `Build succeeded` and compiles nothing — I ran the mutated DLL and got 7 phantom
  failures. `Build succeeded` is not evidence a compile happened. `git checkout -- <file>` writes
  a fresh timestamp; a file copy does not.
- **`git stash` on a clean tree stashes nothing, and `git stash pop` then pops a stranger's.**
  Reaching for a stash baseline applied a six-week-old WIP from the shared stack (stashes are
  per-repo, not per-worktree). Nothing was lost — a failed pop keeps the entry — but use
  `git checkout <sha> -- <path>` or a throwaway worktree for baselines, never bare `stash`.
- **Local Docker contention is not a diff failure.** A full local run failed 14 tests, every one
  inside `InitializeAsync()` at `NpgsqlDatabaseCreator.Exists`, with **32 `postgres:16` containers
  live** from sibling sessions. CI on clean infra: 4999 tests, 4985 passed, 14 skipped, 0 failed.

---

## 2026-08-01 — twenty-six landed; Wave 4 handed over; two PRs left

| Merged | Packet | Result |
|---|---|---|
| FE #83 | WP-10 — the residency retraction swept one page and missed six | FE `d34af29` |
| BE #129 | WP-19 backend — the failure's cause and its wait, readable by a client | BE `5b6d874` |
| FE #80 | WP-02 round 2 frontend — ext lint was never linting `tests/` | FE `6aa01a5` |
| BE #131 | **rescued** — stop the WP-14 report writers dirtying the working tree | BE `7aa830a` |

The Wave 4 session handed its remaining PRs over in full. Its state table was already stale — #73,
#100, #70 and #75 had landed overnight, G1 shipped as #81 and G4 as #79 — so what actually transferred
was **#69 and #77**.

### BE #131 — work that existed only on an unpushed local branch

The WP-14 chip committed `3b7cec6` in its worktree and never pushed. Two Api integration tests wrote
their generated report into `docs/ops/`, so every suite run left the tree dirty with fresh seed GUIDs
and EXPLAIN timings. Recovered, rebased, landed.

This is the second time in a day that the same risk surfaced: a collision audit found **six of seven**
concurrent sessions had zero pushed commits. **A session's worktree is not storage.** Anything worth
keeping should be pushed to a branch the moment it is committed.

Also confirmed done-by-another-route: the "RevisionAuthorityHostCoverageTests fails in worktrees" chip
had nothing to do — its fix is already on main, and the file says so: *"Another session found and
fixed this first, with a synthetic-tree regression."*

### FE #69 — three failures, none of them the packet's fault

The token sweep had been rebased onto a main that moved twenty-odd times underneath it.

1. **Budget breach.** `privacy/page.tsx` hit 18/17 — #83 added a raw `color: "#1E6D29"` Link. The
   ratchet is **shrink-only**, so raising the baseline would break its own rule; converted all eight
   occurrences in that file to `var(--brand-green-deep)` instead.
2. **Two stale exemptions.** WP-29 added click-through `href`s to the stat row, changing the lines the
   exemptions anchor on. The staleness check caught it — exactly its purpose.
3. **The one worth keeping.** The refreshed anchors *still* did not match, because
   `textColorScan.ts:216` truncates each hit to `.trim().slice(0, 120)`. **An exemption whose
   `contains` exceeds 120 characters can never match — silently — and presents as an unexempted
   violation.** Both stat-row lines cross 120 once the `href` is on them.

Before touching any colour I checked the consumer rather than the property name: `s.color` is rendered
as `background:` on an 8×8 dot and on the proportion bar, never as a glyph. Changing it would have
desynchronised the legend from the bar it labels. The scanner's `color:`-means-text heuristic is
right most of the time and wrong here, which is why the exemption mechanism exists.

A concurrent push fixed the same two anchors independently, with shorter anchors and a reason
recording the re-verification — took theirs.

### FE #77 — why "green" was not green

`gh pr checks` showed **two** checks, both Vercel. The three real jobs — build, unit, e2e — appeared
to have never run. They had: one `pull_request` run at 16:17, all three green. **Against a head the
branch no longer has.**

So the PR carries a green run bound to a superseded commit, and nothing has ever been run against
`6147a76`. Same family as TRAP 11 and TRAP 23: **a true signal attached to a different object than
the one being merged.** `.base.sha` is not containment; `MERGEABLE` is not the merged program; and a
green run is not a green *head*.

It unblocks when #69 lands, because `update-branch` will finally trigger a run against a head that
still exists.

---

## 2026-08-01 — TWENTY-EIGHT landed. Both repos at zero open PRs.

| Merged | Packet | Result |
|---|---|---|
| FE #69 | WP-30 — the guards now scan what the compiler scans | FE `e28664f` |
| FE #80 | WP-02 round 2 frontend — ext lint was never linting `tests/` | FE `6aa01a5` |
| BE #131 | rescued — stop the WP-14 report writers dirtying the tree | BE `7aa830a` |
| FE #77 | WP-31 — one dialog contract, a measured tap-target floor | FE `1852590` |

**FE `1852590` · BE `7aa830a`. Zero open pull requests in either repository.**

### Every worktree swept — nothing is stranded

Both repos' worktrees were audited for unpushed work, since that risk had already bitten twice. Two
candidates looked live and both were already on main: WP-10's local branch is the same commit as #83,
and WP-23a's five commits are #119's pre-squash originals. The "RevisionAuthority fails in worktrees"
chip had nothing to do either — its fix is on main via #128, and the file records it.

**Caveat worth keeping for the next audit:** `git rev-list origin/main..HEAD` is a *useless* signal in
this repo. Every PR is squash-merged, so a fully-merged branch still reports "ahead" forever. The
signal that actually distinguishes stranded work is whether the head commit is contained by any remote
branch.

### FE #77 — three failures stacked on each other

1. Its "green" was a run from 16:17 bound to a **superseded head**. Nothing had ever run against
   `6147a76`. Same family as TRAP 11 and TRAP 23: **a true signal attached to a different object than
   the one being merged.** `.base.sha` is not containment; `MERGEABLE` is not the merged program; a
   green *run* is not a green *head*.
2. It was stacked on the **unmerged** #69. Once #69 squashed into main, eleven files conflicted with
   themselves — the branch's copy of #69 against main's copy of #69.
3. So a merge would have fought its own history. Replayed the six a11y commits onto main instead, and
   **dropped `e55bea0`** (the AA-contrast fix) as superseded — #69's third pass had already split
   `ghostTierTextColor` out with the same reasoning.

The `OnboardingWizard` collision this plan predicted at Wave 4 planning time did happen. It was one
import line.

### A shared-toolchain hazard, recorded because it is silent

`bun install` at the **repo root** went stale when #80 added `@vitest/eslint-plugin`. Worktrees resolve
`eslint.config.js` upward to the root, so the pre-commit lint gate broke in *every* worktree at once,
reporting only `ERR_MODULE_NOT_FOUND`. A root install fixed all of them.

**When a merged PR adds a dev dependency, every existing worktree's gate is broken until the root is
reinstalled** — and the failure names a module, not the cause.

---

## 2026-08-01 — WP-40: the capability ledger re-derived from code

`04-CAPABILITY-TRUTH-LEDGER.md` was written on 2026-07-27 against BE `63b89b5` / FE `e5da230`. Twenty-eight
PRs landed after it. Re-derived against BE `7aa830a` and FE `1852590` by reading code, not the old ledger
and not commit messages. Nine parallel verifiers, one section each, every one instructed to answer
**NOT VERIFIABLE** rather than infer. Rows that could not be evidenced were **deleted, not softened**.

### The plan's own claims about the ledger were three of the wrong entries

1. **`FE/src/lib/capability-ledger.ts` does not exist.** The plan names it as WP-40's target implementation
   and describes it in the present tense. Zero hits for `capability-ledger` / `capabilityLedger` /
   `CapabilityLedger` in the frontend repo, and no CI check of that shape under another name. **The ledger
   is still prose, so it is still rotting.** The re-derivation fixes the content; it does not fix the
   mechanism.
2. **The "build throws on a typo'd id" precedent is true by a different mechanism than claimed.** It is a
   *runtime-during-prerender* throw at `format-catalog.ts:45`, reached because `parseStatus()` runs at
   module-init scope inside a literal imported by two prerendered pages — one Server Component
   (`formats/page.tsx:10`) and one Client Component that still prerenders (`(home)/page.tsx`, `"use client"`
   at `:1`) — caught by `bun run build` (FE `ci.yml:74`). **`tsc --noEmit` does not catch it**:
   `parseStatus(catalogId: string)` takes a bare `string`, so a typo is type-legal. **No test pins the
   throw** — delete it, return a default, nothing goes red — although `bun run test` trips it incidentally,
   because `gatedCapabilityClaims.test.ts:6` imports the module. It also covers `IMPORT_FORMATS` only;
   `OUTPUT_FORMATS` is seven hardcoded literals, which is precisely where the Peppol over-claim below
   survived.
3. **Frontend `CLAUDE.md` §11.5 names the wrong guard.** `CLAUDE.md:475-477` says the tier rule is
   "guarded by `BillingFeatureGateCoverageTests`". That class is a hand-typed
   `Dictionary<BillingFeature, string>` of free text (`BillingFeatureGateCoverageTests.cs:55-68`) whose only
   assertions are `ContainKey` and a count — **delete the `HasFeatureAsync` call from `AuditController.cs:49`
   and every one of its tests stays green.** The real guard is `BillingGateEnforcementIsRealTests` +
   `BillingGateIlScanner`, which reads compiled IL (`MethodBody.GetILAsByteArray()` at
   `BillingGateIlScanner.cs:249`) and carries a negative control at `:147-155`. The IL scanner's own doc
   comment says this. **The rule is enforced; the sentence describing how is false.** Correcting §11.5 is a
   one-line frontend change — not taken here, this chip is file-disjoint to the ledger.

### Claims deleted outright

- **`parse-xml-generic`** — never a capability. `OrderParserFactory.cs:54-69` sniffs IDoc → UBL → cXML and
  throws on any other root. No copy claims it; the row invited someone to.
- **`parse-peppol-bis` as a separate row** — merged into `parse-ubl`. Both tests that name it assert
  *identical* behaviour to plain UBL.
- **`out-email` liveProven `✓`** — `FINAL-REPORT.md:66` says outbound Postmark is blocked pending account
  approval, with a cross-domain send returning 412. Inbound Postmark being live is a different capability.
- **"14,713-row catalog proven"** — the number's only occurrence in the repo is a prose comment above a
  **two-item** fixture (`CatalogFormatAndMappingTests.cs:305`). The real scale test uses 250 and 5,000.
- **"7 of 207 sites Id-only" / "1 of 87 endpoints tested"** — not re-measured, and the denominator is stale:
  162 route attributes across 37 controllers today. Carrying a number nobody re-derived is the failure this
  document exists to stop.
- **"provenance shown in FE"** (AI suggestions) — the only component rendering `.provenance` is imported
  solely by its own test. The calibration overlay is dead the same way, so users see **raw** model
  confidence.
- **"acceptance profiles are browser-only"** — refuted. `AcceptanceGate.EvaluateAsync` blocks server-side at
  `OrderTransformService.cs:484-500`, and both hosts register it.

### Rows that got stronger, with the evidence

- **`emit-custom-tree` is reusable across orders** (WP-12). `SupplierPromotedOutputTreeTransformTests.cs:170`
  designs on order A, promotes, seeds order B with no override, asserts byte equality.
- **`reusable-input-mapping` has a UI caller** (WP-13). `WorkshopStatusBar.tsx:337-345`.
- **`order-passport` carries a hash.** `PassportDto.cs:154`, `:183`; `ProveWhatWasSentTests.cs:148` plus a
  tamper twin.
- **The 27-cell routing matrix runs on CI.** `[DockerRequiredTheory]` is a *declared* skip that does not
  skip on `ubuntu-latest`; CI run `30695203087` shows the per-cell `Passed` lines. Count pinned at `:277-279`.
- **Billing is 9 of 10 enforced, IL-verified**, across API controllers *and* Worker poll jobs.

### Open unknown #6 answered, and the answer costs a marketing claim

**Does UBL output satisfy Peppol BIS 3? There is no proof, and two mandatory-field violations are visible in
the emitter.** `find -name "*.sch"` returns zero files; `PeppolBisValidator` rejects any non-`<Invoice>` root;
`UblProfileChecker.cs:55-58` asserts only that the conformance ids are **non-empty**, never that they equal
the Peppol values. Meanwhile `UblOrderTransformService.cs:93` writes a **GUID** into
`SellerSupplierParty/PartyName/Name` (its own comment calls it a placeholder) and no `cbc:EndpointID` is
emitted for either party. `format-catalog.ts:98` hardcodes this as `status: "live"` while
`standards/catalog.ts:107` says `partial`. **The two frontend files contradict each other and the hardcoded
one wins on the marketing page.**

### The one genuine unenforced tier claim

**`BillingFeature.Sso` refuses nothing.** Its only production reference is a pure `PlanHasFeature` table
lookup (`StripeBillingService.cs:191`) surfaced as `BillingStatus.SsoAvailable`. Its exemption in the IL
scanner is granted on the grounds that "the flag drives the Settings availability/upsell only" — and
**`ssoAvailable` has zero consumers in the frontend.** There is no Settings SSO tab. `plans.ts:308` and
`/security` both sell it. Founder call: wire the surface, or stop selling the bullet.

### TRAP 28 — a frozen fixture and a truth guard can require opposite actions

`changelog/page.tsx:41` advertises IMAP as "**Integration+ plans**". IMAP has gated at **Growth** since the
channels were decoupled. Two CI guards disagree about the fix: `changelog-append-only.test.ts:45-94` freezes
that line byte-for-byte, and `gatedCapabilityClaims.test.ts:213` exists to catch exactly this over-claim —
except its regex is `\b(integration|distributor|enterprise) plan\b`, which **"Integration+ plans" does not
match** (the `+` breaks it and the noun is plural). So the claim is live, the freeze forbids editing it, and
the catcher is blind to it.

**The resolution is a new dated changelog entry correcting the tier, never an edit** — plus widening the
regex to `plans?` and `\+?`. Generalised: *when an append-only guard and a truth guard cover the same line,
the truth guard must be satisfied by an append, and the append-only guard's job is to make sure you notice.*
A guard that freezes a claim is only safe if a second guard can still see the claim.

### The refutation pass found 12 defects in my own first cut, and the shape is worth keeping

Two adversarial refuters — one per repo — were pointed at the finished ledger and told that finding
nothing would be a failure. Of roughly 200 citations, **~85% held verbatim**. What broke:

- **9 flat-wrong line numbers.** Four of them were inside the "Refuted — do not re-open" table, whose whole
  purpose is to stop someone re-investigating. A wrong line there *forces* the re-derivation it forbids.
- **3 refuted claims of fact.** "Two statically prerendered Server Components" (one is `"use client"`);
  "the only component rendering `.provenance`" (there are two, both dead); "three confidence buckets no
  code implements" (three-bucket ladders exist at 90/75 and 0.85/0.60 — the real defect is that the help
  article's 85/70 matches **neither**, and `MappingEditor.tsx:500` ships a third number).
- **~14 range drifts**, and the pattern in them is diagnostic: **the start line was right in every single
  case and the end line was wrong in fourteen.** Ends were being estimated, not read. `dialog-a11y` was
  cited as `:91-380` when `:380` is a bare `),` inside an argument list.
- **2 self-contradictions inside one table cell.** `parse-cxml` said "6 real fixtures" and then named seven
  documents. A row that refutes itself is the worst possible defect in a document whose premise is that
  every claim is backed by a citation that says what the claim says.
- **1 undercount that hid a PII finding.** The real-fixture census missed five real distributor catalog
  fixtures — 14 vs the true 20 — one of which (`cxml-index-soap.xml`) carries a real customer name and a
  real local filesystem path.

**The rule this earns: a citation is two assertions, and only one of them was being checked.** "This fact
is true" and "it is at this line" fail independently. The first survived nine times where the second did
not. A verification pass that reads the file to confirm the fact, then writes the line number from
memory of where it was looking, produces exactly this distribution — high fact accuracy, drifting anchors,
ends worse than starts. **For a document whose entire value is that its anchors resolve, the anchor needs
its own read.**

Related and cheaper: **prefix every `ci.yml` citation with the repo.** Four of five instances in the first
cut were bare, and two `ci.yml` files exist with different line numbers. A reader checks the wrong file and
concludes the ledger is wrong about something it is right about.

### Scope, stated rather than implied

Two files touched: `04-CAPABILITY-TRUTH-LEDGER.md` and this entry. Three sibling chips were in flight —
WP-39 (`docs/qa/**`), WP-38 (backend delivery/SFTP), WP-36 (frontend failure surfaces). Their territory is
**cited, never edited**, in three places: the SFTP host-key gap, the residency ground-truth doc's
`SelfHostedOcr` row, and the live-channel unknowns. WP-38's branch `wp38/sftp-host-key-proof` holds two
commits not on `main` and they touch FTPS certificate messaging only — **its proof doc is not evidence for
the ledger until it merges**, and the ledger says so rather than borrowing it.

This chip branched off `docs/v1-master-plan` rather than `main`, because at dispatch time the entire plan
directory lived only on that branch. **#133 merged at 10:21Z while this ran** — as a merge commit, not a
squash, so the plan branch is an ancestor of `main` and the rebase was a fast no-conflict replay.

### A sibling landed mid-flight and refuted one of my rows — which is the system working

Between the re-derivation and the rebase, WP-38's proof half merged (BE #136 `fb4561b`, #137 `d4ad090`).
Three consequences, all folded in:

1. **REFUTED:** I wrote that FTPS certificate validation was untested wiring and that "deleting the wiring
   at `:145-148` leaves the suite green." #136 added `FtpsCertificateRejectionTests.cs` — **four ungated
   `[Fact]`s driving a real TLS handshake** against an in-process stub. Its header says it exists precisely
   because a unit test on `ShouldAcceptCertificate` cannot see the wiring. The claim was true when written
   and false four hours later.
2. **STRENGTHENED:** the SFTP host-key finding stopped being an inference. I had it resting on SSH.NET's
   documented default, labelled *vendor*. WP-38 ran two host-key sets against a real OpenSSH container and
   flipped the server between them with every other credential held identical: *"same result, no warning,
   no log line."* Proven on ProcuLink's own code path.
3. **NEW:** U-3 — no delivery channel requires TLS; `http://` is permitted everywhere, so a Directo config
   can ship the PO and its credentials as a cleartext form body.

Also a **conflict recorded rather than resolved**: WP-38's summary row calls HTTP/email/ERP "already
live-proven (2026-07-02)" while the fable5 FINAL-REPORT says outbound Postmark is blocked and logs a 412.
The 412 is specific and reproducible; the summary row sits in a packet scoped to SFTP/FTPS whose own §4
calls the other channels a read-only assessment. The ledger keeps ✗ **and says both documents disagree**.
Neither file was edited — WP-38's territory is cited, not touched.

**The lesson is about ledger cadence, not about WP-38.** A document that pins claims to `file:line` has a
half-life measured in hours while packets are landing. Two of my rows moved between `git commit` and
`git push`. That is the argument for `FE/src/lib/capability-ledger.ts` in one line: **prose re-derived by
hand is correct at a commit, and a typed constant with a CI check is correct at a merge.**

---

## 2026-08-01 — session close. 44 merged, zero open PRs, both mains green.

FE `024aad5` · BE `501ce1a`. Waves 0–4 complete; the plan itself is on `main`.

### What is verified, and by what

Do not re-audit these from scratch — the evidence exists:

- **Every packet was adversarially refuted by its own session before it reached me**, and its tests
  mutation-checked (revert the fix, the test must go red).
- **Every merge passed three gates**: branch contains current `main`, a CI run *since* that, and
  test-side changes beside the production files.
- **Both mains were verified green after every merge**, not just on the PR.
- **WP-39 is a real signed-in production pass** — `docs/qa/2026-08-01-wp-39-authenticated-production-pass.md`.
  Upload → parse (<10 s) → review → inline mapping → transform (six formats) → send confirm all
  work live.
- **The handshake fix was verified in production, not just CI**: `/upload?__clerk_handshake=junk`
  → 307 → `/sign-in`.

### The one real gap — start next week here

**About fifteen packets landed AFTER WP-39's production run**, including WP-36's nine screens, the
SFTP host-key work, the SSO/Peppol claim removal and both P1 audit-trail fixes. The composed product
has therefore not been exercised end to end by a real user since. **A second authenticated production
pass is the highest-value next action** — same method as WP-39, which records how to obtain the
session (the older method in the docs no longer works).

### Remaining packets

| Packet | State |
|---|---|
| **WP-33** auto-send | Next natural packet, and the highest-risk one in the plan. Founder ruling stands: **dry-run one full week before a single real order moves unattended.** Now sensible because WP-39 proved the manual path. Deserves a fresh session with full budget. |
| WP-35 replay that re-processes · WP-37 page the founder · WP-41 a11y + visual-regression CI | not started |
| WP-16 designer depth II | blocked on design brief **DB-2** |

**Two known defects deliberately left open:** the UBL `SellerSupplierParty/PartyName/Name` GUID
placeholder and the missing `cbc:EndpointID` (`UblOrderTransformService.cs:93`). They were kept out
of the Peppol claim removal on purpose — fixing them under a banner we were removing would have
confused both. They are now free-standing and small.

**Founder-owned, still open:** rotate the three keys (Clerk, R2, ElevenLabs — open since 2026-07-24),
and WP-03 check 2 (one test email with the org default cleared).

---

## 2026-08-06 — second authenticated production pass, and six findings

Full record: `docs/qa/2026-08-06-second-authenticated-production-pass.md`. Real Chrome, real
production Clerk session, real org (26 orders, 25 suppliers, Growth plan). Read-only.

This closes the gap the 2026-08-01 session close named: ~15 packets had landed after WP-39's run,
so the composed product was unverified live. It now is.

**Verified working live:** WP-36's failure panel (good copy, two real actions, honest caveat);
mobile 390 px with zero horizontal overflow; status accounting internally consistent; the
dashboard omits rather than fabricates a blocker count while loading.

**Findings:** F1 the operator is still shown raw HTML as a failure reason — FE #94 guarded
`rejectionReason` while production's markup came through `order.errorMessage`, rendered verbatim
by design. F2 the correct operator sentence already exists in `passport.deliveryAttempts[*]`
(WP-19 wrote it) and the panel ignores it. F3 three delivery numbers on one screen, one unlabelled
all-time rate between two "last 30 days" cards. F4 Growth is sold an "Audit log" bullet while
`PlanConstants.cs:276` gates the only audit capability at Operations. F5 three different "active
supplier" counts (1 / 25 / 26). F6 nothing warns that three queue entries are the same PO.

**Two new traps, both about guards that look complete.**

**TRAP 29 — a render test that nulls the other inputs is the blind spot.** FE #94 added a
render-level test precisely to pin the *wiring*, having learned that unit tests pin a function
without pinning its use. It still missed, because its fixture set `errorMessage: null`
(`supplierReasonRendered.test.tsx:56`, `unconfirmedFriction.test.tsx:65`) and production's markup
arrived through exactly that field. *When a value can reach the screen through more than one
field, pin every field that can carry it — otherwise the fixture, not the code, is what passes.*

**TRAP 30 — the uncovered class between two billing guards.** `gatedCapabilityClaims.test.ts`
catches a capability sold at the wrong *named tier* and needs a tier word to match;
`BillingFeatureGateCoverageTests` pins the ladder per `BillingFeature` *enum member*. A plan-card
bullet naming a capability that no `BillingFeature` grants at that tier has neither a tier word
nor an enum member, so both are blind to it. "Audit log" on Growth lived there.

**Dismissed after investigation — do not re-open.** The org's `orderLimit: 100000` /
`supplierLimit: 30` on Growth is the designed admin override (`AdminDtos.cs:68-69`,
`AdminController.cs:378-403`), not a billing failure. "Good morning, Dim" is a **founder-approved
mock (2026-07)** per `DashboardContextLine.tsx:3` — CLAUDE.md §12's anti-pattern entry is stale
and is being corrected; the greeting stays. `DXOH` twice in the supplier list is an initials
avatar, not a supplier code. The duplicate-PO orders are correct two-format test ingests, and
WP-22 dedupes documents, not POs.

**Not covered, so absence is not a pass:** no upload→deliver write path was run this pass;
`/library/mappings`, `/library/rules`, `/drafts`, `/operations/connectors`, `/operations/webhooks`
unopened; tablet 768 px unchecked.
