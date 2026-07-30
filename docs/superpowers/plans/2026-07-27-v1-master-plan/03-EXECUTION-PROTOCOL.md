# Execution Protocol — agents, skills, tokens, and the no-new-bugs contract

This file is how the plan gets built without becoming the thing the plan is fixing.

---

## 1. The no-new-bugs contract

The audit's worst findings all share one shape: **shipped surface with no wiring behind it, and a test suite that could not tell.** Four mechanisms prevent us from repeating that.

**M1 — CI is the first packet.** `WP-01` lands before anything else. Until it does, 1096 frontend assertions gate nothing and every "tests pass" claim in this plan is unverified.

**M2 — The orphan guard (`WP-04`) is permanent.** A `DbSet` with no non-CRUD reader, or a route with no inbound link, fails the build. This is the mechanism that makes R1 real. It is RED today on five surfaces — that is the point.

**M3 — RED-first is checked, not trusted.** Every PR body must contain the failing output of the first test run, verbatim. A packet that cannot produce one is re-scoped: it means the test does not test the thing.

**M4 — The three-way gate before merge.** In order, no skipping:
1. `superpowers:verification-before-completion` — commands run, output pasted, no claim without evidence.
2. `code-review:code-review` on the diff.
3. An **adversarial verify agent** whose only instruction is to refute the PR's own claim of correctness, defaulting to "refuted" when uncertain. *(This is exactly the mechanism that killed 12 of 25 findings in the audit that produced this plan — it works, and it is cheap relative to shipping a wrong fix.)*

**M5 — Assert the difference (R6).** Where two paths must agree — mock vs real, dashboard count vs inbox count, tree emitter vs flat emitter — the test fails if either changes alone. Three of the plan's regression guards are of this form: `WP-05`, `WP-29`, `WP-12`'s byte-parity assertion.

---

## 2. Agent topology

One packet = one worktree = one agent chain. Never run parallel agents in a shared checkout — EF snapshot and `.next` collisions are a known failure mode in this repo.

**Per-packet chain (the default):**

```
scout ──> plan ──> implement ──> review ──> verify
(read)   (write)   (TDD)        (diff)    (refute)
```

- **scout** — read-only. Loads only the files named in the packet, confirms the cited line numbers still hold, reports drift. *Cheap; catches the "auditor read a stale tree" failure that produced two wrong P0s in the source audit.*
- **plan** — `superpowers:writing-plans`. Emits the RED test first, then the change.
- **implement** — `superpowers:test-driven-development`. Worktree-isolated.
- **review** — `code-review:code-review`.
- **verify** — adversarial refuter (M4.3).

**Concurrency:** 4–6 packets in flight. The real limit is founder review bandwidth, not agents. Do not queue more than you can merge in a day.

**Model routing:**
- scout / mechanical renames / deletions → cheaper tier
- plan / implement on `L`+ packets, all of Wave 2, anything touching delivery or billing → strongest tier
- verify → strongest tier, always. A cheap refuter is worse than none.

**Worktrees:** `superpowers:using-git-worktrees`. Note the known trap — a worktree's `node_modules` is empty and resolution walks up to the main repo. Use `bun run`, never `npx` (it downloaded `next@16` against a 15.5 build once).

---

## 3. Skill routing

| Situation | Skill |
|---|---|
| Any packet touching ≥3 files, or any packet with a **decision** in it | `superpowers:brainstorming` **first** |
| `M`+ packet | `superpowers:writing-plans` → `superpowers:executing-plans` |
| Every implementation | `superpowers:test-driven-development` |
| A bug surviving one fix attempt | `superpowers:systematic-debugging` |
| Before any "done" | `superpowers:verification-before-completion` |
| Before every merge | `code-review:code-review` |
| Any UI packet | `frontend-design` + `ui-ux-pro-max` |
| Any packet with a11y or contrast in scope | `web-design-guidelines` |
| After a UI wave | `design-review` |
| Wave 4 packets | the matching brief from `02-DESIGN-BRIEFS.md` **before** any code |
| Independent packets ready together | `superpowers:dispatching-parallel-agents` |
| Wave complete | `superpowers:finishing-a-development-branch` |

`/frontend-design` is a **quality lens on the locked Bridge Layer direction**, not a licence to invent an aesthetic (R10).

---

## 4. Token discipline

The audit cost ~6.7M subagent tokens across 69 agents. Most of that was re-exploration. Four rules cut it hard.

**T1 — Packets carry their own context.** Every `01-WORK-PACKETS.md` entry names its files and line numbers. An agent opens those and nothing else. If a packet needs exploration to start, the packet is under-specified — fix the packet, do not send the agent hunting.

**T2 — One context pack per wave, not per packet.** Before a wave starts, one scout agent produces a ≤300-line orientation note for that wave's subsystem. Every packet in the wave receives it verbatim. Written once, read many.

