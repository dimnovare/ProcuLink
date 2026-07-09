# Postmark inbound-webhook hardening — Cloudflare Worker

**Status: PREPARED, NOT DEPLOYED** (written 2026-07-09). Deployment needs the
founder's Cloudflare access — nothing in this folder has been deployed, and no
backend code has been changed.

## Why this exists

The inbound-email flow is:

```
{slug}@orders.proculink.eu
  → Cloudflare Email Routing MX
  → Postmark inbound
  → webhook POST https://api.proculink.eu/api/inbound-email/postmark?token=…
```

Postmark **inbound** webhooks are **not HMAC-signed** and cannot send custom
headers — the only authentication today is the shared token embedded in the
webhook URL (`?token=`, checked in
`ProcuLink.Api\Controllers\InboundEmailController.cs`). A URL-embedded secret
is weaker than a signature: it appears in logs, screenshots, and browser
history, and anyone holding it can POST forged order emails from anywhere on
the internet.

The accepted mitigation (deferred at inbound-email launch, prepared here) is a
small Cloudflare Worker in front of the webhook that:

1. **Allows only Postmark's published webhook source IPs** — everything else
   gets 403 before it ever reaches the API.
2. **Adds a second secret header** (`X-Inbound-Proxy-Secret`) that the API is
   then configured to *require* — this is what actually closes the
   direct-to-origin path (see "Honesty notes").
3. **Rate-limits** — best-effort in the Worker, properly via a Cloudflare WAF
   rate-limiting rule (below).

The Worker itself is `worker.js` in this folder — dependency-free, ~180 lines,
readable top to bottom.

---

## Verified current state (2026-07-09)

`api.proculink.eu` is **NOT proxied through Cloudflare**. Verified via DNS:

```
api.proculink.eu.  CNAME  example.invalid.   → 69.46.46.125 (Railway edge, not a CF IP)
```

Consequence: **a Worker route on `api.proculink.eu` cannot intercept traffic
today** — Worker routes only fire on proxied (orange-cloud) hostnames. That
gives two deployment variants; **Variant B is recommended**.

---

## Decision point 1 — Variant A or Variant B

### Variant B (recommended): dedicated proxied hostname `inbound.proculink.eu`

Bind the Worker to a new hostname `inbound.proculink.eu` (a *Workers Custom
Domain* — Cloudflare creates the proxied DNS record automatically). The Worker
forwards valid requests to `https://api.proculink.eu` (which stays DNS-only,
untouched). Then change the Postmark inbound webhook URL to:

```
https://inbound.proculink.eu/api/inbound-email/postmark?token=<existing token>
```

- ✅ Zero impact on existing API traffic (frontend, Clerk, Stripe webhooks,
  REST ingress, uploads) — nothing else changes.
- ✅ No SSL-mode or Railway-proxy interactions to worry about.
- ➖ One-time Postmark webhook-URL change (Postmark dashboard → your server →
  Message Streams → *Inbound* stream → Settings → Webhook URL).

### Variant A: proxy `api.proculink.eu` + Worker route

Flip `api.proculink.eu` to proxied (orange cloud) in Cloudflare DNS, then add
a Worker route `api.proculink.eu/api/inbound-email/*`.

- ✅ No Postmark URL change.
- ⚠️ **ALL API traffic** now flows through Cloudflare, not just the webhook:
  - Zone SSL/TLS mode must be **Full (strict)** — *Flexible* would loop
    against Railway's HTTPS redirect.
  - Cloudflare Free-plan limits then apply to every API request, notably the
    **100 MB request-body cap** (browser uploads, catalog imports).
  - Railway's dashboard may warn about a detected Cloudflare proxy; Railway's
    Let's Encrypt renewal for the custom domain must keep working through the
    proxy (`/.well-known/acme-challenge` passes through, but this is a moving
    part you now own).
  - Cloudflare terminates TLS for all API traffic (minor data-handling
    consideration).
