# ProcuLink V1 Master Plan — "True To Our Claims"

**Created:** 2026-07-27
**Audited state:** BE `origin/main` `63b89b5` · FE `origin/main` `e5da230` · both repos zero open PRs
**Source audit:** the 14-track product/UX/architecture/market audit run 2026-07-27 (248 findings, 25 adversarially refuted, 12 of those killed or downgraded)
**Owner:** solo founder. Every decision below assumes there is no second engineer and no support team.

---

## 0. The one-sentence goal

Make ProcuLink a product where **every screen a customer can click does what it says**, the **supplier-output workbench actually survives to the next order**, and a stranger can go from sign-up to a delivered supplier-ready PO **without the founder in the loop**.

Nothing in this plan adds a capability we do not already half-own. This is a *finishing* plan, not an expansion plan.

---

## 1. Non-negotiable operating rules

These exist because the audit's worst findings were all "shipped surface with no wiring behind it". Breaking these rules is how that happened.

**R1 — No new surface without a consumer.** A screen, endpoint, or entity may not merge unless a test proves something downstream reads it. Enforced by `WP-04` (the orphan guard).

**R2 — RED first, always.** Every packet starts with a failing test that encodes the defect or the missing behaviour. A packet whose first test passed on the first run is rejected and re-scoped — it means the test does not test the thing.

**R3 — One packet, one worktree, one concern.** No packet touches more than one wave's concern. Cross-wave coupling is expressed as a dependency, never as a bigger packet.

**R4 — Every deletion is a redirect.** We delete four navigable surfaces in Wave 1. Each one ships with a permanent redirect and a route test, so no bookmark, help link, or search result 404s.

**R5 — A comment that justifies is a proof obligation.** If a comment says "because X", a test asserts X. (Earned rule from the 2026-07 delivery wave; it is why that layer is the strongest part of the codebase.)

**R6 — Assert the difference, not the sameness.** When two paths must behave identically, the test must fail if either one changes alone.

**R7 — Local green ≠ CI green.** Windows dev, Linux CI. After every push: `gh run list`. Wave 0 makes CI meaningful; until `WP-01` lands, treat every "tests pass" claim as unverified.

**R8 — No production data mutation without a written pre-list.** Post the org slugs / order ids before touching anything. Stripe live objects and secrets rotation stay founder-only.

**R9 — Never create a Neon database branch.** Standing rule. Test against local Postgres `:5435` or Testcontainers.

**R10 — The Bridge Layer visual direction and the 3-column Order Workshop are LOCKED.** Wave 4 changes hierarchy, density, naming and navigation. It does not invent a new aesthetic and does not replace the workshop layout.

---

## 2. The shape of the plan

Six waves. Waves 0–2 are the ones that decide whether this is a business. Waves 3–6 are the ones that decide whether it is a good one.

| Wave | Name | Why it exists | Gate to leave it |
|---|---|---|---|
| **0** | Ground truth & guardrails | We currently cannot tell a passing test from a skipped one, and two P0s hinge on an unread env var | CI runs every suite; no test can pass vacuously; both production truth-checks answered |
| **1** | Stop lying | Four navigable surfaces do work that has no effect. Every one is a terminal trust event | No screen writes to a store nothing reads; every marketing claim maps to a ledger row |
| **2** | The wedge | A designed output dies with the order, and cannot carry a delivery address. This is the whole differentiator | An output designed once is reused automatically on the next order, and can emit ship-to |
| **3** | Enforcement & recovery | Configured rules never fire; any 4xx is a permanent dead end | Every configured rule blocks on the server; no order status is a dead end |
| **4** | Concepts & UI | ~50 taught nouns for a 9-concept job; nav is backend modules | 9 nouns, 4 nav items, first-run completes in one sitting with no supplier cooperation |
| **5** | Self-running | The founder is currently the runtime | Every failure has an obvious operator action; the founder is paged, not polling |
| **6** | Prove it | Half the capability matrix has no live evidence | Every ledger row is either live-proven or labelled honestly in-product |

**Wave 0 and Wave 1 can run concurrently.** Everything else is sequential at the wave level and heavily parallel inside a wave.

---

## 3. Wave detail

### Wave 0 — Ground truth & guardrails  *(target: 3 days)*

