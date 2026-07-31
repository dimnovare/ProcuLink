# Monitoring runbook

**What this covers:** the two scheduled GitHub Actions workflows that watch production,
what each one actually checks, where their alerts land today, how to wire them somewhere
a human will see them, and what to do when each one goes red.

**Written 2026-07-25.** `.github/workflows/uptime.yml` has cited this path since it was
added (header comment + the `Worker unhealthy` failure annotation) — the file simply had
never been written. This is that file.

> **Scope note.** This is the *monitoring* runbook only. `ProcuLink.Api/Program.cs:80`
> points at a **Stripe go-live runbook** in this same directory (`docs/deployment`).
> That document does not exist either and is *not* written here — inventing Stripe
> go-live steps from the code would be worse than a dangling pointer. Treat that
> reference as still-dangling.

---

## 0. Read this first: nothing here pages anyone

Both workflows signal failure the same way — **the GitHub Actions run goes red**. That is
the entire delivery mechanism. There is no PagerDuty, no SMS, no on-call rotation, and by
default no Slack message. **The alert is exactly as good as this repo's GitHub Actions
notification settings**, which are account-level and are not stored in the repo, so this
document cannot tell you whether they are currently on. Verify them (§4) — do not assume.

There is a **second, independent** alert path for the Worker-down condition only — a
recurring Hangfire job that reports to Sentry (§3). It has its own blind spot, described
there. The two paths are complementary, not redundant, and neither is a pager.

---

## 1. `uptime.yml` — production liveness probe

| | |
|---|---|
| File | [`.github/workflows/uptime.yml`](../../.github/workflows/uptime.yml) |
| Schedule | every 10 minutes (`*/10 * * * *`), plus `workflow_dispatch` |
| Runs against | production, from a GitHub-hosted runner (genuinely external — not from Railway) |
| Concurrency | `group: uptime`, no cancel — a slow probe never overlaps the next tick |
| Cost | ~2 runner-minutes/hour; comfortably inside the free budget |

### What it checks

Two steps, either of which fails the job:

**1. API readiness — `GET https://api.proculink.eu/health/ready`**
(overridable via repo variable `API_READY_URL`)

Fails on:
- curl failure / timeout (25 s) → `::error title=API unreachable::`
- any status other than 200 → `::error title=API readiness not 200::`
- a 200 body whose `.workerHealthy` is not `true` → `::error title=Worker unhealthy::`
- a 200 body whose `.revisionAuthority` is not `true` → `::error title=Revision authority is OFF::`
  (WP-21 — see [`revision-authority-production-smoke.md`](../ops/revision-authority-production-smoke.md))

