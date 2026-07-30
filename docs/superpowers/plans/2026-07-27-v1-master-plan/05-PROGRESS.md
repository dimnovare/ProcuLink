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

## Wave 0 — Ground truth & guardrails

| WP | Title | Status | Branch | Notes |
|---|---|---|---|---|
| 01 | CI runs the tests we already wrote | 🟠 | `ci/wp01-reconciled` (in flight) | **Two sessions built this independently.** `wp01/ci-gate@c00dc39` (chip) is CI-validated and carries the `NEXT_PUBLIC_USE_MOCK:'false'` fix; `ci/run-the-tests-we-already-wrote@4728000` (mine) has the guard test but the mock-mode defect and 3 vacuous assertions. FIX-01 merges best-of-both. |
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
