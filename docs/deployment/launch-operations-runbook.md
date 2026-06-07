# ProcuLink — Launch Operations Runbook

**Audience:** whoever is on call after launch (founder + any agent).
**Created:** 2026-06-07 (Wave 8). **Scope:** day-2 operations — health, deploys,
incident response, maintenance. For one-time setup see the linked runbooks; this
doc is the single entry point that ties them together.

> Source-of-truth companions (do not duplicate — follow these for their topic):
> - Live state sweep: [`2026-06-07-launch-readiness-verification.md`](2026-06-07-launch-readiness-verification.md)
> - Domain/DNS topology: [`proculink-eu-cutover.md`](proculink-eu-cutover.md)
> - Stripe TEST→LIVE swap (founder-only): [`stripe-go-live-runbook.md`](stripe-go-live-runbook.md) + [`stripe-go-live-checklist.md`](stripe-go-live-checklist.md)
> - Live channel test-fires: [`../live-endpoint-test-fires.md`](../live-endpoint-test-fires.md)

---

## 0. Topology at a glance

| Piece | What/where | Notes |
|---|---|---|
| Frontend | Vercel project `project-proculink` → `proculink.eu` + `www` | auto-deploys on push to `main` |
| API | Railway project `lucid-generosity`, service **`ProcuLink`** → `api.proculink.eu` | auto-deploys on push to `main` |
| Worker | Railway service **`aware-amazement`** | the **only** Hangfire executor (see §2) |
| Database | Neon Postgres | additive migrations applied on API boot |
| Object storage | Cloudflare R2 — `proculink` (private order data) + `proculink-public` (marketing) | private bucket: pre-signed URLs only, **never make public** |
| Auth | Clerk **production** instance (`clerk.proculink.eu`) | tokens carry `azp`; API validates it |
| Billing | Stripe **TEST** mode | live swap = founder-only, June-9 gate |
| Errors | Sentry (API + Worker) | `WorkerHealthAlertJob` posts heartbeat/dead-letter alerts |
| Product analytics | PostHog | ingest only |

Dashboards: Railway (railway.app, project `lucid-generosity`), Vercel
(dimnovare-9994), Neon (neon.tech), Cloudflare (R2 + DNS), Sentry, PostHog,
Stripe. Hangfire dashboard is dev-only (not exposed in prod).

---