That third condition is the point of the whole workflow. `/health/ready` runs the
`ready`-tagged health checks (database, storage, migration flag, Worker heartbeat, and the
revision-authority flag's effective value) and maps
**Healthy *and* Degraded → HTTP 200**, Unhealthy → 503. A dead Worker is deliberately
**Degraded, not Unhealthy** — see `WorkerHeartbeatHealthCheck` in
`ProcuLink.Api/Controllers/HealthController.cs`: a Worker outage must not evict the API from
rotation, because the API still serves the dashboard, reads and billing. So a stopped Worker
leaves the API answering a cheerful HTTP 200 while uploads silently never parse, transform or
deliver. A plain HTTP-200 uptime checker would see nothing wrong. This workflow additionally
parses the flattened `workerHealthy` boolean that `HealthResponseWriter` emits for exactly
this purpose.

**2. Marketing site — `GET https://proculink.eu`** (overridable via `SITE_URL`)

2xx/3xx pass (Vercel legitimately 30x's apex→www); **≥ 400 fails**.

### Where `workerHealthy` comes from

No custom heartbeat table. Hangfire already writes a per-server heartbeat (~30 s interval)
into the **shared** Postgres storage; the API shares that storage and reads the most recent
server heartbeat through `IMonitoringApi`. `workerHealthy` is false when **no Hangfire server
is registered at all**, or when the newest heartbeat is **older than 60 s**
(`WorkerHeartbeatHealthCheck.HeartbeatDeadlineSeconds`, one missed 30 s beat of leeway). That
deadline is kept in lock-step with `OpsHealthService.WorkerHeartbeatDeadline` so the health
endpoint and the in-app ops screens never disagree.

### Tuning it

Hostnames come from repo **Variables** (Settings → Secrets and variables → Actions →
Variables), defaulting to the prod surfaces: `API_READY_URL`, `SITE_URL`. Nothing secret is
involved — the readiness body is counts, ages and booleans only, never credentials.

---

## 2. `postmark-ip-drift.yml` — inbound-mail allowlist drift

> **Status as of 2026-07-25: not on `main`.** This workflow lives in **open PR
> [#58](https://github.com/dimnovare/ProcuLink/pull/58)** (`fix(inbound-email): detect
> Postmark webhook IP drift before mail queues`). Everything below describes the PR's
> content; it is not yet running on a schedule. If PR #58 has since merged, delete this
> banner.

| | |
|---|---|
| Schedule | weekly, Mondays 06:00 UTC (`0 6 * * 1`), plus `workflow_dispatch` |
| Also runs | on PRs touching `docs/infra/postmark-inbound-verify-worker/**` — **tests only** |

Two jobs:

- **`worker-tests`** — `node --test` in `docs/infra/postmark-inbound-verify-worker`. Offline
  and deterministic, so it is safe to gate PRs on.
- **`drift`** — runs `check-postmark-ips.mjs`, which fetches Postmark's published webhook
  source IPs and compares them against the hardcoded `POSTMARK_WEBHOOK_SOURCES` allowlist in
  `docs/infra/postmark-inbound-verify-worker/worker.js`. **Deliberately not wired to
  `pull_request`** — a PR must never go red because a third-party support page had a bad
  minute.

The checker exits non-zero on drift **and on every "cannot tell" outcome** (fetch failure,
renamed Webhooks section, unparsable entry, unreadable allowlist). A silent pass after a page
redesign is the exact failure it exists to prevent.

Its own runbook is
[`docs/infra/postmark-inbound-verify-worker/README.md`](../infra/postmark-inbound-verify-worker/README.md).
The one thing worth repeating here: **the Cloudflare Worker is deployed by hand.** Cloudflare
has no CI hook to this repo, so editing `worker.js` and merging changes *nothing* in
production until someone redeploys it.

---

## 3. The second alert path: `WorkerHealthAlertJob` → Sentry

Independent of GitHub Actions, the Worker runs `worker-health-alert` **every 5 minutes**
(`ProcuLink.Worker/Worker.cs`). It calls `WorkerHealthAlertService`, which alerts when
**either**:

- no healthy Worker is beating (same 60 s freshness rule as `/health/ready`), **or**
- all-org dead-letter + failed-delivery orders ≥ **25**
  (`WorkerHealthAlertOptions.DeadLetterThreshold`, configurable under `WorkerHealthAlert`).

While the condition persists it re-alerts at most every **30 minutes**
(`MinAlertIntervalMinutes`) so a long outage does not spam.

The sink is `SentryWorkerAlertSink`. **Two blind spots, both load-bearing:**

1. **No DSN → no alert, silently.** Sentry initialises *disabled* when `Sentry:Dsn` is empty,
   and the sink becomes a no-op. The checked-in `ProcuLink.Worker/appsettings.Production.json`
   ships `"Dsn": ""` — the live value, if any, is a Railway variable (`Sentry__Dsn`) on the
   `aware-amazement` service. **Verify it in the Railway dashboard before counting Sentry as
   an alert path.** If it is unset, GitHub Actions is the *only* thing watching production.
2. **The job runs *on* the Worker.** If the Worker is dead, the job that would report the
   Worker dead is also dead. This path detects a *backlog spike* well and a *hung* Worker
   sometimes; it cannot detect a *stopped* one. That asymmetry is precisely why `uptime.yml`
   probes from outside.

Also on the Worker: `worker-heartbeat` every 2 minutes writes a `WORKER-HEARTBEAT` log line
(and a Sentry breadcrumb) proving the recurring-job **dispatcher** is firing, not merely that
the Hangfire server thread is alive. Grepping the Railway logs for `WORKER-HEARTBEAT` is the
fastest liveness check there is.

---

## 4. Where failure notifications go today — and how to wire them properly

### Today (default)

A failed run shows up in the repo's **Actions** tab. Whether anyone is *told* depends on
per-account GitHub notification settings that live outside this repo. Two behaviours matter:

- GitHub notifies you about workflow runs **you triggered**.
- For **scheduled** (`cron`) runs there is no human trigger, so GitHub attributes the run to
  the **user who last modified the workflow file** — that account is the one that gets the
  failure notification. Nobody else is told by default.

Consequences worth internalising: if the last person to touch `uptime.yml` has Actions email
notifications off, **a 10-minute production probe has been failing into the void**. And if
someone else edits the workflow later, the notification quietly follows *them*.

### Verify the current state (do this once, now)

1. <https://github.com/settings/notifications> → **Actions** section → enable **Email**
   (and/or Web), and tick **"Send notifications for failed workflows only"** — with a
   10-minute cron you do not want the successes.
2. Watch the repo: <https://github.com/dimnovare/ProcuLink> → **Watch** → **Custom** →
   tick **Actions**.
3. Prove it end-to-end rather than trusting the checkboxes: Actions tab → **Uptime monitor**
   → **Run workflow**, against a deliberately bad `API_READY_URL` repo variable (e.g.
   `https://api.proculink.eu/health/ready-nope`), confirm the mail actually arrives, then put
   the variable back. Test the alarm, not the wiring diagram.

### Wire it to Slack (recommended — survives a change of workflow author)

Install the GitHub app in the Slack workspace, then in the target channel:

```
/github subscribe dimnovare/ProcuLink workflows:{name:"Uptime monitor" event:"schedule"}
```

Add the second workflow once PR #58 lands:

```
/github subscribe dimnovare/ProcuLink workflows:{name:"Postmark webhook IP drift"}
```

A channel subscription is attributed to the *repo*, not to whoever last committed the cron
file, which removes the failure mode above. Verify with a manual `workflow_dispatch` run —
same rule: prove the message lands.

### If you want a real pager

Nothing in this repo provides one. The two realistic upgrades, cheapest first:

- **Sentry alert rules** — if the Worker DSN is set (§3), Sentry can already email/Slack/page
  on the `ProcuLink Worker health degraded` event. This costs no new infrastructure.
- **An external uptime SaaS** (Better Stack, Healthchecks.io, UptimeRobot) pointed at
  `https://api.proculink.eu/health/ready`. Note the same trap this workflow exists to dodge:
  a plain 200-check will **not** catch a dead Worker. Configure a keyword/JSON assertion on
  `"workerHealthy":true`, or the monitor is decorative.

---

## 5. Triage

### `Worker unhealthy` — `workerHealthy=false`

Meaning: background jobs (parse / transform / deliver) may not be running. Customer uploads
land and then sit. The API is *fine*, which is why this is easy to miss.

1. **Confirm and get detail:**
   ```bash
   curl -sS https://api.proculink.eu/health/ready | jq .
   ```
   The `worker` check's data bag carries `activeWorkers`, `secondsSinceWorkerHeartbeat` and
   `heartbeatDeadlineSeconds`. `activeWorkers: 0` = nothing registered (process down/crashed).
   A non-zero count with a stale age = registered but hung or restarting.
2. **Railway Worker logs** — service `aware-amazement`. Look for a crash loop, an OOM, or a
   failed start. `WORKER-HEARTBEAT` every ~2 min means the dispatcher is alive; its absence
   with a live process means jobs are wedged, not that the container is gone.
3. **The `hangfire.*` tables** in Neon Postgres (schema `hangfire`) — the storage both
   processes share:
   - `hangfire.server` — one row per live server, with its `lastheartbeat`. **Empty, or every
     row stale, is the machine-readable form of this alert.** A stale row from a container
     that died without deregistering is normal; Hangfire reaps it.
   - `hangfire.job` — `statename` tells you whether work is `Enqueued` (piling up, nothing
     draining — the classic signature), `Processing`, `Succeeded` or `Failed`.
   - `hangfire.jobqueue` — depth per queue. The Worker consumes, in priority order:
     `critical`, `delivery-retry`, `polling`, `background`, `default`.
4. **Usual causes, in rough order:** the Railway Worker service stopped or is crash-looping;
   a bad deploy; database connection exhaustion (the Worker's Npgsql pool is capped at 20,
   the API's at 30, against Neon's ~100 ceiling); Neon itself unavailable — in which case the
   API readiness check would normally be failing too, so check *which* alert fired.
5. **Recovery:** restart the Worker service in Railway. Enqueued jobs resume — Hangfire jobs
   in this codebase are required to be idempotent, and delivery is at-least-once with a claim
   guard, so a restart does not double-send. Once it is beating, watch the backlog drain in
   `/api/ops/health` (below) rather than declaring victory off a green probe.

**Operator surface:** `GET /api/ops/health` (authenticated, org-scoped) returns the
problem-state counts an operator actually wants — `parsingStuck`, `deliveringStuck`,
`transformFailed`, `deliveryFailed`, `deliveryDeadLetter`, `slaBreached`, `openExceptions`,
plus `activeWorkers` / `secondsSinceWorkerHeartbeat` / `workerHealthy`.
`GET /api/ops/dead-letter` lists the dead-letter queue with each order's last delivery error.
The Hangfire dashboard at `/hangfire` is **local dev only** — it is not exposed in production.

### `API readiness not 200` / `API unreachable`

503 means a `ready`-tagged check reported **Unhealthy** — database, storage (R2) or the
migration-readiness flag. The JSON body names which. On boot the migration flag starts
`Pending` and flips to `Succeeded`; a failed migration marks readiness Unhealthy *and* reports
to Sentry, so a 503 right after a deploy is usually a migration, not an outage. `curl failed`
with no status is DNS / TLS / Railway edge — check the Railway service and status pages
before touching code.

### `Site not healthy` / `Site unreachable`

Vercel. Check the deployment status and the domain configuration for `proculink.eu`. 3xx is
*not* a failure here — only ≥ 400.

### Postmark IP drift (once PR #58 lands)

The annotation names the exact IPs and the fix. Two things not to forget: mail is **not** lost
during drift (the Cloudflare Worker refuses unknown sources with 503, Postmark retries for
~10.5 h, then each message parks in `Failed`, re-fireable for 45 days — PR #57), and the fix
requires a **hand redeploy** of the Cloudflare Worker. Merging the allowlist change ships
nothing.

---

## 6. What is *not* monitored

Stated plainly so nobody mistakes a green Actions tab for coverage:

- **No paging.** Failures produce a notification at best (§4). Overnight, expect hours of lag.
- **Detection lag is real.** Up to 10 min for uptime, up to 7 days for Postmark IP drift (a
  deliberate trade — the recovery window is far longer than the lag).
- **Per-org symptoms are invisible here.** One supplier's endpoint refusing deliveries, one
  org's AI budget latched, one connection's mapping silently producing empty fields — none of
  these move `workerHealthy`. They surface in `/api/ops/health` and the in-app exceptions,
  which nothing polls externally.
- **The dead-letter threshold is all-org and absolute** (25). A small org drowning while a
  large one is healthy will not trip it.
- **No synthetic end-to-end transaction.** Nothing uploads a test order every N minutes and
  asserts it was delivered. `workerHealthy` proves the Worker is *beating*, not that the
  pipeline is *correct*.
- **Cloudflare, Postmark, Neon, Clerk and Stripe are unmonitored** by this repo beyond the
  side effects that surface in the two probes above.