**T3 — `git grep` from the repo root, never `grep -r`.** `.claude/worktrees/` contains full copies of the repo; a raw recursive grep returns other sessions' worktrees and reads as evidence of a separate track. This wasted real time during the audit. `git grep` sees tracked files at HEAD only.

**T4 — Read `origin/main`, not the working tree.** Production deploys from `origin/main`. The local tree drifts (it was 9 commits behind during the audit, which produced two wrong P0s). Use `git -C <repo> show origin/main:<path>` when a file matters.

**T5 — Structured returns.** Subagents return JSON against a schema, never prose. Prose gets re-summarised; JSON gets consumed.

**T6 — Caveman for chat, normal for artifacts.** Chat replies compressed. Code, commits, PR bodies, and these docs written normally.

---

## 5. Merge train

Sequential merges, one at a time, per wave. Never batch — the 2026-07-24 wave hit two real collisions on shared queue docs and both were caught only because merges were sequential.

**Per merge:**
1. Rebase on current `main`.
2. Full local suite: `dotnet test ProcuLink.slnx` and `bun run test` + `bun run build`.
3. Push, then **`gh run list`** — local green ≠ CI green (Windows dev, Linux CI).
4. Merge.
5. Watch the deploy: Vercel from FE `main`, Railway from BE `main`. Vercel has dropped a main-push webhook before; confirm the deploy is Ready rather than assuming.
6. Post-merge smoke on the touched surface.

**`git merge-base --is-ancestor` lies about squash-merged PRs.** To check whether something landed, grep `main` for the content.

---

## 4b. TOKEN DISCIPLINE — MANDATORY, BOTH SESSIONS

Founder directive 2026-07-30: **preserve tokens.** Two sessions plus their subagents share one ceiling,
and the audit alone cost ~6.7M subagent tokens. These are not preferences.

**CAVEMAN MODE IS ON for all chat output.** Drop articles, filler, pleasantries, hedging. Fragments are
fine. Technical terms stay exact; quoted errors stay verbatim.
- **Chat replies: terse.** A table or a short list beats prose. Do not restate what the founder just
  read. Do not narrate what you are about to do and then do it.
- **Written normally, NOT caveman:** code, commit messages, PR bodies, and these plan documents. Those
  are artifacts other people and future agents read cold.
- Drop caveman only where compression risks a misread: security warnings, irreversible-action
  confirmations, and multi-step sequences where fragment order matters. Resume immediately after.

**T1 — packets carry their own context.** `01-WORK-PACKETS.md` names files and line numbers. An agent
opens those and nothing else. If a packet needs exploration to start, the PACKET is under-specified —
fix the packet, do not send the agent hunting.

**T2 — one context pack per wave, not per packet.** One scout produces a <=300-line orientation note for
the wave's subsystem; every packet in that wave receives it verbatim.

**T3 — `git grep` from the repo root, never `grep -r`.** `.claude/worktrees/` holds full repo copies; a
raw recursive grep returns other sessions' worktrees and reads as evidence of a separate track.

**T4 — read `origin/main`, not the working tree.** Production deploys from `origin/main`. The local tree
drifts — it was 9 commits behind during the audit and that produced two wrong P0s. Use
`git -C <repo> show origin/main:<path>` when a file matters.

**T5 — subagents return JSON against a schema, never prose.** Prose gets re-summarised; JSON gets consumed.

**T6 — never dump a large result into chat.** Design specs, audit output and journal contents go to a
FILE, then the chat carries the path and the verdict. The three DB specs are ~76-85k chars each; pasting
one into chat would cost more than producing it.

**T7 — do not re-verify what the other session already verified with a citation.** Read their citation.
Re-check only when the claim is load-bearing AND the evidence is an inference rather than a quote.

**T8 — coordinate the agent ceiling.** Messages are cheap; launches are not. If the other session reports
starts-with-no-results, throttle rather than retry into a wall.

## 5b. Merge-order constraints (earned the hard way, 2026-07-30)

Discovered by the parallel execution session while landing real PRs. These are ordering facts, not
preferences — violating one produces a broken deploy or an unverifiable claim.

1. **FRONTEND retirements merge BEFORE backend retirements.** Otherwise a live page calls an endpoint
   that has already been deleted.
2. **WP-01 merges before any other frontend PR.** Until the CI gate lands, no frontend test claim on
   any other PR has actually been checked.
3. **WP-20 merges before WP-19.** Both rewrite `DeliveryService`; doing them concurrently guarantees a
   conflict in the one file where a bad merge is most expensive.
4. **WP-11 merges before WP-22.** Both edit `IngressController.cs` — WP-11 adds the billing gate the
   controller has never had (a frozen Pilot can currently still push orders), WP-22 replaces
   check-then-create idempotency with an atomic claim. The small additive guard lands first; the
   delicate rewrite rebases on top.
