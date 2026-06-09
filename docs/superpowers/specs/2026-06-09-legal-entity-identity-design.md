# ProcuLink Legal Entity Identity Design

## Decision

ProcuLink remains the customer-facing product and brand. Diip Solutions OÜ is
the legal entity that operates the service, contracts with customers, processes
personal data, owns the product intellectual property, and issues invoices.

## Authoritative identity

- Official source: Estonian e-Business Register,
  `https://ariregister.rik.ee/eng/company/17527757/Diip-Solutions-O%C3%9C`
- Product and brand: ProcuLink
- Legal entity: Diip Solutions OÜ
- Registry code: 17527757
- Registered address: Uus-Sadama tn 15-2, 10120 Tallinn, Estonia
- VAT status: not VAT registered as of June 9, 2026

The personal email shown in the Estonian Business Register must not be published.
Public contact addresses remain the role-based `@proculink.eu` aliases.

## Public wording

Full operator notice:

> ProcuLink is a product operated by Diip Solutions OÜ, registry code 17527757,
> registered at Uus-Sadama tn 15-2, 10120 Tallinn, Estonia.

Compact footer:

> © 2026 Diip Solutions OÜ · ProcuLink

Legal contracts and privacy documents name Diip Solutions OÜ as the party and
describe ProcuLink as the service or product. Product navigation, domains,
metadata, UI labels, email display names, and service project names remain
ProcuLink.

## Implementation

The frontend has one immutable legal-entity module containing the authoritative
identity and derived public strings. Terms, Privacy, DPA, the one-pager, both
marketing footers, and Organization structured data consume that module.

Trust documentation and legal-page regression tests are updated to reject the
old fabricated entity, registry code, and address.

## Out of scope

- Publishing the founder's personal email address
- Adding a VAT number before Diip Solutions OÜ is VAT registered
- Renaming the product, repositories, infrastructure projects, domains, or
  customer-facing sender names away from ProcuLink
- Changing legal clauses beyond the factual identity correction
