# Postmark inbound-webhook hardening — Cloudflare Worker

**Status: DEPLOYED** (written 2026-07-09; live on `inbound.proculink.eu` and
proven end-to-end with real mail on 2026-07-24 — see STATUS.md, OPS-1). Variant
B was taken and the API-side gate is on: `Inbound__Postmark__ProxySecret` is set
on the Railway API service.

> ⚠️ **The Worker is deployed by hand.** Cloudflare has no CI hook to this repo,
> so editing `worker.js` here changes nothing in production until the founder
> redeploys — see [Redeploying after a change to `worker.js`](#redeploying-after-a-change-to-workerjs).

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

1. **Allows only Postmark's published webhook source IPs** — everything else is
   refused with **503** before it ever reaches the API (retryable on purpose —
   see [Status codes are retry instructions](#status-codes-are-retry-instructions)).
2. **Adds a second secret header** (`X-Inbound-Proxy-Secret`) that the API is
   then configured to *require* — this is what actually closes the
   direct-to-origin path (see "Honesty notes").
3. **Rate-limits** — best-effort in the Worker, properly via a Cloudflare WAF
   rate-limiting rule (below).

The Worker itself is `worker.js` in this folder — dependency-free and readable
top to bottom; `worker.test.mjs` next to it pins its behaviour.

---

## Status codes are retry instructions

Postmark retries **any non-200** inbound webhook response ten times over ~10.5
hours, then files the message as **Failed** — which stays manually re-fireable
for the 45-day inbound retention. Two statuses opt out of that safety net:

| Response | Postmark's behaviour | Recoverable? |
|---|---|---|
| **200** | message settles as `Processed` | ❌ never re-delivered, cannot be re-fired |
| **403** | **retries stop on the first attempt**, message never reaches `Failed` | ❌ gone by both routes |
| any other non-200 | 10 attempts over ~10.5 h → `Failed` | ✅ automatic retries **and** manual re-fire |

Source: [Understanding inbound webhook retries](https://postmarkapp.com/support/article/understanding-inbound-webhook-retries-in-postmark).
(Confirmed in production on 2026-07-24: one 422'd message produced three
attempts in six minutes and stayed `Scheduled`.)

**This Worker therefore spends neither 200 nor 403.** Every refusal it can emit
is a condition an operator fixes — a stale IP allowlist, a mis-set webhook URL,
a burst — and none of them is a judgement about the mail, which the Worker never
reads. Its full response surface:

| Gate | Status | Reason string |
|---|---|---|
| Path is not `/api/inbound-email/postmark` | `404` | `not found` |
| Method is not POST | `405` | `method not allowed` |
| **`CF-Connecting-IP` not in `POSTMARK_WEBHOOK_SOURCES`** | **`503`** | `source address not allowed` |
| Per-isolate token bucket empty | `429` | `rate limited` |

The IP gate gets **503, not 403**, because the hardcoded allowlist is the single
most likely thing in this file to go stale — Postmark has changed its published
IPs before. Under 403 that drift would destroy every real purchase order on its
first attempt, with no retry and nothing in `Failed` to re-fire, and the only
evidence would be entries in an activity log nobody is watching. Under 503 the
same drift costs ~10.5 hours of delay and then parks each message in `Failed`,
where it survives for 45 days and can be re-fired the moment the list is
refreshed. 429 is deliberately **not** reused for it: the token bucket already
means that, and one meaning per status is what keeps Postmark's activity log
readable at a glance. The reason strings stay terse and distinct for the same
reason — `source address not allowed` is the founder's IP-drift signal.

This matches the model the API itself uses — see the `Postmark` action and the
`Ignored` helper in `ProcuLink.Api\Controllers\InboundEmailController.cs`, where
200 means "no re-delivery could ever change this" and non-200 means "try again".

### Running the Worker's tests

`worker.test.mjs` pins that contract (no dependencies, no CI wiring — this repo
has no JS pipeline). Re-prove it before any hand-deploy:

```bash
node --test docs/infra/postmark-inbound-verify-worker/worker.test.mjs
```

(or `node --test` from inside this folder — the local `package.json` exists only
to mark `worker.js` as an ES module for Node; wrangler ignores it.)

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

## Decision point 1 — Variant A or Variant B  ✅ *decided: Variant B, shipped*

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
   Postmark IP, so a 503 here is the success signal):

   ```bash
   curl -i -X POST "https://inbound.proculink.eu/api/inbound-email/postmark?token=x" -d '{}' -H "Content-Type: application/json"
   # expect: 503 {"error":"source address not allowed"}
   curl -i "https://inbound.proculink.eu/anything-else"
   # expect: 404 {"error":"not found"}
   ```

   A **403 here means an old build of the Worker is still live** — redeploy.
   (503 is the IP gate refusing you while keeping Postmark's retry window open;
   see [Status codes are retry instructions](#status-codes-are-retry-instructions).)

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
   but against `api.proculink.eu/api/inbound-email/postmark` (expect 503), and
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

## API-side change  ✅ *shipped and live*

Implemented in `ProcuLink.Api\Controllers\InboundEmailController.cs`, action
`Postmark` (`POST /api/inbound-email/postmark`), section *1b. Edge-proxy secret*,
and `Inbound__Postmark__ProxySecret` is set on the Railway API service. The
original specification is kept below as the record of what was built.

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
  drift: inbound emails failing in Postmark → Activity with 503
  `source address not allowed`. Fix: update the array, redeploy the Worker,
  then re-fire the affected messages — they retry for ~10.5 hours on their own
  and any that ran out of attempts are sitting in **Failed** (filter the inbound
  activity list by that status; the default view hides them) and can be re-fired
  with `POST /messages/inbound/{id}/retry` or the **Retry** button. Nothing is
  lost for 45 days.

### Redeploying after a change to `worker.js`

**Nothing here deploys itself.** Cloudflare is not wired to this repo — merging
a change to `worker.js` leaves production running the old code until the founder
redeploys by hand:

- **wrangler:** `bunx wrangler deploy` from a folder containing `worker.js` +
  the `wrangler.toml` above. Secrets and the custom domain survive; only the
  script is replaced.
- **dashboard:** Cloudflare → **Workers & Pages** → `postmark-inbound-verify` →
  **Edit code** → paste the new `worker.js` in full → **Deploy**.

Then re-run the step-4 smoke test above and confirm the IP refusal reads
**503**, not 403. No Postmark, Railway, or DNS change is needed for a
`worker.js`-only change, and rollback is Cloudflare → the Worker → **Deployments**
→ *Rollback* to the previous version.
- **Secret rotation:** generate a new value, update the Railway env **and**
  the Worker secret in quick succession (order: Worker first, then Railway —
  or briefly unset the Railway var to disable the check during rotation).
- **This folder is documentation + source of truth for the Worker code.** If
  the deployed Worker is ever edited in the dashboard, mirror the change back
  here.
