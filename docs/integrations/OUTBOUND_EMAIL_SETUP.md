# Outbound Email Setup — HTTP Email API (Postmark)

_Operator runbook. ProcuLink sends ALL outbound mail through a managed HTTP email API over HTTPS
(port 443), NOT raw SMTP. Reason: the cloud host (Railway) blocks outbound SMTP ports 25/465/587,
so a raw `SmtpClient.ConnectAsync` always fails there. HTTPS is never blocked, and the provider
relays from deliverability-managed IPs (SPF/DKIM/DMARC)._

## What this powers (two consumers, one provider)

| Consumer | Class | When |
|---|---|---|
| Transactional mail (support contact form, notifications) | `PostmarkEmailSender : IEmailSender` | always, when a token is set |
| Supplier **Email** delivery channel (PO as attachment) | `EmailApiDeliveryDispatcher` (protocol `email`) | when a supplier's delivery config uses protocol `email` |

Both delegate to `IEmailApiClient` → `PostmarkEmailApiClient`. One Postmark account/token serves both.
The legacy raw-SMTP delivery dispatcher (`SmtpDeliveryDispatcher`, protocol `smtp`) is **retired from
offered channels** — kept only as a self-host opt-in (`Delivery:EnableSmtp=true`).

## Behaviour without configuration (safe default)

No `Email:Postmark:ServerToken` →
- `IEmailApiClient.IsConfigured = false`.
- Transactional `IEmailSender` falls back to `MailKitEmailSender` (only if `Smtp:Host` is set — self-host) else `ConsoleEmailSender` (logs only). Support form still 200s.
- The `email` delivery channel returns a clean `delivery_failed` with _"Email delivery is not configured on this deployment"_ — honest, never a silent success.

## Activation steps

### 1. Provider (Postmark)
1. Postmark account → create a **Server** (or reuse the inbound one). Copy its **Server API Token**.
2. **Sender Signatures / Domains**: verify the sending domain `proculink.eu` (or a sub-domain like
   `mail.proculink.eu`). Postmark gives you the DNS records for step 2.

### 2. DNS (deliverability — do all three)
Add the records Postmark shows for the verified domain:
- **DKIM** — the `TXT` record Postmark generates (signs every message).
- **Return-Path / CNAME** — Postmark's custom Return-Path (aligns bounce handling, improves DMARC).
- **SPF** — ensure `proculink.eu`'s SPF `TXT` includes `include:spf.mtasv.net` (Postmark).
- (Recommended) a **DMARC** `TXT` at `_dmarc.proculink.eu`, e.g. `v=DMARC1; p=none; rua=mailto:dmarc@proculink.eu`.

The `From` address you use (`Email:Postmark:From`, default `orders@proculink.eu`) MUST be on a
verified domain, or Postmark rejects the send.

### 3. Config (Railway env on BOTH the API and the Worker)
The Worker runs `TransformOrderJob → DeliveryService`, so the **Worker** also needs the token for the
`email` delivery channel; the API needs it for the support form (and test-fire). Set on both:
```
Email__Postmark__ServerToken=<server API token>
Email__Postmark__From=orders@proculink.eu          # must be a verified sender
Email__Postmark__MessageStream=outbound            # optional, defaults to "outbound"
```
(`__` is the .NET/Railway form of the `:` config separator.) Redeploy both services.

### 4. Verify
- **Transactional:** submit the support contact form; confirm receipt + a `200` in Postmark's Activity.
- **Delivery:** on a test supplier, set delivery protocol to **Email**, recipients = a mailbox you
  control, then use the supplier delivery **test-fire** (`POST /api/suppliers/{id}/delivery-config/test-fire`)
  or run a real order through transform→deliver. Confirm the PO arrives as an attachment, FROM the
  verified sender, with the buyer in Reply-To.

## Per-supplier Email delivery config (what the UI writes)

Protocol `email`. ConfigJson shape (no credentials — sent from ProcuLink's verified sender):
```json
{
  "toAddresses": "po@supplier.example, sales@supplier.example",
  "replyTo": "purchasing@your-company.example",
  "subjectTemplate": "Purchase Order {poNumber}",
  "bodyTemplate": "Please find the attached purchase order ({fileName}).",
  "attachmentFileName": "optional-override.xml"
}
```
`toAddresses` may be a comma-separated string or a JSON array. `{poNumber}` and `{fileName}` are
substituted. `fromAddress` is also accepted but must be a provider-verified domain — default to the
platform sender and leave it unset.

## EU data-residency note

Postmark is US-hosted. The `IEmailApiClient` seam is provider-neutral — to send in-EU instead, add a
`MailgunEmailApiClient` (EU region) or `SesEmailApiClient` (eu-central-1) implementing the same
interface and swap the DI registration. No change to the sender or dispatcher. Flag this for buyers
with strict EU-residency requirements; until then Postmark is the single-provider default (it also
backs inbound — see [INBOUND_EMAIL_ACTIVATION.md](INBOUND_EMAIL_ACTIVATION.md)).