5. **A new PR gets ZERO Actions runs until GitHub computes its merge ref.** `gh pr view --json mergeable`
   does NOT trigger the computation. `gh api repos/OWNER/REPO/pulls/N` DOES. Empty commits and
   close/reopen both fail. This silently costs ~20 minutes per PR if you do not know it.

**Generalised rule, from (3) and from the WP-04 sequencing finding:** backend `ci.yml` runs an
unfiltered `dotnet test ProcuLink.slnx` on `push: [main]`, so **any** packet that lands a
deliberately-RED guard turns main red and blocks every queued merge behind it. This is not a WP-04
constraint — it applies to every guard packet either session writes (WP-02, WP-04, and any future
architecture test). Land guards GREEN with a dated, shrink-only allowlist; never land a red gate.

## 5c. Two sessions, one repo

Both sessions collided on WP-01 and WP-12 on 2026-07-27/30 — four branches for two packets, roughly a
day of duplicated agent work. What prevents a repeat:

- **`05-PROGRESS.md` is the shared source of truth.** Claim a packet there BEFORE starting it, and
  update it when a PR opens or lands. It is the only artifact both sessions read.
- **Announce before starting**, and check `git branch --sort=-committerdate` plus `gh pr list` in both
  repos first. A branch you did not create means someone else is on it.
- **Do not discard a colliding branch unread.** Both collisions produced work worth keeping: one
  session found the CI mock-mode defect empirically by pushing to a real runner, the other found it by
  inspection — and on WP-12 each found defects the other missed. Reconcile, do not restart.
- **The session with working CI wins the tie.** A green PR beats a locally-green branch, because
  Windows-local green is not Linux-CI green.

## 6. Wave exit gates

A wave is not done when its packets merge. It is done when its gate passes.

| Wave | Exit gate |
|---|---|
| 0 | A deliberately-broken assertion fails a PR check. `dotnet test` prints zero silent skips. WP-03's two answers are in the ledger. |
| 1 | WP-04's orphan guard is green with **zero suppressions**. Every marketing string maps to a ledger row. |
| 2 | Design an output on order A → promote → identical file → order B renders **byte-identically**, unattended, and contains a delivery address. |
| 3 | The state-machine invariant test passes: no non-terminal status has an empty edge set. A configured blocking rule blocks on all four entry paths. |
| 4 | A stranger completes sign-up → delivered PO unaided, recorded. Vocab gate green on the extended jargon list. |
| 5 | Every failure status has a control that performs a real recovery, proven by a test that iterates `OrderStatusMachine`. |
| 6 | Zero ledger rows claim `live-proven` without an evidence link. |

---

## 7. Standing hazards (each cost real debugging time — respect them)

- **The Worker is mandatory.** Nothing parses, transforms or delivers without the single Railway Hangfire worker. "Nothing happens" usually means the worker is down.
- **AI "broken everywhere"** = the per-org monthly token cap latched. Check `GET /api/billing/ai-usage` *before* debugging code.
- **EF traps.** `GetByIdAsync` is `AsNoTracking` — mutate + `SaveChanges` is a silent no-op. `ExecuteUpdate`/`ExecuteDelete` commit immediately outside `SaveChanges`; wrap mixed persistence in one explicit transaction. `ExecuteDeleteAsync` removes rows but tells the change tracker nothing — detach stale entries or the *next* writer in scope becomes the victim.
- **Never fire-and-forget on a scoped `DbContext`.**
- **InMemory masks Postgres FK and insert-order issues.** Anything concurrency-shaped needs a real-Postgres test.
- **Make new Postgres fixtures class-scoped.** xUnit builds one class instance per test; the repo's per-test `IAsyncLifetime` convention starts and migrates one container *per test*.
- **429 has three meanings** — pilot expired / order limit / rate limit. Read the body.
- **Delivery routes via the pinned revision snapshot.** Editing the legacy delivery config is inert for pinned orders.
- **`bun` only.** Never `npm install`.
- **Never create a Neon database branch.** The project has exactly one branch, `production`.

---

## 8. Founder-only gates (agents must stop and ask)

Stripe live-mode objects · secrets rotation · production data deletion beyond a posted pre-list · DPA counter-signature · anything sending a real PO to a real third-party supplier · enabling `WP-33`'s auto-send for the first time on a live org.

---

## 9. Day one

1. Answer the four blocking decisions in `00-MASTER-PLAN.md §6`. All four are one-liners; three of them are "retire it".
2. Run **WP-03** (two production checks — `railway variables | grep -i RevisionAuthority`, and one test email). Either answer may delete work from Wave 3.
3. Merge **WP-01**. Fifteen lines. Everything after it is measurable.
4. Start **WP-04** and **WP-12** in parallel worktrees. WP-12 is the longest pole and the highest value in the plan.