## 1. Health checks — what to hit first

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://api.proculink.eu/health   # 200 = process alive (fast liveness)
curl -s https://api.proculink.eu/health/ready                              # "Healthy" = DB + storage + migrations OK
curl -s -o /dev/null -w "%{http_code}\n" https://proculink.eu             # 200 = frontend up
```

- `/health` is a **fast liveness** probe (no dependencies). If this fails, the API process is down → check Railway `ProcuLink` service logs/restart.
- `/health/ready` is **readiness**: it checks the DB, object storage, and that EF migrations applied. A failed migration **flips readiness to unhealthy and captures to Sentry but keeps the process up** (migrate-fail-loud), so `/health` can be 200 while `/health/ready` is unhealthy — that combination means "running on a bad/old schema; do not trust writes."
- **Worker health is not on an HTTP endpoint** — it reports a Hangfire heartbeat. Check it via the operator UI `/operations/health` (worker banner + "last heartbeat") or:

```bash
railway variables --service ProcuLink --json | python .live-fixtures/workercheck.py   # hangfire heartbeat age
railway variables --service ProcuLink --json | python .live-fixtures/dbcheck.py        # column/migration/row sanity
```

(`.live-fixtures/` is gitignored; the scripts read the DB connection string from the piped Railway secret and never print it.)

---

## 2. The one architectural fact that drives most incidents

**The API hosts no Hangfire server — the Worker (`aware-amazement`) is the sole
job executor.** Uploads only create a stub + enqueue; all parse / OCR / vision /
transform / deliver work runs in the Worker. Therefore:

- **Worker down ⇒ every upload stalls at `parsing` forever** (no errors in the API — it looks like "nothing happens"). This is the single most common production symptom. See §4.1.
- Only **one** worker should run. A second worker with a different R2 secret causes *intermittent* failures (jobs land on either at random). The duplicate `ProcuLink-Worker` service was deleted 2026-06-03; keep it that way.

---

## 3. Routine deploy

1. Land code on `main` (both repos). Backend gate before pushing: `dotnet test ProcuLink.slnx` (≥988 green) **and** `cd ../project-proculink && bun run build` (clean). Never push red.
2. Push → Railway auto-deploys `ProcuLink` (API) + `aware-amazement` (Worker) and applies additive migrations on API boot; Vercel auto-deploys the frontend. `railway.toml watchPatterns` skips redeploy for doc-only commits.
3. Verify after deploy: `/health` 200, `/health/ready` Healthy, Worker heartbeat fresh (§1). Drive one upload→parse if the change touched the pipeline.

> Migrations must be **additive/nullable** for zero-downtime auto-apply. A hand-written EF migration also needs an explicit `HasColumnName(...)` mapping in `ProcuLinkDbContext` (there is no global snake_case convention) — otherwise the column-name mismatch surfaces as a runtime "column does not exist".

---

## 4. Incident playbook (symptom → diagnose → fix)

### 4.1 Uploads stuck at "parsing"; nothing progresses
- **Diagnose:** `/operations/health` worker banner OR `workercheck.py` heartbeat age. Stale heartbeat ⇒ Worker down/unhealthy. Check Railway `aware-amazement` logs + Sentry (Worker project).
- **Fix:** restart/redeploy `aware-amazement` on Railway. Once it's back, stuck orders are auto-requeued by `StuckOrderDetectionService` (bounded by `requeue_count` before dead-lettering); force a specific one via §5.2.

### 4.2 Intermittent parse/transform/deliver failures, `SignatureDoesNotMatch` in logs
- **Cause:** R2 credential mismatch — either the Worker's `Storage__R2SecretAccessKey` doesn't match its access key, or a second worker is running with a stale secret. (This exact issue once masqueraded as "Worker not consuming.")
- **Fix:** confirm only **one** worker runs; set the matching R2 secret on **both** API and Worker (§5.1). Note: R2 GET must use a pre-signed URL + HttpClient (SDK chunked GET signing is rejected by R2) — already implemented in `DownloadAsync`; don't "simplify" it back.

### 4.3 `/health/ready` unhealthy (but `/health` 200)
- **Cause:** DB unreachable, object storage unreachable, or a migration failed on boot (migrate-fail-loud).
- **Diagnose:** Sentry will have the captured exception; `dbcheck.py` confirms which migrations applied. Check Neon status + connection count.
- **Fix:** resolve the underlying dependency. If a migration failed, fix it and redeploy — do **not** route traffic while readiness is unhealthy (writes run on a bad schema).

### 4.4 Neon connection-count errors / pool exhaustion
- The connection string is built with a pool ceiling (API ≈30 / Worker ≈20, via `BuildPooledConnectionString`, read lazily; skipped when `Pooling=false`). If still exhausted, check for a connection leak or scale the Neon plan. Don't raise the ceiling above what Neon allows for the tier.

### 4.5 Dead-letter spike (deliveries exhausting retries)
- **Diagnose:** `/operations/health` dead-letter table, or `GET /api/ops/dead-letter`. Each row shows the last error + response code.
- **Fix:** correct the supplier endpoint/credentials/delivery-config, then requeue (§5.2). `WorkerHealthAlertJob` raises a Sentry alert on a dead-letter spike (anti-spam throttled).

### 4.6 Billing webhook / checkout problems
- Stripe is in **TEST** mode pre-launch. For the live swap and its rollback, follow [`stripe-go-live-runbook.md`](stripe-go-live-runbook.md) — **founder-only**. The Stripe webhook is deliberately exempt from rate limiting; don't add it.

---

## 5. Maintenance procedures

### 5.1 Rotate the R2 secret (must update BOTH services)
```bash
railway variables --service ProcuLink        --set "Storage__R2SecretAccessKey=<new-secret>"
railway variables --service aware-amazement  --set "Storage__R2SecretAccessKey=<new-secret>"
```
Both auto-redeploy. Verify with one upload→parse→deliver. (R2 S3 credential = token id as access-key-id + **SHA256(token value)** as the secret.) Mismatched secrets across the two services is the §4.2 incident.

### 5.2 Requeue a stuck / dead-lettered order
- **UI:** `/operations/health` → dead-letter table → "Requeue delivery".
- **API:** `POST /api/ops/orders/{id}/requeue-delivery` (admin/operator auth). This requeues even dead-lettered orders (the older `retry-delivery` rejects them).

### 5.3 Restart the Worker
- Railway → service `aware-amazement` → Restart (or redeploy). It is GitHub-auto-deploying; a push to `main` also redeploys it. After restart, confirm a fresh heartbeat (§1).

### 5.4 Post-launch secret rotation (chat-exposed during the push)
Rotate after launch and never commit/print: Clerk keys, R2 secret, Cloudflare API token, Sentry DSN/token, PostHog token, ElevenLabs key. Stripe live keys are set only during the founder's live swap.

---

## 6. Verify-command reference (read-only)

```bash
# Railway var NAMES + set/blank (never values):
railway variables --service ProcuLink --json | python -c "import sys,json;d=json.load(sys.stdin);[print(('SET ' if str(v).strip() else 'BLANK')+' '+k) for k,v in sorted(d.items()) if not k.startswith('RAILWAY_')]"
# DB / migration / row checks + Worker heartbeat:
railway variables --service ProcuLink --json | python .live-fixtures/dbcheck.py
railway variables --service ProcuLink --json | python .live-fixtures/workercheck.py
# DNS (email auth):
nslookup -type=txt _dmarc.proculink.eu 8.8.8.8
```

---

## 7. Launch gate summary (2026-06-07)

Green: infra (API/Worker/Vercel/Neon online), health/readiness, email auth
(SPF + DKIM + DMARC all resolving), observability (Sentry capturing, PostHog
ingesting), billing wired (TEST mode). **The only remaining launch gate is the
founder-only Stripe live-mode swap** (target June 9) — [`stripe-go-live-runbook.md`](stripe-go-live-runbook.md).
