# Reliability & Incident Response

ProcuLink's job is to deliver your purchase orders to your suppliers reliably and on time. This page explains how we operate the platform and what happens when something breaks.

## Architecture for resilience

- **Stateless API**: the `ProcuLink.Api` process can be horizontally scaled and restarted at any moment without data loss. Sessions are not stored locally.
- **Worker isolation**: order parsing, transformation, and delivery run on a separate `ProcuLink.Worker` process (Hangfire-on-Postgres). Worker restarts do not affect the API.
- **Database**: PostgreSQL on Railway, EU region. Automated daily backups, point-in-time-recovery on Distributor and Enterprise plans.
- **File storage**: Cloudflare R2 with object versioning; deletions are recoverable for 30 days.

## Retry policy

Every background job has automatic retries with exponential backoff:

| Job | Attempts | Backoff |
|---|---|---|
| `ParseOrderJob` | 3 | 30s → 120s → 600s |
| `TransformOrderJob` | 3 | 30s → 120s → 600s |
| `DeliverOrderJob` | 3 | 30s → 120s → 600s |
| `EmailPollingJob` | – | Re-runs every 5 minutes anyway |

After 3 attempts, the order is moved to `delivery_failed` (or equivalent failed state per job) and surfaced in the customer's exception queue. The audit trail records every attempt with HTTP status, response body (truncated to 4 KB), and timestamp.

Operators can manually re-queue a failed delivery via `POST /api/orders/{id}/redeliver`, which bypasses the `AutoDeliver` flag and runs the dispatch fresh.

## Delivery audit trail

Every dispatch attempt is recorded in the `delivery_attempts` table with:

- `OrderId`
- `ArtifactId`
- `DispatcherType` (`http_webhook`, `erp_erply`, `erp_directo`, etc.)
- `HttpStatusCode`
- `ResponseSnippet` (4 KB max, secrets redacted)
- `Success` / `ErrorReason`
- `AttemptNumber`
- `CreatedAt`

This trail is visible per-order in the app and exportable via the audit API. It is the single source of truth when a customer asks "did the supplier receive my PO?"

## Observability

- **Error tracking**: Sentry captures unhandled exceptions in both API and Worker, with PII scrubbing on the wire.
- **Structured logging**: every log line includes `OrgId`, `OrderId` (when applicable), and a correlation ID. Logs are searchable via Railway's log viewer.
- **Health endpoints**:
  - `GET /health/live` — basic liveness (process is running)
  - `GET /health/ready` — readiness (DB reachable, R2 reachable, Hangfire queue depth healthy)

## Status page

Public status page at **status.proculink.eu** (UptimeRobot-backed, available at general-availability launch). Three monitors:

- API endpoint (`/health/live`)
- Marketing site
- Frontend app

Status incidents are posted with timestamps, scope, impact, and updates every 30 minutes until resolved.

## Incident response

When something is broken:

1. **Detection** — Sentry alert or status-page check fires; operator paged.
2. **Triage** — within 15 minutes during EU business hours, within 1 hour outside, an operator confirms scope.
3. **Customer communication** — for incidents affecting >1 customer, a status-page post within 30 minutes of triage. For incidents affecting a single customer, direct email within 60 minutes.
4. **Mitigation** — usually a rollback (Railway deploy revert is <2 minutes) or a worker restart.
5. **Post-incident review** — published within 7 days for any incident lasting >30 minutes. Format: what happened, what was the impact, root cause, what we changed.

## SLAs (per plan)

| Plan | Uptime target | Support response (business hours, CET) | Support response (off-hours) |
|---|---|---|---|
| Pilot | Best effort | 1 business day | – |
| Growth | 99.0% monthly | 1 business day | – |
| Operations | 99.5% monthly | 4 business hours | 1 business day |
| Distributor | 99.7% monthly | 2 business hours | 4 hours |
| Enterprise | Contract-specific (typically 99.9%) | 1 business hour | 1 hour |

Uptime is measured against the API endpoint. Scheduled maintenance is excluded with at least 7 days' notice (Operations+) or 24 hours (Growth).

## What we are honest about

- We are a small team. There is no 24/7 NOC. Incidents outside Tallinn business hours are mitigated as fast as we can but not as fast as a Fortune-100 IT department.
- Our SLAs are credible because we operate on Railway + Cloudflare + Postgres — none of which we run ourselves. If Cloudflare R2 has an outage in the EU region, ProcuLink has an outage.
- We do not promise zero data loss. We promise daily backups and 30-day R2 versioning, which means worst-case point-of-loss is the last 24 hours.

If your business requires stronger guarantees (real-time replication, 4-nines SLA, named on-call engineer), that's an Enterprise conversation.

## Reporting an issue

- In-app: top-right "Help" → "Report an issue"
- Email: `support@proculink.eu`
- Critical (production outage on your account): `urgent@proculink.eu` — paged immediately on Operations+ plans

Please include: organisation name, affected order IDs, what you observed, what you expected, and a screenshot if possible.
