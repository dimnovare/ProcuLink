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

Test files:
- `ProcuLink.Infrastructure.Tests/Services/Dispatchers/LiveEndpointDeliveryTests.cs` (HTTP/OAuth2, HTTP, SMTP, SFTP delivery)
- `ProcuLink.Infrastructure.Tests/Services/Ingress/SftpIngressServiceTests.cs` (`Live_SftpIngress_RealPollImportsFile`)
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
```

## Not yet provable (and why)

- **S3 ingress — BLOCKED by a real code gap.** `S3IngressService.PollAsync` calls
  `_s3ClientFactory.Create(accessKeyId, secretKey, region, serviceUrl: null)` and
  `S3IngressConfig` has **no `ServiceUrl` column**, so the client always resolves an
  AWS region endpoint. **Cloudflare R2 ingest cannot work** despite the XML doc /
  capability copy saying "S3 or Cloudflare R2". **Fix:** add `ServiceUrl` to
  `S3IngressConfig` (+ migration) and pass it through `Create(...)`; then it's
  testable against R2 or MinIO with the same harness. Until then S3 ingest needs a
  real AWS S3 bucket + creds.
- **FTPS delivery.** `FtpsDeliveryDispatcher` is standalone like SFTP — same harness
  applies — but needs an FTPS server (TLS + passive ports). Deprioritized.
- **Inbound email (Postmark webhook / MX).** The ingest (`InboundEmailRouter`) is
  unit-tested; the LIVE path needs MX records on `proculink.eu` + a Postmark account
  (DNS/founder). Could be wired via Cloudflare Email Routing → Worker → the inbound
  endpoint with a DNS-edit token.
