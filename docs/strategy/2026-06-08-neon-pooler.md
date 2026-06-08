# Neon pooled endpoint — design + analysis (DESIGN ONLY)

**Date:** 2026-06-08
**Status:** Design proposal. No code/config changed. Nothing applied to prod.
**Author:** infra track (Neon pooled endpoint)
**Scope:** Move the **API** to Neon's **pooled** endpoint (PgBouncer, transaction mode) while keeping the **Worker/Hangfire** on the **direct** endpoint.
**Change class:** Config-only (Railway env vars). No source changes required for the recommended split. Reversible by reverting one env var.

---

## TL;DR / recommendation

| Process | Endpoint | Why |
|---|---|---|
| **ProcuLink.Api** (`lucid-generosity` / `ProcuLink`) | **Pooled** (`...-pooler...`, PgBouncer transaction mode) | App DbContext queries are transaction-mode-safe (no Postgres enums/composites, no session state, no advisory locks, no `LISTEN/NOTIFY`). The API only *enqueues* Hangfire jobs + reads `IMonitoringApi` snapshots — these are short transactions, also safe. Pooling absorbs the API's bursty HTTP concurrency without pinning Neon's connection ceiling. |
| **ProcuLink.Worker** (`aware-amazement`) | **Direct** (unpooled) | Hangfire.PostgreSql 1.20.10 holds **session-level `pg_advisory_lock`** for distributed fetch/queue coordination. Session-level advisory locks are acquired on one connection and released later on (what PgBouncer treats as) a *different* connection in transaction mode → locks leak / never release / wrong-session errors. Hangfire is **not** safe behind a transaction-mode pooler. It must use a direct, sticky, session-stable connection. |

**One subtlety (must-fix before flipping the API):** the API process *also* opens the Hangfire storage connection (`UsePostgreSqlStorage`, `Program.cs:270`) **and** runs `MigrateAsync` + a raw-ADO phantom-migration reconciler on boot (`Program.cs:761`, `:844-925`). Migrations and (to a lesser degree) the API's Hangfire storage client are **not** ideal on a transaction-mode pooler. The clean design is to give the API **two** connection strings — pooled for EF app queries, direct for Hangfire storage + the boot migration — rather than a single shared string. See §5 (Option B, recommended) vs §6 (Option A, simpler but with caveats).

---

## 1. How the connection string is read and used today

Single key, `ConnectionStrings:DefaultConnection`, consumed by every DB consumer in both processes. Prod value is injected by Railway as env var **`ConnectionStrings__DefaultConnection`** on **both** services (`docs/deployment/proculink-eu-cutover.md:15`).

### 1.1 API (`ProcuLink.Api/Program.cs`)

- **EF DbContext** — `Program.cs:72-79`:
  ```csharp
  builder.Services.AddDbContext<ProcuLinkDbContext>(options =>
      options.UseNpgsql(BuildPooledConnectionString(
          builder.Configuration.GetConnectionString("DefaultConnection"), maxPoolSize: 30)));
  ```
  `BuildPooledConnectionString` (`:81-99`) sets `MaxPoolSize=30`, `ConnectionIdleLifetime=60`, `ConnectionPruningInterval=10` (a *client-side* Npgsql pool, separate from PgBouncer; both can coexist — see §4.3).
- **Hangfire storage (enqueue + monitoring only)** — `Program.cs:265-270`:
  ```csharp
  var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
  builder.Services.AddHangfire(cfg => cfg
      .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
      .UseSimpleAssemblyNameTypeSerializer()
      .UseRecommendedSerializerSettings()
      .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
  // No AddHangfireServer here — the Worker process is the sole Hangfire executor.
  ```
  The API has **no `AddHangfireServer`** (confirmed `:271-273`). It only enqueues and exposes `IMonitoringApi` via `JobStorage.GetMonitoringApi()` (`:278-279`).
- **Boot migration** — `app.Lifetime.ApplicationStarted` runs `db.Database.MigrateAsync()` with a 6-attempt backoff (`:723-805`) plus a raw-ADO **phantom-migration reconciler** that opens `db.Database.GetDbConnection()` and runs hand-written `information_schema` queries + `INSERT INTO __EFMigrationsHistory` (`:844-925`). This is multi-statement work that *assumes a stable session* across the reconcile (it opens the connection at `:846-848` and runs several commands on it).

### 1.2 Worker (`ProcuLink.Worker/Program.cs`)

