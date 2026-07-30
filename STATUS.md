# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

> **Pruned 2026-07-02.** The founder purged ~143 stale planning docs (commit `9456a08`), and
> this file was cut from ~1,290 lines of session-by-session narrative to the current state.
> Implementation history (Phases 0–6, Groups A–L, Waves 1–4, UI passes 1–15, the June launch
> waves) lives in `git log` — do not re-execute old checklists. The active plan + verified
> capability ground truth is
> [`docs/prompts/2026-07-02-fable5-production-push-master-prompt.md`](docs/prompts/2026-07-02-fable5-production-push-master-prompt.md).

---

## Snapshot (2026-07-30) — post-merge CI on `main` was being cancelled; fixed in BOTH repos

- **BE [PR #86](https://github.com/dimnovare/ProcuLink/pull/86) MERGED `a38cb9f`; FE
  [PR #58](https://github.com/dimnovare/project-proculink/pull/58) MERGED `c319c805`.** One-line
  concurrency fix, identical in both `ci.yml` files, plus a comment correction (below).
- **The bug.** Both repos had `concurrency: {group: ci-${{ github.ref }}, cancel-in-progress: true}`.
  Every merge to `main` shares the ref `refs/heads/main`, so **each merge cancelled the PREVIOUS
  commit's post-merge run.** Six voided main runs measured on 2026-07-30 alone:

  | repo | commit | main-push run |
  |---|---|---|
  | FE | `9cea6e5` (FE #54) | unit+lint+conformance ✅, build ✅, Playwright **cancelled** |
  | FE | FE #53 | **cancelled at 29s** |
  | FE | FE #43 | **cancelled at 14s** |
  | BE | `d5a20fb` (BE #83) | **cancelled** ~25s in, when BE #76 landed |
  | BE | BE #79 | **cancelled**, when BE #85 landed |
  | BE | `5d3dc31` (BE #85) | **cancelled**, when `051b2eb` landed |

  A `cancelled` conclusion is **not a pass and not a failure — it is no information.** Never report
  one as green.
- **Why it mattered.** FE #49 landed in the seconds *between* FE #54's pre-merge base-SHA check and
  its merge, so `main` became a combination no run had covered — and the post-merge run that exists
  to catch exactly that had been cancelled. FE #54 was only confirmed green because it was
  re-verified by hand. Re-reading the base SHA before merging is necessary but **not sufficient**
  while several sessions merge concurrently; the post-merge run is what closes the window. BE #85's
  base then went stale three times in ~30 minutes, which is the same story.
- **`cancel-in-progress: false` alone would NOT have fixed it** — and that was this session's own
  first recommendation, which was wrong. Per the workflow-syntax docs: *"any existing `pending` job
  or workflow in the same concurrency group will be canceled and the new queued job or workflow
  will take its place."* With a shared group a merge train still loses every run except the first
  in-progress one and the last queued one. **The group itself has to differ per commit:**

  ```yaml
  group: ci-${{ github.event_name == 'pull_request' && github.ref || github.sha }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}
  ```

  `pull_request` → group by ref, so a new push to a PR still cancels its own older run (that
  cancelling is the point). `push` on main → group by SHA, unique per commit, so nothing can cancel
  another commit's verification and runs go in parallel rather than queueing.
  `cancel-in-progress` is set from an expression rather than deleted, so the PR path is unchanged
  by construction.
- **Verified:** both files parsed with `yaml.parse` before pushing (group, `cancel-in-progress`,
  `on.push.branches` and every job id read back unchanged); both workflows then parsed on GitHub —
  PR runs appeared and went green (BE 1 check, FE 4 checks) — and **both main-push runs dispatched
  after merge**, which is what proves the expression evaluates on the `push` path too. Frontend
  gates: `bunx tsc --noEmit` exit **0** (clean since FE #56), `bun run test` 122 files / 1276 tests
  green, lint + pageshell + vocab clean.
- **Limit, stated plainly:** a PR run can only exercise the `pull_request` branch of the
  expression. The anti-cancellation behaviour itself is only observable on the next merge train.
  **If a main-push run ever shows `cancelled` again, this did not work.**
- **Also fixed: an overclaim in `src/test/route-reachability.test.ts`.** Its MUTATION COVERAGE
  header credited *"an out-of-tree harness that reverts each one in turn"*. **That harness is in
  neither repo** — the only occurrences of the phrase were the comment and a STATUS.md entry
  quoting it. A claim of coverage nobody can re-run is not coverage. Replaced with the reproducible
  procedure and one dated result: reverting comment stripping in `src/test/sourceScan.ts` reddens
  **both** guards (3 failures in the reachability guard, 2 in the crawl), which is also what proves
  the shared module is genuinely shared. The mutation target moved into `sourceScan.ts` — mutate
  that module, not the guard.
- **Closes the follow-up list from the FE #54 entry below.** FE #57 merged (parse-gate stall
  escalation), FE #55 closed (superseded duplicate parser), FE #56 merged (CI now typechecks, and
  it fixed the two long-standing `tsc` errors). Nothing outstanding from that wave.

---

## Snapshot (2026-07-30) — Wave 1 WP-09 column drop is BLOCKED, not forgotten (no code shipped)

- **The contract half of `organisations.webhook_secret_encrypted` was NOT written.** Its
  precondition — BE [PR #75](https://github.com/dimnovare/ProcuLink/pull/75)
  (`wave1/backend-retirements`, the EXPAND half) merged **and** deployed to both Railway services
  — is not met. Verified rather than assumed, and re-verified after `origin/main` moved under the
  session (`cd7feba` → `34ce1e1`, #81):
  - **#75 is OPEN and still a DRAFT.** Its CI is green (run `30545346830`), so the block is the
    draft flag and the merge decision, not a failing build.
  - **The expand migration `20260730094527_Wave1RetireDeadSubsystems` is absent from `main`.**
    It exists only on the PR branch. (Do not be fooled by `20260531090840_Wave1SecurityIndexes`,
    which is unrelated and from May.)
  - **Both services run `cd7feba`** — API `ProcuLink` and Worker `aware-amazement`, both
    `SUCCESS`, deployed within 1s of each other. Neither carries #75.
- **Dropping the column today would break production immediately — not merely during a deploy
  window.** Because the expand half never landed, the live build still *maps* the property:
  `Organisation.WebhookSecretEncrypted` is present in `ProcuLink.Core/Entities/Organisation.cs`
  **and** in the current `ProcuLinkDbContextModelSnapshot.cs` on `main`. EF therefore emits
  `o.webhook_secret_encrypted` on every non-projected `Organisation` query, so the drop yields
  Npgsql `42703` on `EmailPollOrgJob` → `HasFeatureAsync` → `StripeBillingService.LoadOrgAsync`
  on **every IMAP poll cycle**. The Worker never migrates and is mandatory — nothing parses or
  delivers without it.
- **`Wave1ColumnDropStaysDeferredTests.cs` was left alone.** It lives only on the #75 branch, so
  there was nothing on `main` to delete. Deleting it is the deliberate act of contracting and
  belongs to the same PR as the drop migration.
- **How to verify the precondition next time** (one read-only call; the Railway MCP server is
  unauthorized in non-interactive sessions, but the `railway` CLI is installed and logged in):

  ```bash
  railway status --json | python -c "import json,sys; d=json.load(sys.stdin); [print(n['node']['serviceName'], ((n['node'].get('latestDeployment') or {}).get('meta') or {}).get('commitHash','?')[:12]) for e in d['environments']['edges'] if e['node']['name']=='production' for n in e['node']['serviceInstances']['edges']]"
  ```

  Both services must print the **same** hash, and that commit must contain the unmap. The
  load-bearing proof is `git grep WebhookSecretEncrypted <sha> -- ProcuLink.Core/Entities/Organisation.cs
  ProcuLink.Infrastructure/Migrations/ProcuLinkDbContextModelSnapshot.cs` returning nothing;
  historical `*.Designer.cs` snapshots always still match and read as false positives.
- **Housekeeping done:** the two scratch databases left by the WP-09 verification work,
  `plk_d1_probe` (54 tables) and `plk_d4_probe` (56 tables), were dropped from the shared
  `proculink-postgres` dev container after confirming zero active connections. No container was
  removed or pruned — the daemon is shared, and `proculink_dev` plus another session's
  `proculink_wp12_*` were left untouched.
- **Still to do:** un-draft and merge #75 → let both services deploy it → re-run the check above →
  only then the hand-written drop migration (`dotnet ef migrations add` generates nothing here,
  since the model already omits the property, so the model snapshot must stay unchanged) plus the
  deletion of `Wave1ColumnDropStaysDeferredTests.cs`. **Merging #75 also executes
  `DROP TABLE output_templates` and `DROP TABLE validation_rules` against production at the next
  API startup — irreversible, so it is a founder call, not an agent one.**

---

## Snapshot (2026-07-30) — the two frontend link gates are one parser (frontend PR #54, MERGED)

- **Frontend `wp04b/converge-link-extractors` → [PR #54](https://github.com/dimnovare/project-proculink/pull/54),
  MERGED as `9cea6e5`.** Two commits: land an orphaned commit, then converge.
- **An orphaned commit was found and landed.** `src/test/linkExtract.ts` was never on frontend
  `main`. It sat in `71a7701` on `wave1/frontend-retirements`, pushed **after** PR #47 merged
  (squash `ded9e04`), so it had no PR and CI never ran on it — while every branch-level signal read
  "landed". **Durable trap: a push to a branch whose PR has already merged is silently orphaned —
  no PR, no CI, no path to main.** Check with `git ls-tree --name-only origin/main <path>`; use
  `git diff --stat origin/main..<branch>` for the unlanded set, because the commit *list*
  over-reports after a squash-merge (branch-side originals look absent by SHA while their content
  is on main).
- **Two near-identical source-link extractors are now one.** New shared module
  `src/test/sourceScan.ts` owns comment stripping (js + mdx modes), string-literal masking,
  `readLiteral`, `literalsInRegion`, the anchor regexes and an `extractRaw` core. Each guard keeps
  its own pattern selection and normalisation policy, because those encode genuinely different
  questions: `route-reachability.test.ts` is INBOUND ("does anything navigate TO this page?") and
  uses a `«dyn»` sentinel with structural matching; `link-crawl.test.ts` is OUTBOUND ("does every
  link we ship land somewhere?") and must SKIP computed paths — requiring a dynamic route segment
  would flag `/help/${slug}` as a 404 when every article is its own static page.
- **One asymmetry preserved, not resolved.** The nav-call anchor is exported in two forms:
  reachability counts `new URL(…)`, the crawl does not. Sharing one set would change one guard's
  behaviour, and there is no evidence which answer is right — so `sourceScan.test.ts` pins the
  difference instead. It is invisible to both existing suites, so either collapsing edit would read
  as a harmless tidy-up.
- **Proof, not assertion.** Planted the two probe 404s (`/library/templates-that-never-existed`
  under `components/bridge/`, `/totally-dead-route-xyz` under `app/(app)/library/suppliers/`): the
  crawl went RED on exactly those two, **before and after** the convergence, identically. Then a
  mutation check — neutering `stripComments` in `sourceScan.ts` alone turned **both** guards red
  (3 + 2 failures), which is what proves the sharing is real rather than a copy left behind. All
  reverted; tree confirmed clean.
- **`link-crawl.test.ts` has a ZERO diff in the convergence commit** — `linkExtract.ts` re-exports
  `stripComments`/`syntaxFor`, so its 9 extraction-decision tests, its `>250` file floor and its
  widened-tree assertions all run against an unchanged import surface. Stronger evidence than
  editing it and asserting it still passes.
- **Main verified green AFTER the merge, not just on the PR.** Frontend PR #49 landed as `50639e3`
  in the gap between the pre-merge SHA check and the merge, so frontend `main` became a combination
  no CI run had covered. Re-verified locally on `9cea6e5`: `bun run test` 116 files / 1243 tests green without
  `NEXT_PUBLIC_USE_MOCK`; `lint`, `check:pageshell --strict`, `lint:vocab` all exit 0; `bunx tsc
  --noEmit` still only the two pre-existing errors (`src/lib/seo.test.ts:36`,
  `src/lib/planGate.test.ts:61`), both untouched. #49's new `.gitattributes` pins only `*.sh` and
  `.githooks/**` to LF, so it does not renormalise `.ts`.
- **Deliberately left out of scope:** the parse-gate stall escalation riding in the same orphaned
  commit (`parseStall.ts`, `ParsingGate.tsx`, `OrderWorkshop.tsx`, `useOrderReview.ts`) is product
  code touching the locked Order Workshop layout. Now carried by
  [frontend PR #57](https://github.com/dimnovare/project-proculink/pull/57), correctly based on
  `9cea6e5` with no `src/test` files.

**NEEDS DOING:**

1. **Close [frontend PR #55](https://github.com/dimnovare/project-proculink/pull/55)**
   (`fix/link-crawl-and-parse-stall`, draft, base `fc9f0e6`). Superseded first attempt carrying all
   eight files of `71a7701`, including a standalone 265-line `src/test/linkExtract.ts` — **the
   duplicate parser #54 exists to delete. Merging it reintroduces the drift.** Owning session
   notified; not closed here because it is another session's in-flight branch.
2. **Repoint the reachability guard's out-of-tree mutation harness.** Three of its targets
   (comment stripping, both link-tuple loosenings) moved into `sourceScan.ts`. Reverting one there
   now reddens *both* guards, which is a stronger signal — but a harness that patches this file by
   line needs updating. Noted in that file's MUTATION COVERAGE header. **Unverified: the harness is
   not in either repo.**
3. **`cancel-in-progress` silently voids post-merge verification during a merge train — in BOTH
   repos.** `.github/workflows/ci.yml` is byte-equivalent on this point in `ProcuLink` and
   `project-proculink`: it triggers on `pull_request` **and** `push: branches: [main]`, but sets
   `concurrency: {group: ci-${{ github.ref }}, cancel-in-progress: true}`. Every merge to `main`
   shares the ref `main`, so each merge CANCELS the previous commit's main run. Observed
   2026-07-30, not inferred:

   | repo | commit | main-push run |
   |---|---|---|
   | frontend | `9cea6e5` (FE #54) | unit+lint+conformance ✅, build ✅, Playwright **cancelled** |
   | frontend | FE #53 | whole run **cancelled at 29s** |
   | frontend | FE #43 | whole run **cancelled at 14s** |
   | backend | `d5a20fb` (BE #83) | whole run **cancelled** ~25s in, when BE #76 landed |

   Five frontend PRs merged inside ~4 minutes, so only the last got a completed post-merge check;
   the backend is hit less often only because its merges are more spaced out, not because it is
   configured differently. Everything before the last commit in a train is verified solely as a PR
   against a base that is already stale — exactly the "`clean` ≠ tested" failure mode.
   **Fix: drop `cancel-in-progress` for the `push: main` trigger, keep it for `pull_request`** —
   cancelling redundant PR runs is the point; cancelling `main`'s only post-merge proof is not.
   Until then, verify `main` locally after any merge that landed into a train, as FE #54 did.
4. `wave1/frontend-retirements` still holds `71a7701`/`e0b108e` and will conflict on
   `src/test/linkExtract.ts` + `src/test/link-crawl.test.ts` — resolution is take `main`'s for
   both. Once #57 lands the branch has nothing left and can be deleted.

---

## Snapshot (2026-07-27) — manufacturer part number is a real matching key (BE PR, OPEN)

- **`feat/manufacturer-part-matching`, PR open, not merged.** Two real customer POs (sanitised,
  now fixtures) drove it: a punchout Ariba/KSB order whose `SupplierPartID` is the buying
  network's internal id and whose only usable key is `<ManufacturerPartID>REDACTED-ORDER-DATA`
  (REDACTED-PARTY). The resolver used to **echo that part number back as the supplier item code at
  0.95** — right by luck in the Maersk order (where the two identifiers are the same string),
  wrong in the punchout one, and it bypassed the catalog allow-list. `SupplierProduct` now has
  `manufacturer_part_number` (+ a normalised key column and index) and `manufacturer_name`; an
  unresolved line falls back to an exact manufacturer-part lookup and suggests the supplier's
  OWN code. Suggest-only; an ambiguous part number (two supplier codes) suggests nothing.
- **Import alias `manufacturer part id` was retargeted** from `external_id` (the idempotent
  re-sync key) to the new field; Jarltech's `ORIGINAL_ART_NO` / `MANUFACTURER` now land properly.
  `CanonicalFields` had **two hand-copied duplicates** (`SuppliersController`,
  `CatalogSourceSettingsService`) that would have 400'd / silently dropped the new targets —
  both now derive from the parser's list.
- **Still cannot match:** UBL `ManufacturersItemIdentification`, X12 `PO1` `MG`, EDIFACT `PIA`
  are unread, so only cXML and the LLM PDF path supply a manufacturer part number today.

## Snapshot (2026-07-27) — supplier auto-detect, frontend half (frontend PR #37, OPEN)

- **Frontend `feat/supplier-auto-detect-fe` → [PR #37](https://github.com/dimnovare/project-proculink/pull/37), not merged.** Consumes BE #70 (`7ef2ed5`).
  `AssignSupplierBanner` renders up to 3 ranked candidates from `OrderDto.supplierSuggestions`
  (score chip, plain-language reason, collapsed per-signal breakdown, one-click Assign that
  posts `suggestionId` so the acceptance is attributable). Suggest-only: no preselection, no
  auto-assign, manual picker stays. Supplier profile Overview gains an Identifiers card (VAT /
  registration / EDI-GLN / primary domain) saved through `PUT /api/suppliers/{id}` — patch
  semantics honoured (always strings, never null) and re-rendered from the server's normalised
  response. Not AI-branded: heuristic scores reuse the ConfidenceChip ramp, never the `ai` token.
  Mock `ord-004` carries 3 DTO-shaped candidates. tsc + 1096/1096 vitest + build + lint green;
  browser QA at 1280/768/390 with no overflow.
- **Left out on purpose:** suggestions in the inbox row — `OrdersController` loads them in
  `Get(id)` only, so per-row rendering costs one order fetch per unrouted row.

## Snapshot (2026-07-26) — setup docs: step-by-step guide framework (frontend PR #36, OPEN)

- **Frontend `feat/guide-framework-and-exemplars` → [PR #36](https://github.com/dimnovare/project-proculink/pull/36), not merged.** Phase 1 = framework +
  two exemplar guides so the format can be judged before the other ~30 articles migrate:
  `/help/guides/receive-orders-by-email` (client) and `/admin/guides/onboard-a-new-client`
  (admin, server-side allowlist gate — 404 not 403, prose never reaches a non-admin bundle).
  Guides stay plain `.mdx`; `bun run guides:capture` writes real screenshots to
  `public/guides/` and regenerates its manifest from disk. Playwright launches fine on the
  founder machine, so the shipped shots are real, not placeholders.
- **Two backend follow-ups the runbook had to document around.** (a) The catalog-sync 403 code
  is `catalog_sync_requires_integration` but the gate is `BillingFeature.SftpIngestion` =
  **Growth** (`PlanConstants.cs:286`) — the code name is misleading. (b) `CLAUDE.md` §11.5 has
  drifted from `PlanConstants.cs`: Integration is 1,500 orders (not 1,000) and **Distributor**
  is missing from the documented ladder.
## Snapshot (2026-07-27) — P0: supplier auto-detect was registered but never wired (PR open)

- **BE #70 produced zero suggestions on production, on every path, since it shipped.** `OrderService`
  hand-constructs `OrderIngestionService` with a positional argument list and never forwarded
  `ISupplierSuggestionService`, so DI resolved the scorer into `OrderService` and it was dropped —
  `SafeSuggestSuppliersAsync` then early-returned empty, silently. Both the file-backed parse
  (`ParseStoredFileAsync`, every emailed/ingested order) and the prose-only sync path
  (`CreateStubFromParsedOrderAsync`) were dead. Live proof: `order_supplier_suggestions` had 0 rows
  for all time. Fixed by forwarding it; the whole call now uses named arguments.
- **Audited the same class of bug across every optional ingestion dependency.**
  `structuredExtractor`, `aiDecisions`, `catalogRetrieval`, `effectiveConfig`, `productCodeSearch`,
  `aiUsage`, `cxmlResolver` all reach their sub-service — `supplierSuggestions` was the only dead
  one. Separately, `ICatalogRetrievalService` is not registered in the Worker (only the API); the
  ingestion service self-constructs it there, so behaviour matches.
- **Silence is no longer possible:** a missing scorer now logs Warning once per process naming it as
  a composition-root wiring fault, not an absence of candidates.
- Tests: 2 composition-root tests resolving `IOrderService` from a real container (one behavioural,
  one that walks the ingestion constructor and fails on ANY unforwarded dependency) + 1 real-Postgres
  test reproducing the live scenario. All three RED first, verified by re-unwiring after the fix.

---

## Snapshot (2026-07-26) — supplier auto-detect backend (BE-5 P1+P2+P3)

- **An order that arrives without a supplier now comes with ranked candidates and a reason.**
  Spec + founder rulings: `docs/superpowers/specs/2026-07-24-supplier-auto-detect-from-document-design.md`
  (D1 identity columns YES · D2 sender DOMAIN only, 12-month retention · D3 suggest-only, never
  auto-assign · D4 shared layouts suggest all bound suppliers · D5 upload unchanged). New
  `order_supplier_suggestions` table, `Supplier` identity columns (VAT / registration number /
  EDI-GLN / primary domain, live in the supplier API contract — the profile form is a separate FE
  chip), and one additive migration `AddSupplierAutoDetect`.
- **Six deterministic signals, no LLM, no spend:** identity (0.45), sender domain (0.35), layout
  fingerprint (0.30), catalog overlap (≤0.25), name (0.20), sender-domain history (≤0.20), summed
  and capped at the existing 0.99 ceiling. A SHARED layout splits its weight evenly across every
  bound supplier, so it offers all of them and can never break a tie between them. Candidates below
  0.10 are dropped rather than shown as noise. Weights are a documented prior, not a measurement —
  the decision rows are what will replace them.
- **Nothing auto-assigns at any score.** The routing write stays exclusively in `assign-supplier`,
  which now takes an optional `suggestionId` and records `accepted` / `rejected` / `manual`. A
  supplier the scorer never named gets a `manual` row *including when it suggested nothing at all* —
  how often we stay silent is as much a measurement as how often we are wrong.
- **Two gaps the spec did not anticipate, both closed:** (1) a prose-only unrouted order (BE-1's
  case) is persisted whole and never enqueues `ParseOrderJob`, so the `ParseStoredFileAsync` hook
  could never see it — it would have been the one class of unrouted order silently getting no
  suggestions while the UI offered them everywhere else; it now has its own hook. (2) The sender
  domain must be captured on ROUTED orders too, or the domain→supplier history has nothing to learn
  from — capture only the unrouted ones and the signal never accumulates.
- **Retention is wired but DORMANT.** The 12-month sender-domain scrub is a column update (never an
  order delete) inside `DataRetentionService`, whose `Enabled` defaults to **false** — so on a
  deploy that has not set `DataRetention:Enabled=true`, the clock does not actually run. Pinned by a
  test rather than left as a comment.
- Tests: 41 pure-scoring + 25 service (InMemory) + 8 parse-hook + 3 DTO read-path + 5 retention +
  6 router sender-domain, plus **11 real-Postgres** (migration round-trip, the partial unique
  `WHERE decision IS NULL` index, re-score-in-place, the cross-supplier catalog probe, domain
  history, and all of `assign-supplier`'s decision recording — which cannot run on InMemory at all,
  because the atomic `unrouted → parsing` claim is an untranslatable `ExecuteUpdateAsync`).
  The new Postgres fixture is **class-scoped** per the 2026-07-25 lesson, not one container per test.
  **BE PR #70 — open, not merged. MERGEABLE/CLEAN, CI green: 4,049 passed / 0 failed / 2 skipped**
  (Transform 1,221 + Infrastructure 1,138 + Api 1,690 with **zero** skips, so every Postgres test
  ran on the Linux runner). Locally the full Api.Tests Postgres set could NOT be completed: this
  host's Docker VM is 1.86 GB and the pre-existing convention starts one container per test, which
  wedged the engine twice and left 36 orphan containers. Every local failure was
  `Docker.DotNet.DockerApiException` or `Npgsql: Timeout during reading attempt` — **zero assertion
  failures** — and reaping by `label=org.testcontainers=true` (the four named dev DBs carry no such
  label; verified before deleting) returned this feature's 23 tests to green. CI is the authority,
  and CI ran all 1,690.
## Snapshot (2026-07-26) — P1 F2 fixed: the fingerprint learns the operator's correction again

- **The file-backed re-parse no longer poisons its own scope.** OPS-3's F2: `ExecuteDeleteAsync`
  in `ParseStoredFileAsync` left the `Include`d lines tracked against deleted rows, and
  `entity.Lines = lineEntities` (after the commit) cascaded them to `Deleted` — so every writer
  that ran LATER in the same Hangfire scope threw `DbUpdateConcurrencyException` and was swallowed:
  exception reconcile at Error, then the schema fingerprint at Warning, which is how the operator's
  assign-supplier correction vanished on the normal path. PR #60's detach is now mirrored onto the
  file-backed branch. The swallow in `ParseOrderJob` **stays** (a failed LEARN must not fail a
  committed PARSE) and is pinned by tests for both halves — job survives, failure still reaches the
  log at Warning with the exception attached. **Prod is NOT cleaned:** the OPS-3 run left one
  poisoned `SchemaFingerprints` row — the layout bound to the supplier F1's since-deleted fallback
  guessed — and it survives deleting the ROUTETEST orders (no FK), which #68 does not undo either.
  Cleanup SQL is in PR #69's description and must run BEFORE those orders are deleted. **F3 remains
  open:** the binding is now correct, but nothing reads it at ingest.

## Snapshot (2026-07-26) — F1 FIXED: inbound email never guesses a supplier again

- **The oldest-active-supplier fallback in `InboundEmailRouter.ResolveSupplierIdAsync` is
  deleted.** Contract now: the org's configured Email-intake default (`email_config`
  `defaultSupplierId`) routes the mail; with none configured — or one that no longer resolves —
  the message imports **`unrouted`** via BE #52's park (`CreateUnroutedStubAsync` + ParseOrderJob),
  answers Postmark **200**, and is resolvable through assign-supplier + FE #32's banner. The
  resolver is now byte-for-byte the same contract as the three pull channels
  (`SftpIngressService` / `S3IngressService` / `EmailPollOrgJob`), each verified fallback-free.
  **This makes `unrouted` reachable on production for the first time** — the OPS-3 proof showed
  it was not, while an org held ≥1 supplier.
- **The one-time backfill was evaluated and deliberately NOT shipped.** Measured read-only on
  the production database (9 orgs): **0** orgs have a default that resolves; 3 already park
  (zero suppliers); 1 parks correctly (many suppliers, genuinely ambiguous — the founder org);
  **5** would flip from silently-routed to parked. All 5 are dormant pre-launch orgs created the
  same day (2026-06-03), all `pilot`/`trialing`, and **every one has 0 lifetime orders** — so no
  production mail flow changes. One of the 5 would have had its **sample** supplier pinned as a
  permanent email default, which is the guess-written-down failure mode rather than a fix for it.
  A data migration only ever touches state at deploy time (orgs created later get park semantics
  from birth), so it would have been permanently dead code. Parking costs a dormant org one
  click, and it is the click that asks the right question.
- Tests: 4 new real-Postgres cases + 1 Docker-free unit test, RED-first (control: with the
  fallback restored, the unit test reports `UnroutedCalledWith … found 0`). The old
  `ActiveSupplier_StillRoutes_UnchangedBehaviour` was mislabelled — it seeded one supplier with
  **no** default and asserted routing, i.e. it pinned the fallback; replaced by
  `ConfiguredDefaultSupplier_Routes`. `SeedSupplierAsync` in the unit suite now names the default
  explicitly, because owning a supplier no longer routes anything.
- **#65's routing matrix updated in the same PR** (it merged as `adfa00b` mid-task, so this
  rebase picked it up). Cell **`3b`** asserted *"inbound email, no default, 2 suppliers (oldest
  wins)" → `Routed`* — it pinned the fallback as intended behaviour — and is now
  **`ParkedUnrouted`**, with the seeded expectation, the header table, the reachability caveat on
  cells 3c/3d/6b, and the mutation-testing row rewritten to match. The other 26 cells are
  untouched.
- `OrderStatusConstants.Unrouted`'s reachability contract (read by the frontend repo) now records
  that the push channel was a listed producer that could not actually reach the status, and why.

## Snapshot (2026-07-26) — truth pass: seven stale claims corrected, two of them P0

Chasing five founder replies re-measured the standing gap list. **Seven claims in these docs
were wrong**; five were "still broken" items that had in fact been fixed, one was a guess
never checked, and one was my own error. Corrected in place, each with its evidence:

| Claim | Reality (measured 2026-07-26) |
|---|---|
| **P0** CF Email Routing broken, support@ mail lost | **Resolved** — 12/12 rules Active; 30-day log = 15 received / **15 forwarded** / 0 failures |
| **P0** founder org frozen `read_only`, ingest dead | **Resolved** — `personal-workspace-d3be` is `plan=growth, accountStatus=active` |
| #57 Worker "STILL NEEDS A HAND-REDEPLOY" | **Already deployed** — non-Postmark IP gets **503** (was 403); health + site both 200, so not a coincidental outage |
| OpenAI "DPA presumably unsigned" | **Signed** — Ironclad *"Complete — DPA (Diip Solutions OÜ and OpenAI)"* to `legal@` |
| OPS-2 blocked on a plan bump + Logicom creds | **Both done** — org on Growth; 14,713 rows synced |
| CLEANUP-1: delete two BE branches | **Already gone** from the remote |
| Merge queue: FE #29, BE #45 | **Both merged** |

- **My own error, corrected:** OPS-2's docs claimed the frozen `personal-workspace-d3be` was
  *a different org, unreachable from the browser session*. **There is only one org.** The
  **Clerk** slug (`dim-s-organization-…`) and the **ProcuLink DB** slug
  (`personal-workspace-d3be`, `7a3b01e1-…`) name the same tenant — admin
  `GET /api/admin/organisations` shows it holding exactly the 23 suppliers that work created.
  **Never infer org identity from a Clerk slug; match on DB slug or org id.** The conclusion
  built on it survives: the org read `trialing` when measured, so the **plan tier** really was
  the blocker, which is why raising it to Growth unblocked the sync.
- ⚠️ **New standing hazard from that same fact:** the live Growth subscription sits on the
  **primary production org** — the one receiving real mail at
  `redacted@example.invalid`. **Cancelling it re-freezes real order
  ingest**, not a throwaway workspace. Plan the exit before ending it.
- **Persona/OpenAI verification is NOT an email problem.** No Persona message reached
  Cloudflare at all in 30 days, while every message CF did receive was forwarded. Check which
  address OpenAI holds on the account rather than re-checking routing.
- **Genuinely still open:** the five vendor cred pastes (OPS-2's last piece), OpenAI EU
  project + ZDR + org verification, config gaps (`NEXT_PUBLIC_BOOK_DEMO_URL`, status page,
  subprocessors, cookie banner), OPS-3's CSP/Sentry sweep, cred rotation, and the
  order-review-screen typeahead (untested — needs a Jarltech PO on prod).
- **Method note worth keeping:** every one of these was a doc trusted past its expiry date.
  Cheap live probes settled them in minutes — a `curl` for the Worker, the CF activity log for
  mail, `GET /api/admin/organisations` for org state. Re-measure before re-planning.
## Snapshot (2026-07-26) — OPS-3: routing matrix proven per channel; two P1s found

- **`docs/qa/2026-07-fable5-push/2026-07-25-routing-matrix-live-proof.md`.** All three PUSH
  channels route to the right vendor **on prod** (upload → explicit supplierId; REST ingress →
  supplier by NAME; inbound email → org default, order visible ~3 s after Postmark accepted it).
  All three PULL channels route to their configured default **LOCALLY** (atmoz/sftp, MinIO,
  Ethereal), each with a RED negative control — those tests return silently without their env,
  so a bare green proves nothing. **P1 F1: `unrouted` is unreachable on production.** With the
  org default cleared, an emailed PO did not park — `InboundEmailRouter.cs:464-472` falls back to
  the org's **oldest active supplier** (measured: it landed on "ProcuLink Sample Supplier"), so
  BE #52's park and FE #32's banner have no reachable prod trigger while an org has ≥1 supplier;
  no pull channel has this fallback. **P1 F2: BE #54's learn-from-correction silently fails on the
  real path** — assign-supplier routed the order correctly but `SupplierIdsCsv` stayed empty;
  `LearnSupplierFromCorrectionAsync`'s SaveChanges threw `DbUpdateConcurrencyException` (0 rows)
  and `ParseOrderJob.cs:150-153` swallowed it. PR #60's phantom-row detach landed on the
  **file-less** branch only; the file-backed re-parse (`OrderIngestionService.cs:1025-1027`) still
  leaves `Deleted` phantoms and the *next* writer in the scope — the fingerprint — is the victim
  (#60 correctly refuted the passport emit being one; it saves *before* the reflection). Net
  effect is worse than "did not learn": the layout ended up bound to the supplier F1 guessed,
  with the human's choice dropped. **F3:** the binding has no consumer at all — `LookupAsync`'s
  only caller is `/api/upload/detect-format` and `FingerprintBoost.Apply` drops `SupplierIds`, so
  "the fingerprint suggests a supplier" is not a claim the product can make yet. **F4 (fixed
  here):** `Live_ImapIngress` has been dead since `de4ea0e` (seeded no `Supplier`, so it hit the
  unrouted branch and NRE'd on an unstubbed mock) — env-gated, so CI's "2 skipped" hid it.
  **F5 doc fix:** "Dim's Organization" **is** `personal-workspace-d3be` (one org row, two slugs);
  OPS-2's claim they were different orgs is wrong, and the DB slug is what
  `IngressController.cs:47-53` matches. Test data to delete: 2 ROUTETEST suppliers + 4
  ROUTETEST- orders (ids in the doc); API keys already revoked, email default restored to null.


## Snapshot (2026-07-26) — supplier routing is proven per channel, in one table

- **Every ingress channel's routing is now one re-runnable matrix**, real Postgres, 27/27 green:
  `ProcuLink.Api.Tests/Integration/SupplierRoutingMatrixPostgresTests.cs`. Cells cover manual
  upload, REST ingress (GUID + case-insensitive name), inbound email (org default → oldest-supplier
  fallback → park), SFTP/S3/IMAP pull (valid / NULL / soft-deleted default), `assign-supplier`
  (claim + revision pin, 409), fingerprint learning, and DESADV's 501. Each assertion names its
  cell. Test-only — no production code touched. Handover:
  [`docs/qa/2026-07-fable5-push/2026-07-26-supplier-routing-matrix.md`](docs/qa/2026-07-fable5-push/2026-07-26-supplier-routing-matrix.md).
  **BE PR #65 — open, not merged. MERGEABLE, CI green: `Build + test (213 baseline)` PASS 13m45s,
  Api.Tests 1,663 passed on the Linux runner and all 27 cells `Passed` there.** Locally the same
  commit showed 2 reds — `DeliveryClaimEquivalencePostgresTests` +
  `DeliveryConfigRepublishPostgresTests`, both `Npgsql: Exception while reading from stream` — and
  CI's 1,663 is exactly the local 1,661 plus those two, so they were this host's contention. Run
  ALONE with the matrix excluded, the claim-equivalence suite fails **19/64** with the failing
  cases moving between runs: it is `IAsyncLifetime` + a 64-case theory, i.e. one container PER
  CASE. Making it class-scoped is queued as separate work.
- **Fingerprint auto-routing is NOT shipped, and the matrix now pins that.** The only production
  consumer of `SchemaFingerprint.SupplierIdsCsv` is `FormatDetectionController.cs:58`, and even
  there `FingerprintBoost.Apply` returns `detected with { Confidence, Reasoning, SeenCount }` —
  `match.SupplierIds` and `SampleSupplierName` are DROPPED, so even "suggest-only" overstates it:
  the layout is recognised, the supplier is never offered. (Independently measured live by the
  OPS-3 pass / PR #64.) A repeat layout arriving supplier-less still parks `unrouted`; cell 6b
  fails the day anything starts auto-binding. Caveat: that park needs an org with no usable
  supplier — inbound email otherwise falls back to the oldest active supplier (cell 3b).
- **Proven load-bearing, not just green:** three deliberate mutations (email oldest→newest fallback,
  SFTP resolver dropping the soft-delete filter, a 6b auto-bind probe) failed exactly cells 3b, 4c,
  6b with no collateral, then were reverted.
- **New Postgres-only trap:** `SupplierConnection.ActiveRevisionId` ↔
  `SupplierConnectionRevision.ConnectionId` cannot be inserted in ONE `SaveChanges` — EF cannot
  order the cycle ("circular dependency was detected"). Seed as connection-unpinned → revision →
  pin. InMemory accepts the single write, so the InMemory revision-authority tests never see it.
- **The class-scoped-fixture lesson from #59 was applied, and it mattered more here:** xUnit builds
  a fresh test-class instance per *theory case*, so a per-class `IAsyncLifetime` container would
  have started and migrated **27** containers. `IClassFixture` keeps it to one; the 27 cells run
  in ~3 s.

---
- **Cross-check with the OPS-3 live pass above (independent provenance, same conclusions).** That
  pass measured prod; this suite reads and exercises the code — they agree without either being
  told the other's answer. Its **F3** is the same finding as this suite's fingerprint correction
  (`FingerprintBoost.Apply` drops `SupplierIds`), reached from the opposite direction. Its **F1**
  (email falls back to the oldest active supplier, so `unrouted` is unreachable on prod for an org
  with ≥1 supplier) is what matrix cell **3b** asserts as intended behaviour — read the two
  together: 3b proves the fallback works, F1 says the fallback is why the park cannot be reached.
- **KNOWN LIMIT of cell 6a, given OPS-3's F2.** Cell 6a drives
  `SchemaFingerprintService.RecordParseSuccessAsync` directly, as the sibling suite does, so it
  proves the recorder binds the operator's choice on re-entry. It does **not** prove the real
  `ParseOrderJob` file-backed re-parse ever reaches that recorder — and F2 measured that it does
  not (phantom `Deleted` rows → `DbUpdateConcurrencyException` → swallowed at
  `ParseOrderJob.cs:150-153`). **So cell 6a is green while production does not learn.** Closing F2
  is the fix; this cell is the unit-level guard, not the end-to-end proof.

## Snapshot (2026-07-25) — BE-1's KNOWN GAP closed

- **A prose-only email to a supplier-less org now becomes a real, resolvable order.** PR #52 left
  this pinned as a KNOWN GAP: the body-NLP fallback persisted through
  `CreateStubFromParsedOrderAsync`, which required a supplier, so such a message was accepted (200)
  and audited but produced NO ORDER — while an attachment on the same message was parked `unrouted`.
  Closed with `IOrderService.CreateUnroutedStubFromParsedOrderAsync`, a separate entry point rather
  than a nullable id on the routed method, so `IngressController` (REST push, which must have a
  supplier) still fails to COMPILE if it ever loses one. Parked with NULL supplier, NULL
  `ConnectionRevisionId` — a revision belongs to a supplier connection, and borrowing the org's only
  other supplier's revision would bind the order to a counterparty nobody chose; `assign-supplier`
  pins it when the operator picks.
- **The bigger half was RESOLVABILITY, and it was not in the brief.** A prose order has no source
  file. `assign-supplier` resolves an unrouted order by flipping it to `parsing` and re-enqueueing
  `ParseOrderJob` → `ParseStoredFileAsync`, which began by downloading the source file and returned
  `Fail("Order has no source file key.")`. Shipping only the create half would have made the
  operator's assignment WEDGE the order in `parsing` permanently: the job throws, burns its 3
  Hangfire retries, lands red, and can never be re-assigned because `assign-supplier` accepts only
  an `unrouted` order. A file-less order's persisted lines ARE its parsed data, so that branch now
  re-resolves them against the chosen supplier (header untouched — there is no file to re-read it
  from, and the extractor's copy is the only one). A file-less order with NO lines still fails loudly.
- **Trap found and fixed (would have shipped silently):** `ExecuteDeleteAsync` removes rows but tells
  the change tracker nothing. `ParseStoredFileAsync` Includes the order's lines, and a file-less order
  always has some, so reflecting the new set onto `entity.Lines` severed the stale ones from their
  required parent, EF cascaded them to `Deleted`, and the *passport emit's* `SaveChanges` then issued
  a DELETE matching 0 rows → `DbUpdateConcurrencyException` raised AFTER the persist had already
  committed, i.e. an order correctly resolved in the database but reported as a failed parse. Stale
  entries are now detached the moment their rows go. The file-backed re-parse escapes this only by
  emitting its passport event BEFORE its in-memory reflection — I suspected it had the same defect,
  tested it, and the suspicion was **refuted**; that behaviour is now pinned by a regression test
  (with its precondition asserted, so it cannot pass vacuously).
- Tests: 5 InMemory (`OrderIngestionUnroutedParsedOrderTests`) + 4 real-Postgres
  (`UnroutedParsedOrderAssignSupplierPostgresTests`); the PR #52 pinning test
  `NoSupplierConfigured_BodyOnlyEmail_SucceedsButCreatesNoOrder` was flipped RED-first to
  `…_CreatesUnroutedOrder`, and the re-resolve's RED output was the predicted
  `Order has no source file key.` exactly. **BE PR #60 — open, not merged. MERGEABLE/CLEAN,
  CI green: 3,888 passed / 0 failed / 2 skipped** (the 2 are the env-gated live-feed tests).
  Locally Infrastructure showed 1 red — `FireIntegrationTriggerJobReliabilityTests.
  TwoConcurrentFinalFailures_OnPostgres…`, `Npgsql: Timeout during reading attempt`, the known
  Testcontainers contention flake; it passed 6/6 in isolation and 1054/1054 on CI's Linux runner.

## Snapshot (2026-07-25) — frozen orgs are recoverable from the product

- **`POST /api/admin/organisations/{id}/account-status` — BE PR #59, MERGED `0e1ac58`.**
  Closes the gap the founder org exposed on 2026-07-24: an org frozen by a Stripe cancel
  (`account_status=read_only`) could only be lifted with a raw production UPDATE. `[AdminOnly]`,
  cross-tenant by route id, same shape as `SetOrganisationLimits`. **Exactly one transition is
  permitted — `read_only` → `trialing`, and only on a Pilot org with no live
  `StripeSubscriptionId`** (the reconciliation sweep skips a blank subscription id, so nothing
  fights the write; with a live id it would be re-derived from Stripe, so the endpoint refuses
  rather than lie). Every other status stays owned by its writer. The endpoint keeps NO copy of
  the trial-expiry rule: it writes `trialing`, then calls `MarkPilotExpiredIfNeededAsync` and
  returns whatever that leaves behind, so the response can never claim a status the DB does not
  hold. Audit row `admin.org.account_status_changed` (who/when/from/to + effective).
  **Founder-org recipe is TWO calls, in order:** extend the trial via `.../limits` first (while
  still `read_only` the arbiter early-returns, so nothing moves), *then* this endpoint —
  otherwise the lapsed Pilot window re-expires it immediately, which the response says out loud.
  20 unit tests + 3 real-Postgres; **local Api.Tests 1627/1627, 0 failed, 0 skipped** — the
  `0 skipped` is half the result, since a wedged Docker probe skips the Postgres tests and still
  prints `Passed!`. An earlier run of the same commit showed 17 failures, all
  `Npgsql: Timeout during reading attempt` in `InitializeAsync` and 15 of them in pre-existing
  suites the change does not touch: a sibling session had left **76 orphan Testcontainers** on the
  host. Reaping those (filter `label=org.testcontainers=true` ONLY — the four named dev DBs carry
  no such label and were verified absent from the set before deleting) returned the identical
  commit to green. **Lesson for new Postgres fixtures: make them class-scoped.** xUnit builds one
  test-class instance per test, so the repo's per-test `IAsyncLifetime` convention starts AND
  migrates one container PER TEST — the exact load `PostgresContainerCollection`'s own comment
  warns about. This paragraph missed the #59 merge window by one commit, hence the follow-up.

## Snapshot (2026-07-24, late) — Postmark IP drift is now DETECTED

- **The Worker's hardcoded Postmark IP allowlist is watched — BE PR #58 (open).** #57
  (now merged, `52d9961`) made drift *survivable* (503 → ~10.5 h of retries → `Failed`,
  re-fireable 45 days); it did not make anyone *notice*. `.github/workflows/postmark-ip-drift.yml`
  runs `check-postmark-ips.mjs` weekly (Mon 06:00 UTC + on demand), re-reading Postmark's
  support article and failing loudly on any difference from `POSTMARK_WEBHOOK_SOURCES`.
  Verified rather than assumed: Postmark publishes **no** machine-readable IP list (no JSON/
  API/txt), and the article mixes ~48 SMTP/MX/DKIM addresses with the 4 webhook ones — so the
  extraction is scoped to its `<h2 id="webhooks">` anchor, and every "cannot tell" outcome
  (fetch error, renamed heading, non-IP bullet) exits non-zero rather than reporting no drift.
  A Worker-side 503 counter was rejected: no sink exists (no KV/DO/Analytics Engine binding)
  and it could only fire after mail was already refused. Proven both ways — green against the
  live article, and red on a simulated drift. 12 new tests (20 in the folder), now CI-run on
  PRs touching it. **This PR needs no redeploy** (worker.js changes are comments only); the
  #57 hand-redeploy **is now done — measured 2026-07-26** (see the #57 entry). Found in
  passing: `uptime.yml` cites
  `docs/deployment/monitoring-runbook.md` twice — that file does not exist.

## Snapshot (2026-07-24, late) — BE-6 fixed

- **BE-6 (P1) closed — the generic XML catalog parser no longer drops every second field.**
  `SupplierCatalogFileParser.FlattenElement` double-advanced the `XmlReader` per scalar child,
  so `a,b,c,d` flattened to `[a|c]`; Jarltech's 14,713 items would have imported with no name
  and no price. Fixed by the guard the cXML path already carried. 3 RED-first tests, each with
  ≥4 scalar children — the pre-existing XML fixtures topped out at 2 children, which is exactly
  why the suite never caught it. **This lifts the "do not enable element-based XML feeds" gate
  on OPS-2** once the PR merges. Attribute feeds (100MEGA) and cXML Index were never affected.
  **BE PR #55 — open, not merged. CI green: 3,863 passed / 0 failed / 2 skipped** (the 2 are
  the env-gated live-feed tests). Local Api.Tests skipped its 130 Postgres tests — the Docker
  Desktop engine wedged under cross-worktree Testcontainers load and the runner still printed
  `Passed!`; CI on Linux ran all 1596 with 0 skips, which is the result that counts.

## Snapshot (2026-07-24, evening) — wave MERGED

- **All 14 wave PRs are merged and both repos have zero open PRs.** FE #28–#33 (catalog-tab
  polish, inbound-address card, SEO, navbar dedup, assign-supplier UI, catalog-picker scale)
  and BE #45–#53 (PunchOut + auto-detect specs, OPS-1/OPS-2 findings, store:false, log
  levels, email-park-unrouted, upsert batching, set-based clear). Merge train was
  sequential with per-PR conflict weaves on the shared queue docs; two real collisions:
  BE-4's level-pin test vs BE-1's `{Mode}` log template (test updated, all green) and
  #51's InMemory DeleteAsync test vs #53's ExecuteDelete (dropped in favour of the
  Postgres coverage). Item-level detail in the struck queue + bullets below.
- **Open after the train:** FOUNDER P0 — the founder org is `account_status=read_only`
  (Stripe cancel test), every ingest channel dead on it; lift it, then re-fire the parked
  Postmark message. Queue: ~~BE-6~~ (fixed, snapshot above), ~~BE-1's 422-retry
  residual~~ (fixed, #56 below), ~~the schema-fingerprint learning gap~~ (fixed, #54
  below), FE `lint:vocab` pre-existing red. Founder halves: OpenAI DPA/EU project,
  **OPS-2's remaining five vendor cred pastes** (the Jarltech half is DONE — org is on
  Growth and the scheduled pull landed 14,713 rows; see the OPS-2 entry below), and —
  once the Worker 403→503 PR merges — a **hand-redeploy of the CF inbound-verify
  Worker** (nothing else ships it; see that entry below).
- **2026-07-24: the CF inbound-verify Worker no longer drops mail on IP drift — BE PR #57
  (MERGED, `52d9961`). ✅ DEPLOYED — verified 2026-07-26:** a `POST` to
  `inbound.proculink.eu/api/inbound-email/postmark` from a non-Postmark IP returns **503**
  (the #57 retryable refusal) where the old code returned 403; `api.proculink.eu/health` and
  `proculink.eu` both answered 200 in the same check, ruling out a coincidental upstream 503.
  That one-line probe re-verifies the deployed Worker any time, without CF dashboard access.
  Previously: `worker.js`'s source-IP gate answered
  **403**, the one status that makes Postmark stop retrying on the first attempt *and* never
  file the message as `Failed` — so a purchase order refused there was gone by both routes,
  automatic and manual. The allowlist is hardcoded and Postmark has changed its published
  webhook IPs before, so the most likely failure in that file was silent, total order loss.
  Now **503** `source address not allowed`: same refusal, full ~10.5 h retry window, message
  lands in `Failed` and stays re-fireable for 45 days. 429 was rejected because the token
  bucket four lines down already owns it. The Worker now spends neither 200 nor 403 —
  nothing it refuses is a decision a retry cannot change — which mirrors
  `InboundEmailController`'s model (its stale "the Worker already spends 403" comment is
  corrected). Pinned by `worker.test.mjs` (8 tests, `node --test`, no deps, not in CI —
  this repo has no JS pipeline). **Cloudflare is not wired to this repo: merging changes
  nothing until the founder redeploys** (`bunx wrangler deploy`, or dashboard →
  `postmark-inbound-verify` → Edit code → paste → Deploy), then re-runs the README smoke
  test and confirms the refusal reads 503. No Postmark/Railway/DNS change needed; rollback
  is the Worker's Deployments → Rollback. README also de-staled: it said "PREPARED, NOT
  DEPLOYED" and "API-side change NOT implemented" — both live since OPS-1.
- **2026-07-24: inbound-webhook retry contract fixed — BE PR #56 (open), BE-1 residual
  closed.** OPS-1 measured that 422 does not stop Postmark; the documented policy is 10
  retries over ~10.5 h on any non-200, then the message is filed `Failed` (still re-fireable
  by hand for 45 days), while 200 marks it `Processed` and unrecoverable. The status is
  therefore split by whose fault the failure is, via a new `InboundEmailRejectionKind`:
  **200 `{status:"ignored"}`** for sender-side-permanent (address not ours, unknown tenant
  slug, empty body, missing recipient) and **422 with its retries intact** for
  our-side-transient (org row missing behind a `TenantMapping`, blocked account status —
  a read-only freeze is reversible in minutes, so the retries are the grace window). An
  unlabelled rejection keeps its retries. The false "422 keeps Postmark from retrying"
  comment is deleted; the `rejected_read_only` audit row is pinned by a test.
  ~~**Not fixed, noted:** the CF inbound-verify Worker's 403 on IP-allowlist drift~~ —
  **fixed, see the entry above.**
- **2026-07-24: the schema fingerprint now learns from operator corrections (BE-5 P0, PR #54).**
  `assign-supplier` re-parses, but the recorder short-circuited on the order's existing
  `SchemaFingerprintHash`, so `SchemaFingerprint.SupplierIdsCsv` could only ever accumulate
  suppliers already known at ingest — every human routing correction was discarded. The
  guard is now supplier-aware (bind the unknown supplier, return, never touch
  `ParseSuccessCount`/`LastSeenAt`) rather than the hash being cleared, which would have
  re-armed the count and double-counted the layout. Proven RED-first on real Postgres
  through the endpoint itself.

## Snapshot (2026-07-24) — routing/catalog/ops wave queued

- **Active queue: `docs/prompts/2026-07-24-open-queue-handover.md`** — 9 parallel chip
  items (FE: assign-supplier UI [doc: `2026-07-24-assign-supplier-ui.md`], navbar dedup,
  catalog-picker scale, marketing SEO; BE: email-park-unrouted, row-cap raise,
  Responses `store:false`, webhook log level, supplier-auto-detect SPEC; OPS: live
  inbound-email e2e, prod vendor-feed test) + founder actions. Chips run **Opus 4.8
  Extra** per founder. Same-day verification findings: ~~**P0 — CF Email Routing
  forwarding is broken for all 12 addresses**~~ — **RESOLVED, re-verified 2026-07-26:
  all 12 forwarding rules Active, and the 30-day activity log shows 15 received / 15
  forwarded / 0 failures**; OpenAI org is an unverified Personal account
  (API-call-logging Disabled, no EU project, no ZDR) — **but the DPA IS signed**
  (Ironclad "Complete" mail to `legal@`, corrected 2026-07-26; the old "no DPA" was a
  guess). Org verification is blocked upstream: **Persona has sent nothing to `info@`**
  in 30 days of CF logs, so that is an OpenAI-side problem, not an email one.
  The June CF API token is dead (401). Routing truth (code-verified): every channel either requires
  a supplier (upload 400, REST ingress 400) or parks `unrouted` (the three pull channels,
  and — since BE-1 below — the inbound-email webhook, which used to 422-reject);
  BE `assign-supplier` endpoint live at OrdersController.cs:583 with **no FE
  caller** — that's FE-1. Phase 1b enqueue gap: FIXED since `74ac036`+`de4ea0e` (old
  entries below are stale); both routing worktree branches fully merged (CLEANUP-1).
- **2026-07-24: catalog import memory bounded — BE PR #51 (open), BE-2 done.** The row cap
  was already 200k (raised in `efff40e`; the queue's "50k" was stale doc, now corrected in
  3 comments). The real gap was downstream: `UpsertManyAsync` tracked the whole file for one
  `SaveChanges`. Measured on real Postgres with a synthetic 200k-row CSV — single batch:
  615 MB retained, **1.43 GB peak working set**, 200k rows tracked; 5k batches: 24 MB
  retained, 415 MB peak, 0 tracked. Batch size swept (1k/5k/10k → time flat ~46 s, memory
  linear 11/22/38 MB) so it's picked on memory alone; insert costs ~40% more wall time
  (29 s → 41 s), which a background sync absorbs. Each batch detaches only the rows it
  touched — **never `ChangeTracker.Clear()`**, which would detach the tracked
  `SupplierCatalogSource` that `CatalogPullService` writes its sync status to afterwards
  (regression test pins it). Atomicity traded knowingly: the upsert is idempotent by
  (org, supplier, code), and `LastFileHash` has exactly one writer (post-success) so a
  partial import is always re-fetched, never skipped as unchanged.
- **2026-07-24 FE-1 done — assign-supplier UI, FE PR #32 open (not merged).** The
  `unrouted` park finally has an in-app exit: `apiClient.assignSupplier` (409 = the atomic
  `unrouted → parsing` claim matched no row, i.e. already routed — kept distinct from a 400
  "Supplier not found" via `ApiHttpError`), `AssignSupplierBanner` on the order page keyed on
  `order.status` (NOT the issue count — that screen's badge reads "Needs review" for these
  orders), and the inbox row action in place of the blank supplier name. The banner is a
  banner, not a gate: the extracted lines underneath are the evidence for "whose order is
  this?". `SupplierPicker` extracted from `UploadWorkbench` for reuse; `ord-004` mock fixture
  added (mock mode previously could not reach the flow). Deviation: the inbox action
  NAVIGATES to the order page — `InboxView` has no per-row action cells, so inline assign
  would mean a second copy of the picker + 409 handling. 20 new tests; 869 vitest green
  (90 files); tsc + `bun run build` green; browser-verified at 1440/390 in mock mode.
- **2026-07-24 FE-2 done — double-navbar dedup, FE PR #31 open (not merged).** Real cause
  was NOT a one-tab hub (no hub has <2 tabs): on top-level routes the topbar's context row
  rendered a lone unlinked crumb ("Dashboard") directly under the active nav item of the
  same name. New `isLonePageCrumb()` (breadcrumb.ts) hides that row at `md+` only — where
  the primary nav row is visible; below `md` the nav row is behind the hamburger and the
  crumb is the sole page label (those pages ship `PageHeader titleHidden`). Hub strips and
  ancestor trails ("Workbench / Drafts") untouched. Single-tab-hub guard added anyway
  (`hubShowsTabs`) + `>=2 tabs` invariant pinned in BridgeSidebar.test.tsx. Browser-verified
  at 1440/768/767/390px; 861 vitest green (88 files), `bun run build` green.
- **2026-07-24 FE-3 done — catalog picker scale, FE PR #33 open (not merged).** All three
  gaps land on one shared seam: `src/lib/catalogCodes.ts` (query-key contract + the pure
  `catalogPageClaim` verdict), `useCatalogCodeSearch` (250ms-debounced server-side lookup),
  `CatalogCodeResults` (shared option list + status line). (a) the review picker searched
  only the 1000 rows it had fetched — now `?q=` server-side. (b) `MagicMappingPreview`'s
  manual entry is a combobox over the same lookup, delivering the typeahead the help pages
  already promised (supplier read from the existing `["order", orderId]` cache — the
  mapping-preview payload carries no supplier id). (c) orphaned `CatalogHintCard` mounted in
  `OrderWorkshop`: desktop Issues tab + a new `MobileTriage` `hintSlot`, fed server truth
  (`exceptionCount`, order lines). Search keys extend the empty-query probe's prefix, so the
  existing `["supplier-catalog-codes", supplierId]` invalidation after import/sync/clear
  still sweeps them, and an order view fires one catalog request. Honesty: the "Searched
  only the first N of M" hedge is gone (the server searches the whole catalog), "no catalog
  for this supplier" now requires a settled zero-row page, a full page says "showing the
  first N". Also fixed a mock/API divergence — mock `getSupplierCatalog` returned the MATCH
  count as `total` while the API returns the whole-catalog count (`SupplierCatalogService
  .CountAsync` ignores `?q=`), which is the exact number "no catalog" vs "no match" turns
  on, so mock-mode QA of that copy proved nothing. 875 vitest green (91 files, was 849),
  tsc/lint/build clean. Browser-verified on a 121-row seeded catalog: typing `CROSS` finds
  row 121 — unreachable under the old client-side filter — and a miss reads "No product
  matches". Geometry NOT measured: the browser pane reports zero-width rects while hidden,
  so responsive checks were CSS-reasoned only.
- **2026-07-24 FE-4 done — marketing SEO, FE PR #30 open (not merged).** Prod-verified
  defects, now fixed: all 33 help articles canonicalised to `/help` (children inherit the
  layout's `alternates.canonical`); pages declaring their own `openGraph` served NO
  `og:image` (a page-level block REPLACES the root's, never merges); `/` + legal/support
  pages had no canonical at all. `src/lib/seo.ts` `pageMetadata()`/`helpArticleMetadata()`
  now drive 50 pages; landing moved into a `(home)` route group so a server layout can
  carry its metadata (route still `/`). Sitemap unchanged + test-pinned against the page
  tree. 964 vitest green (89 files), `bun run build` 77/77 pages. NOTE: `bun run lint:vocab`
  is red on main already — "Proton Bridge" ×2 + "Wiring it from Zapier" in help prose this
  PR did not touch; no CI runs that gate.
- **2026-07-24 — BE-1 done (BE PR #52, open):** the Postmark inbound webhook no longer
  422-rejects a message whose org has no supplier. It imports the attachments via
  `CreateUnroutedStubAsync` + ParseOrderJob (parked `unrouted`, resolvable by FE-1's
  assign-supplier UI) and answers 200; audit `inbound_email.rejected_no_supplier` →
  `inbound_email.unrouted_no_supplier`. 422 kept for unparseable recipient / unknown slug /
  org-not-found / blocked account status. `InboundEmailRouter` is now the FOURTH writer of
  `unrouted` (first PUSH channel) — `OrderStatusConstants` reachability doc updated.
  ~~KNOWN GAP: body-NLP fallback still skipped without a supplier (no supplier-less
  `CreateStubFromParsedOrderAsync`); pinned by a test, not silent.~~ **CLOSED 2026-07-25 —
  see the snapshot at the top of this file.**
- **2026-07-24 BE-3 done — Responses API opts out of server-side storage, BE PR #50 open
  (not merged).** `OpenAiProductCodeSearch` (flag-gated product web search) is the only
  Responses API caller, and that API stores request + response payloads by default; the
  prompt carries customer PO line descriptions. Request construction moved to the pure
  `BuildOptions` seam with `StoredOutputEnabled = false` ("store" in the payload); 2 new
  tests pin the storage opt-out and the rest of the request shape. The four Chat
  Completions callers are untouched (that API defaults to store=false). Infrastructure
  1033/1033 + Transform 1218/1218 green; the 25 Api.Tests reds were all Testcontainers
  container-start failures under cross-chip Docker contention, none in this path. Code
  half only — the founder half (DPA, EU-residency project, ZDR) is still open.
- **2026-07-24: supplier auto-detect SPEC (BE-5) — BE PR #48 open, spec only, no code.**
  `docs/superpowers/specs/2026-07-24-supplier-auto-detect-from-document-design.md`. Signal
  audit: header parties EXIST; supplier-name match HALF and VAT match BLOCKED (`Supplier`
  carries no VAT/reg-nr/EDI/domain); catalog overlap needs a cross-supplier query +
  `(OrgId, Code)` index; sender address is SHA-256-only by GDPR design. Strongest signal
  already ships and wasn't on the brief: `SchemaFingerprint.SupplierIdsCsv`. **New defect:**
  that binding never learns from a correction — `assign-supplier` leaves
  `SchemaFingerprintHash` set, so the re-parse short-circuits and the chosen supplier is
  never bound. S-sized P0, worth shipping alone. Six founder decisions in the spec; #1
  (supplier identity columns) and #2 (sender-domain persistence) block signals outright.
- **2026-07-24 done:** FE #28 merged (`a5c2404`, catalog-tab polish); FE PR #29 open
  (inbound address on Email intake tab, 851/851 green); BE PR #45 open (PunchOut L1
  spec + queue strikes); Stripe test coupon deleted (0 remain); FE `feat/design-system-v1`
  deleted per founder (archived at tag `archive/design-system-v1`); **BE-4 done (BE PR #49)** —
  inbound-email webhook log levels: routine per-message narration → Debug (prod runs
  `Default=Information`), blocked-account reject raised Information → Warning, order-created
  stays Information; 5 level tests pin it. `InboundEmailController` needed no change (it
  already logs only rejects/misconfig).
- **2026-07-24 OPS-1 — live inbound-email transport PROVEN with real mail; ingest blocked.**
  Postmark `/email` → `redacted@example.invalid` (MessageID
  `c4fe887c-…`) delivered `250 Ok: queued as 28384453CA4`, and the API logged the routed
  webhook **~2 s later** — so MX → Postmark inbound → `inbound.proculink.eu` CF verify-Worker
  → API, incl. `Inbound__Postmark__ProxySecret`, are all correct end-to-end. **No order was
  created:** the founder org is `account_status=read_only` since 06:35:50 UTC (the Stripe
  cancel test's "frozen Pilot"), so `InboundEmailRouter.cs:136` refuses ingest
  (`inbound_email.rejected_read_only` ×3, 0 orders — nothing to clean up). **P0 for the
  founder:** that org's every ingest channel is dead until the status is lifted; then re-fire
  the still-`Scheduled` message via `POST /messages/inbound/d7dcf55d-…/retry` — no resend.
  Measured side-finding: **422 does not stop Postmark retrying** (3 attempts in 6 min for one
  message), so `InboundEmailController.cs:134`'s "422 keeps Postmark from retrying" comment is
  false — folded into BE-1's scope.
- **2026-07-24 OPS-2 re-run: prod auth SOLVED, now BLOCKED on the PLAN GATE (not read-only).**
  The founder's Chrome is signed in to prod as `redacted@example.invalid`, org **Dim's
  Organization** (`org_3EVIvV7ecKosZoL0fvYnhLdXNow`) — the only org on that Clerk user, so
  the frozen `personal-workspace-d3be` is not reachable and its `read_only` state is *not*
  what blocks this item. That org is `plan=pilot`, `accountStatus=trialing`, admin-overridden
  limits (100k orders, 30 suppliers, 17 used). **Enabling a catalog source is gated on
  `BillingFeature.SftpIngestion`, whose minimum plan is Growth** (`PlanConstants.cs:286`),
  so `HasFeatureAsync` returns false on Pilot and `PUT …/catalog/source` with
  `IsEnabled:true` 403s `catalog_sync_requires_integration`
  (`SuppliersController.cs:957`). Config-save (`IsEnabled:false`), `test-fetch`, and
  `catalog/import` are **not** gated. Lifting this is a founder billing decision.
  **Prod inventory:** none of the six vendors exists as a supplier — all 18 are demo/`ZZ`-test/
  sample, and only "FastParts Inc" has any catalog source (https, disabled, no creds). So
  nothing was pre-configured and nothing could be verified by reading prod.
  **Vendor creds recovered** from the archived audit transcript for 5 of 6 (Logicom's
  `ConsumerSecret`/`CustomerId` are not present in recoverable form). **All five probed
  read-only, off-prod, and all authenticate today:** Ingram `PRICE.ZIP` 2,728,550 B ·
  Also/Actebis `pricelist-1.txt.zip` 516,383 B · REDACTED-PARTY `redacted-fixture` 6,202,918 B ·
  100MEGA HTTP 200 but **109 s to first byte** (5-min `CatalogPullService.OverallDeadline`
  covers it, but it is the tightest feed) · Jarltech HTTP 200 `<priceinfo>` XML.
  **Second pass (founder supplied Logicom creds + authorised a plan bump): BE-6 PROVEN IN
  PROD.** `36ccd9e` (#55) is deployed; a live `test-fetch` of the real Jarltech feed returned
  HTTP 200 in 8.2 s, 19,515,097 B, 14,713/14,713 rows with code, **all 20 header columns**,
  and `SHORT_EN→name` + `YOUR_PRICE_NET→price` both mapped — the two fields the bug used to
  eat. Comma decimals parse (`130,41`→`130.41`). **Jarltech gate lifted.** Six vendor supplier
  records now exist on prod; Jarltech's source is configured and test-fetched (`isEnabled:false`).
  The other five stay unconfigured: extracting a Clerk token was refused by the permission
  classifier, and every remaining route needs the agent to handle creds in the clear — a
  handling constraint, not a knowledge gap. The plan bump was authorised but **not performed**:
  no admin route sets `Plan` (`AdminController.cs:315-397` is limits-only), so the only path is
  a live Stripe Checkout that collects a payment method even at 100% off — a founder act, and
  cancelling it is what froze `personal-workspace-d3be` this morning.
  **Third pass: rows + typeahead PROVEN on prod with no billing change.** `catalog/import` is
  not billing-gated, so a 38-row subset of the **real** Jarltech feed was imported
  (`created:38`) and verified live: `total=38` with code/name/price/currency, `take` honoured,
  `q=CounterCache`→4 (name), `q=8070`→15 (code prefix), `q=zzzznope`→**0** (no fall-back), and
  the Catalog tab renders "Product catalog · 38". Proves parse → `UpsertManyAsync` → catalog
  list → typeahead → UI on real vendor data; does **not** prove the scheduled pull job, which
  the plan gate still blocks. Param gotcha: that endpoint takes **`q`/`take`**, not
  `search`/`page`/`pageSize` — unknown params are silently ignored.
  **No payment action was taken** — the classifier refused Clerk token extraction,
  Checkout-session creation, and the 1.35 MB file upload; none was routed around.
  **Fourth pass: 100%-off coupon created on LIVE Stripe** (founder-authorised). This morning's
  `REDACTED-TAXID` was gone, so a new one exists on `acct_1TbeHcLSwazJxGKo`:
  `OPS-2 catalog sync test — 100% off`, **100% off forever, max redemptions 1**, customer-facing
  code kept out of git. Cap is deliberate — 100%-off-forever + unlimited redemptions on a live
  account is a standing liability if leaked. A coupon moves no money; **the checkout itself was
  left to the founder** (`Mode="subscription"` collects a payment method even at 100% off).
  ⚠️ Plan the exit before redeeming — cancelling is what froze `personal-workspace-d3be` today.
  The six new vendor suppliers moved the org to **23/30** suppliers.
  **Fifth pass — OPS-2's Jarltech half is DONE, scheduled pull proven end-to-end on prod.**
  Founder redeemed the coupon: `plan=growth`, `accountStatus=active`, live `cs_live_…` session
  — so Checkout → webhook → plan-flip is verified on real infra as well. Enable then returned
  **200 `syncEnqueued:true`** (403 gone — "plan tier, never `account_status`" was exactly right)
  and the Worker's pull finished in seconds: **`lastSyncStatus=ok`, created 14,675, updated 38,
  skipped 0, total 14,713.** 14,675+38=14,713 — the 38 manual-import rows were matched by code
  and **updated, not duplicated**, so idempotent upsert on `(org,supplier,code)` is proven too.
  600 rows sampled from pull-created records: **600/600 with name AND price** — BE-6 at full
  scale, versus a pre-fix prediction of "14,713 products with no name and no price". Catalog
  typeahead works at 14,713 rows. **Not exercised:** order-review-screen typeahead (no Jarltech
  PO on prod) — the backing endpoint is proven, the review binding is not; don't conflate them.
  **Not a bug:** fast synthetic typing drops all but the first char in that search box
  (debounced controlled input) — automation artifact, verified by appending one char at a time.
  Five vendors still need a founder cred paste; the plan gate no longer blocks them.
  **Sixth pass (2026-07-27) — all 5 remaining vendor endpoints ALIVE, all 5 blocked on the cred
  paste (permanent: an agent must not type passwords/API keys).** Credential-free probes: Ingram
  `SSH-2.0-Maverick_SSHD`, Also/Actebis `SSH-2.0-mod_sftp/0.9.9`, REDACTED-PARTY `220 SC-WEBSHOP3 FTP`,
  100MEGA `401` (up + gating), Logicom TLS OK — **none dead**. Jarltech proved the piece the fifth
  pass could not: the **unattended** sync ran `2026-07-26T22:00:28Z` (cron `0 * * * *`,
  `syncIntervalHours=24`) with **created 0 / updated 14,713 / skipped 0, total still 14,713** —
  idempotent upsert at full scale on a *scheduled* run. 800/800 sampled rows carry name AND price.
  REDACTED-PARTY's **credential-free** config is pre-staged (`ftp` is the only protocol that saves without
  a password), `isEnabled=false`, which also proved its host clears the SSRF guard — but its mapping
  has **no `name` column**, so test-fetch before enabling or it imports ~10,782 nameless products.
  100MEGA deliberately left unconfigured: its URL path is elided in the doc and was not guessed.
  New API quirk: `https`/`logicom` saves still require `Host`/`RemotePath` (send `""`) — non-nullable
  record params, implicit-required fires before the protocol branch. Per-vendor table, paste-ready
  payloads, and a "how to add the next vendor" recipe are in the OPS-2 QA doc.
  Off-prod findings from the first run stand: **P1 defect BE-6** — the generic XML catalog parser silently drops
  every second scalar child (`CatalogXmlParsers.cs:338-364` double-advances;
  repro `a,b,c,d` → `[a|c]`), so element-based XML feeds import with no name/price;
  attribute feeds (100MEGA) and cXML Index unaffected. **Jarltech un-blocked** (was 503,
  now 200 / 19.5 MB / 14,713 items) but must not be enabled until BE-6 lands. **BE-2's
  50k-cap premise is stale** — cap is already 200k + 256 MB. Handoff (per-vendor config
  values + paste-ready read-only prod probe):
  `docs/qa/2026-07-fable5-push/2026-07-24-ops2-vendor-feed-prod-test.md`.

## Snapshot (2026-07-23) — delivery-reliability + UI waves shipped

- **Delivery reliability (BE #28–#36, 2026-07-16→17, all merged + live):** supplier rejections
  land in `rejected_by_supplier` (never re-sent); `DeliveryOutcome {Dispatched, ClaimLost,
  NotRetryable}` retry contract (unbounded-loop fix); Retry pre-flip removed; the
  `ready_to_deliver` rescue sweep discriminates on artifact age (silent-lost-order fix);
  webhook status callbacks require a dispatch MARKER (`IdempotencyKey`/`ArtifactSha256`), one
  shared predicate; **crash-after-ACK now PARKS (`delivery_unconfirmed`) instead of duplicating
  the PO on erp_*/email**. `RedeliverableStatusInvariantPostgresTests` pins the four-list drift.
- **UI wave (FE #19–#26, founder-approved via mockups, all merged + live):** park operator UI;
  retry visibility (no more double-click dead-letters); inbox status truth (`sending`,
  `unrouted`, full failed-bucket — nothing renders as "New" falsely); **order-page chrome 5 rows
  → 2 (~348px → ~148px)**; navbar de-dup + dashboard context line; **Fields|Lines per-line
  mapping view** in the workshop; polish + gate-context pass.
- **Open engineering: the 2026-07 delivery-reliability queue is EMPTY.** Canonical claim
  predicate shipped (#43, `97fd19b` — see below; it also closed the billing-release
  load-then-save window). All three park follow-ups merged: supplier-ACK resolution (#38,
  `2459de1`), billing-held truthful restore (#40, `c85f127`), park race fix (#42, `77820b5`).
  Remaining engineering is the long-deferred list below (RLS, invoice rerouting, …).
  ~~DockerProbe wedged-engine chip~~ DONE (#44, `bbaf2ae`): probe now requires a non-empty
  `{{.ServerVersion}}` response, not just exit 0 — gated tests skip instead of erroring.
- **2026-07-23: the B cut SHIPPED — BE PR #37 (merged, `7052053`).** Ops requeue supersedes
  attempt rows (`CapSupersededAt`) instead of deleting them; `DeliveryAttempt.CountsAgainstCap`
  is the ONE cap predicate (all five sites); numbering ascends across requeues; evidence
  predicate untouched + assert-the-difference tests; refused-rejection re-send P1 CLOSED
  (`CapWithoutErasingEvidencePostgresTests.C2` pins the compound path; KNOWN_GAP deleted —
  it stayed green because its seed never included the erasure step, documented in the PR).
  Suites: Api 1512 / Infra 999 / Transform 1218 green.
- **2026-07-23:** `StatusJourney` errDot — FE PR #27 MERGED (`f590402`) + Vercel prod deploy
  verified Ready (same minute — the morning's webhook drop did not recur): the red X now sits
  on the node that failed (`{ failed: n }` stage variant; bare `failed`→Parse per
  ParseOrderJob.cs:67-73, `transform_failed`→Transform, delivery failures→Deliver);
  845/845 vitest + tsc + build green.
- **2026-07-23: supplier ACK resolves a park — BE PR #38 (merged, `2459de1`), queue item 3.**
  `delivery_unconfirmed` added to `WebhookReportableFrom` (status proxy SOUND for this member:
  sole writer `ParkUnconfirmedAsync` always leaves a marker row, so the evidence half already
  passed); terminal webhook writes now close the SLA window (`DeliveryDueAt`/`SlaBreached`) in
  the same atomic claim; the `unconfirmed` attempt row is never rewritten to success. TDD
  RED-first, 3 InMemory + 1 real-Postgres tests; Api 1514 / Infra 999 / Transform 1218 green.
  Adjacent pre-existing gap found here (dispatch-4xx keeps a live `DeliveryDueAt`) now FIXED —
  see the #39 bullet below.
- **2026-07-23: billing-held park restores truthfully — BE PR #40 (merged, `c85f127`),
  queue item 4.** `PurchaseOrderEntity.HeldFromStatus` (nullable, migration
  `20260723135012`) records a hold's origin on the LIVE row; `ReleaseBillingHeldOrdersAsync`
  now RESTORES a held park to `delivery_unconfirmed` (SLA nag reopened, NO re-drive — the
  auto re-send of an unknown-outcome PO was the duplicate the park exists to prevent) while
  every other hold keeps release+re-drive; `delivery_held → delivery_unconfirmed` added to
  BOTH transition maps. Reverses the old release doc's "human already chose" justification —
  the choice was stale and uncancellable while held (`MarkDelivered` gates on the park).
  TDD RED-first (6 red observed); real-Postgres migration round trip
  (`BillingHeldParkRestorePostgresTests`). Api 1515 / Infra 1002 / Transform 1218 green.
  Pre-existing release-vs-webhook write window named in the method doc + chip'd toward #36.
- **2026-07-23: SLA window closes on supplier rejection — BE PR #39 (merged, `160bd63`).** The
  adjacent gap found during #38: a dispatch 4xx moved the order to `rejected_by_supplier` but
  `PersistAttemptAsync` cleared `DeliveryDueAt`/`SlaBreached` only on success, so once the
  deadline passed the SLA sweep could raise a false "delivery overdue" on a settled order. Now
  closed on `result.Success || isSupplierRejection` (5xx/transient stays open — that order still
  owes a delivery); manual `MarkRejectedAsync` clears the window too; `rejected_by_supplier` added
  to `DeliverySlaService.ExcludedStatuses` belt-and-braces for legacy rows (both the sweep SELECT
  and the atomic claim). TDD RED-first, 2 InMemory + 1 real-Postgres
  (`DeliverySlaConcurrencyPostgresTests.RunAsync_RejectedBySupplierOrder_IsNotFlagged`). Suites on
  the #39 branch green: Api 1512 / Infra 1000 / Transform 1218. NOTE: this worktree's base
  predated #38's code; the squash applied cleanly onto the #38-containing main and #38's webhook
  SLA-close was verified intact post-merge (WebhookIngressController lines 325/347/377).
- **2026-07-23: automatic activation never claims a park — BE PR #42 (merged, `77820b5`),
  queue item 5.** The dispatch claim admitted `delivery_unconfirmed` unconditionally
  (both relational + InMemory branches); a Hangfire refetch (~30-min non-sliding invisibility,
  bare `UseNpgsqlConnection`, Worker Program.cs:133) of a dead automatic activation could
  therefore claim a park the stuck sweep created meanwhile, find no `dispatching` row to
  re-adopt, open a FRESH attempt and SEND — defeating the park. Fix = the handover's candidate,
  verified correct: the claim gates the `delivery_unconfirmed` member on
  `!requireAutoDeliver`, so only an operator activation can claim a park (Redeliver +
  ops requeue are the only `requireAutoDeliver:false` producers, verified exhaustively).
  Bug proven live RED on real Postgres before the fix (`AutomaticParkClaimPostgresTests`);
  refusal shape is benign `Success + ClaimLost` (predicate is the enforcement — no advisory
  pre-read, which the sweep could invalidate anyway). Also corrected `HoldForBillingAsync`'s
  false "automatic queue can never hold a park" justification (billing gate runs pre-claim;
  safe post-#40). Residual documented: a refetch of a dead OPERATOR redeliver may re-execute
  that one accepted human send (pre-existing at-least-once semantics). Api 1519 / Infra 1004 /
  Transform 1218 green. "~50%" mechanism confirmed, probability not measured.
- **2026-07-23: the canonical claim predicate — BE PR #43 (merged, `97fd19b`), #36 done.**
  New `DeliveryClaim` factory (Core): `ClaimableForDispatch(requireAutoDeliver,…)` /
  `ClaimableForRetry`, org-scoping inside the predicate, #42's conditional park member encoded
  in the factory (flattening structurally impossible); five named claim sets on
  `OrderStatusMachine`. Five sites repointed: dispatch relational + InMemory (InMemory now
  enforces the staleness gate), retry relational + InMemory (InMemory previously had NO gate —
  now returns the exact relational lost-claim contract), `HoldForBillingAsync`, Retry
  endpoint's bare literal. Asymmetries preserved + pinned (27 unit tests: operator-vs-auto
  differ exactly by the park; dispatch-vs-retry likewise; subset invariants with non-vacuity
  RAN); 64-case real-Postgres matrix pins Npgsql translation ≡ C# evaluation.
  **Secondary closed: `ReleaseBillingHeldOrdersAsync` per-row atomic claims** — release now
  loses the race to a supplier callback (old overwrite-backwards bug proven RED live via
  deterministic interceptor interleave, `BillingReleaseWebhookRacePostgresTests`); #40 restore
  semantics byte-exact. Suites: Api 1585 / Infra 1031 / Transform 1218 — the chip's full-Api
  run had 28 Testcontainers-contention fails (all re-ran green), so a clean single-run
  1585/1585 was re-verified before merge. Docker-wedge incident → DockerProbe chip filed.
- **Founder gates:** sweep hand-back design call (stuck sweep returns `delivering`+fresh
  timestamp; 4 tests pin it). ~~Preimage relocation~~ DONE 2026-07-23: moved (not deleted) to
  `C:\Users\Dmitri.REDACTED-PARTY\Documents\proculink-private\`, SHA256-verified, tree clean.
- **Ops note 2026-07-23:** GitHub Actions went silent ~07:00–08:15 UTC+3 and Vercel dropped one
  main-push webhook (recovered; interim prod deploy went out via `vercel deploy --prod`).
- **Process rules earned this wave** (durable memory has detail): a comment that JUSTIFIES is a
  proof obligation ("because X" ⇒ verify X); assert-the-difference over comment-the-difference;
  `git merge-base --is-ancestor` LIES about squash-merged PRs (grep main for content instead);
  worktree grep hits are copies of main, not evidence of a separate track.

- **2026-07-24 routing/catalog recon (code-verified) — three STALE claims in this file
  corrected:** (1) the Phase 1b "SFTP/S3 enqueue gap" was FIXED long ago (`74ac036`
  2026-06-30 enqueues ParseOrderJob; `de4ea0e` 2026-07-09 ships unrouted import for
  SFTP/S3/IMAP pull) — the §06-26 line below and the deferred-list entry are outdated;
  (2) the "two routing worktrees in flight" are fully merged (both tips are ancestors of
  main, zero unique commits) — branches are stale pointers, safe to delete; (3) Postmark
  inbound is no longer "token-only, verification deferred": prod sets
  `Inbound__Postmark__ProxySecret`, so the CF verify-Worker edge gate appears deployed
  (verify with one real email). **Known operator gap found:** BE
  `POST /api/orders/{id}/assign-supplier` exists (OrdersController.cs:583) but the FE has
  NO control calling it — an `unrouted` order shows "Needs supplier" with no in-app way
  to resolve it. Also: review-picker catalog typeahead is client-side over the first
  1000 rows only; upload-preview manual entry has no typeahead; `CatalogHintCard` is
  orphaned (never rendered).
- **2026-07-24: queue items 7+8 DONE.** Item 7 — FE PR #28 **MERGED** (`a5c2404`, founder
  grant in-session); item 8 — BE PR #45 open. Also founder-requested cleanup done: the
  Stripe test coupon (`zFUfTMBz` / promo `REDACTED-TAXID`, redeemed 1/1, already inactive)
  deleted via live Stripe API — 0 coupons remain. Item 7 (Catalog-tab
  polish) — FE PR #28: logicom out of the generic protocol picker (offer⇔works held — a
  saved logicom source keeps its tile; keyboard nav follows the visible set), tile labels
  left-aligned, empty-state dashed border 1px→2px (root cause was NOT overlap — geometry
  showed a 12px clear gap; Windows 125% scaling renders 1px as a 0.8px hairline Chromium
  can drop per-edge). 849/849 vitest (4 new, RED first) + tsc + build green; verified
  live at 1440px/390px via computed styles (no screenshots — Browser pane can't composite
  hidden; note: pane DOM/JS tools DO work now, only Playwright CDP stays blocked). Item 8
  (PunchOut L1) — BE PR #45, spec only:
  `docs/superpowers/specs/2026-07-24-punchout-l1-supplier-hosted-catalog-design.md`
  (revision-bundle fit, no-local-code-list AI implications with the allow-list guard kept
  strict, BuyerCookie-correlated browser cart return, ~3.5–4.5 wk estimate, decisions
  D1–D5). Fact-check against the handover pointer: PunchOut exists only as FE copy — no
  vocabulary in `standards/catalog.ts`, no protocol code in either repo.
- **Stripe LIVE webhook verified end-to-end (2026-07-24, founder-present):** real checkout on
  prod with a 100%-forever coupon (`REDACTED-TAXID`, max 1 redemption) — €0.00 invoice paid, webhook
  endpoint `api.proculink.eu/api/billing/webhook` delivered with 0% errors, org flipped to
  Growth (`upgraded to growth via Stripe checkout cs_live_…` in API logs), then cancellation
  reverted it (`subscription cancelled — reverted to frozen Pilot`). BOTH directions of the
  billing pipeline proven on live Stripe with zero money moved. Coupon self-expired (1/1);
  left in Stripe as the audit record. Remaining untested: `amount > 0` invoice branches
  (needs a real charge + refund, ~€4–5 in non-returned Stripe fees).
  **Unintended live side effect, found by OPS-1:** the cancellation left the founder org at
  `account_status=read_only` ("frozen Pilot"), which blocks every ingest channel for that org
  — the OPS-1 test email was refused at the router. Un-freeze the org before any further prod
  live-ops, and treat "cancel a live subscription" as a state-changing act, not a free probe.

## Snapshot (2026-07-04)

- **Production is LIVE** at `proculink.eu` + `api.proculink.eu` (launched 2026-06-09 window).
  Live QA 2026-06-29/30 verdict: **CONDITIONAL GO** — 7 inbound formats, 6 outbound formats,
  and HTTP delivery proven live on prod (locale-safe). `/health/ready` green 2026-07-04
  (DB + migrations + storage + worker all Healthy).
- **Active work:** the Fable-5 production-hardening push (master prompt above) — prove every
  advertised capability live from a clean slate, click-audit the entire UI, consolidate
  design drift, make marketing truthful, fix everything found.
- **Billing:** Stripe is **LIVE** (verified 2026-07-02 via API: `sk_live` key in Railway;
  Growth €149 / Operations €399 / Integration €999 / Distributor €1,499 monthly + all 4
  yearly prices, all active in live mode). Real-money infrastructure — no test checkouts
  against prod. **Annual billing is LIVE** (`ANNUAL_BILLING_ENABLED` defaults ON; verified
  on prod 2026-07-04: pricing toggle Monthly/Annual·save-17% switches to live Stripe yearly
  prices). Remaining billing to-do: verify the live webhook end-to-end on a real subscription
  event (founder — real money).

## Durable identity rule (2026-06-09)

ProcuLink is the product and customer-facing brand. The operating legal entity is
**Diip Solutions OÜ**, registry code **17527757**, registered at Uus-Sadama tn 15-2,
10120 Tallinn, Estonia. Frontend source of truth: `project-proculink/src/lib/legal-entity.ts`
(legal pages, footers, one-pager, JSON-LD consume it). **Never restore the fabricated
"ProcuLink OÜ" / 17477775 / Katusepapi identity.** Do not publish the founder's personal
registry email or invent a VAT number.

## Deployment topology (verified live)

| Piece | State |
|---|---|
| Frontend | Vercel, auto-deploy from FE `main`; `https://proculink.eu` is the single canonical origin (`www` → 308 to apex); `NEXT_PUBLIC_USE_MOCK=false` |
| API | Railway (EU) service `ProcuLink` → `api.proculink.eu`; auto-deploys from BE `main`; EF migrations apply on startup (fail-loud + phantom reconciler) |
| Worker | Railway service `aware-amazement` — the **single** Hangfire worker, GitHub auto-deploy, **mandatory** (nothing parses/delivers without it). Railway CLI linked, project `lucid-generosity` |
| DB | Neon Postgres (also hosts Hangfire) |
| Storage | Cloudflare R2: `proculink` (private order data — pre-signed URL GETs only; SDK chunked GET signing is rejected by R2) + `proculink-public` (marketing assets, `assets.proculink.eu`) |
| Auth | Clerk **production** instance (`clerk.proculink.eu`, `pk_live_…`); org id/slug read from the Clerk v2 `o` claim; force-org-creation (adopt-on-create + softened-resolve) deployed + live-verified 2026-06-30 |
| Inbound email | `{slug}@orders.proculink.eu`: CF Email Routing MX → Postmark → `POST /api/inbound-email/postmark` — proven live with a real email |
| Outbound email | Postmark HTTPS is the **canonical** email delivery path (SMTP is dead on Railway); domain verified (SPF/Return-Path/DKIM via CF API); **Postmark ACCOUNT APPROVED + cross-domain send LIVE-VERIFIED 2026-07-04** (test-fired the `email` delivery channel on prod → clean `{success:true,200}` to an external recipient; the prior 412 gate is cleared). Powers 3 roles single-vendor: outbound `email` delivery, transactional (support/notifications), inbound parse. |
| DNS | Cloudflare — edit **only** via scoped API token (the dashboard SPA won't render in the browser tool) |
| Observability | Sentry capturing (API + Worker + frontend); PostHog EU ingesting; `/health` (liveness) + `/health/ready` (DB + storage + migration checks); Worker heartbeat alert |
| Email auth | SPF + DKIM + DMARC (`p=none`) complete on `proculink.eu` |
| Stripe | **LIVE mode** (`sk_live` verified 2026-07-02); all 8 monthly+yearly price IDs set in Railway and active | 

Prod env vars are fully set in Railway (API + Worker) and Vercel; the required-key list is
enforced by `StartupConfigurationValidator` + `appsettings.Production.json` — verify infra
(`railway variables`, Stripe dashboard) before trusting any doc's gap claim.

## Test / build state

- Backend: **1,029 tests green, 0 failures** (224 Transform + 452 Infrastructure + 353 Api)
  — last count recorded here 2026-06-07 at `main` `216b3fa`. Substantial code landed since;
  run `dotnet test ProcuLink.slnx` for the live count before claiming green.
- Frontend: `bun run build` clean (48 routes at last record). Mock e2e suite green;
  live e2e recipe: `PROCULINK_QA_BYPASS_AUTH` + local PG :5435 + `Delivery__EncryptionKey`
  + Worker running.
- Windows dev, Linux CI/prod — after pushing check `gh run list`; local green ≠ CI green.

## What happened 2026-06-09 → 2026-07-02 (summary; detail in git log + memory)

- **North Star pivot (06-09):** versioned Supplier Connection platform (draft → test →
  publish → archive, `ConnectionRevisionId` pinning, replay/impact diff) — V1–V10 shipped,
  plus confidence-calibration (per-org accept-rate buckets).
- **Output-layer restructuring (06-15):** 100% complete — trust P0s, `OutputNode` AST +
  emitters + output designer (IncludeWhen conditionals, format presets, namespaces),
  cXML DTD, plain-language validation messages.
- **Order Workshop (06-18 → 06-21):** unified 3-column order review (`OrderWorkshop`) with
  inline source picker + "bind any source field" flexible mapping — live; layout now locked.
- **Hardening + audits (06-16 → 06-24):** four-track push (idempotency, retry, GDPR erase,
  AI-usage atomic), strand-race fix, full 5-lens audit (0 P0), workbench UX + mobile audits.
- **cXML address blocks (06-25):** ShipTo/BillTo/Contact emission, MapForce byte-identical,
  proven on a real REDACTED-PARTY PDF→cXML round trip.
- **Supplier routing (06-26):** Phase 0 (nullable SupplierId + `unrouted` status) + Phase 1
  (hold + assign-supplier re-parse) shipped; producer dormant; Phase 1b blocked on the
  SFTP/S3 enqueue gap. Two routing worktrees in flight — see master prompt operating rule 8.
- **Design (06-26 → 06-29):** design-system v1 branch `feat/design-system-v1` review-gated;
  a redesign handoff spec + 228 screenshots were produced for Claude Design (the spec was
  removed in the 07-02 docs purge; recover from git history at `4f0855f` if needed).
- **Prod launch QA + fixes (06-29 → 06-30):** pre-launch audit → CONDITIONAL GO; Postmark
  HTTP email channel (PR #15 overage retry, #14 Clerk `o` claim, #11 force-org-creation)
  merged + deployed; inbound email live end-to-end on a real org.
- **07-01:** shared `useConfirm()` dialog primitive replaced all native confirms; mapper
  toolbar/API-key polish. **07-02:** docs purge (`9456a08`) + master prompt (`93a5b10`) +
  this CLAUDE.md/STATUS.md cleanup.

## Known issues / limitations (honest capability edges)

- **EDIFACT INVOIC / DESADV are stubs** (no commercial EDI licence — EdiFabric rejected);
  DESADV upload returns 501. UI must present these as "coming soon", never as errors.
- **ERP connectors (`erp_erply` / `erp_directo`):** no live ERP sandbox creds — verified via
  unit/request-shape tests + mock REST test-fires only; label honestly.
- **Scanned/image-only PDFs:** every extracted line is review-flagged (no text layer to
  verify numbers against); illegible scans fail with an honest message. Assisted, not silent.
- **Postgres RLS not implemented** — final-deferred by design (post-revenue redesign);
  app-level `.Where(OrganisationId == …)` scoping enforces isolation everywhere.
- **Postmark:** account APPROVED 2026-07-04 — outbound customer email delivery is unblocked
  and live-verified (cross-domain send returns 200). Inbound webhook signature verification
  is still deferred (needs a CF Worker).
- Design-system drift (duplicate primitives, e.g. `UnifiedStatusBadge` ×2) — inventory in
  master prompt Appendix C; fix in the push's Phase 3.

## Open items — founder / ops gates (not code)

1. ~~Stripe live-mode swap~~ **DONE** (verified 2026-07-02: `sk_live` + live `whsec_` + all
   8 live price IDs in Railway). Remaining billing to-do is engineering: verify the live
   webhook end-to-end with a real subscription event and wire the annual toggle.
2. **Rotate chat-exposed secrets** — Clerk, R2, ElevenLabs, Cloudflare API token; delete
   `~/.proculink-cf-creds.env`. Deletable cruft: the `proculink-livetest` delivery Worker + KV.
   **2026-07-02 addition:** supplier catalog feed credentials (Ingram, Also/Actebis, 100MEGA,
   REDACTED-PARTY, Logicom, Jarltech URLs) were pasted in chat — rotate/re-issue with those vendors
   after the push, and store only in encrypted catalog-source config.
3. **A real PO to a real supplier's endpoint** — controlled-endpoint deliveries are proven
   live (code 200, verified at receiver); an actual third-party supplier remains untested.
4. **Monitored support/ops mailbox + alert destinations** (Sentry/ops alerts need a real
   destination the founder watches).
5. **OpenAI compliance for customer data** — EU-residency project + DPA + zero-retention
   before real customer PO text flows through extraction at scale.
6. **Google Search Console** — use the Domain property `proculink.eu`, submit
   `https://proculink.eu/sitemap.xml` (apex, not www).
7. **The actual selling.**

## Open items — engineering (deferred by design / next up)

- Postgres RLS (needs a real-Postgres two-org test harness + Hangfire/migration role
  exemptions before it can land green).
- Invoice-pipeline rerouting (needs a PO↔invoice link migration + relational test; the
  original plan doc was purged — re-plan if picked up).
- Frontend `api-client.ts` split, retry-consolidation, denormalize/partition — audit-flagged
  counterproductive pre-revenue; don't do without a fresh reason.
- Neon pooler + `DataRetentionSweepJob` enablement — env-only flips; both dormant safe-by-default.
- Full app CSP (script/style/connect — needs Clerk/Stripe/PostHog/Sentry testing); per-page
  SEO metadata on the remaining marketing pages; Sentry stale-issue resolve.
- Supplier-routing Phase 1b (SFTP/S3 enqueue gap) + integrating the two in-flight routing
  worktrees (`routing-phase0-nullable-supplier` @ `056aff6`, `routing-phase1-hold-assign`
  @ `2fed48e`).
- "Your inbound address" card in Settings (the `{slug}@orders.proculink.eu` address now
  exists but isn't surfaced in-app).
- Design consolidation per master prompt Appendix C; `feat/design-system-v1` branch is
  review-gated, not merged.

## Open items — founder configuration gaps (feature works, config missing)

| Area | Action | Where | Effect when missing |
|---|---|---|---|
| ~~Clerk post-signup redirect~~ | **Resolved — no change.** Redirect is code-controlled (SignUp `fallbackRedirectUrl` → `/onboarding/select-organization` → `/bridge`). New sign-ups correctly land on `/bridge`, which hosts the onboarding checklist. `/welcome` is the **post-checkout** (paid) confirmation page, not the signup landing — routing new (unpaid) signups there would be wrong. | — | — |
| Status page | Host a status board, set the URL | `NEXT_PUBLIC_STATUS_URL` (Vercel + `.env`) | Footer link hidden |
| Book-a-demo CTA | Create Cal.com/Calendly slot | `NEXT_PUBLIC_BOOK_DEMO_URL` | Pilot book-a-demo cards hidden |
| ~~Support-form delivery~~ | **Resolved** — `IEmailSender` resolves to `PostmarkEmailSender` whenever `Email:Postmark:ServerToken` is set (it is, in prod), so the support form now delivers via Postmark (approved). Optional: send one real test to confirm the ops mailbox receives it. | — | — |
| DPA counter-signature | Staff `legal@proculink.eu`, sign DPAs within 5 business days as committed on `/dpa` | Operational | Trust commitment becomes false |
| Subprocessor notifications | Maintain the subscriber list; 30-day advance notice per `/subprocessors` | Operational | Trust commitment becomes false |
| Cookie banner copy | Review live banner tone (incognito) | Browser smoke test | Cosmetic |

Closed since this table was first written: PostHog (ingesting live), `Frontend:Url` (set to
prod domain), walkthrough video (real R2-hosted video live on `/watch` — the Loom env var is
superseded).

---

## Archive note

Everything before June 2026 — Phase 0–3 build-out, the Next.js migration, commercial Groups
A–L, Waves 1–4 (invoice/ASN + Zapier/Make layer), Group I UI passes 1–15, Group J/K/L, the
2026-06 launch waves (0–8 + Wave D), and the per-session narratives that used to live here —
is implemented history. See `git log` (both repos) and the memory files. Treat all of it as
shipped unless a section above explicitly reopens it.
