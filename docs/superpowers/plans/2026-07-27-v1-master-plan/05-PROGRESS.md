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

### New small packets found in passing — unowned

| Item | Evidence | Options |
|---|---|---|
| **A 404 is shipping to admins today** | `src/lib/guides.ts:262-263` registers slug `unfreeze-a-pilot-workspace` at `/admin/guides/unfreeze-a-pilot-workspace`; `git ls-tree -r --name-only origin/main \| grep -i unfreeze`(piped) returns nothing | **Write the page** (preferred) or delete the entry. The capability EXISTS — BE #59 (`0e1ac58`) shipped `POST /api/admin/organisations/{id}/account-status`, and STATUS.md already documents the recipe including the critical detail that it is TWO calls in order: extend the trial via `.../limits` FIRST while still `read_only`, THEN flip the status, or the lapsed Pilot window re-expires it immediately. The runbook prose exists; only the page is missing. Deleting the entry stops the 404 but loses the one runbook that recovers a frozen customer org. |
| `/one-pager` unreachable in-app | print collateral, published only via `sitemap.ts` | link it, or delete it — founder call |
| This is the reverse of what the guard checks | `route-reachability.test.ts` finds routes with no inbound link; this is an inbound link with no route | extend the guard to check both directions |

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