The audit could not distinguish a green suite from a silent one. Fix the instruments before trusting any reading.

- **WP-01** CI runs vitest + lint + pageshell + vocab. *15-line change protecting 1096 existing assertions.*
- **WP-02** Every env-gated test skips **visibly** or is deleted. `Live_ImapIngress` has been dead since `de4ea0e` and CI counted it as passing.
- **WP-03** Two production truth-checks (revision-authority flag; `unrouted` reachability). Each can flip a P0 to a non-issue. **Do these first — they may delete work from Wave 3.**
- **WP-04** Orphan guard: a repo test asserting every `DbSet` has a non-CRUD reader and every route has an inbound link.
- **WP-05** Mock/real parity harness. Mock mode is what CI exercises; today it teleports past two delivery states and returns `passed:true` for an unvalidated order.

**Leaving gate:** a deliberately-broken assertion fails a PR; `dotnet test` prints zero silent skips; WP-03's two answers are written into the ledger.

### Wave 1 — Stop lying  *(target: 5 days, concurrent with Wave 0)*

Every item here is a place a customer does work that has no effect. Cheapest trust ROI in the plan.

- **WP-06** Retire `/library/templates` + the `OutputTemplate` entity (orphan; also silently discards the body on save — `config` vs `configJson`).
- **WP-07** Resolve `/library/rules` + `/library/rule-definitions`: wire into `SupplierAcceptanceService` or retire. **Decision required before build** (see §6).
- **WP-08** Retire the dead routes: `/drafts`, `/upload/preview/[orderId]` and its 1,539-line component.
- **WP-09** Webhook ingress: ship the org-secret writer, or retire the controller. **Decision required.**
- **WP-10** Marketing truth: `/security` EU-residency wording, `/customers` invented pilot profiles, the `EU · Data residency` hero stat.
- **WP-11** Billing gate honesty: fix the four error codes naming the wrong plan; enforce or remove the ten unenforced `BillingFeature` gates; disclose the cancel→read-only freeze on `/pricing`.

**Leaving gate:** WP-04's orphan guard is green with zero suppressions.

### Wave 2 — The wedge  *(target: 2.5 weeks)*

The single highest-value work in the plan. An operator designs a supplier's exact output once; it applies forever.

- **WP-12** Carry `OutputTree` through promotion. Additive JSONB on `PoMappingConfig` — **no migration**, same pattern `Output` already uses.
- **WP-13** Wire the promote control (`promoteMapping()` currently has zero callers; the help docs already tell users to click it).
- **WP-14** Widen the canonical output row from 10+11 fields to the full entity (~45+25). Unblocks **ship-to**, which no custom output can emit today.
- **WP-15** Designer depth I: node reorder, typed JSON leaves, the 8 existing manipulators, CSV dialect panel.
- **WP-16** Designer depth II: structured conditional builder, namespace presets, `OutputFieldValidator` on the tree path, fix `designerFormat()` silently rewriting a cXML/UBL/X12 tree to generic XML.

**Leaving gate:** design an output on order A, promote, upload an identical file → order B renders **byte-identically** with zero designer interaction, and the output contains a delivery address.

### Wave 3 — Enforcement & recovery  *(target: 2 weeks)*

- **WP-17** Server-side acceptance gate with an explicit operator override.
- **WP-18** Validation runs at every breakpoint (today: nothing below 1024 px).
- **WP-19** Split 4xx; give `rejected_by_supplier` an operator exit. 401/404/429 are currently permanent.
- **WP-20** Content-type + filename derivation table; SFTP overwrite off.
- **WP-21** Revision authority: decide and act (WP-03 informs this).
- **WP-22** Postmark inbound dedupe; atomic REST-ingress claim.
- **WP-23** `POST /orders/{id}/resolve` status guard.
- **WP-24** Recovery UI: `transform_failed` exit, live `/operations/health` deep links, adopt the orphaned stall escalation.

**Leaving gate:** a state-machine invariant test proves no non-terminal status has an empty edge set, and every failure state has a named UI control.

### Wave 4 — Concepts & UI  *(target: 3.5 weeks)*

The largest wave and the one most likely to be deferred forever. It is scheduled, not hoped for.

