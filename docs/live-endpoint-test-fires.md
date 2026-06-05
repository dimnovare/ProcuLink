# Live endpoint test-fires

Integration tests that fire the **production** dispatchers/pollers at **real
external endpoints** (no mocks), closing the "unit-tested but never proven
against a real server" gap flagged in STATUS.md.

All are gated behind `PROCULINK_LIVE_ENDPOINT_TESTS=1` and read endpoints/creds
from env vars (nothing secret is committed), so the normal suite / CI skips them.

## Proven live (2026-06-05)

| Channel | Production code exercised | Real endpoint | Verified by |
|---|---|---|---|
| **HTTP + OAuth2 delivery** | `HttpDeliveryDispatcher` | Cloudflare Worker (public HTTPS) | dispatcher `Success` **+** Worker KV receipt showing a real `client_credentials` token fetch then `Bearer` on `/po` |
| **HTTP plain delivery** | `HttpDeliveryDispatcher` | same Worker `/po-plain` | `Success` + receipt |
| **SMTP delivery** | `SmtpDeliveryDispatcher` | Ethereal (`smtp.ethereal.email`, STARTTLS+auth) | `Success` + IMAP `SEARCH` found the message (subject + attachment) |
| **SFTP delivery** | `SftpDeliveryDispatcher` (SSH.NET) | `atmoz/sftp` container | uploaded file content on the server matches the payload |
| **SFTP ingress** | `SftpIngressService` + `RenciSftpClientFactory` | `atmoz/sftp` container | `PollAsync` connected, listed, downloaded, imported (count ≥ 1) |
| **IMAP ingress (email pull)** | `EmailPollOrgJob` (MailKit) | Ethereal IMAP (`imap.ethereal.email`) | `CreateStubAsync` called for the seeded CSV attachment |
| **S3 / R2 ingress** | `S3IngressService` + `AmazonS3ClientFactory` (real `AmazonS3Client`, `ServiceURL` set) | **Cloudflare R2** bucket via `https://<accountid>.r2.cloudflarestorage.com` | `PollAsync` listed + downloaded + imported a real PO CSV (count ≥ 1) — proves the new `ServiceUrl` column closes the R2 gap |
| **Inbound email (transport)** | Cloudflare Email Routing → `proculink-inbound-email` Email Worker → `InboundEmailController` | **real email** to `inbound@proculink.eu` (CF MX `route1.mx.cloudflare.net`) | Worker tail: `envelope_to=inbound@proculink.eu → slug=demo attachments=1 backend_status=…` — real MIME parsed, attachment extracted, correct Postmark JSON POSTed to live `api.proculink.eu` (see "Inbound email" below for the remaining founder switch) |

Test files:
- `ProcuLink.Infrastructure.Tests/Services/Dispatchers/LiveEndpointDeliveryTests.cs` (HTTP/OAuth2, HTTP, SMTP, SFTP delivery)
- `ProcuLink.Infrastructure.Tests/Services/Ingress/SftpIngressServiceTests.cs` (`Live_SftpIngress_RealPollImportsFile`)
- `ProcuLink.Infrastructure.Tests/Services/Ingress/S3IngressServiceTests.cs` (`Live_S3Ingress_RealPollImportsFile`)
- `ProcuLink.Api.Tests/Jobs/LiveImapIngressTests.cs` (IMAP ingress)

## Provision the endpoints

**Cloudflare Worker** (HTTP + OAuth2): `worker.js` + `wrangler.toml` (kept under a scratch dir; see report). Routes: `/token` (client_credentials → bearer), `/po` (bearer-validated receiver), `/po-plain` (no auth), `/receipts` (dump), `/reset`. Deploy: `wrangler kv namespace create RECEIPTS` → `wrangler deploy`.

**Ethereal** (SMTP + IMAP mailbox): `curl -X POST https://api.nodemailer.com/user -H 'content-type: application/json' -d '{"requestor":"proculink-livetest","version":"1.0.0"}'` → returns `{user,pass,smtp,imap}`.

**SFTP** (atmoz): `docker run -d --name pl-sftp -p 2222:22 atmoz/sftp testuser:testpass:::upload`. NB: atmoz chroots to the user's home, so the ingress `RemoteDirectory` must be **relative** (`upload`), not `/upload`.

## Run

