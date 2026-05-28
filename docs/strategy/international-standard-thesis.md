# ProcuLink — International Standard Thesis

_Last updated 2026-05-28._

---

## The bet

ProcuLink is betting that there is room for a **standards-visible,
dual-persona, multi-channel outbound PO router** that is cheaper, faster to
configure, and more honest about what it is than the incumbent EDI / B2B
gateway vendors.

The wedge is outbound purchase orders for buyer / procurement teams. The
durable goal is to become the international standard for outbound B2B PO
routing: any input format / channel → canonical PO → any output format /
channel. Best in class for a 30-year procurement veteran. Effortless for a
six-months-into-the-job procurement analyst.

---

## Why this market needs it

The buyer-side procurement team typically faces three intersecting problems:

1. **Format fragmentation.** Each supplier requires a different shape: cXML
   1.2, UBL 2.1, Peppol BIS 3.0, EDIFACT ORDERS, X12 850, supplier-specific
   CSV / XLSX templates, supplier-specific PDF layouts, or plain email
   attachments. A buyer with 200 suppliers regularly has 5–10 distinct
   formats in play.
2. **Channel fragmentation.** Each supplier accepts delivery through a
   different channel: HTTP webhook, SFTP, FTPS, SMTP, AS2, PEPPOL Access
   Point, supplier portal upload, or a faxed PDF. The buyer's ERP usually
   speaks at most two of those natively.
3. **Mapping fragility.** Even within one format, each supplier wants their
   own SKU codes, their own line-of-business field, their own date format,
   their own currency convention. The mapping is per-supplier and decays
   the moment the supplier changes anything.

The incumbents solve this with heavy, expensive, slow-to-configure VAN /
EDI gateways. They are best-in-class for compliance and worst-in-class for
the procurement analyst who just wants to send tomorrow's purchase order
without filing a ticket.

There is a structural opening for a SaaS that:

- speaks every meaningful standard out of the box.
- can be configured by a procurement professional without consulting a
  third-party integrator.
- shows the operator exactly which standard field maps to which canonical
  field, so it earns trust from veterans rather than asking them to take it
  on faith.
- prices an order of magnitude below SPS Commerce, TrueCommerce, Babelway,
  or Pagero for the same coverage.

---

## What makes ProcuLink different

| Dimension | Incumbents (SPS / TrueCommerce / Babelway / Pagero) | ProcuLink |
|---|---|---|
| **Time to first delivery** | Weeks to months. Implementation services bill separately. | Under 15 minutes for a novice, under 5 for an expert. Self-serve. |
| **Standards visibility** | Hidden in the integrator's tooling. The buyer's analyst rarely sees field mappings. | Inline. Every field in expert mode shows the matching UBL / EDIFACT / X12 / cXML / Peppol BIS / ISO 20022 reference. |
| **Persona** | One density level — usually optimised for the integrator, not the end user. | Dual-persona by design. Novice: wizard, templates, AI defaults. Expert: density, hotkeys, raw view. |
| **AI** | Bolted on, often opaque. | Provider-neutral interface (OpenAI structured outputs first). Every suggestion shows confidence + provenance + Accept/Edit/Reject. Never auto-applies. |
| **Pricing** | Per-document or per-trading-partner with high minimums. Enterprise-only. | Flat monthly plans starting at €149. No per-document fees inside the plan limit. Pilot tier is honestly free for 14 days. |
| **Channel coverage** | Often gated behind a service tier or per-channel charge. | One plan, all supported channels (HTTP / SFTP / SMTP / partner-wrapped AS2 / partner-wrapped PEPPOL / webhook in). |
| **Honesty about scope** | Bundled compliance / spend analytics / payment claims. | Outbound PO first. Invoice + ASN second. Nothing else pretended. |

We are not differentiating on "we also have AI" — every vendor in the
category will have AI by 2027. We are differentiating on **standards
visibility + dual-persona UX + honest scope** because those are durable
preferences of the procurement professional buyer, and they require
discipline rather than capital.

---

## The Cinderella shoe + Learn loop combined thesis

Two metaphors define the product:

### The Cinderella shoe — any input fits any output

Every supplier requires a specific shape. The product must accept whatever
the buyer has — CSV, XLSX, PDF, cXML, UBL, EDIFACT, X12 — and emit whatever
the supplier requires, in whatever channel they accept. The canonical PO
model is the foot; every format is a shoe. ProcuLink's job is to make the
shoe fit, every time, without the procurement analyst learning the
underlying standards (unless they want to, in expert mode).

This is the standards + channels axis: depth (every supported format
matches the relevant ISO / Peppol / EDIFACT / X12 / cXML / ISO 20022
conformance) and breadth (every channel a supplier might mandate is
supported, either natively or via a partner wrap).

### The Learn loop — the product gets better with every order

`Parse → Normalize → Validate → Review exceptions → Transform → Deliver → Learn`.

Every order that flows through the system produces three pieces of durable
data:

1. **Mapping evidence** — which buyer field actually means which supplier
   field for this supplier? Stored anonymised in the supplier mapping
   library (Horizon 3).
2. **Validation evidence** — which orders get rejected by this supplier,
   for what reason? Feeds back into validation rules so the next operator
   sees the rejection coming before they send.
3. **Channel evidence** — does this supplier accept this format on this
   channel reliably? Surfaces in per-supplier SLA timers and the public
   library.

The Learn loop is the long-term moat. A competitor can copy our standards
coverage in a year. A competitor cannot copy the accumulated supplier
mapping library or the validation rules learned from thousands of
real-world orders.

### Combined

The Cinderella shoe is the **entry promise**: bring any input, get any
output, on any channel. The Learn loop is the **retention promise**: the
longer you use ProcuLink, the less work each order takes, because the
product has learned this supplier's quirks already (either from your past
orders or, in Horizon 3, from the anonymised library).

---

## What we do not promise (yet)

Honesty is a positioning weapon in a category full of bundled marketing
claims. ProcuLink does not promise:

- Full ERP replacement.
- Accounts payable workflow or invoice approval routing.
- Spend analytics, supplier risk scoring, or sourcing.
- Payment rails.
- Compliance certifications we have not yet earned (no SOC 2 claim until
  it is real).
- Horizons 2 and 3 features — they are on the roadmap, not in the product.

This list will shrink as the roadmap delivers. Until then, every public
claim is checked against this list.

---

## What we expect to learn

This thesis is the working hypothesis as of 2026-05-28. The next 4–6 weeks
of Horizon 1 will tell us:

- Whether a first-time procurement analyst can reach "successful delivery"
  on their own in under 15 minutes (the novice persona promise).
- Whether a 30-year veteran trusts the standards-visibility surface enough
  to recommend it to their counterparties (the expert persona promise).
- Which standards / channels block the most pilot conversions (and so
  reorder Horizon 2 priorities).
- Whether the "honest free Pilot, pay if you keep using it" pricing
  converts at the rate the unit economics need (currently estimated; not
  yet validated).

If any of these assumptions break, the thesis updates. This document is
the place to record that update.

---

## References

- Roadmap: `docs/superpowers/plans/2026-05-28-phase-6-international-standard-roadmap.md`
- Standards matrix: `docs/standards-matrix.md`
- Canonical PO model: `docs/canonical-po-model.md`
- Format / channel ground truth: `docs/format-channel-roadmap.md`
- ICP and outreach: `docs/gtm/icp-target-list-template.md`
