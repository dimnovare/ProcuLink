# GDPR & Data Protection

This page explains how ProcuLink complies with the EU General Data Protection Regulation (GDPR) and equivalent national privacy laws in Estonia, Latvia, Lithuania, and the wider EU/EEA.

## Roles

- **You** (the customer organisation) are the **Data Controller** of the purchase orders, supplier records, and buyer records you process through ProcuLink.
- **Diip Solutions OÜ** (registry code 17527757, Uus-Sadama tn 15-2,
  10120 Tallinn, Estonia), operating the **ProcuLink** service, is the
  **Data Processor**.
- We act only on documented instructions from you, as defined in our Data Processing Agreement (DPA).

## Data residency

| Component | Location | Provider |
|---|---|---|
| Source files (uploaded POs) | EU | Cloudflare R2, EU region |
| Transformed artifacts | EU | Cloudflare R2, EU region |
| PostgreSQL database | EU (Frankfurt) | Railway |
| Hangfire job storage | EU (Frankfurt) | Railway Postgres |
| Application compute | EU (Frankfurt) | Railway |
| Authentication tokens | EU served | Clerk |
| Billing data | EU served | Stripe |
| AI processing (optional) | US | OpenAI |
| Error tracking | EU | Sentry (sensitive data scrubbed) |
| Transactional email | EU | Postmark / SendGrid |

If `Ai:OpenAI:ApiKey` is configured for your organisation, line item descriptions and supplier identifiers may be transmitted to OpenAI in the US under the EU-US Data Privacy Framework. AI processing is opt-in per organisation. If you require EU-only AI processing, you can disable AI suggestions or wait for our planned EU-region model option (Q4 2026).

## What we process

| Category | Purpose | Legal basis |
|---|---|---|
| Account identity (email, name) | Authentication, support | Contract |
| Organisation details (name, VAT, address) | Billing | Contract |
| Purchase order content | Core service: parse, map, transform, deliver | Contract |
| Supplier delivery credentials (encrypted) | Core service: dispatch | Contract |
| Audit logs (actor, action, timestamp) | Security, compliance | Legitimate interest + legal obligation |
| Usage analytics (aggregated) | Product improvement | Legitimate interest |
| Cookies (session, CSRF) | Site functionality | Necessary |

We do **not** process special-category personal data, biometric data, or data relating to children. We do **not** sell or share data with marketing networks.

## Retention

- **Source files & transformed artifacts**: 90 days by default. Customer may extend up to 7 years (regulatory archives) on Operations+ plans.
- **Order metadata** (lines, mappings, status): retained for the lifetime of your subscription, deleted within 30 days of subscription cancellation unless you request export.
- **Audit logs**: 13 months rolling.
- **Backups**: 30 days rolling, then permanently destroyed.

## Your rights as a data subject (under GDPR Art. 15–22)

For your own personal data held by ProcuLink:

- **Access**: email `privacy@proculink.eu` and we respond within 30 days.
- **Rectification**: edit in app; for closed accounts, email us.
- **Erasure ("right to be forgotten")**: documented procedure, fulfilled within 30 days, audit-logged.
- **Restriction**: email us; we suspend processing while disputes are resolved.
- **Portability**: full export of your account data (orders, mappings, audit log) via `GET /api/export/full` (planned Q3 2026) or by emailing us.
- **Objection**: applies to legitimate-interest processing; we evaluate case-by-case.
- **Automated decision-making**: none of our processing has legally significant automated decisions. AI suggestions are always advisory; humans confirm every mapping.

Your end-users (your suppliers, your buyers) — if they appear as personal data in your orders — exercise their rights with **you, the Controller**, not us. We assist you within 7 business days.

## Data Processing Agreement (DPA)

A standard DPA based on the EU Standard Contractual Clauses (SCCs, 2021 modular) is available on request to all paid plans. Pilot accounts can sign the DPA before going live. Enterprise customers may negotiate amendments.

Email `privacy@proculink.eu` to receive the DPA template.

## Sub-processors

The current list of sub-processors is maintained in `security.md`. We notify customers at least 30 days in advance of adding a new sub-processor and provide a right to object.

## Breach notification

In the event of a personal data breach affecting your data:

- We notify you within **72 hours** of becoming aware.
- We notify the relevant Supervisory Authority (Estonian Data Protection Inspectorate / AKI) as required.
- The notification includes: nature of the breach, categories and approximate number of data subjects, contact point, likely consequences, and mitigation steps.

## Transfers outside the EEA

The only routine transfer outside the EEA is **OpenAI (US)**, used only if AI suggestions are enabled. The transfer is covered by:

- The EU-US Data Privacy Framework (DPF), to which OpenAI's parent OpenAI OpCo, LLC is certified.
- A Data Processing Addendum between ProcuLink and OpenAI.
- Standard Contractual Clauses as a fallback.

Customers may disable AI suggestions to eliminate this transfer entirely.

## Contact

- Privacy questions: `privacy@proculink.eu`
- Security incidents: `security@proculink.eu`
- Data Protection Officer: TBC at general availability (Estonia does not currently require a DPO for organisations of ProcuLink's size; we plan to appoint one when we reach the threshold).

Diip Solutions OÜ (registry code 17527757) is registered at Uus-Sadama tn
15-2, 10120 Tallinn, Estonia, operates the ProcuLink service, and reports to
the Estonian Data Protection Inspectorate (AKI) as required.
