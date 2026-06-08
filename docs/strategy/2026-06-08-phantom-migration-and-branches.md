# Phantom-migration cleanup + full branch audit (design only)

**Date:** 2026-06-08
**Author:** senior .NET/Postgres engineer (design + analysis pass)
**Scope:** ProcuLink backend (`C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`, .NET 8)
**Mode:** DESIGN ONLY — no source/appsettings edits, no build/test runs, no prod (Railway/Neon/Stripe) changes.

> Companion / prior art: `docs/strategy/2026-06-08-program-design.md` (section
> "phantom-migration-branches — effort M", L189-229) already designed the migration-mechanism
> change at a high level. **That doc's branch graph is now STALE** — it references backend HEAD
> `3bc5f4a` / frontend `f7ad356` and a different branch set. This doc supersedes its branch-audit
> table with the live state as of `origin/main` = `f018d77` and keeps its migration design but adds
> the concrete release-step mechanism, the phantom/no-op migration inventory, and the live-worktree
> safety caveat it did not capture.

---

## TL;DR

- **Phantom migrations (Part A):**
  - Two migrations are genuine **no-op (empty `Up`/`Down`) history-only rows** —
    `20260524204729_FixSupplierPoMappingsTimestamps` and `20260525105538_FixAutoDeliverValueGeneration`.
    They are harmless and idempotent (snapshot-only corrections). **Keep them; do not delete.**
  - Five **phantom-PRONE** migrations are reconciled at runtime by a hand-rolled
    `ReconcilePhantomMigrationsAsync` in `ProcuLink.Api/Program.cs:844-925` (sentinel-checks +
    hand-inserts `__EFMigrationsHistory` rows). This is a one-time prod-state patch living
    permanently in startup code.
  - **No model-snapshot drift detected**: the snapshot includes the newest entity
    (`supplier_products`, from the 2026-06-08 migration) —
    `ProcuLinkDbContextModelSnapshot.cs` (3 references). A `dotnet ef migrations has-pending-model-changes`
    run is the only way to *prove* zero drift; this design does NOT run it (no build allowed), so it is
    flagged as the one verification gate.
  - **Migrations are applied IN-PROCESS on API startup** (`Program.cs:723-805`), fire-and-forget,
    after the HTTP server is listening, 6 retries with backoff. The **Worker does NOT migrate**
    (grep of `ProcuLink.Worker` for `Migrate*` = empty) — so there is **no API↔Worker race today**.
    The real risk is **two API instances** racing `MigrateAsync()` if/when the API scales >1.
  - Design: move migration application out of startup into an explicit **release step**
    (`dotnet ProcuLink.Api.dll --migrate-only`) wired as a Railway pre-deploy command, default-on
    startup migrate kept until the release step is proven in prod. Mechanism specified below. **Not applied.**

- **Branch audit (Part B):** `origin/main` = `f018d77`. **Only ONE branch carries genuinely unmerged
  content: `feat/schema-fingerprint`** (`+` on `git cherry`). Every other branch is fully merged
  (0 ahead, or "1 ahead" but `git cherry` shows the patch already in main). **Local `main` is 10 commits
  BEHIND `origin/main`** and fully contained in it (fast-forwardable). Full KEEP/DELETE table below.
  **Critical safety caveat: the 5 `auto/*` branches are each CHECKED OUT in a live `.claude/worktrees/`
  worktree** (likely owned by concurrent agents) — they cannot be `git branch -d`'d while checked out,
  and deleting them may disrupt in-flight work. **Recommend confirming those agents are done before any cleanup.**

---

# Part A — Phantom migration analysis

## A.1 Migration inventory

41 migration pairs + 1 model snapshot live in `ProcuLink.Infrastructure/Migrations/`. Listed oldest→newest:

| # | Migration ID | Notes |
|---|---|---|
| 1 | `20260522140108_InitialSchema` | base |
| 2 | `20260523105318_AddSupplierSoftDelete` | |
| 3 | `20260524065837_AddStripeFieldsToOrganisations` | |
| 4 | `20260524070232_TrialStartedAtServerDefault` | real `AlterColumn` (server default `now()`) |
| 5 | `20260524204258_AddSupplierPoMappings` | |
| 6 | **`20260524204729_FixSupplierPoMappingsTimestamps`** | **NO-OP** (empty `Up`/`Down`) |
| 7 | `20260525072352_AddSupplierDeliveryConfig` | |
| 8 | **`20260525105538_FixAutoDeliverValueGeneration`** | **NO-OP** (empty `Up`/`Down`) |
| 9 | `20260525130145_AddBillingPlanFieldsToOrganisations` | |
| 10 | `20260525201221_AddAiMappingSuggestionsToOrderLines` | |
| 11 | `20260526055759_AddEmailConfigToOrganisations` | |
| 12 | `20260527230444_AddIdempotencyKeysAndAiUsageMonthly` | |
| 13 | `20260528090855_AddWave2IngressTables` | |
| 14 | `20260528110812_AddBuyersRulesTemplates` | |
| 15 | `20260528120215_AddInvoicesAndLines` | **phantom-PRONE** (sentinel-reconciled) |
| 16 | `20260528120226_AddAdvanceShippingNotices` | **phantom-PRONE** |
| 17 | `20260528120230_AddTenantApiKeysAndOrgSlug` | **phantom-PRONE**; also a real `migrationBuilder.Sql` slug backfill |
| 18 | `20260528120235_AddIntegrationSubscriptions` | **phantom-PRONE** |
| 19 | `20260528150709_AddIsSampleFlags` | **phantom-PRONE** |
| 20 | `20260529052831_AddWebhookSecretToOrganisation` | |
| 21 | `20260529064321_AddDataProtectionKeys` | real `CreateTable data_protection_keys` |
| 22 | `20260529123347_AddSchemaFingerprintsAndDeliveryRetry` | |
| 23 | `20260529133501_AddDeliveryRejectionReason` | |
| 24 | `20260530114731_AddDeliveryReliabilityFields` | |
| 25 | `20260530120144_AddSchemaFingerprintUniqueIndex` | |
| 26 | `20260530202549_AddPurchaseOrderQueryIndexes` | |
| 27 | `20260530213442_AddOrderConfirmation` | |
| 28 | `20260531090840_Wave1SecurityIndexes` | |
| 29 | `20260531101812_Wave2MappingCorrections` | |
| 30 | `20260531102341_Wave2PassportEvents` | |
| 31 | `20260531103802_Wave2BuyerNameColumn` | |
| 32 | `20260531160907_Wave2OrderExceptions` | |
| 33 | `20260531164438_Wave2AcceptanceProfiles` | |
| 34 | `20260601133116_AddDefaultSupplierForPullIngress` | |
| 35 | `20260604184540_AddDeliveryConfigOutputFormat` | |
| 36 | `20260605090921_AddS3IngressServiceUrl` | |
| 37 | `20260605170911_AddOrderEnrichmentFields` | |
| 38 | `20260605180412_AddSelfHostedOcrFlag` | |
| 39 | `20260606105014_AddOrderDirectionToOrganisation` | |
| 40 | `20260606112433_AddPricingOverridesAndOverageBilling` | |
| 41 | `20260607090000_AddOrderRequeueCount` | |
| 42 | `20260607135109_AddEmailPollingFlagAndPollingIndexes` | |
| 43 | `20260608073042_AddSupplierProducts` | newest; entity present in snapshot |

## A.2 The two genuine "phantom" (no-op) migrations

Both have an empty `Up` AND empty `Down`:

- `ProcuLink.Infrastructure/Migrations/20260524204729_FixSupplierPoMappingsTimestamps.cs:11-20` — empty `Up`/`Down`.
- `ProcuLink.Infrastructure/Migrations/20260525105538_FixAutoDeliverValueGeneration.cs:11-20` — empty `Up`/`Down`.

**Interpretation:** these are the classic EF pattern where someone changed only fluent config /
value-generation metadata (which alters the *model snapshot* but emits no DDL), then ran
`dotnet ef migrations add` to capture a history checkpoint. The `.cs` body is empty; the change lives
entirely in the paired `.Designer.cs` snapshot. They are **safe, idempotent, and must be KEPT** — EF
needs the `__EFMigrationsHistory` rows to stay aligned with the snapshot lineage. Deleting them would
make EF think later migrations descend from a different snapshot and could trigger spurious diffs.

**Recommendation: leave both in place.** They are not a problem; they are correctly captured no-ops.

## A.3 The five phantom-PRONE migrations + the runtime reconciler

`ProcuLink.Api/Program.cs:825-842` hard-codes 5 May-28 Wave 3/4 migration IDs, each with an
`information_schema` sentinel:

```
20260528120215_AddInvoicesAndLines           → table 'invoices' exists
20260528120226_AddAdvanceShippingNotices     → table 'advance_shipping_notices' exists
20260528120230_AddTenantApiKeysAndOrgSlug    → column 'organisations.slug' exists
20260528120235_AddIntegrationSubscriptions   → table 'integration_subscriptions' exists
20260528150709_AddIsSampleFlags              → column 'purchase_orders.is_sample' exists
```

`ReconcilePhantomMigrationsAsync` (`Program.cs:844-925`): opens the raw `DbConnection`
(`Program.cs:846-848`), confirms `__EFMigrationsHistory` exists (`:853-867`), reads `ProductVersion`
(`:870-877`) and applied IDs (`:880-887`), then for each phantom-prone ID whose sentinel object exists
but whose history row is missing, **hand-INSERTs the history row** (`:911-923`) so the subsequent
`MigrateAsync()` skips re-applying SQL that would otherwise fail with `42701 column already exists`.

**Why these are "phantom" in prod:** their SQL was applied out-of-band (or by a deploy that crashed
after the DDL committed but before the history row), so the DB schema is ahead of
`__EFMigrationsHistory`. The reconciler is a **one-time prod-state patch that became permanent code.**

**Smell, not a bug (already noted in `2026-06-08-program-design.md:211`):** the raw connection opened at
`Program.cs:846-848` is never explicitly closed/disposed — it relies on `DbContext` scope disposal at
`await using var scope` (`Program.cs:727`). Harmless today; worth tidying.

## A.4 Where migrations are applied (Part A core question)

- **API, in-process, fire-and-forget, post-listen:** `ProcuLink.Api/Program.cs:723-805`.
  - `app.Lifetime.ApplicationStarted.Register(() => { _ = Task.Run(async () => { ... }); })`
    (`Program.cs:723-725`).
  - Calls `ReconcilePhantomMigrationsAsync(db, migLogger)` (`Program.cs:744`).
  - Loops `await db.Database.MigrateAsync();` up to 6× with 3/6/9/12/15s backoff (`Program.cs:757-779`,
    actual call at `Program.cs:761`).
  - On success: `MigrationReadiness.MarkSucceeded()` (`Program.cs:765`).
  - On final failure: `LogError` + `SentrySdk.CaptureException` + `MigrationReadiness.MarkFailed()`
    (`Program.cs:791-803`) — process stays UP (liveness unaffected).
- **Worker does NOT migrate:** grep of `ProcuLink.Worker` for `Migrate|MigrateAsync|EnsureCreated|GetPendingMigrations`
  returns **no matches**. So there is **no API↔Worker concurrent-migrate race** today.
- **Deploy path has no release/migrate phase:** `railway.toml` has only `[build]` + `[deploy]`
  (`restartPolicyType`/`restartPolicyMaxRetries`) — **no `startCommand`, no pre-deploy/release command**
  (`railway.toml:19-21`). `Dockerfile:33` CMD = `sh -c "ASPNETCORE_URLS=… dotnet ProcuLink.Api.dll"`.
  Migrations are therefore coupled to app startup, by design today.

### A.4.1 Readiness gap (correctness issue worth fixing)

`MigrationReadiness` is a **2-state volatile bool** (`HealthController.cs:40-55`): `HasFailed` is `false`
both *while migrating* and *after success*; it only flips `true` after the 6th failed attempt
(`MarkFailed()`). `MigrationReadinessHealthCheck` (`HealthController.cs:92-102`) returns **Healthy** while
"in progress OR succeeded". **Consequence:** during the Neon cold-start migrate window, `/health/ready`
reports ready **before the schema is actually applied** — a "ready-before-migrated" gap. This is the
single highest-value correctness fix in this track.

## A.5 Design — move migration application into an explicit RELEASE STEP (NOT applied)

Goal: stop two API processes (current single instance, but the moment API scales >1) from racing
`MigrateAsync()` on boot, and make schema land **before** new app traffic. Keep the proven path; phase it.

The mechanism reuses the **exact same** `ReconcilePhantom + MigrateAsync + retry` logic so prod and
release-step behaviour cannot diverge.

### Phase 1 — zero-behaviour-change cleanups (S)
1. **Own the reconciler connection.** In `ReconcilePhantomMigrationsAsync` (`Program.cs:844-925`) either
   document the intentional reuse of the open `DbConnection` by `MigrateAsync`, OR switch to
   `await db.Database.OpenConnectionAsync()` + `CloseConnectionAsync()` in a `finally`. No behaviour change.
