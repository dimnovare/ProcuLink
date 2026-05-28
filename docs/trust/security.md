# Security at ProcuLink

ProcuLink handles purchase orders and supplier delivery credentials for your business. This page explains the concrete controls we operate today, not aspirations.

## Authentication

- **Identity provider**: [Clerk](https://clerk.com) (SOC 2 Type II). ProcuLink does not store passwords.
- **Session tokens**: JWTs signed by Clerk; verified on every API request against Clerk's published JWKS.
- **Tenant isolation**: every API request resolves an `OrgId` from the JWT via `TenantResolutionMiddleware`. Every database query is scoped by `OrgId` at the EF Core level. There is no API path that can read another organisation's data.
- **Multi-factor authentication**: enforced via Clerk; available to all customers at no extra cost.
- **Single sign-on**: SAML/OIDC available via Clerk on Distributor and Enterprise plans.

## Credential encryption (supplier delivery endpoints)

ProcuLink stores HTTP bearer tokens, ERP API keys, and ERP passwords required to deliver purchase orders to your suppliers' systems. These are:

- **Encrypted at rest with AES-GCM** (authenticated encryption — both confidentiality and tamper-evidence). Implementation: `DeliveryEncryptionService`.
- The 32-byte AES key (`Delivery:EncryptionKey`) is provisioned via Railway environment variables. It is **never committed to source control** and **never logged**.
- Credentials are decrypted only at the moment of dispatch, by the Hangfire worker process, and never returned to the API surface — reads from `/api/suppliers/{id}/delivery-config` always return a **redacted** representation.
- Encryption is symmetric per-deployment; production and development use distinct keys.

## Storage

- **Source files and transformed artifacts**: Cloudflare R2, EU region (`auto` with EU bias). Cloudflare is GDPR-compliant and operates an EU sovereign network.
- **Object access**: S3-compatible API with signed credentials. Pre-signed download URLs expire in 15 minutes.
- **Database**: PostgreSQL on Railway, EU region. TLS required for all connections. Daily automated backups, 7-day retention on development, 30-day retention on production.
- **In transit**: HTTPS only for all browser, API, and webhook traffic. HSTS enabled on the marketing and app domains.

## Audit logging

Every state-changing action against an order is persisted to an `audit_events` table with:

- `OrgId` (tenant)
- Action name (`Created`, `Parsed`, `Resolved`, `Transformed`, `Delivered`, `DeliveryAttempt`, `DeliveryFailed`, etc.)
- Payload (relevant context — never includes credentials or full PII)
- Actor (user ID or `system` for automated jobs)
- Timestamp (UTC)

Logs are retained for the lifetime of the organisation account. Per-customer log export is on the Q3 2026 roadmap.

## AI usage (optional)

- AI mapping suggestions are **opt-in per organisation**. If `Ai:OpenAI:ApiKey` is not configured for your deployment, no calls are made.
- When enabled, ProcuLink sends only the **line item description, supplier identifier, and candidate supplier catalog** to OpenAI — never customer master data, pricing, or buyer identity.
- The OpenAI API is called with the `no-train` posture (we use the standard `gpt-5-mini` API, which Anthropic/OpenAI do not train on by default in the business tier).
- Per-organisation monthly token caps (`Ai:OpenAI:MonthlyTokenLimitPerOrg`) prevent runaway spend.
- AI suggestions are **never auto-applied** — every suggestion is labelled "AI suggested" with confidence + reason + provenance, and the user must accept or reject.

## Sub-processors

| Service | Purpose | Region |
|---|---|---|
| Cloudflare R2 | File storage | EU |
| Railway | Compute + Postgres | EU (Frankfurt) |
| Clerk | Authentication | EU customers served from EU |
| Stripe | Billing | EU customers served from EU |
| OpenAI (optional) | AI mapping suggestions | US |
| Sentry | Error tracking (PII-scrubbed) | EU |
| Postmark / SendGrid | Transactional email | EU |

The full sub-processor list is also available on `docs/trust/gdpr.md`.

## Vulnerability disclosure

Email security issues to `security@proculink.app` (TBC at launch). We respond within 1 business day and credit disclosers in our changelog if requested.

## What we don't yet have

To be honest about the gaps:

- **SOC 2** — planned for 2027 once revenue justifies the audit cost (~€30k–€50k).
- **ISO 27001** — same.
- **Penetration test** — scheduled before EU general availability.
- **Bug bounty programme** — not yet live.

Customers requiring SOC 2 attestation today should evaluate Distributor or Enterprise plans, which include a tailored security review and DPA.