- **WP-25** Concept reduction: ~50 taught nouns → 9. Rename/merge/hide only; code identifiers unchanged.
- **WP-26** Nav restructure: **Orders · Suppliers · Activity · Settings**.
- **WP-27** Onboarding: a terminal delivery channel that needs no supplier cooperation (email / download) as the default; first run completes in one sitting.
- **WP-28** Order Workshop: compress up to seven chrome bands; surface the issue list. *Layout locked.*
- **WP-29** Inbox: make `ready` a first-class "Ready to send" state with a filter chip; label the five-dot pipeline.
- **WP-30** Design-token enforcement: the hex lint the design doc specifies but was never written; landing palette; the 2.93:1 contrast failure.
- **WP-31** A11y: focus traps on 11 of 17 dialogs, tap targets, reduced-motion on the marketing hero.
- **WP-32** Degraded-state pattern, starting with the Clerk load failure that currently spins forever.

**Leaving gate:** an unprompted stranger completes sign-up → delivered PO without help, recorded.

### Wave 5 — Self-running  *(target: 2 weeks)*

This wave exists because you are alone.

- **WP-33** Auto-send when clean. **Product decision required** (see §6) — today the code is a review workbench and the marketing is an automation product.
- **WP-34** Artifact download + SHA-256 in the passport (`getDownloadUrl` exists, zero callers).
- **WP-35** Replay that actually re-processes a historical order.
- **WP-36** Every failure surfaces one obvious operator action; measured, not asserted.
- **WP-37** Alerting that pages the founder instead of requiring polling.

### Wave 6 — Prove it  *(target: 1.5 weeks + calendar waits)*

- **WP-38** SFTP host-key verification (**zero hits** repo-wide today) + live SFTP/FTPS/ERP happy-path proof.
- **WP-39** Recorded authenticated production pass through all 12 journeys. *This is the audit's single biggest evidence gap.*
- **WP-40** Reconcile the Capability Truth Ledger against live evidence; anything unproven gets honest in-product labelling.
- **WP-41** Accessibility + visual-regression CI.

---

## 4. Journey scorecard — from today to target

Target is **≥9 on every axis**. A 10 requires a live-proof row in the ledger, which is why Wave 6 exists — a journey cannot score 10 on evidence we have not gathered.

| # | Journey | Today (avg) | Target | Packets that get it there |
|---|---|:-:|:-:|---|
| 1 | Sample order | 5.3 | 9 | WP-27, WP-33 |
| 2 | First real upload | 6.5 | 10 | WP-27, WP-26, WP-39 |
| 3 | Recurring PO | 6.0 | 10 | WP-12, WP-29, WP-33 |
| 4 | Messy PO review | 7.3 | 10 | WP-28, WP-25 |
| 5 | No supplier identified | 7.5 | 10 | WP-25 (naming only — this journey is already strong) |
| 6 | New supplier setup | 6.2 | 9 | WP-25, WP-26, WP-27 |
| 7 | **Custom supplier output** | **3.7** | **10** | WP-12→WP-16, WP-26 |
| 8 | Item-code resolution | 7.5 | 10 | case-normalisation fix in WP-14 |
| 9 | **Validation failure** | **3.7** | **10** | WP-07, WP-17, WP-18 |
| 10 | Delivery rejection/retry | 6.7 | 10 | WP-19, WP-20, WP-24 |
| 11 | Replay after config change | 4.7 | 9 | WP-21, WP-35 |
| 12 | Prove what was sent | 5.3 | 10 | WP-34, WP-39 |

The two journeys ProcuLink must win — #7 and #9 — are today its two worst. They are Wave 2 and Wave 3.

---

## 5. Capability truth — the mechanism, not the promise

A prose matrix rots. `WP-04` + `04-CAPABILITY-TRUTH-LEDGER.md` replace it with a machine-checked artefact:

- One row per capability with six columns: documented / implemented / exposed / self-service / tested / live-proven.
- The **marketing and in-app copy render from the ledger**, exactly as `/formats` already renders from `src/lib/standards/catalog.ts` (which throws at build time on a typo'd id — the one anti-drift mechanism that already works; we are generalising it).
- A row may not claim `live-proven` without an evidence link: an order id, an attempt id, a receiver capture, or a dated QA doc.
- CI fails if a marketing string claims something no ledger row supports.

This is how "true to our claims" stops being a periodic audit and becomes a build error.

---

## 6. Decisions only the founder can make — **BLOCKING**

Four packets cannot start until these are answered. Answer them before Wave 1 begins; they are all one-liners.

1. **`/library/rules`** — wire the org-wide `ValidationRule` table into `SupplierAcceptanceService` as the default profile, or retire it and keep only per-supplier acceptance rules? *(Recommend: retire. Two rule engines is the confusion, not the coverage.)* → WP-07
2. **Webhook ingress** — ship the org HMAC secret writer, or retire the channel until a customer asks? *(Recommend: retire; no customer has asked, and it is currently unreachable anyway.)* → WP-09
3. **Auto-send** — is ProcuLink an automation product or a review workbench? *(Recommend: automation with a per-supplier "auto-send when clean" switch, default OFF. It is the only answer that makes journey #3 worth paying for.)* → WP-33
4. **Revision authority** — turn the flag on in production and accept that pinned orders freeze their config, or retire the versioning subsystem? *(Recommend: turn it on. It is a genuine differentiator EDI VANs bill separately for — but only if it is actually on.)* → WP-21

Plus one non-blocking but important question the audit could not resolve: **the only two real customer POs in the repo (`real-cxml-1.2-ariba-punchout-mpn-differs.xml`, `real-cxml-1.1-mpn-equals-supplier-part.xml`) are orders *received by* the ProcuLink user, not sent by them.** Either they are convenience fixtures, or your first real customer's job is the mirror image of the documented ICP. Confirm before Wave 4 fixes the vocabulary around "outbound".

---

## 7. Explicitly NOT in this plan

Carried verbatim from the audit's "what should not be built". Adding any of these before Wave 6 closes is a plan violation.

Invoices and ASNs beyond the current honest stubs · Peppol / AS2 / AS4 / VAN membership · more ERP connectors (you cannot test the two you ship) · EDIFACT output · PunchOut L1 · IMAP hardening (three channels already have zero live proof — do not add a fourth) · general document automation · a Zapier/Make app · Postgres RLS (app-level scoping measured clean: 7 of 207 sites Id-only, all safe) · **any new top-level noun**.

---

## 8. Timeline

Assumes: one founder reviewing and merging; 4–6 concurrent agent worktrees; the four blocking decisions answered on day one.

| Milestone | Waves | Elapsed | What is true at this point |
|---|---|---|---|
| **Instruments trustworthy** | 0 | day 3 | A green CI actually means something |
| **Honest** | 0+1 | week 1.5 | No screen lies. Every marketing claim maps to a ledger row |
| **Differentiated** | +2 | **week 4** | Design a supplier's exact output once, it applies forever, and it can carry an address |
| **Safe** | +3 | week 6 | Configured rules enforce; no order can dead-end |
| **Self-service sellable** | +4 | **week 9–10** | A stranger completes the whole loop unaided |
| **Runs without you** | +5 | week 12 | Every failure has an obvious action; you get paged, not polled |
| **Proven** | +6 | **week 13–14** | Every ledger row live-proven or honestly labelled |

**Add 2–3 weeks of buffer** for the things that are calendar-bound rather than effort-bound: real vendor SFTP/FTPS endpoints, an ERP sandbox, authenticated production QA, and the founder-only gates (Stripe, secrets rotation, DPA).

**Realistic honest range: 13 weeks best case, 16 weeks expected, 19 weeks if two of the live-proof channels turn out to be broken** (they have never been run — WP-38 is the highest-variance packet in the plan).

**If you can only afford four weeks:** do Waves 0, 1, 2. That gets you honest and differentiated, which is the minimum viable position. Everything after that improves the product; those three waves decide whether there is one.

---

## 9. Files in this plan

| File | Purpose |
|---|---|
| `00-MASTER-PLAN.md` | this file — waves, gates, decisions, timeline |
| `01-WORK-PACKETS.md` | WP-01…WP-41: scope, files, acceptance criteria, tests, deps, skills, risk |
| `02-DESIGN-BRIEFS.md` | Claude Design briefs for the seven surfaces that need design, with locked constraints |
| `03-EXECUTION-PROTOCOL.md` | agent topology, skill routing, token discipline, merge train, no-new-bugs enforcement |
| `04-CAPABILITY-TRUTH-LEDGER.md` | the machine-checkable claim ledger and its schema |