2. **Kill switch for the reconciler.** Gate the call site (`Program.cs:744`) behind
   `app.Configuration.GetValue("Migrations:ReconcilePhantom", true)` (default `true`) so it can be turned
   off the instant prod history is confirmed clean.

### Phase 2 — make readiness honest (S, correctness)
- Replace the `MigrationReadiness` bool (`HealthController.cs:40-55`) with a tri-state enum
  `{ Pending (default), Succeeded, Failed }`.
- Call `MarkSucceeded()` only after `MigrateAsync()` returns (`Program.cs:765`, unchanged location).
- `MigrationReadinessHealthCheck` returns **Unhealthy/Degraded while `Pending`** so `/health/ready` is
  honest during the migrate window; **Healthy only after `Succeeded`**.
- Update any test referencing `HasFailed` (grep test projects before changing the static surface).

### Phase 3 — deliberate release migrate (the core of Part A)
**Exact mechanism:**
1. **`--migrate-only` entrypoint.** Add a top-level `args` branch at the very start of
   `ProcuLink.Api/Program.cs` (BEFORE `builder.Build()`): if `args.Contains("--migrate-only")`, build a
   *minimal* host (DbContext + logging + Sentry only), run `ReconcilePhantomMigrationsAsync` (if still
   enabled) + the same 6-retry `MigrateAsync` loop **once**, log the result, then
   `Environment.Exit(success ? 0 : 1)` / `return` — **never call `app.Run()` / start Kestrel**.
2. **`Migrations:ApplyOnStartup` flag** (default `true`). Wrap the `ApplicationStarted` Task.Run block
   (`Program.cs:723-805`) in `if (applyOnStartup)`. Local dev keeps `true` for convenience.
3. **Railway release command.** Wire the one-shot migrate into the deploy. Two reversible options:
   - **(Preferred) `railway.toml` pre-deploy command:**
     ```toml
     [deploy]
     preDeployCommand = "dotnet ProcuLink.Api.dll --migrate-only"
     restartPolicyType = "ON_FAILURE"
     restartPolicyMaxRetries = 3
     ```
     Railway runs `preDeployCommand` once, using the new image, **before** routing traffic to the new
     deployment; a non-zero exit aborts the rollout (schema never half-lands behind a running app).
   - **(Alternative) a dedicated one-shot Railway service / job** sharing the same image + DB env vars,
     CMD overridden to `--migrate-only`, triggered before the API/Worker deploys.
4. **Cutover:** ship Phase 3 with `ApplyOnStartup=true` (prod unchanged). Run the release command once
   against prod; confirm `__EFMigrationsHistory` contains all 5 phantom IDs + the newest migration; THEN
   set `Migrations:ApplyOnStartup=false` in `appsettings.Production.json`. Now schema lands in the
   release step only — **no boot-time migrate, no multi-instance race.**

### Phase 4 — retire the phantom reconciler (only after Phase 3 proven in prod)
- Once a release-step migrate has run cleanly against prod and history is confirmed to hold all 5 IDs,
  delete `PhantomMigrations()` (`Program.cs:825-842`), `ReconcilePhantomMigrationsAsync`
  (`Program.cs:844-925`), and the call site (`Program.cs:742-754`). It is a one-time patch, not durable
  logic. **No schema impact** — history is already correct.

### Verification gate (must do before shipping any of the above)
- Run `dotnet ef migrations has-pending-model-changes` (NOT done here — no build allowed). Must be clean.
  If it fires, the no-op-migration pattern (an empty `Up` whose `.Designer.cs` captures the model delta)
  is the established remedy — see the two existing no-ops in A.2.

### Reversibility
- Phases 1–3 are additive + flag-gated; each is revertible by flipping config back. The Railway
  `preDeployCommand` is removable in one line. Phase 4 is the only destructive step and is explicitly
  gated on a verified-clean prod history.

---

# Part B — Branch audit (read-only git)

**Reference:** `origin/main` = `f018d77` ("fix(csv): locale-aware decimal parsing…", 2026-06-08 12:10).
`wave-d` is at the **same** SHA as `origin/main` (it is the active integration worktree checkout, not a
divergent branch). **Local `main` = `981adba` is 10 commits BEHIND `origin/main`** and fully contained
in it (no commits in local-main-not-in-origin) → safe fast-forward.