- **EF DbContext** — `Program.cs:121-122`, using the same string after `BuildPooledConnectionString(..., maxPoolSize: 20)` (`:105`).
- **Hangfire storage + server** — `:124-136`:
  ```csharp
  builder.Services.AddHangfire(cfg => cfg
      ...
      .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
  builder.Services.AddHangfireServer(opts =>
  {
      opts.WorkerCount = 10;
      opts.Queues = new[] { "critical", "delivery-retry", "polling", "background", "default" };
  });
  ```
  The Worker is the **sole Hangfire executor** (10 workers across 5 queues). Recurring jobs registered in `Worker.cs:21-60` (email/sftp/s3 polling every 5 min, stuck-order + SLA sweep every 15 min, health alert every 5 min, retention daily).

### 1.3 What this means

Both processes read **one** key. To split endpoints we change **only the env var value per Railway service** (Worker keeps direct, API gets pooled) — *unless* we also want to protect the API's Hangfire-storage + boot-migration paths, which argues for giving the API a second, direct key (§5).

---

## 2. Hangfire.PostgreSql 1.20.10 — PgBouncer transaction-mode compatibility

**Verdict: NOT compatible with transaction-mode pooling. Keep the Worker (and the API's Hangfire storage path) on a direct/session-stable connection.**

Grounding:
- Version pinned at `Hangfire.PostgreSql 1.20.10` (`ProcuLink.Api.csproj:13`, `ProcuLink.Worker.csproj:16`).
- Configured with **defaults only** — `UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString))` in both processes (`Api/Program.cs:270`, `Worker/Program.cs:128`). No `PostgreSqlStorageOptions` are passed (grep for `UseNativeDatabaseTransaction` / `UseSlidingInvisibilityTimeout` / `PostgreSqlStorageOptions` returns only those two call sites — i.e. all options are defaulted).

Why defaults + transaction-mode pooling break:
1. **Session-level advisory locks.** Hangfire.PostgreSql coordinates job fetch/queue access with `pg_advisory_lock` / `pg_advisory_unlock` (session-scoped, not `*_xact_*`). In PgBouncer **transaction mode**, the backing server connection is returned to the pool at the end of each transaction, so the `unlock` (or a later statement that assumes the lock is still held) can land on a **different** physical server connection than the `lock`. Result: advisory locks are never released (leak) or `unlock` fails ("you don't own this lock"), and job processing stalls or duplicates. This alone disqualifies the **Worker** (the executor).
2. **`UseSlidingInvisibilityTimeout` is NOT enabled here** → Hangfire uses the legacy fetched-job model that relies on holding a transaction/connection open while a job is in flight (long-lived connection state). Transaction-mode pooling cannot hold a connection open across the job lifetime.
3. **Schema bootstrap.** Hangfire runs `PrepareSchemaIfNecessary` at storage init; that is one-shot and tolerable, but it is another reason the API's Hangfire-storage client prefers a stable session at boot.

What about `LISTEN/NOTIFY`? Hangfire.PostgreSql's default fetch is **poll-based** (it does not require `LISTEN/NOTIFY`), so that specific incompatibility is not the blocker here — the **advisory-lock + open-connection-per-job** behaviours are. (Generic-statement: `LISTEN/NOTIFY` is also transaction-mode-incompatible, so if a future option enabled it, it would still require the direct endpoint.)

**Conclusion:** Anything that talks to Hangfire **storage** wants a session-stable connection:
- Worker (executor) → **direct, mandatory.**
- API (enqueue + `IMonitoringApi`) → enqueue/read are short transactions and *mostly* fine on a pooler, but to be safe and avoid surprising `IMonitoringApi` snapshots taken mid-fetch, the **clean** design points the API's Hangfire storage at the direct endpoint too (§5, Option B). Option A (API fully pooled incl. Hangfire storage) is *probably* fine because the API never fetches/locks jobs, only inserts + reads — but it is the residual risk to call out.

---

## 3. Is the EF / app-data path transaction-mode safe? Yes.

Checked the model for the things that break transaction-mode pooling:

| Hazard | Present? | Evidence |
|---|---|---|
| Postgres **enum** / composite types (`HasPostgresEnum` / `MapEnum`) requiring a per-session type cache | **No** | grep for `HasPostgresEnum`/`MapEnum`/`HasPostgresExtension` → no hits. Enums are stored as text/int. |
| **jsonb** mapped via Npgsql dynamic JSON (would need runtime type loading) | **No (sent as text)** | `ProcuLinkDbContext.cs:69-71` maps every `JsonDocument?` column with a `ValueConverter<JsonDocument?, string?>` (round-trips raw text). Columns are declared `jsonb` (`:123`, `:207`, `:262`, …) but the *parameter* is text — no per-session OID lookup needed. |
| Server-side **prepared statements** persisted across the pooled connection (`Max Auto Prepare`) | **No** | grep for `Max Auto Prepare`/`MaxAutoPrepare` → no hits. Npgsql auto-prepare is **off** by default. Safe with PgBouncer (no orphaned prepared statements). |
| Npgsql **multiplexing** | **No** | grep for `Multiplexing` → no hits. |
| Session `SET`/temp tables/`LISTEN`/advisory locks in app code | **No** | grep for `LISTEN`/`NOTIFY`/`pg_advisory` in `*.cs` → no app-code hits. The only raw connection use is the boot phantom-migration reconciler (`Api/Program.cs:846`), which is a migration concern (§5), not a steady-state request path. |
| `EnableRetryOnFailure` (would mask transient pooler resets) | **No** (neither help nor hurt) | grep → no hits. Consider adding it as defence-in-depth (§7), but not required. |

**Therefore the API's EF query path is safe on the pooled endpoint as-is — no source change.** The only API concerns are (a) Hangfire storage and (b) the boot migration, both addressed in §5.

---

## 4. Required Npgsql connection-string params for PgBouncer compatibility

Npgsql 8.0.x is in use (via `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11`, `ProcuLink.Infrastructure.csproj:20`). For the **pooled** (PgBouncer transaction-mode) endpoint:

### 4.1 Mandatory
- **`No Reset On Close=true`** — PgBouncer in transaction mode rejects/derails Npgsql's `DISCARD ALL` reset that Npgsql normally issues when returning a connection to its *client-side* pool. Set this so Npgsql does not send the reset. (Npgsql 7+ recognises this; in 8.0 it is the documented PgBouncer flag.)
- **`Server Compatibility Mode=Redshift`** is **NOT** needed for PgBouncer — do **not** set it (that's for Redshift).

### 4.2 Strongly recommended
- **`Max Auto Prepare=0`** — keep auto-prepare disabled (it already is by default; set explicitly to document intent and prevent a future regression from enabling it under PgBouncer, which would leave orphaned prepared statements on shared server connections).
- **`Maximum Pool Size`** — keep the existing client-side ceiling. `BuildPooledConnectionString` already imposes API=30. With PgBouncer fronting Neon you can keep 30 (PgBouncer multiplexes 30 client conns onto far fewer server conns), or raise it later if the API needs more in-flight concurrency. Do not raise above what the Neon plan / PgBouncer `default_pool_size` allows.

### 4.3 Leave as-is / note
- The **client-side Npgsql pool still operates** in front of PgBouncer (two pools stacked). That's fine and normal. Keep `ConnectionIdleLifetime=60` + `ConnectionPruningInterval=10` (already set, `Api/Program.cs:96-97`) so idle client conns are released to PgBouncer.
- **SSL:** Neon requires TLS. Whatever the current string uses (Neon's pooled host typically `sslmode=require`) — keep it. The pooled host is a **different hostname** (`...-pooler...`), so the cert SAN matches; no `Trust Server Certificate` needed.
- **`Multiplexing`** — do **not** enable. Npgsql multiplexing on top of PgBouncer transaction mode is redundant and can interact badly; leave off.

### 4.4 Direct endpoint (Worker + API migration/Hangfire path)
- **No special params** — the direct endpoint is a normal Postgres session. Do **not** add `No Reset On Close` there (you *want* Npgsql's reset on a direct connection). Keep the existing string shape (just swap the host to the **non**-pooler host).

---

## 5. Recommended design — Option B (two strings on the API): pooled EF + direct Hangfire/migration

This is the clean, low-risk target. It keeps every session-stateful concern (Hangfire storage, boot migration) on a direct connection and routes only the high-volume EF query traffic through the pooler.

**Requires a small, additive, reversible source change** (so it does **not** land as part of this design — flagged for a follow-up implementation chip): introduce a second connection-string key, e.g. `ConnectionStrings:HangfireConnection` (falls back to `DefaultConnection` when unset, so existing dev/test/Worker behaviour is byte-identical).

Sketch (NOT applied — design only):
- `Api/Program.cs`: EF `AddDbContext` reads `DefaultConnection` (→ pooled in prod). Hangfire `UsePostgreSqlStorage` and the boot `MigrateAsync`/reconciler read `HangfireConnection ?? DefaultConnection` (→ direct in prod).
- `Worker/Program.cs`: unchanged — both EF and Hangfire read `DefaultConnection` (→ direct in prod).

Then in prod:

```
# API service (Railway: ProcuLink) — EF goes pooled, Hangfire+migration stay direct
ConnectionStrings__DefaultConnection   = <NEON POOLED string>   (host: ...-pooler..., add: No Reset On Close=true;Max Auto Prepare=0)
ConnectionStrings__HangfireConnection  = <NEON DIRECT string>   (host: non-pooler, normal session)

# Worker service (Railway: aware-amazement) — everything direct (UNCHANGED)
ConnectionStrings__DefaultConnection   = <NEON DIRECT string>   (host: non-pooler)
```

**This is the recommended end state**, but because it needs a (tiny) code change it is a separate implementation step. It is fully reversible (unset `HangfireConnection` → falls back to pooled `DefaultConnection`, i.e. Option A).

---

## 6. Option A — config-only (no code): API fully pooled, Worker direct

If you want a **zero-code** first step (and accept the residual risk in §2 point about the API's Hangfire storage + boot migration running on the pooler):

```
# API service (Railway: ProcuLink)
ConnectionStrings__DefaultConnection = <NEON POOLED string>
   host:  ep-xxxx-pooler.<region>.aws.neon.tech   (note the "-pooler")
   plus:  No Reset On Close=true;Max Auto Prepare=0
   keep:  the rest of the existing string (Username/Password/Database/sslmode=require)

# Worker service (Railway: aware-amazement) — UNCHANGED (direct)
ConnectionStrings__DefaultConnection = <NEON DIRECT string>   (current value, ensure host has NO "-pooler")
```

**Residual risks of Option A (must accept explicitly):**
1. **Boot migration on the pooler.** `MigrateAsync` + the raw-ADO phantom reconciler (`Api/Program.cs:761`, `:846-925`) run multi-statement work on a connection that, under transaction mode, may not be the same server session between statements. The reconciler opens the connection and runs a sequence of `information_schema` reads + an `INSERT` (`:852-924`) — these are individual commands (each its own implicit transaction), so they *function*, but advisory-lock-free DDL via `MigrateAsync` is the part to watch. **Mitigation if staying on Option A:** Neon's pooler tolerates DDL per-statement, and EF wraps each migration in its own transaction, so it generally works — but this is exactly why Option B (direct for migrations) is cleaner. Verify on a Neon branch first (§9).
2. **API Hangfire storage on the pooler.** Enqueue (`INSERT`) and `IMonitoringApi` (`SELECT` snapshot) are short and safe; the API never fetches/locks jobs, so the advisory-lock hazard does **not** apply to the API. This is low risk.

**Recommendation:** Option A is acceptable as a fast first move **only if** the migration step is verified green on a Neon branch (it usually is). Otherwise go straight to Option B.

---

## 7. Optional hardening (either option)
- Add `EnableRetryOnFailure()` to the API's `UseNpgsql` so a transient PgBouncer reset (e.g. server-conn recycled) retries instead of surfacing a 500. Currently absent (grep). Additive, low-risk, but technically a code change — defer to the same chip as Option B.
- Keep an eye on PgBouncer `server_idle_timeout` vs Npgsql `ConnectionIdleLifetime=60` — they are independent; no change needed.

---

## 8. Exact change list (copy-paste, DO NOT APPLY)

### Step 0 — capture current values (rollback safety)
```
railway variables --service ProcuLink         # note current ConnectionStrings__DefaultConnection
railway variables --service aware-amazement    # note current ConnectionStrings__DefaultConnection
```
Save both raw strings somewhere safe before changing anything.

### Step 1 — get the two Neon hostnames
In the Neon console, copy:
- **Pooled** connection string — host contains **`-pooler`** (e.g. `ep-cool-name-12345678-pooler.eu-central-1.aws.neon.tech`).
- **Direct** connection string — same host **without** `-pooler`.
Both share the same Username/Password/Database/`sslmode=require`.

### Step 2 — API (Railway service `ProcuLink`) → POOLED (Option A; config-only)
Set `ConnectionStrings__DefaultConnection` to the **pooled** string, with the PgBouncer flags appended:
```
Host=ep-...-pooler.<region>.aws.neon.tech;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Require;No Reset On Close=true;Max Auto Prepare=0
```
(Keep `Maximum Pool Size` unset in the env string — `BuildPooledConnectionString` injects 30 in code.)

### Step 3 — Worker (Railway service `aware-amazement`) → DIRECT (verify, do not change if already direct)
Ensure `ConnectionStrings__DefaultConnection` host has **NO** `-pooler`:
```
Host=ep-...<region>.aws.neon.tech;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Require
```
**Do NOT** add `No Reset On Close` here.

### Step 4 (Option B only — after the follow-up code change lands) — API gets a second, direct key
```
# API service (ProcuLink) — ADD:
ConnectionStrings__HangfireConnection = Host=ep-...<region>.aws.neon.tech;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Require
# (DefaultConnection stays the pooled string from Step 2)
```

> All of the above are **env-var edits in the Railway dashboard / CLI**. No build, no migration, no prod-data touch. Restart/redeploy each service after editing so it re-reads the env.

---

## 9. Verification checklist (run on a Neon branch first, then prod off-peak)

**Pre-flight (Neon branch — safe, throwaway):**
- [ ] Create a Neon branch of prod. Point a local API + Worker at the branch's **pooled** (API) and **direct** (Worker) endpoints.
- [ ] Boot the API → confirm `MigrateAsync` completes (logs `Database migrations applied`) and `/health/ready` is **Healthy** (DB check = `DatabaseHealthCheck`, `Api/Program.cs:559-562`). This proves the migration path survives the pooler (Option A) or the direct Hangfire key (Option B).
- [ ] Run the full upload→parse→transform→deliver golden path against the branch (API on pooled). Confirm orders parse and reach a terminal state — proves Worker (direct) ↔ API (pooled) job round-trip works across the split.

**Prod cutover:**
- [ ] Apply Step 2 (API → pooled). Redeploy `ProcuLink`. Watch logs: no `DISCARD ALL`/reset errors, no `prepared statement "..." does not exist`, no advisory-lock errors.
- [ ] `GET https://api.proculink.eu/health/ready` → 200 Healthy.
- [ ] Hit a few authenticated read endpoints (e.g. dashboard/orders list) → 200, data renders. Confirms EF on pooler.
- [ ] Upload one PO → confirm it parses (proves API enqueue on pooled → Worker fetch on direct).
- [ ] In Neon console / `SELECT count(*) FROM pg_stat_activity` → confirm the API's server-connection count is **lower/flatter** than before (PgBouncer multiplexing working) and Worker connections are stable on the direct endpoint.
- [ ] Hangfire dashboard / `IMonitoringApi`-backed `/api/ops/health` → servers heartbeating, no stuck "Processing" jobs (proves advisory-lock coordination is intact on the Worker's direct endpoint).
- [ ] Let the 5-min recurring pollers fire once (email/sftp/s3) → no errors → confirms Worker scheduling unaffected.
- [ ] Monitor Sentry for 30–60 min: no spike in `Npgsql`/`PostgresException`/connection errors.

---

## 10. Rollback plan

**Fully reversible, single env-var revert, ~1 redeploy.**
1. In Railway, set the API service's `ConnectionStrings__DefaultConnection` back to the **direct** string captured in Step 0.
2. (Option B) Remove `ConnectionStrings__HangfireConnection` (the code falls back to `DefaultConnection`, restoring single-direct behaviour).
3. Redeploy `ProcuLink`. The Worker was never changed, so it needs no rollback.
4. Confirm `/health/ready` Healthy + one upload round-trips.

No schema change, no data migration, nothing irreversible. The worst case (pooler misbehaves) is detected within minutes and reverted with one variable.

---

## 11. Why this is safe to land green (and where it isn't)

- **Safe / config-only:** Option A (API pooled, Worker direct) requires **zero source changes** — only Railway env edits — and is fully reversible. The EF app-query path is provably transaction-mode-safe (§3). The Worker stays direct, so Hangfire's advisory-lock coordination is untouched.
- **The one thing that can't land blind:** the API's **boot migration on the pooler** (Option A) and the **API Hangfire storage on the pooler**. These are low-but-nonzero risk and MUST be verified on a Neon branch before prod (§9). The clean fix is Option B (direct Hangfire/migration key), which needs a tiny additive code change and so is a **separate implementation chip**, not part of this design.
- **Respecting the freeze:** CLAUDE.md freezes *features*; this is infra/reliability config, explicitly listed as a launch infra track (`docs/strategy/LAUNCH_EXECUTION_PLAN.md:42`, `:95`; `docs/strategy/WAVE_D_BACKEND_REMAINING.md:93`) and called out as "founder env/Railway changes (connection-string swap), not code." This doc keeps the recommended first step config-only to match that framing.

---

## 12. Open questions for the founder / before cutover
- Which Neon plan, and what is PgBouncer's `default_pool_size` / `max_client_conn` on it? Confirms the API's client-side `Maximum Pool Size=30` won't exceed PgBouncer's server-side budget.
- Acceptable to do the prod cutover off-peak with a Neon-branch rehearsal first? (Strongly recommended — §9.)
- Go with config-only **Option A now** (accept the migration-on-pooler residual risk, verified on a branch) or wait for the small **Option B** code change (direct Hangfire+migration key) for the cleaner end state? Recommendation: rehearse Option A on a branch; if the migration path is clean, ship Option A now and follow with Option B as hardening.
