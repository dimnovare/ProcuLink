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
| "`/welcome` is an orphan" | **REFUTED** | Frontend links searched; the backend caller never searched. |
| "`/subprocessors` claims an OpenAI DPA we lack" | **NOT-A-FINDING** | Local tree read; it was 9 commits stale and already fixed. |

**The shape, every time: two true facts with one unchecked step between them.** Registry and filesystem
but not the renderer. Dev config but not prod. Frontend links but not backend callers. Before reporting
an inference drawn from two verified facts, name the step between them and go check it.

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