Commands used (read-only): `git branch -a`, `git for-each-ref --sort=-committerdate`,
`git branch --merged origin/main`, `git rev-list --count`, `git cherry origin/main <branch>`,
`git merge-base --is-ancestor`, `git worktree list --porcelain`.

## B.1 Branch table

| Branch | Last commit (date / SHA) | Merged into origin/main? | Ahead | Recommendation | Reason |
|---|---|---|---|---|---|
| `main` (local) | 2026-06-08 / `981adba` | n/a (it IS main) | 0 (10 **behind**) | **KEEP — fast-forward to origin/main** | Stale local checkout; fully contained in origin. `git pull --ff-only` / `git merge --ff-only origin/main`. |
| `wave-d` | 2026-06-08 / `f018d77` | = origin/main | 0 | **KEEP (active worktree)** | Identical SHA to origin/main; it is the `ProcuLink-waved` checkout, not divergent. Do not delete a checked-out branch. |
| `feat/schema-fingerprint` | 2026-05-30 / `2452879` | **NO** (`cherry +`) | 1 | **KEEP — decide: merge or shelve** | The ONLY branch with genuinely unmerged content ("supplier PO schema-fingerprint field-mapping moat"). Matches the schema-fingerprint moat in MEMORY. Local == `origin/feat/schema-fingerprint`. Must NOT be swept. |
| `origin/feat/schema-fingerprint` | 2026-05-30 / `2452879` | **NO** (`cherry +`) | 1 | **KEEP** (until the merge/shelve decision) | Remote twin of the only unmerged content. |
| `feat/delivery-channels-group-n` | 2026-05-30 / `840a75d` | **YES** (`cherry -` = equivalent in main) | 1* | **DELETE** | "1 ahead" is illusory: `git cherry` shows the patch already merged into main. Safe `git branch -D` (–d may refuse). |
| `origin/feat/delivery-channels-group-n` | 2026-05-30 / `840a75d` | YES (`cherry -`) | 1* | **DELETE** | Remote twin; `git push origin --delete`. |
| `auto/stress-suite` | 2026-06-08 / `102b582` | **YES** (ancestor of origin/main) | 0 | **DELETE — but worktree first** | Fully merged via wave-d. **Checked out in `.claude/worktrees/wf_d5787e1d-923-1`** → `git worktree remove` before `git branch -d`. |
| `auto/sso-saml` | 2026-06-08 / `4efb569` | YES (ancestor) | 0 | **DELETE — worktree first** | Merged via wave-d. Checked out in `.claude/worktrees/wf_d5787e1d-923-2`. |
| `auto/catalog-p2` | 2026-06-08 / `53e8d85` | YES (ancestor) | 0 | **DELETE — worktree first** | Merged via wave-d. Checked out in `.claude/worktrees/wf_61315c6b-9d2-1`. |
| `auto/typed-dto` | 2026-06-08 / `a3dfd07` | YES (ancestor) | 0 | **DELETE — worktree first** | Merged via wave-d. Checked out in `.claude/worktrees/wf_61315c6b-9d2-2`. |
| `auto/peppol-track-a` | 2026-06-08 / `1cffe94` | YES (ancestor) | 0 | **DELETE — worktree first** | Merged via wave-d. Checked out in `.claude/worktrees/wf_61315c6b-9d2-3`. |
| `worktree-wf_d5787e1d-923-1` | 2026-06-08 / `ec18f53` | YES (ancestor) | 0 (4 behind) | **DELETE** | Stale local ref at a wave-d merge commit; fully in origin/main. Not a live worktree checkout (the worktree holds `auto/*`). |
| `worktree-wf_d5787e1d-923-2` | 2026-06-08 / `ec18f53` | YES (ancestor) | 0 (4 behind) | **DELETE** | Same as above. |
| `worktree-wf_61315c6b-9d2-1` | 2026-06-08 / `501a476` | YES (ancestor) | 0 (11 behind) | **DELETE** | Stale local ref at a wave-d merge commit; fully in origin/main. |
| `worktree-wf_61315c6b-9d2-2` | 2026-06-08 / `501a476` | YES (ancestor) | 0 (11 behind) | **DELETE** | Same as above. |
| `worktree-wf_61315c6b-9d2-3` | 2026-06-08 / `501a476` | YES (ancestor) | 0 (11 behind) | **DELETE** | Same as above. |

