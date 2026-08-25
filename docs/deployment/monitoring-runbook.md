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

There is a **second, independent** alert path — a recurring Hangfire job that evaluates **six
health conditions plus one meta-condition** and reports to Sentry **and to an email address you
choose** (§3). It has its own blind spot, described there. The two paths are complementary, not
redundant, and neither is a pager.

**One thing to set — and the Worker now insists on it.** In Production the Worker **refuses to
start** unless it has a working alert destination: either `Sentry__Dsn`, or `Alerting__Email__To`
together with `Email__Postmark__ServerToken`. Booting cleanly with a dead alarm is the one
alerting failure that cannot be reported at runtime, because there is nowhere to report it to;
a crash loop in front of whoever is deploying is the only loud option left. See §3.1.

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
- a 200 body whose `.status` is not `Healthy` → `Unhealthy` fails outright (belt-and-braces — it
  also 503s); `Degraded` emits a `::warning::` naming the degraded `checks[]` **and still fails
  the run**, because e.g. a storage (R2) failure is Degraded-with-HTTP-200 by design and a red
  run is the only pager at pilot scale

That third condition is the point of the whole workflow. `/health/ready` runs the
`ready`-tagged health checks (database, storage, migration flag, Worker heartbeat,
recurring-job dispatcher liveness, and the revision-authority flag's effective value) and maps
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

**Absent reads as false.** All three flattened booleans (`workerHealthy`, `recurringJobsHealthy`,
`revisionAuthority`) render `false` when their check entry is missing from `checks[]`.
`workerHealthy` used to do the opposite — a missing `worker` entry rendered `true` — so "the
Worker monitor was dropped from the registration" produced a green probe. To tell "flag off" from
"check missing", look for the entry in `checks[]`.

### `recurringJobsHealthy` — what the heartbeat cannot tell you

`workerHealthy` answers *is a Hangfire server registered and beating*. It cannot answer *are
scheduled jobs actually firing*, and those come apart in the failure mode that matters: a server
whose **recurring-job dispatcher has wedged** (deadlocked job, poisoned queue, storage write
failure on the recurring table) or whose **worker pool is saturated** keeps writing its ~30 s
server heartbeat while nothing runs. `workerHealthy` stays `true`, `/health/ready` stays green,
and uploads land and sit.

`WorkerHeartbeatJob` (every 2 min, logs `WORKER-HEARTBEAT`) was written to close that gap and
closed it **only for a human** — its own remarks say ops greps the Railway logs for the string.
`RecurringJobDispatcherHealthCheck` is that same evidence read automatically: it reads Hangfire's
own `LastExecution` record for the watched jobs out of the shared Postgres storage (no new table,
no new job — the same trick the SFTP/S3 pull-freshness signal uses).

| Watched job | Cadence | Stale at |
|---|---|---|
| `worker-heartbeat` | 2 min | 10 min |
| `worker-health-alert` | 5 min | 20 min |

Deadlines are several times the cadence on purpose: one missed run is a redeploy or a slow cycle.
An **unreadable or never-recorded** last execution counts as stalled — unknown is not healthy, and
in practice it is narrow, because `AddOrUpdate` preserves `LastExecution` across restarts.

The check is **Degraded, never Unhealthy**, for the same reason as the Worker heartbeat: the API
must not evict itself over another process's fault. It therefore reaches `uptime.yml` through the
`.status` Degraded gate above, which fails the run and names the degraded checks; the flattened
`recurringJobsHealthy` boolean and the per-job ages in `checks[].data` are the machine-readable
detail. Watching `worker-health-alert` from the API is also the **outside view §3's second blind
spot lacks** — that sweep runs on the Worker, so it can never report its own death.

**What it still cannot see:** the watched jobs run on the `background` queue, so a saturated
`default` queue with a healthy `background` one would not trip it. It is a dispatcher-liveness
signal, not a queue-depth one.

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

## 3. The second alert path: `WorkerHealthAlertJob` → Sentry + email

Independent of GitHub Actions, the Worker runs `worker-health-alert` **every 5 minutes**
(`ProcuLink.Worker/Worker.cs`). It calls `WorkerHealthAlertService`, which evaluates six health
conditions and one meta-condition. Each has its own trip rule and its own independent 30-minute
cooldown (`WorkerHealthAlertOptions.MinAlertIntervalMinutes`), so a long-running incident on one
condition can never swallow the first notification of another.

| Alert key | Fires when | Triage | Tunable |
|---|---|---|---|
| `worker_heartbeat_lost` | no Hangfire server has beaten within 60 s (same rule as `/health/ready`) | Railway Worker service — is the process up? | — |
| `dead_letter_backlog` | all-org `delivery_dead_letter` + `delivery_failed` orders ≥ **1**, **all time** (lowered from 25 on 2026-08-25: a Pilot org is capped at 20 orders total, so 25 could never fire during a pilot; one dead-lettered order already encodes three concluded failures over ~90 min of backoff) | Operations health page → review and requeue | `DeadLetterThreshold` |
| `pipeline_failure_backlog` | all-org `failed` + `transform_failed` orders ≥ **1** **whose `UpdatedAt` is inside the last 24 h** | Worker logs — parser and output-template errors. These orders failed BEFORE the delivery step, so no delivery alert covers them | `PipelineFailureThreshold`, `PipelineFailureWindowMinutes` |
| `delivery_failure_rate` | ≥ **10** concluded delivery attempts in the last **60 min** and ≥ **50 %** of them failed | Supplier endpoints — rejecting or unreachable | `DeliveryFailureMinAttempts`, `DeliveryFailureWindowMinutes`, `DeliveryFailurePercent` |
| `pull_channel_stalled` | an inbound pull channel with ≥ 1 org enabled has no observed success for ≥ **60 min** | IMAP / SFTP / S3 credentials and the polling jobs | `PullChannelStaleMinutes` |
| `ai_token_cap_latched` | ≥ 1 org **in good standing** is at/over its monthly OpenAI token budget | Raise the limit or wait for the month to roll over | — |
| `alert_sweep_degraded` | the sweep could not read one of its own inputs, so the conditions that input feeds were **not evaluated** | See below — treat as MORE urgent than a health alert | — |

`pipeline_failure_backlog` was missing from this table until 2026-08-25, which is how the row most
likely to fire ended up being the one nobody had documented.

All tunables live under the `WorkerHealthAlert` configuration section. None of them appears in
any `appsettings.json` — the defaults above are what runs.

### Why one condition is windowed and its neighbour is not

`pipeline_failure_backlog` counts only the **last 24 hours** (`PipelineFailureWindowMinutes`,
measured on each order's `UpdatedAt`). `dead_letter_backlog` counts **all time**. The two look
alike and are not, and the difference is in the status machine rather than in the alert:

- `failed` is **declared terminal** — `OrderStatusMachine.Transitions[Failed]` is the empty set,
  so nothing can ever move an order out of it. An all-time count of it can only go up. Paired
  with a threshold of 1, one pilot user who uploaded a single unparseable file and walked away
  pinned the condition bad **permanently**: it could never transition back to healthy, so it
  re-alerted on every cooldown expiry — roughly 48 emails a day, forever, about one abandoned
  file. The window is the drain the condition never had; 24 h after the last failure it clears
  itself and re-arms for the next real one. **The threshold stayed at 1** — the bar was never the
  problem.
- `delivery_dead_letter` and `delivery_failed` both appear in `OrderStatusMachine.RequeueableFrom`,
  so the operations health page's requeue action moves an order out of that count: the number
  falls when the incident is actually handled. And an undelivered purchase order is a *standing*
  incident in a way an abandoned unparseable upload is not — ageing it out would mean the operator
  stops being paged about a supplier that is still not receiving orders. So it stays all-time,
  deliberately.

**What the window costs:** an operator who ignores a real parser outage for 24 h stops being
paged about it. The orders are still counted on the org-scoped operations health page, which is
the surface meant to hold a standing backlog.

### `alert_sweep_degraded` — the alarm reporting on itself

The two inputs (`OpsHealthService.GetWorkerHealthSnapshotAsync` and `IOperationalAlertProbe`) are
read independently and defensively. If one throws, the conditions it feeds are treated as
**unknown and skipped**, the other input's conditions are still evaluated, and the fact that the
sweep was partially blind is raised as `alert_sweep_degraded` through the same sink and the same
cooldown as any other alert. The all-clear log line is suppressed for that run.

This exists because the previous behaviour was the opposite on both counts. A failing probe
degraded to an all-clear value — zero attempts, no channels, no latched orgs — which evaluates as
*not bad* on all three conditions it feeds, and the same run logged `healthy`. A failing snapshot
read was not guarded at all and, with `[AutomaticRetry(Attempts = 0)]` on the job, took every
condition down for that cycle in silence. A permanently broken query was therefore indistinguishable
from a permanently healthy system.

**Triage:** this alert says nothing about whether the system is unhealthy — it says the alarm
cannot see. Treat it as more urgent than a health alert, not less. The message names each blind
input; the accompanying `LogError` carries the exception. Almost always database connectivity.
Job cancellation at host shutdown is deliberately **not** treated as a failure, so a deploy does
not page.

### Where each condition's evidence comes from

- **Heartbeat and dead-letter** — `OpsHealthService.GetWorkerHealthSnapshotAsync`, unchanged.
- **Delivery failure rate** — `delivery_attempts` rows in the trailing window, all orgs.
  `dispatching` (still in flight) and `unconfirmed` (outcome lost to a crash) are **excluded**,
  so a send that has not resolved is never counted as a failure.
- **Pull channels** — the condition is **channel-level, not per-org**, on purpose. For IMAP the
  evidence is `EmailPollingConfig.LastPolledAt` (a real success stamp written by
  `EmailPollOrgJob` after a clean disconnect), taken as the **newest across all enabled orgs** —
  so it answers "is anyone still successfully polling this channel", and one org with broken
  credentials among several healthy ones will **not** trip it. **SFTP and S3 persist no
  last-success timestamp at all**, so their signal is the `sftp-polling` / `s3-polling` recurring
  dispatcher's own last execution from Hangfire storage. That proves the channel is still being
  polled; it does **not** prove any org's SFTP/S3 credentials still work. Closing the per-org gap
  needs a schema change (a `last_successful_poll_at` column on the ingress configs) and is not
  done here.
- **AI token cap** — the verdict is `IAiUsageTracker.IsAtOrOverLimitAsync`, i.e. the exact
  predicate production checks before every OpenAI call, so the `Ai:OpenAI:MonthlyTokenLimitPerOrg`
  override and the delinquency clamp are honoured automatically.

### Deliberate silences (these are not bugs)

Each of these exists because a page that fires when nothing is wrong trains the one person who
reads it to ignore all of them.

- A **high failure rate on a tiny sample** does not trip — 1 failure of 1 attempt is 100 % and
  means nothing. Ten concluded attempts is the floor.
- A pull channel **nobody has switched on** never trips, however stale it looks.
- A pull channel that has **never** recorded a success never trips — a channel configured
  minutes ago has simply not polled yet.
- **Delinquent orgs** (`read_only` / `cancelled` / `trial_expired` / `past_due`) are excluded
  from the AI-latch count: their budget is clamped on purpose by the billing rules, so counting
  them would be a permanent page nobody can action.
- An org that **spent no tokens this month** is not a latch candidate, which is what stops a
  zero-budget org from satisfying `0 >= 0` forever.

### 3.1 Sinks — and the one variable to set

`CompositeWorkerAlertSink` fans every alert out to both transports, each inside its own
try/catch, so a dead Sentry cannot suppress the email and vice versa. Both are safe no-ops when
unconfigured, and neither can throw into the Worker.

| Sink | Configured by | Unset behaviour |
|---|---|---|
| `SentryWorkerAlertSink` | `Sentry__Dsn` on the Worker service | SDK initialises disabled → no-op, **reports not-delivered** |
| `EmailWorkerAlertSink` | `Alerting__Email__To` **and** `Email__Postmark__ServerToken` | logs, sends nothing, **reports not-delivered** |

Each sink returns whether it actually handed the message to a working transport, and the composite
returns whether *any* did. When an alert reaches none of them the sweep logs
`the alert reached no configured transport — NOBODY has been notified` and does **not** count the
alert as raised. "Did not throw" is not "the operator was told".

**Email is the destination this repo now expects you to use**, because the Worker already
registers `IEmailApiClient`/Postmark — no new transport or package is introduced, only the
recipient address. Optional: `Alerting__Email__SubjectPrefix` (default `[ProcuLink alert]`);
subjects are `<prefix> <alert_key>`, so one mail filter catches all of them.

To turn it on, set on the Worker Railway service (`aware-amazement`):

```
Alerting__Email__To=you@example.com
Email__Postmark__ServerToken=<server token>
```

### Startup refuses two configurations, both in Production only

`StartupConfigurationValidator` throws before the Worker serves anything when:

1. **Neither** `Alerting__Email__To` nor `Sentry__Dsn` is set — every condition would be evaluated
   and delivered into a no-op.
2. `Alerting__Email__To` is set but `Email__Postmark__ServerToken` is not — a declared route that
   cannot work. This is refused **even when Sentry is healthy**, because nothing at runtime would
   ever say half the routing is dead: the sink logs one warning per alert while the surviving
   transport keeps reporting success.

Non-production warns instead, so local runs still need no alerting secrets. The token was
previously in neither the required nor the optional key list, which is exactly how rule 2 stayed
invisible.

**Prove it, do not assume it.** There is a gated live-send test that performs a real Postmark
send to a real inbox:

```
PROCULINK_LIVE_ENDPOINT_TESTS=1 \
PROCULINK_LIVE_POSTMARK_TOKEN=<server token> \
PROCULINK_LIVE_ALERT_EMAIL_TO=you@example.com \
PROCULINK_LIVE_ALERT_EMAIL_FROM=alerts@<verified domain> \
dotnet test ProcuLink.Infrastructure.Tests --filter FullyQualifiedName~LiveAlertEmailSendTests
```

It is statically skipped (with a printed reason) when those are absent — never a green no-op.
It now carries `[Trait("Category", "LiveAlert")]`, so `--filter Category=LiveAlert` selects it. That
trait is deliberately *not* `LiveEndpoint`: the daily live-delivery workflow filters on that value,
and adding this class to it would send a real email on every scheduled run.

### The 60-second monthly proof that needs no test send

The live-send test above costs one real email and a token in your shell. Once a month you want the
cheaper question answered — *did the alerts we believe we sent actually leave?* — and you can read
that straight off Postmark without sending anything:

```
curl -sS -H "X-Postmark-Server-Token: $POSTMARK_SERVER_TOKEN"   "https://api.postmarkapp.com/messages/outbound?fromdate=$(date -u -d '30 days ago' +%F)&count=200"
```

Three things to read, in order:

1. **A `401` is the answer, not an error.** It means the server token is revoked or rotated — the
   *configured-but-broken* destination named in blind spot 1 below, caught without sending anything
   and without waiting for a real incident to go unheard.
2. **Find the newest `[ProcuLink alert]` subject.** Its absence when you know a condition fired is
   itself the finding. `SubjectPrefix` is `Alerting:Email:SubjectPrefix`, defaulting to
   `[ProcuLink alert]`.
3. **Open that message's `MessageEvents` and confirm it reads `Delivered`** — Postmark accepting a
   message (HTTP 200) is not the same as an inbox receiving it; a `Bounce` or `SpamComplaint` event
   here is a destination that is configured, accepted, and still reaching nobody.

Step 2 is now checkable from our side too. `EmailWorkerAlertSink` logs one Information line per
**delivered** alert carrying the alert key, the recipient *domains* (never the addresses — the local
part is PII and this line is a Sentry breadcrumb surface) and Postmark's own `MessageId`. Grep the
Railway Worker logs for `emailed alert` and you have both the fact and the identifier to look up
above. Before that line existed, only failures were logged, so a delivered alert left no trace in
ProcuLink at all.

### Blind spots, both still load-bearing

1. **Unconfigured no longer means unheard — it means the Worker will not boot.** This used to be
   a live blind spot: with neither `Sentry__Dsn` nor `Alerting__Email__To` set, every condition
   evaluated correctly and reached nobody, and the checked-in
   `ProcuLink.Worker/appsettings.Production.json` still ships `"Dsn": ""`. Production startup now
   refuses that configuration outright. **What remains:** a destination that is *configured but
   broken* — a revoked Postmark token, a Sentry project that silently drops events. The
   `reached no configured transport` log line covers the first; nothing inside the process can
   cover the second, which is why `uptime.yml` probes from outside and why the live-send test
   above exists.
2. **The job runs *on* the Worker.** If the Worker is dead, the job that would report the
   Worker dead is also dead. This path detects a *backlog spike*, a *failure-rate spike*, a
   *stalled channel* and a *latched cap* well, and a *hung* Worker sometimes; it cannot detect a
   *stopped* one. That asymmetry is precisely why `uptime.yml` probes from outside — and, since
   2026-08-25, why `/health/ready`'s `recurringJobs` check watches `worker-health-alert`'s own
   last execution (§1). A sweep that has stopped running now shows up on an endpoint that is not
   running on the Worker.

Also on the Worker: `worker-heartbeat` every 2 minutes writes a `WORKER-HEARTBEAT` log line
(and a Sentry breadcrumb) proving the recurring-job **dispatcher** is firing, not merely that
the Hangfire server thread is alive. Grepping the Railway logs for `WORKER-HEARTBEAT` is the
fastest liveness check there is — and it is no longer the *only* one: the same job's
`LastExecution` is what `/health/ready`'s `recurringJobs` check reads, so a wedged dispatcher now
fails an automated probe instead of waiting for someone to open the logs.

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
  a plain 200-check will **not** catch a dead Worker. Configure keyword/JSON assertions on
  `"workerHealthy":true` **and** `"recurringJobsHealthy":true`, or the monitor is decorative —
  the second one is what catches a Worker that is beating but not executing anything.

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

### `recurringJobs` Degraded — `recurringJobsHealthy=false`

Meaning: a Hangfire server **is** registered and beating, but its recurring-job dispatcher has not
fired a watched job inside that job's deadline. Symptom is identical to a dead Worker from the
customer's side — uploads land and sit — but `workerHealthy` will typically still be `true`, which
is exactly why this check exists.

1. **Get the ages:**
   ```bash
   curl -sS https://api.proculink.eu/health/ready \
     | jq '.checks[] | select(.name=="recurringJobs") | .data'
   ```
   Each entry carries `id`, `cronMinutes`, `deadlineMinutes` and `minutesSinceLastExecution`. A
   **missing** `minutesSinceLastExecution` means the last execution could not be read at all — a
   brand-new database, or Hangfire storage unreachable. Check the `database` check in the same
   body before assuming a wedge.
2. **`hangfire.jobqueue` depth**, per queue — a deep `background` queue with nothing draining is
   a saturated pool; an empty queue with stale `LastExecution` is a wedged dispatcher.
3. **`hangfire.job` where `statename = 'Processing'`** with a long-running row is the usual
   culprit for a saturated pool: one job holding a worker slot indefinitely.
4. **Recovery** is the same as a dead Worker — restart the Railway Worker service. Jobs are
   required to be idempotent, so a restart mid-flight is safe.
5. **If it clears and returns**, the wedging job is the thing to fix, not the Worker.

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

### `delivery_failure_rate` — over half of recent sends failed

The alert carries the ratio and the window. Half the traffic failing is almost never one bad
supplier — look for a shared cause first.

1. `/api/ops/health` and the delivery log: is the failure concentrated on **one supplier**, or
   spread across all of them?
2. Spread across all → suspect the Worker's outbound path: expired credentials, a DNS/egress
   change, or `OutboundRequestGuard` refusing a newly-resolved address.
3. One supplier → their endpoint or their credentials. The attempt rows carry the response code
   and body verbatim; a 401/403 is a credential rotation, a 5xx is theirs.
4. Nothing is lost while you triage — failed attempts retry on the existing backoff, and orders
   that exhaust it land in the dead-letter queue, which has its own alert.

### `pull_channel_stalled` — inbound POs are not being picked up

The alert names each stalled channel and the age of its last observed success.

1. **email** — the stamp is real, and the alert means the *newest* success across every enabled
   org has aged out, so this is normally all orgs at once, not one. Check the `EmailPollOrgJob`
   logs first, then per-org IMAP settings; a single org's wrong password or moved folder stalls
   that org silently and does **not** raise this alert.
2. **sftp / s3** — the signal is the dispatcher's own last execution, so a stall here means the
   *recurring job* stopped, not that one org's credentials broke. Confirm with the Worker logs
   (`WORKER-HEARTBEAT` present but no `sftp-polling` line) and check the Hangfire `polling`
   queue for a wedged job.
3. Nothing is dropped: the source files/messages stay where they are and are picked up on the
   next successful poll. The dedupe ledgers (`imported_sftp_files`, `imported_s3_objects`,
   `email_import_records`) prevent a re-import of anything already ingested.

### `ai_token_cap_latched` — an org's AI budget is exhausted

Not an outage. PDF extraction for that org silently falls back to the regex path, which is
worse but not broken, and the counter clears when the calendar month rolls over.

1. Decide whether the spend is legitimate. The per-org counter is `ai_usage_monthly`; the plan
   ceilings are `PlanConstants.AiMonthlyTokenLimits`.
2. To lift it now: move the org up a plan, or set `Ai:OpenAI:MonthlyTokenLimitPerOrg` — that
   config key overrides **every** plan value and is the emergency lever, so put it back after.
3. If the same org latches every month, its plan is wrong for its volume, not its budget.

### Support: "PO 4500012580 isn't arriving" — find the org from the PO number

A customer quotes a PO number and nothing else. Every downstream question (did it parse, is
the mapping wrong, what did delivery say) needs the owning organisation and order id first,
and support tickets do not carry those. The admin lookup answers with both:

```bash
curl -sS -H "Authorization: Bearer $ADMIN_JWT" \
  "https://api.proculink.eu/api/admin/orders/find?po=4500012580" | jq .
```

Admin-only (the `Admin:UserIds` / `Admin:Emails` allowlist — a non-admin token gets 403),
read-only, capped at 20 matches, exact stored spellings first and then newest first. The
match is the exact `po_number` **or** its normalized key (trim + upper-case), so a casing
or padding difference between the ticket and the document still resolves. Each row carries
`orgId` / `orgName` / `orgSlug`, `orderId`, `status`, `supplierName` and timestamps — enough
to open the right org's context and keep triaging with `/api/ops/health` or the order itself.
Two rows in two different orgs is not an error; the same customer PO number can legitimately
exist on both sides of a buyer/supplier pair. A blank `po` is refused with 400.

---

## 6. What is *not* monitored

Stated plainly so nobody mistakes a green Actions tab for coverage:

- **No paging.** Failures produce a notification at best (§4). Overnight, expect hours of lag.
- **Detection lag is real.** Up to 10 min for uptime, up to 5 min for the §3 conditions, up to
  7 days for Postmark IP drift (a deliberate trade — the recovery window is far longer than the
  lag).
- **Some per-org symptoms are still invisible.** One supplier's endpoint refusing deliveries
  and one connection's mapping silently producing empty fields do not move `workerHealthy` and
  are not among the §3 conditions. They surface in `/api/ops/health` and the in-app exceptions,
  which nothing polls externally. (A latched AI budget *is* now covered — see §3.)
- **A broken SFTP/S3 credential per org is not detected.** §3's pull-channel condition proves
  the channel is still being polled, not that any given org's poll succeeds. Only IMAP has a
  real per-org success stamp.
- **The dead-letter threshold is all-org and absolute** (1 since 2026-08-25; was 25). At 1 the
  old caveat — a small org drowning unnoticed inside a healthy fleet — no longer applies to the
  backlog condition: any org's first dead-lettered order trips it. It still applies to the
  delivery failure rate, which is a system-wide ratio, so one supplier failing inside a lot of
  healthy traffic will not trip *that* condition; the backlog condition is what catches it now.
- **No synthetic end-to-end transaction.** Nothing uploads a test order every N minutes and
  asserts it was delivered. `workerHealthy` proves the Worker is *beating*, not that the
  pipeline is *correct*.
- **Cloudflare, Postmark, Neon, Clerk and Stripe are unmonitored** by this repo beyond the
  side effects that surface in the two probes above.