- ⚠️ Bigger blast radius: a Cloudflare incident or misconfiguration now
  affects the whole API, not just inbound email.

**Recommendation: Variant B.** It is additive, reversible in one Postmark
settings change, and cannot break anything that works today.

## Decision point 2 — where the second secret lives

One secret value, generated once, stored in exactly two places:

| Where | Key | Role |
|---|---|---|
| Cloudflare Worker (encrypted secret) | `INBOUND_PROXY_SECRET` | Worker stamps it into `X-Inbound-Proxy-Secret` on every forwarded request |
| Railway — **API service `ProcuLink`** (env var) | `Inbound__Postmark__ProxySecret` | API requires the header to match (after the API-side change below) |

The Railway **Worker** service (`aware-amazement`) does **not** need it — it
never serves the HTTP webhook. Do not put the secret anywhere else (not in the
frontend, not in Postmark, not in git).

Generate it (32 random bytes, base64):

```powershell
# PowerShell (Windows)
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```
```bash
# or bash
openssl rand -base64 32
```

---

## Deploy — Variant B (recommended)

### Option 1: wrangler CLI

1. Copy this folder somewhere outside the repo checkout (or work in place —
   nothing here is a build artifact) and add a `wrangler.toml` next to
   `worker.js`:

   ```toml
   name = "postmark-inbound-verify"
   main = "worker.js"
   compatibility_date = "2026-07-01"

   # Workers Custom Domain — Cloudflare creates the proxied DNS record
   # for inbound.proculink.eu automatically on deploy.
   routes = [
     { pattern = "inbound.proculink.eu", custom_domain = true }
   ]

   [vars]
   ORIGIN_HOST = "api.proculink.eu"
   ```

2. Authenticate and deploy (needs your Cloudflare account with the
   `proculink.eu` zone):

   ```bash
   bunx wrangler login
   bunx wrangler deploy
   bunx wrangler secret put INBOUND_PROXY_SECRET   # paste the generated secret
   ```

   (`bunx wrangler` avoids a global npm install, per project convention.
   A scoped API token also works: set `CLOUDFLARE_API_TOKEN` with
   *Workers Scripts: Edit* + *Zone: DNS: Edit* on `proculink.eu` and skip
   `wrangler login`.)

### Option 2: dashboard paste (no CLI)

1. Cloudflare dashboard → **Workers & Pages** → **Create** → *Create Worker*
   → name it `postmark-inbound-verify` → **Deploy** the hello-world, then
   **Edit code** → replace everything with the contents of `worker.js` →
   **Deploy**.
2. Worker → **Settings** → **Variables and Secrets**:
   - Add **secret** `INBOUND_PROXY_SECRET` = the generated value.
   - Add plaintext var `ORIGIN_HOST` = `api.proculink.eu` (optional — this is
     the default).
3. Worker → **Settings** → **Domains & Routes** → **Add** → *Custom Domain* →
   `inbound.proculink.eu`. Cloudflare creates the DNS record and certificate
   automatically.

### Then, for either option

4. **Smoke-test the front door** (from your own machine — you are *not* a
   Postmark IP, so a 403 here is the success signal):

   ```bash
   curl -i -X POST "https://inbound.proculink.eu/api/inbound-email/postmark?token=x" -d '{}' -H "Content-Type: application/json"
   # expect: 403 {"error":"source address not allowed"}
   curl -i "https://inbound.proculink.eu/anything-else"
   # expect: 404 {"error":"not found"}
   ```

5. **Point Postmark at the Worker**: Postmark dashboard → server → Message
   Streams → **Inbound** stream → Settings → change the webhook URL host from
   `api.proculink.eu` to `inbound.proculink.eu` (keep the path and the
   existing `?token=` exactly as they are).

6. **Prove the mail path**: send a test email to the known-good inbound
   address (`personal-workspace-d3be` org slug at `orders.proculink.eu`) and
   confirm the POST shows **200** in Postmark → Activity → Inbound, and the
   order appears in the app.

