# Inbound Email Activation — `orders@{slug}.proculink.eu`

_Operator runbook. The code is already shipped (`InboundEmailController` + `InboundEmailRouter`,
registered in `ProcuLink.Api/Program.cs`). Turning it on is **DNS + one env var** — no code change._

> **Why this exists.** Customers ask "just give me an email address to forward orders to"
> within minutes of the first demo. This is that feature. ProcuLink does **not** run its own
> MX/SMTP server (Railway blocks inbound mail anyway). A managed provider (Postmark Inbound)
> receives the mail on its MX, parses the MIME, and **POSTs an HTTPS JSON webhook** to us.
> HTTPS/443 is never blocked, so this works on Railway where raw SMTP does not.

---

## What already works without this

| Inbound channel | State | Needs |
|---|---|---|
| Browser upload | ✅ live | nothing |
| Inbound REST API `POST /api/ingress/{slug}/orders` (+ `plk_` key) | ✅ live | hand the customer their API key |
| IMAP poll (app polls a mailbox **you** own) | ✅ live, works on Railway (port 993) | per-org IMAP config + Worker running (`*/5 * * * *`) |
| **Email address** `orders@{slug}.proculink.eu` | ✅ code built, **dormant** | this runbook |

The email-address path and the IMAP path are independent. IMAP = you own a mailbox and we poll
it. Email-address = the provider owns the MX and pushes to our webhook. Most customers want the
email-address path because there is no mailbox for them to provision.

---

## Endpoint contract (already deployed)

- **Route:** `POST /api/inbound-email/postmark`
- **Auth:** shared secret in header `X-Postmark-Server-Token`, compared (constant-time) against
  config key `Inbound:Postmark:WebhookToken`. If that key is unset, the endpoint returns
  **401 "Inbound webhook is not configured."** — this is the current production state.
- **Tenant routing:** the recipient `orders@{slug}.proculink.eu` → `slug` → `Organisation.Slug`
  (auto-generated kebab-case + 4-hex suffix at org creation, e.g. `acme-trading-9f3a`). No per-org
  setup. Override/extra mapping: `Inbound:Postmark:TenantMapping:{slug} = <orgGuid>`.
- **Host suffix:** `Inbound:Postmark:HostSuffix` (default `.proculink.eu`). Set this if you host the
  inbound MX on a sub-domain (e.g. `.inbound.proculink.eu`).
- **Attachments accepted:** `.csv .xlsx .pdf .xml .cxml .edi .x12 .txt`. Cap **10 MB** per
  attachment (`IngressLimits.MaxFileBytes`). One order stub created per supported attachment.
- **Body fallback:** if no attachment yields an order and the message has a text body, the
  email-body NLP extractor runs (needs an OpenAI key; **skipped for no-egress orgs**).
- **Gates:** orgs in `read_only` / `trial_expired` status are rejected (audited, no order). The org
  must have **at least one supplier** — the router resolves the IMAP default supplier, else the
  oldest active supplier.
- **Responses:** `200` with `{orgId, createdOrderIds}`; `401` bad/missing token; `422` for genuinely
  unprocessable mail (unknown slug, blocked status, no supplier) — `422` tells Postmark **not** to retry.

---

## Activation steps

### 1. Provider (Postmark)
1. Create a Postmark account; add a **Server**; enable the **Inbound** stream.
2. Note the inbound address Postmark gives you (`<hash>@inbound.postmarkapp.com`) and/or configure
   an **Inbound Domain** so mail to your own domain is accepted.
3. Set the **Inbound Webhook URL** to `https://api.proculink.eu/api/inbound-email/postmark`.
4. Add a **custom header** `X-Postmark-Server-Token: <a-long-random-secret>` to the webhook
   (Postmark supports a basic-auth URL or a custom header; this controller checks the header).
   Generate the secret: `openssl rand -hex 32`.

### 2. DNS
Point inbound mail for the tenant domain at Postmark. Two shapes:

- **Whole sub-domain (recommended):** MX for `*.proculink.eu` (or a dedicated `*.inbound.proculink.eu`)
  → Postmark inbound MX (`inbound.postmarkapp.com`, priority 10). Then set
  `Inbound__Postmark__HostSuffix=.proculink.eu` (or `.inbound.proculink.eu`) to match.
- **Single address:** forward one mailbox to the Postmark inbound hash address. Simpler, but every org
  shares one address — only viable if you put the org slug in the `+slug@` plus-tag and adjust routing.

Add Postmark's recommended SPF/DKIM/Return-Path records for the **outbound** side (see the outbound
runbook) — not required for inbound but needed once outbound email delivery is on.

### 3. Config (Railway env on the **API** service)
```
Inbound__Postmark__WebhookToken=<the secret from step 1.4>
Inbound__Postmark__HostSuffix=.proculink.eu        # only if MX domain differs from default
# Optional per-slug override when an org's display slug must differ from Organisation.Slug:
# Inbound__Postmark__TenantMapping__some-slug=<orgGuid>
```
(`__` is the Railway/.NET env-var form of the `:` config separator.) Redeploy the API.

### 4. Verify
1. From a real mailbox, send a CSV-attached email to `orders@<a-real-org-slug>.proculink.eu`.
2. Watch Postmark's **Inbound activity** — confirm a `200` from our webhook.
3. Confirm a new order appears for that org (Inbox / `GET /api/orders`).
4. Negative check — replay the same webhook payload with a wrong token; expect `401`.

Local/manual webhook smoke (replace token + slug):
```bash
curl -i -X POST http://localhost:5223/api/inbound-email/postmark \
  -H "Content-Type: application/json" \
  -H "X-Postmark-Server-Token: <token>" \
  -d '{
        "From":"buyer@example.com",
        "OriginalRecipient":"orders@<slug>.proculink.eu",
        "Subject":"PO test",
        "TextBody":"see attached",
        "Attachments":[{"Name":"po.csv","ContentType":"text/csv","Content":"<base64-csv>"}]
      }'
```

---

## EU data-residency note

Postmark is US-hosted. For buyers with strict EU-residency requirements, the inbound webhook is
**provider-neutral on our side** (`InboundEmailController` maps any provider's payload into the
internal `InboundEmailPayload`). To run inbound in-EU instead, add a thin controller action that
maps the provider's JSON shape and reuses the same `IInboundEmailRouter`:

- **Mailgun (EU region)** — Routes → "store and notify" → POST to our webhook. EU region available.
- **SendGrid Inbound Parse** — POSTs `multipart/form-data` (not JSON); needs a small adapter action.

No router/pipeline change — only a new provider-shaped controller action. Until then, Postmark is the
single-provider default (it also backs outbound email delivery — see the outbound email channel).

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Webhook returns `401 "not configured"` | `Inbound:Postmark:WebhookToken` unset | set the env var + redeploy |
| Webhook returns `401 "Invalid webhook token"` | header secret ≠ config | align Postmark custom header with the env var |
| `422 "does not look like an inbound ProcuLink address"` | recipient host ≠ `HostSuffix` | fix MX domain or `HostSuffix` |
| `422 "Unknown tenant slug"` | slug ≠ any `Organisation.Slug` | check the org's real slug or add a `TenantMapping` override |
| `422 "no supplier configured"` | org has zero suppliers | add a supplier (or set the IMAP default supplier) |
| Mail accepted, no order | every attachment unsupported/oversized and no body, or no-egress org with no attachment | check supported extensions / 10 MB cap; check the `inbound_email.*` audit events |
