# ProcuLink — Monitoring & Alerting Runbook

**Audience:** founder (one-time setup) + on-call.
**Created:** 2026-06-13 (`feat/monitoring-alerting`).
**Goal:** *see problems before customers do.* This is a **~20-minute checklist** to
turn on every alert that the code already emits. The code is shipped; the alert
*rules* live in dashboards (Sentry / Railway / GitHub) and are config, not code —
so they live here.

> Companion docs (don't duplicate — follow each for its topic):
> - Day-2 operations entry point: [`launch-operations-runbook.md`](launch-operations-runbook.md)
> - Domain/DNS topology: [`proculink-eu-cutover.md`](proculink-eu-cutover.md)

---

## 0. What's already wired (no setup needed)

| Signal | Source | Where it surfaces |
|---|---|---|
| Unhandled API exceptions | `Sentry.AspNetCore` on the API WebHost | Sentry (events ≥ Error) |
| Background-job exceptions | `AddSentry` on the Worker logging pipeline | Sentry (events ≥ Error) |
| Dead/stale Worker + dead-letter spike | `WorkerHealthAlertJob` (every 5 min) → Sentry | Sentry message event |
| Worker recurring-pipeline liveness | `WorkerHeartbeatJob` (every 2 min) | Worker logs `WORKER-HEARTBEAT` + Sentry breadcrumb |
| DB / storage / migration / **worker** readiness | `GET /health/ready` (structured JSON) | HTTP 200/503 + `workerHealthy` flag |

Sentry is a **no-op without a DSN** — confirm `Sentry__Dsn` is set on **both** the
`ProcuLink` (API) and `aware-amazement` (Worker) Railway services first, or none of
the Sentry rules below will ever fire.

---

## 1. Health endpoints — the contract

```bash
# Liveness — fast, dependency-free. 200 = process alive.
curl -s -o /dev/null -w '%{http_code}\n' https://api.proculink.eu/health

# Readiness — structured JSON. 200 (Healthy/Degraded) or 503 (Unhealthy).
curl -s https://api.proculink.eu/health/ready | jq .
```

`/health/ready` body shape:

```json
{
  "status": "Healthy",          // Healthy | Degraded | Unhealthy
  "ready": true,                // false only when status == Unhealthy (HTTP 503)
  "workerHealthy": true,        // false = no/stale Worker heartbeat (still HTTP 200!)
  "totalDurationMs": 11.4,
  "checks": [
    { "name": "database",   "status": "Healthy", "description": "Database reachable.",        "durationMs": 4.0 },
    { "name": "migrations", "status": "Healthy", "description": "Migrations applied …",        "durationMs": 0.0 },
    { "name": "storage",    "status": "Healthy", "description": "Storage reachable.",          "durationMs": 1.1 },
    { "name": "worker",     "status": "Healthy", "description": "Worker beating (1 registered, 7s …).",
      "data": { "activeWorkers": 1, "secondsSinceWorkerHeartbeat": 7.0, "heartbeatDeadlineSeconds": 60 } }
  ]
}
```

**Key behaviour to remember:**
- A failed migration / unreachable DB → `status:Unhealthy` → **HTTP 503**.
- A **dead/stale Worker is `Degraded` → still HTTP 200**, with `workerHealthy:false`.
  This is deliberate: a dead Worker must not evict the API (it still serves reads /
  billing / dashboard). The **JSON flag** and the **uptime workflow** + **`WorkerHealthAlertJob`**
  carry that alert instead. So always check `workerHealthy`, not just the status code.
- The body **never** contains secrets or stack traces — safe to expose publicly.

> Note: this supersedes the older claim in `launch-operations-runbook.md` §1 that
> "Worker health is not on an HTTP endpoint" — it now is, via `workerHealthy`.

---

## 2. External uptime monitoring (free) — `.github/workflows/uptime.yml`

A scheduled GitHub Action (every 10 min) curls `/health/ready` + the marketing
site and **fails** the run on non-200, an unreachable host, **or** `workerHealthy:false`.
A failed run is the alert.

### Wire the notification (pick one) — ~5 min

1. **Email (default, zero config).** GitHub emails you on a failed scheduled
   workflow on the default branch **if** you have it enabled:
   GitHub → your avatar → **Settings → Notifications → Actions** → check
   *"Send notifications for failed workflows only"* and ensure email delivery is on.
   This is the lowest-effort path and needs no secrets.

2. **Slack (recommended for a shared channel).** Add a step to `uptime.yml` that
   posts on failure, and store the webhook as a repo secret:
   - Slack → create an **Incoming Webhook**, copy the URL.
   - GitHub → repo **Settings → Secrets and variables → Actions → New repository secret**:
     `SLACK_WEBHOOK_URL = https://hooks.slack.com/services/…`
   - Append to the `probe` job:
     ```yaml
     - name: Notify Slack on failure
       if: failure()
       run: |
         curl -sS -X POST -H 'Content-type: application/json' \
           --data '{"text":"🔴 ProcuLink uptime probe FAILED — see GitHub Actions run ${{ github.run_id }}"}' \
           "${{ secrets.SLACK_WEBHOOK_URL }}"
     ```

### Optional — override the probed URLs

If hostnames change, set repo **Variables** (not secrets): `API_READY_URL`,
`SITE_URL`. Defaults are `https://api.proculink.eu/health/ready` and
`https://proculink.eu`.

### Test it now

GitHub → **Actions → Uptime monitor → Run workflow** (manual dispatch). A green run
confirms the probe + (if added) the Slack step work.

---

## 3. Sentry alert rules — create these (≈10 min)

Sentry → **Alerts → Create Alert → Issues** (one rule each, unless noted). All use
*"When an event is captured"* / *"matching ALL filters"* and notify your email +
(optional) the Slack integration. Suggested settings inline.

| # | Name | Condition / filter | Why |
|---|---|---|---|
| 1 | **High error rate** | *Metric alert* — Number of errors **> 20 in 1 hour** | Catches a broad regression/outage spike, not a single blip. |
| 2 | **New issue spike** | *Issue alert* — *"A new issue is created"* → notify immediately | First sighting of a brand-new crash class = deploy regression. |
| 3 | **Worker-health alert** | *Issue alert* — message contains `ProcuLink Worker health degraded` | Fired by `WorkerHealthAlertJob`: no healthy worker OR dead-letter+failed ≥ threshold (25). The single most important rule — it's the "Worker not consuming" pager. |
| 4 | **AI usage cap latched** | *Issue alert* — message contains `Organisation AI usage cap reached` | The 2026-06-12 incident class: all PDFs silently fall back to regex when an org hits its monthly AI cap. Verify via `GET /api/billing/ai-usage` before touching code. |
| 5 | **Delivery dead-letter** | *Issue alert* — message contains `delivery_dead_letter` OR rule #3 covers it via the backlog threshold | Orders whose automatic retries are exhausted — a customer's PO is NOT reaching the supplier. |
| 6 | **Parse: no line items surge** | *Metric alert* — events with message `File contains no line items.` OR `Extracted order contains no line items.` **> 5 in 1 hour** | A single bad upload is normal; a *surge* means a parser/extractor regression or a new supplier file shape silently failing. (Scanned/illegible PDFs fail with the user-facing "scanned or image-only" copy — a surge there means OCR/vision is mis-set.) |
| 7 | **Webhook signature failures** | *Metric alert* — events containing `signature mismatch` OR `Stripe webhook signature validation failed` **> 10 in 1 hour** | A burst = a misconfigured secret after a rotation (Stripe/HMAC), or a probe/abuse attempt. Stripe one breaks billing reconciliation. |
| 8 | **Migration / readiness failure** | *Issue alert* — message contains `Database migrations failed` | The app is serving on a stale schema (`/health/ready` is 503). |

Tuning: thresholds are starting points for low current volume — raise them once you
have a baseline so you're not paged on noise. Rules 3 and 4 are the two that map to
real past incidents; do those first if you only have 5 minutes.

---

## 4. Railway platform alerts (≈3 min)

Railway → project `lucid-generosity` → **each** service (`ProcuLink` **and**
`aware-amazement`) → **Settings → Notifications**:

- Enable **Deploy failed** notifications (a bad build/migration never reaching prod
  is itself an outage signal).
- Enable **Deploy crashed / service unhealthy** notifications.
- Confirm the destination is your email and/or a connected Slack/Discord webhook.

Do this for the Worker too — a crash-looping Worker is exactly the "nothing is
processing" failure mode, and Railway's own crash notification is the fastest signal.

---

## 5. Diagnosing the Worker in prod — Hangfire + the `hangfire.*` tables

The Hangfire **dashboard is dev-only** (not exposed in prod). In prod you diagnose
the Worker two ways:

### a) `workerHealthy` + the Worker log line
- `curl …/health/ready | jq .workerHealthy` — `false` means no Hangfire server has
  beaten within 60 s (the readiness check reads the shared Hangfire heartbeat).
- Railway → `aware-amazement` → **Logs**, grep for `WORKER-HEARTBEAT` (every 2 min).
  Its **absence** means the recurring-job dispatcher is wedged even if the server
  process is up — a stronger signal than the heartbeat alone.

### b) The `hangfire.*` tables in Neon (SQL, read-only)

Neon → SQL editor (the documented prod path, since the dashboard is off). Hangfire
keeps its state under the `hangfire` schema:

```sql
-- Is a Worker registered + how fresh is its heartbeat?
SELECT id, lastheartbeat, now() - lastheartbeat AS age
FROM   hangfire.server
ORDER  BY lastheartbeat DESC;
-- No rows, or age > ~1 min, = dead/stale Worker.

-- Are jobs piling up unprocessed? Counts by state.
SELECT s.name AS state, count(*)
FROM   hangfire.job j
JOIN   LATERAL (
         SELECT name FROM hangfire.state
         WHERE  jobid = j.id ORDER BY id DESC LIMIT 1
       ) s ON true
GROUP  BY s.name
ORDER  BY count DESC;
-- A growing 'Enqueued'/'Scheduled' pile with no 'Processing' = Worker not consuming.

-- Recently failed jobs (newest first).
SELECT j.id, j.invocationdata, j.createdat
FROM   hangfire.job j
JOIN   LATERAL (
         SELECT name FROM hangfire.state
         WHERE  jobid = j.id ORDER BY id DESC LIMIT 1
       ) s ON true
WHERE  s.name = 'Failed'
ORDER  BY j.createdat DESC
LIMIT  20;
```

(The exact schema/column names follow Hangfire.PostgreSql's default `hangfire`
schema. If a query errors on a column name, `\d hangfire.server` / `\d hangfire.job`
in psql shows the live columns.)

### c) Operator UI
`/operations/health` shows the worker banner ("last heartbeat"), the dead-letter
queue, and a requeue action (`POST /api/ops/requeue-delivery`) — the no-SQL path.

---

## 6. The 20-minute setup checklist

- [ ] Confirm `Sentry__Dsn` set on **both** `ProcuLink` and `aware-amazement` (Railway).
- [ ] `.github/workflows/uptime.yml` is on `main`; run it once via **Run workflow** (green).
- [ ] Turn on GitHub failed-workflow email **or** add the Slack step + `SLACK_WEBHOOK_URL` secret (§2).
- [ ] Create Sentry rules **#3 (Worker health)** and **#4 (AI cap)** first, then #1, #2, #5–#8 (§3).
- [ ] Enable Railway **Deploy failed** + **crashed** notifications on both services (§4).
- [ ] Smoke-test: `curl …/health/ready | jq '{status,workerHealthy}'` returns `Healthy` / `true`.
- [ ] Bookmark the Neon `hangfire.server` / `hangfire.job` queries (§5) for incident time.

Done = every alert the code emits now reaches a human.