7. Proceed to the **API-side change** below — until that ships and the Railway
   env var is set, the old direct door is still open (see "Honesty notes").

### Optional but recommended: real rate limiting (Cloudflare WAF)

The Worker's built-in token bucket is per-isolate (each Cloudflare datacenter
isolate has its own bucket, reset on eviction) — a best-effort backstop only.
For a real distributed limit, add the WAF rule (1 rate-limiting rule is
included on the Free plan):

Dashboard → `proculink.eu` zone → **Security** → **WAF** → **Rate limiting
rules** → Create:

- If: `Hostname equals inbound.proculink.eu`
- Rate: e.g. **300 requests / 1 minute per IP** (generous — normal traffic is
  one POST per inbound email, and only Postmark's 4 IPs get past the Worker)
- Action: **Block** for 1 minute.

## Deploy — Variant A (only if you deliberately choose it)

1. Cloudflare dashboard → `proculink.eu` → DNS → edit `api.proculink.eu` →
   toggle **Proxy status** to *Proxied* (orange cloud). Keep the CNAME target
   `example.invalid` exactly as is.
2. Zone → **SSL/TLS** → set encryption mode to **Full (strict)**. Verify
   `https://api.proculink.eu/scalar` (or any endpoint) still answers before
   going further — if it errors, flip the proxy back off and stop.
3. Deploy the Worker as above, but with a **route** instead of a custom
   domain — in `wrangler.toml`:

   ```toml
   routes = [
     { pattern = "api.proculink.eu/api/inbound-email/*", zone_name = "proculink.eu" }
   ]
   ```

   (or dashboard: Worker → Settings → Domains & Routes → *Route* →
   `api.proculink.eu/api/inbound-email/*`, zone `proculink.eu`.)
4. No Postmark URL change needed. Smoke-test with the same curl as Variant B
   but against `api.proculink.eu/api/inbound-email/postmark` (expect 403), and
   confirm an unrelated API endpoint is **not** intercepted.
5. Note: the Worker rewrites the request to `ORIGIN_HOST` = the same hostname;
   same-zone subrequests go straight to origin and cannot re-trigger the
   Worker (Cloudflare prevents Worker recursion), so this is safe.

---

## Railway env addition

On the Railway **API service `ProcuLink`** only (not the Worker service):

```
Inbound__Postmark__ProxySecret=<the generated secret>
```

Do **not** set this until the API-side change below is deployed *and* the
Cloudflare Worker is live in front of Postmark — the check is opt-in-by-config
precisely so the rollout order can never break inbound mail.

## API-side change (NOT implemented in this task — description only)

**File:** `ProcuLink.Api\Controllers\InboundEmailController.cs`, action
`Postmark` (`POST /api/inbound-email/postmark`).

**Change:** immediately after the existing shared-token gate (the
`CryptoEquals(expected, presented)` check that returns
`Unauthorized(new { error = "Invalid webhook token." })`, around line 80–84),
add a second, **opt-in** gate:

- Read `_config["Inbound:Postmark:ProxySecret"]`.
- If it is null/whitespace → **skip the check entirely** (feature off; today's
  behavior; makes the deploy inert until the Railway env var is set).
- If it is set → read the request header `X-Inbound-Proxy-Secret` (add a
  constant next to the existing `TokenHeader`, e.g.
  `private const string ProxySecretHeader = "X-Inbound-Proxy-Secret";`) and
  compare with the existing `CryptoEquals` helper (constant-time). On
  mismatch/absence: `_logger.LogWarning(...)` and return
  `Unauthorized(new { error = "Invalid webhook token." })` — the **same**
  message as the token failure, so probers can't tell which layer rejected
  them.
- Keep the existing token check as-is — the layers stack, neither replaces the
  other.

Add matching empty-string defaults to `appsettings.Development.json` under a
new `"Inbound": { "Postmark": { "ProxySecret": "" } }` node if the `Inbound`
section is materialized there (it currently is not — config comes from Railway
env in prod), and cover with a unit test in `ProcuLink.Api.Tests`: secret
configured + wrong/missing header → 401; secret configured + correct header +
correct token → 200; secret not configured → today's behavior unchanged.

**Rollout order (zero mail loss):**

1. Deploy CF Worker + custom domain (inert — nothing points at it yet).
2. Merge + deploy the API-side change (inert — env var not set).
3. Flip the Postmark inbound webhook URL to `inbound.proculink.eu` (Variant B).
4. Prove a real email end-to-end (Postmark Activity shows 200).
5. Set `Inbound__Postmark__ProxySecret` on the Railway API service → the
   direct `api.proculink.eu` door now rejects requests that lack the header,
   even with a valid `?token=`.
6. Re-prove one more real email end-to-end.

Rollback at any step = undo that step; no step strands mail (Postmark retries
failed webhook deliveries and shows failures in the Activity log).

---

## Honesty notes — what this does and does not protect against

**What it stops:**

- **Random-internet probing of the webhook.** Today anyone who finds or
  guesses the URL can hammer `POST /api/inbound-email/postmark` — brute-force
  the token, flood junk payloads into the rate-limit budget, fuzz the parser.
  After hardening, non-Postmark sources are dropped at Cloudflare's edge and
  never touch Railway.
- **A leaked `?token=` becoming a forgery key.** The token rides in the URL,
  so it leaks easily (logs, screenshots, Postmark dashboard viewers, browser
  history). Once the API requires `X-Inbound-Proxy-Secret`, a leaked token
  alone is useless: the attacker would also need the proxy secret (never in a
  URL) *and* — to come through the front door — a Postmark source IP.
- **Runaway floods**, partially: per-isolate token bucket in the Worker plus
  the (recommended) WAF rate-limiting rule cap replay/retry storms.

**What it does NOT stop:**

- **A Postmark compromise.** Payloads are unsigned; anyone who can originate
  traffic from Postmark's webhook IPs (Postmark itself, an attacker inside
  Postmark, or in the worst case an attacker who acquires those AWS addresses)
  can still deliver fully forged order envelopes that will be parsed and
  routed. Spoofed-source *content* remains possible from that position —
  there is no cryptographic origin proof to check.