```bash
# delivery (HTTP/OAuth2 + plain + SMTP + SFTP)
PROCULINK_LIVE_ENDPOINT_TESTS=1 \
PROCULINK_LIVE_HTTP_BASE=https://<worker>.workers.dev \
PROCULINK_LIVE_SMTP_HOST=smtp.ethereal.email PROCULINK_LIVE_SMTP_PORT=587 \
PROCULINK_LIVE_SMTP_USER=<eth-user> PROCULINK_LIVE_SMTP_PASS=<eth-pass> \
PROCULINK_LIVE_SMTP_FROM=<eth-user> PROCULINK_LIVE_SMTP_TO=<eth-user> \
PROCULINK_LIVE_SFTP_HOST=localhost PROCULINK_LIVE_SFTP_PORT=2222 \
PROCULINK_LIVE_SFTP_USER=testuser PROCULINK_LIVE_SFTP_PASS=testpass PROCULINK_LIVE_SFTP_DIR=upload \
dotnet test ProcuLink.Infrastructure.Tests --filter "Category=LiveEndpoint"

# SFTP ingress (PROCULINK_LIVE_SFTP_INGEST_DIR=upload)
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~Live_SftpIngress"

# IMAP ingress
PROCULINK_LIVE_ENDPOINT_TESTS=1 PROCULINK_LIVE_IMAP_HOST=imap.ethereal.email PROCULINK_LIVE_IMAP_PORT=993 \
PROCULINK_LIVE_IMAP_USER=<eth-user> PROCULINK_LIVE_IMAP_PASS=<eth-pass> PROCULINK_LIVE_SMTP_HOST=smtp.ethereal.email \
dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~Live_ImapIngress"

# S3 / R2 ingress (proven against a real Cloudflare R2 bucket)
PROCULINK_LIVE_ENDPOINT_TESTS=1 \
PROCULINK_LIVE_S3_BUCKET=<bucket> PROCULINK_LIVE_S3_REGION=auto \
PROCULINK_LIVE_S3_ENDPOINT=https://<accountid>.r2.cloudflarestorage.com \
PROCULINK_LIVE_S3_ACCESS_KEY=<r2-access-key-id> PROCULINK_LIVE_S3_SECRET=<r2-secret> \
PROCULINK_LIVE_S3_PREFIX= \
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~Live_S3Ingress"
```

> R2 S3 credentials: create an account-scoped API token with the **Workers R2
> Storage Read** permission group; the Access Key ID is the token **id** and the
> Secret Access Key is the **SHA-256 hex of the token value** (Cloudflare's R2
> S3 credential derivation).

## S3 / R2 ingress — FIXED + proven (2026-06-05)

The earlier code gap is closed. `S3IngressConfig` now has a nullable `ServiceUrl`
column (migration `AddS3IngressServiceUrl`, single additive nullable `text`),
`S3IngressService` passes `config.ServiceUrl` through `IAmazonS3ClientFactory.Create`,
and the self-serve settings API/DTO + the frontend **Settings → S3/R2 pull** tab
carry an "Endpoint URL" field. Proven live against a real Cloudflare R2 bucket
(`Live_S3Ingress_RealPollImportsFile`): the production `AmazonS3Client` (with
`ServiceURL` set) listed, downloaded and imported a real PO CSV.

## Inbound email — transport proven live; one founder switch remains

**Wired and proven (no Postmark account needed):**
- Cloudflare Email Routing is already enabled on `proculink.eu` (CF MX in place).
- An **Email Worker** `proculink-inbound-email` (scratch dir
  `~/proculink-inbound-worker`, `postal-mime`) parses the raw MIME, extracts the PO
  attachment(s), synthesises the recipient the backend expects
  (`orders@{slug}.proculink.eu`; slug from a `+subaddress` else the `DEFAULT_TENANT_SLUG`
  var), and POSTs the Postmark-shaped JSON to
  `https://api.proculink.eu/api/inbound-email/postmark` with the
  `X-Postmark-Server-Token` header (Worker secret `INBOUND_WEBHOOK_TOKEN`).
- A non-disruptive Email Routing rule `inbound@proculink.eu → Worker` is live.
- **Verified with a REAL email** (direct-to-MX SMTP, SPF-authorised via a throwaway
  `livetest.proculink.eu` TXT, since removed): Worker tail showed
  `envelope_to=inbound@proculink.eu → slug=demo attachments=1 backend_status=401
  backend_body={"error":"Inbound webhook is not configured."}` — the full chain
  (MX → Email Routing → Worker → MIME parse → JSON → live backend) works.

**The only remaining step (founder / Railway):**
1. Set `Inbound:Postmark:WebhookToken` on the `api.proculink.eu` Railway service to
   the value of the Worker secret (saved to `~/.proculink-inbound-token.txt`), then
   the backend will accept the POST instead of returning 401.
2. Point the Worker var `DEFAULT_TENANT_SLUG` (or use `inbound+{slug}@proculink.eu`)
   at a **real org slug**, and ensure that org has at least one supplier (the router
   rejects orgs with no supplier). Decide the production addressing scheme:
   apex `inbound+{slug}@` sub-addressing vs a wildcard subdomain for the native
   `orders@{slug}.proculink.eu` UX.

The backend order-creation half (CSV-attachment Postmark payload → order stub +
parse job) is covered green by `InboundEmailRouterTests`
(`HappyPath_SingleCsvAttachment_CreatesOneOrderAndEnqueuesParseJob`, etc.).

## Still not provable here

- **FTPS delivery.** `FtpsDeliveryDispatcher` is standalone like SFTP — same harness
  applies — but needs an FTPS server (TLS + passive ports). Deprioritized.
