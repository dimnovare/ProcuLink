# proculink.eu — Production Domain Cutover Runbook

Live infra wiring for the `proculink.eu` custom domain. Verified via CLI on 2026-05-30.
DNS is parked at **Cloudflare** (nameservers `kami.ns.cloudflare.com` + `kolton.ns.cloudflare.com`);
zone goes active after the registrar (**Zone**) finishes DNSSEC-off and switches nameservers.

## Topology (verified)

| Piece | Where | Detail |
|---|---|---|
| Frontend | **Vercel** | project `project-proculink` (scope `dimnovare-9994s-projects`) → `project-proculink.vercel.app` |
| API | **Railway** | project `lucid-generosity` / env `production` / service **ProcuLink** (Dockerfile, `/health`, port **8080**) |
| Worker | **Railway** | same project, service **aware-amazement** (`Dockerfile.worker`, no public domain) |
| Region | Railway | `europe-west4` (EU) |
| DB | Postgres | `ConnectionStrings__DefaultConnection` on both Railway services (parity ✓) |
| Registrar | **Zone** | DNSSEC being disabled, then NS → Cloudflare |
| DNS | **Cloudflare** | wrangler logged in (`redacted@example.invalid`); zone pending activation |

## Domain scheme

- `proculink.eu` (apex) + `www` → **Vercel** (frontend)
- `api.proculink.eu` → **Railway** ProcuLink/API (custom domain added in dashboard 2026-05-30)

## DNS records to add in Cloudflare (once zone is active — ALL **DNS-only / grey cloud**)

> Grey cloud is required: Vercel and Railway each terminate their own TLS and do their own
> domain verification. Proxying (orange) breaks cert issuance + Railway's TXT verification.

| Type | Name | Value | Source |
|---|---|---|---|
| A | `proculink.eu` (apex) | *use the exact IP Vercel shows* (currently `76.76.21.21`) | Vercel → add domain |
| CNAME | `www` | `cname.vercel-dns.com` | Vercel |
| CNAME | `api` | `example.invalid` | Railway (custom domain) |
| TXT | `_railway-verify.api` | `railway-verify=a055cfa8a4df5617495d04a71344dfb0a4ac30dc2cb5b61d99b55e6deca1d3ae` | Railway (verification) |
| CNAME | `clerk` / `accounts` / `clkmail` … | *from Clerk prod instance* | Clerk (if/when prod instance created) |

## Env changes at cutover

**Railway — both ProcuLink (API) and aware-amazement (Worker):**
- `Frontend__Url` → `https://proculink.eu` (drives backend CORS allow-origin + Stripe success/cancel redirects). ⚠️ Do NOT change before DNS is live — breaks CORS for the current Vercel URL.

**Vercel — project-proculink (Production):**
- `NEXT_PUBLIC_API_BASE_URL` → `https://api.proculink.eu` (currently the Railway `.up.railway.app` URL). Redeploy after.

## Verify before launch
- [ ] **`NEXT_PUBLIC_USE_MOCK` = `false`** in Vercel Production (if `true`, the live site runs on mock data — launch blocker).
- [ ] `NEXT_PUBLIC_API_BASE_URL` resolves to the live API.
- [ ] API `/health` returns 200 over `https://api.proculink.eu`.

## Still requires dashboards (not CLI)
- **Clerk production instance** ⚠️ biggest gotcha — current `Clerk__Authority` is the **dev** instance (`golden-alpaca-43.clerk.accounts.dev`). Create a prod instance, set domain `clerk.proculink.eu` (adds its own Cloudflare CNAMEs), then swap `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` + `CLERK_SECRET_KEY` (Vercel) and `Clerk__Authority` (Railway, both services). The `clerk` CLI can manage config-as-code but not provision the prod domain.
- **Stripe** — point the webhook to `https://api.proculink.eu/api/billing/webhook`; confirm `Stripe__WebhookSecret`. Create the **Distributor €1,499/mo price** and set `Stripe__DistributorPriceId` (already a key on Railway).

## Optional founder-config vars (features stay hidden until set — not blockers)
`NEXT_PUBLIC_STATUS_URL`, `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`, `NEXT_PUBLIC_BOOK_DEMO_URL`.

## Status log
- ✅ 2026-05-30 — Railway custom domain `api.proculink.eu` (port 8080) added (dashboard; CLI `railway domain` returns Unauthorized — likely account/plan gate on custom domains).
- ✅ 2026-05-30 — Worker `DataProtection__EncryptionKey` copied from API → parity True.
- ✅ Already configured (despite STATUS "pending"): Vercel PostHog keys, Clerk post-signup redirect (`NEXT_PUBLIC_CLERK_SIGN_UP_FORCE_REDIRECT_URL`), Sentry.
- ⏳ Pending: Zone DNSSEC-off + NS switch → Cloudflare zone active → add DNS records above → flip env URLs → Clerk prod instance → Stripe webhook URL.

## CLI access (this machine)
Railway ✅ (`redacted@example.invalid`), Vercel ✅ (`dimnovare-9994`), Cloudflare/wrangler ✅, Clerk CLI not installed. No `gh`.