- **Email-sender spoofing.** This hardens the *webhook transport*, not the
  *mail*. Anyone on the internet can still email
  `{slug}@orders.proculink.eu`; it arrives via legitimate Postmark IPs with
  the valid secret. Enforcing SPF/DKIM/DMARC verdicts (Postmark includes them
  in the payload's `Headers`) and/or per-org sender allowlists is a separate,
  future control at the application layer.
- **Direct-to-origin bypass — until the API-side change ships.** In *both*
  variants the app remains directly reachable: `api.proculink.eu` stays
  DNS-only (Variant B), and the Railway-issued `example.invalid`
  hostname exists regardless of variant. Edge IP-allowlisting alone is
  therefore decorative; **the API requiring `X-Inbound-Proxy-Secret` is the
  step that makes it real.** Do not stop after deploying the Worker.
- **DoS at scale.** Cloudflare absorbs edge floods, but the Free-plan Worker
  and WAF limits are blunt instruments; the in-Worker bucket is per-isolate
  best-effort only.

**Maintenance:**

- **Postmark IP drift:** the allowlist is hardcoded in `worker.js`
  (`POSTMARK_WEBHOOK_SOURCES`, marked `REFRESH-ME`). Source of truth:
  <https://postmarkapp.com/support/article/800-ips-for-firewalls>. Symptom of
  drift: inbound emails failing in Postmark → Activity with 403
  `source address not allowed`. Fix: update the array, redeploy the Worker.
- **Secret rotation:** generate a new value, update the Railway env **and**
  the Worker secret in quick succession (order: Worker first, then Railway —
  or briefly unset the Railway var to disable the check during rotation).
- **This folder is documentation + source of truth for the Worker code.** If
  the deployed Worker is ever edited in the dashboard, mirror the change back
  here.
