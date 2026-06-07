# Launch-readiness verification — 2026-06-07 (live prod sweep)

Full sweep of every launch-critical surface, driven against the live founder session
+ authoritative network probes. Backend `main` `4af5dd5` (988 tests), frontend `main`
`0ceb156`. **One concrete gap: DMARC missing.** Everything else is green.

## Infrastructure

| Surface | State | Evidence |
|---|---|---|
| Railway API `ProcuLink` | **Online** | `api.proculink.eu` healthy; service Online |
| Railway Worker `aware-amazement` | **Online** | heartbeat ~29s; processes transform/deliver jobs |
| Vercel `project-proculink` | **Ready** | prod = `main 0ceb156` (matches HEAD); domains proculink.eu + www |
| Neon Postgres | healthy | all migrations applied (incl. `requeue_count`, `is_sample`, `slug`) |

### Railway env (names only — values never read)
- **API** has all required keys: `Clerk__Authority`, `ConnectionStrings__DefaultConnection`,
  `Ai__OpenAI__ApiKey`, `Analytics__PostHog__{ApiKey,Host}`, `DataProtection__EncryptionKey`,
  `Delivery__EncryptionKey`, `Security__ApiKeyHashSecret`, `Sentry__Dsn`, `Storage__R2*` (incl.
  `R2AccountId`), **all Stripe keys** (`SecretKey`, `WebhookSecret`, Growth/Operations/Integration/
  Distributor + Yearly price IDs), `Smtp__*` (support form), `Admin__{Emails,UserIds}`,
  `Inbound__Postmark__WebhookToken`, `Frontend__Url`.
- **Worker** has its needed subset (DB, R2, both encryption keys, ApiKeyHashSecret, Sentry DSN,
  OpenAI, PostHog, Clerk, Frontend). Correctly omits API-only keys (Stripe/Admin/Smtp/Inbound).

## DNS (proculink.eu — authoritative `Resolve-DnsName`)

| Record | Value | Status |
|---|---|---|
| A `proculink.eu` / `www` | 76.76.21.21 (Vercel) | ✅ |
| `api.proculink.eu` | CNAME → `*.up.railway.app` | ✅ |
| MX `proculink.eu` | route1/2/3.mx.cloudflare.net | ✅ inbound email routing |
| SPF `proculink.eu` | `v=spf1 include:_spf.mx.cloudflare.net ~all` | ✅ (CF routing) |
| SPF `send.proculink.eu` | `v=spf1 include:amazonses.com ~all` | ✅ (Resend MAIL FROM) |
| MX `send.proculink.eu` | `feedback-smtp.eu-west-1.amazonses.com` | ✅ (Resend bounces) |
| DKIM `resend._domainkey` | RSA key present | ✅ Resend signing |
| GSC | `google-site-verification=…` | ✅ |
| DMARC `_dmarc.proculink.eu` | `v=DMARC1; p=none; rua=mailto:dim.novare+dmarc@gmail.com; fo=1` | ✅ **ADDED 2026-06-07** |

**Resend outbound is correctly configured** (SPF via `send.` subdomain + root DKIM) — SPF+DKIM
align. **DMARC was the only gap and is now closed:** added via the Cloudflare API (founder approved
`p=none`), verified resolving via 1.1.1.1 + 8.8.8.8. `p=none` is monitoring-only (cannot harm
deliverability); it enables aggregate reports + improves inbox placement at strict receivers
(Gmail/Microsoft). **Escalate to `p=quarantine` post-launch** once reports confirm all legit mail
passes. The email-auth trifecta (SPF + DKIM + DMARC) is now complete.

## Observability

- **Sentry** — DSN set on API + Worker; wiring correct (`UseSentry()` installed ahead of
  `UseExceptionHandler()`, so unhandled exceptions propagate through Sentry's scope before the
  handler converts them; `/health/ready` failures explicitly `CaptureException`). **Capturing.**
  - **All 22 open issues are STALE** (2d–2wk old, pre-fix): Organisation-not-resolved ×98
    (fixed `917cafd`), `is_sample`/`slug`/`data_protection_keys` migration errors (migrations
    since applied), R2 signature on upload/sample-order (fixed `28bf8d7`/`7a92959`), GET /api/orders
    DbCommand ×133 (`is_sample`-era), missing `R2AccountId` startup (now set). All these exact paths
    were exercised successfully this session. The 2–4d frontend errors (`/inbox/:orderId` 'status'/
    'length' TypeErrors, `/library/suppliers`) **do not reproduce** on the current build — both routes
    render clean with zero console errors.
  - Minor follow-up (next chip): the Postmark inbound webhook logs at **Error** for unauthorized/
    unconfigured calls (likely bot probes on the public endpoint) → downgrade auth failures to Warning
    to cut Sentry noise. Recommend bulk-resolving the stale issues so a post-launch error stands out.
- **PostHog** — receiving **live events** (founder `user_3EV…` pageview on /inbox, web library).
  Confirmed ingesting from prod.

## Billing (Stripe)

- TEST mode. Browser dashboard is hard-blocked (financial-site guardrail) — verified instead via
  the Stripe API earlier this session (4 products + monthly/yearly prices active, incl. Distributor)
  and via Railway env (`SecretKey` + `WebhookSecret` + all 8 price IDs present). Distributor checkout
  session created successfully.
- **Live-mode swap is founder-only** (target June 9) — runbook: `docs/deployment/stripe-go-live-runbook.md`.

## Net

Launch-ready across infra, observability, email (SPF+DKIM+DMARC all green), and billing
(test mode). **No open infra actions** — the DMARC gap is closed. The only remaining
launch gate is the founder-only Stripe live-mode swap (target June 9). Everything else is
verified green on the current deployed build.