\* "1 ahead" per `rev-list` but the commit is an *equivalent patch* already in main (`git cherry` `-`).

## B.2 Critical safety caveat — live worktrees

`git worktree list --porcelain` shows the 5 `auto/*` branches are **each checked out in a live worktree**
under `.claude/worktrees/`:

```
.claude/worktrees/wf_61315c6b-9d2-1  → auto/catalog-p2
.claude/worktrees/wf_61315c6b-9d2-2  → auto/typed-dto
.claude/worktrees/wf_61315c6b-9d2-3  → auto/peppol-track-a
.claude/worktrees/wf_d5787e1d-923-1  → auto/stress-suite
.claude/worktrees/wf_d5787e1d-923-2  → auto/sso-saml
```

Per the project memory ("Concurrent chips in shared dir" / "Chip collision 2026-05-29"), these worktrees
are very likely owned by **concurrent agents still running**. Implications:
- `git branch -d auto/*` will **fail** ("checked out at …") while the worktree exists.
- `git worktree remove` / `git branch -D` on a branch an active agent is mid-edit on can **destroy
  in-flight work** and corrupt that agent's session.
- **Therefore: do NOT clean up the `auto/*` branches or their worktrees until you have confirmed those
  agents have finished and their work is merged.** This is a coordination gate, not a git mechanic.

The bare `worktree-wf_*` *branch refs* (B.1) are different SHAs from the `auto/*` tips and are NOT the
checked-out branch of any worktree — they are stale leftover refs and are safe to `git branch -D` once
confirmed merged (they all are: ancestors of origin/main).

## B.3 Recommended branch-cleanup sequence (design only — do NOT execute here)

1. **Coordination gate:** confirm the 5 concurrent agents on the `auto/*` worktrees are done and their
   content is in `origin/main` (already true by ancestry — but the *agents/working trees* may still be live).
2. **Fast-forward local main:** `git switch main && git merge --ff-only origin/main` (resolves the
   10-commit lag; zero risk, no merge commit).
3. **Decision on `feat/schema-fingerprint`** (the only unmerged content): either merge it into main now
   (it is the schema-fingerprint moat) or KEEP the branch + add a tracking note. **Do not delete it.**
4. **Delete fully-merged stale local refs:** `worktree-wf_61315c6b-9d2-{1,2,3}`,
   `worktree-wf_d5787e1d-923-{1,2}`, `feat/delivery-channels-group-n` (use `-D`; `-d` may refuse the
   `cherry -` ones).
5. **Remove the `auto/*` worktrees ONLY after the gate in step 1:** `git worktree prune` then
   `git worktree remove <path>` per worktree, then `git branch -d auto/*`.
6. **Delete merged remotes:** `git push origin --delete feat/delivery-channels-group-n`. Hold
   `origin/feat/schema-fingerprint` pending step 3.

---

## Risks / things that can't land green blindly

- **Cannot prove zero snapshot drift without a build.** Entity-level check passed (snapshot has
  `supplier_products`), but only `dotnet ef migrations has-pending-model-changes` is authoritative — run
  it before touching the migration mechanism.
- **`--migrate-only` + `ApplyOnStartup=false` cutover is prod-state-dependent:** it is only safe AFTER a
  release-step migrate has been observed to leave `__EFMigrationsHistory` complete in prod. Flipping
  `ApplyOnStartup=false` before that risks a deploy with stale schema and no migrate. Keep default-on
  until verified.
- **Railway `preDeployCommand` semantics must be confirmed against the actual Railway project config**
  (whether it runs on the new image pre-traffic, and whether non-zero aborts the rollout). Do not wire it
  blind to prod.
- **Branch deletion is destructive and the `auto/*` branches are live worktrees** owned by likely-running
  concurrent agents — premature `worktree remove` / `branch -D` can lose uncommitted work. Coordination
  gate is mandatory.
- **Local `main` being 10 behind** means any cleanup run from this checkout should fast-forward first to
  avoid acting on a stale graph.

## Verification commands (read-only; run before any change)

- `dotnet ef migrations has-pending-model-changes -p ProcuLink.Infrastructure -s ProcuLink.Api` (drift gate)
- `git cherry origin/main feat/schema-fingerprint` (expect `+` = unmerged)
- `git worktree list --porcelain` (confirm which agents/worktrees are still live)
- `git merge-base --is-ancestor <branch> origin/main && echo merged` (per branch before deleting)
