# ProcuLink — Investor-Grade Analysis (2026-05-30)

Companion to `2026-05-30-four-lens-product-analysis.md`. Ten sections: market sizing,
competitive mapping, customer economics, failure modes, value bundle, year-1 plan, GTM,
architecture, MVP scope, seed-investor verdict.

> **Method:** built from live web research (Estonian/Baltic/Nordic company registries,
> Eurostat SBS, SPS Commerce SEC filings, competitor rate cards) executed by a 7-agent
> workflow, reconciled against the verified code audit. Every number is traceable; sources
> are listed at the end. State reflects `main` on 2026-05-30.

---

## Bottom line

ProcuLink is a **€1–3M ARR business over 3 years**, with a theoretical €10–30M ceiling in 5–7
years *only if* a network/standards moat that does not exist yet gets built. The honest answer
to "€300k / €3M / €30M": **realistically €1–3M ARR; lifestyle-profitable at €300–500k (reachable
in 18–24 months); not venture-scale.** That is a good bootstrap. Most of the strategy mistakes
trace to pricing and to building it as if it were the €30M case.

---

## 1. Market sizing — honest

Estonia bottom-up (sourced: ~120k active enterprises; Eurostat size-class split):

| Band | Count | With real PO pain | Realistic WTP |
|---|---|---|---|
| Micro (0–9) | ~52,800 | ~2,640 (5%) | €0–60/mo |
| Small (10–49) | ~5,400 | ~1,890 (35%) | €149–399/mo |
| Medium (50–249) | ~1,500 | ~825 (55%) | €399–999/mo |
| Large (250+) | ~300 | ~0 (owned by SPS/Pagero/SAP) | — |

Headline **Estonia SAM ≈ €6–20M (base €12M)**; Baltics ≈ €28–94M; Nordics ≈ €78–260M; EU
mid-market ≈ €0.5–1.6B. **The headline is misleading and should not be quoted to an investor**:
the unit economics (§3) prove the micro/lower-small segment has a WTP of ~€12/mo — 12× below the
€149 entry price. Strip the non-payers and the *serviceable-obtainable* Baltic market is the
~400–800 medium-volume distributors, and a 2-founder team realistically closes **~100–350
customers over 3 years.**

Trajectory (pessimistic→base): Y1 18–30 customers / €84–180k ARR; Y2 60–120 / €0.3–0.6M; Y3
**120–350 / €0.46–2.0M ARR (base ~€1.0M).**

Thresholds: lifestyle floor **€300–500k ARR** (EU SMB SaaS at 3–5× ARR → €1–2.5M exit).
Venture-scale = €5M ARR in Y3 with a €50M path — needs 1,000+ customers or up-market move +
Nordics-via-channel; reachable in theory (EU mid-market €0.5–1.6B) but **not by a solo founder in
3 years, and EU B2B-integration multiples (4–8×, not US 15–20×) compress the prize.**

**Verdict: a €1–3M ARR business. Bootstrap it.**

---

## 2. Competitive mapping

| Category | Examples | Threat |
|---|---|---|
| Enterprise EDI networks | SPS Commerce ($638M rev, ~$14k ARPU, US retail), TrueCommerce ($500–5k/mo + €5–50k impl.), Orderful ($189–399/mo/partner) | Low Y1 (wrong geo). Win on price (5–10×) + no implementation project. |
| Compliance networks | **Pagero/Thomson Reuters + Omniva**, Basware, OpusCapita | **Highest Y3 threat** — $800M TR capital + Baltic e-invoice mandates (LV Jan 2026, EE 2025) → compliance buying cycle they bundle PO routing into at zero marginal cost. |
| Procurement suites | SAP Ariba, Coupa | Low unless target's parent is on SAP; Ariba free-to-join for suppliers. |
| Baltic AP incumbents | **Fitek/OpusCapita (GEP)** — ~2,000 Baltic customers, 40k users | **Biggest Year-1 threat** by *adjacency* — already in the finance+procurement dept of the exact ICP; PO-outbound is one module away. |
| iPaaS | Celigo ($20k/yr), Boomi, MuleSoft ($55k median) | Non-competitors for first 10 (price band). |
| **The real substitute** | **Make.com + a freelancer (€800 once + €10.59/mo)** | **Most dangerous for the first 10** — works for 2–3 simple suppliers for ~18 months. |
| **Local mirror-incumbent** | **Docura (EE)** — €0.10/doc, free ≤20/mo, 200+ customers, **native Erply** | Today supplier→retailer (opposite direction) — but **already owns the Erply relationship ProcuLink is betting on**; buyer-side PO routing is its most natural extension. |

**Fear most:** Y1 = Fitek/OpusCapita + Make-plus-freelancer; Y3 = Pagero/Thomson Reuters.

**Positioning:** **"Lighter/cheaper than enterprise EDI," not "smarter than Zapier"** (the buyer
doesn't know Zapier). One-liner: *"Send purchase orders to any supplier in their required format —
no EDI consultants, no per-document fees, live in one day."*

**Moat honesty:** "smarter than Zapier" is a **12–18 month moat, not permanent.** Real
defensibility = accumulated per-customer mapping/validation config (switching cost) + Erply/Directo
connector depth + local execution speed — *not* a technical barrier an iPaaS can't cross.

---

## 3. Customer economics, stress-tested

| | Small (100 ord) | Medium (1,000) | Large (10,000) |
|---|---|---|---|
| Problematic orders/mo | 20 | 250 | 1,500 |
| **Monthly manual cost** | **€48** | **€917** | **€8,400** |
| Savings @70% automation | €34 | €642 | €5,880 |
| Rational WTP (35% of savings) | **€12** | **€225** | **€2,058** |
| Current plan match | Growth €149 | Growth €149 | Integration €999 |
| **Verdict** | **12× OVERPRICED** | priced *below* WTP | **2.1× UNDERPRICED** |

The most important commercial finding:
- **Do not sell to small suppliers on labor ROI** (€149 is 12× their WTP). Use them as a
  **viral/mandated channel** — a large buyer mandates ProcuLink delivery, the *buyer* pays.
- **Sweet spot = the medium / Markit profile** (~500–1,000 orders/mo). Operations €399 anchor.
- **The top tier is underpriced** — a large distributor saves €5,880/mo; Integration €999 leaves
  €1,000–1,500/mo on the table. Even Enterprise €2,500 is rational. **Raise the ceiling.**

**Strongest pricing model = flat tier + per-supplier onboarding fee** (€350–600/supplier for the
first 3, €150 after). Mapping config is the real cost driver; the fee captures it, produces cash
at close, creates sunk-cost stickiness, and naturally filters out the unprofitable small segment.

**Net recommendation (reconciled across §1/§3/§7):** anchor **Operations €399**; keep Growth as the
entry; **raise Integration toward €1,500 or route large buyers to Enterprise €2,500**; add a
**per-supplier mapping fee**, *waived for design partners #1–5* (logo + testimonial + intro),
standard from customer #6.

---

## 4. Failure modes (ranked, 1 = most likely to kill it)

| # | Risk | Mitigation | KPI (target) |
|---|---|---|---|
| 1 | Runway out before repeatable sales | Call Markit this week; one pilot > every feature | Days to first signed pilot ≤30 |
| 2 | Pain real but won't pay | Anchor €/order vs manual minutes; show AP before IT | Pilot→paid ≥40% in 60d |
| 3 | Each customer too much custom work | Pre-build Erply/Directo starter templates (JSON, no code) | Onboarding ≤2h/customer |
| 4 | Becomes a consultancy | Cap concierge 4h/customer/mo; turn each manual step into UI | Self-serve supplier config ≥70% after #3 |
| 5 | Mapping/AI cost destroys margins | Enforce token cap; batch AI; deterministic-first | AI cost ≤8% of per-customer MRR |
| 6 | ERP/EDI vendor owns the relationship | Win on multi-supplier fan-out + audit; certified-connector partnerships | 5 live accounts alongside their ERP |
| 7 | Buyer/supplier finger-pointing | Sell to procurement/CFO, not IT | Sales cycle ≤3 weeks SMB |
| 8 | AI/PDF accuracy not production-grade | Lead with CSV; keep ParseFailedPanel fallback | Text-PDF line accuracy ≥90% |
| 9 | Security/compliance blocks adoption | Ship SSRF allowlist before live data; sign DPA | SSRF fixed pre-pilot; DPA signed pre-prod |
| 10 | Market too fragmented | Geography-agnostic playbook; replicate after 10 Baltic refs | €3k MRR before geo-expansion |

---

## 5. Value bundle — what to build next

Weighted scoring (Impact×2, Effort-inv×1.5, WTP×2, Defensibility×1, 2-founder×1):

| Rank | Feature | Score | Build |
|---|---|---|---|
| 1 | **Erply/Directo starter mapping templates** | 41.5 | JSON config, **zero code, this week.** First delivery 45→<15 min. |
| 2 | Exception dashboard (all-orders-in-exception view) | 39.5 | 1 query + 1 page, ~1 day |
| 3 | Human-in-the-loop clarification | 36.5 | 2–3 days |
| 4 | SLA tracking UI (wire existing SlaTimer) | 35.5 | ~1 day |
| 5 | Cross-org mapping library (anonymized, opt-in) | 34.0 | The only real long-term moat — but needs many customers first |

**Build FIRST: the Erply/Directo templates.** Highest sales leverage, zero code, unblocks the
warmest ICP. Everything else is a self-inflicted sales barrier until it exists.

---

## 6. Year-1 plan (pessimistic)

5% cold→demo, 33% demo→pilot, 40% pilot→paid, €399 ARPU.

| Mo | Demos | Pilots | Paying | MRR | Milestone |
|---|---|---|---|---|---|
| 1 | 3 | 1 | 0 | €0 | Live QA happy-path on Railway+Vercel |
| 3 | 5 | 2 | 2 | €798 | SFTP live; first testimonial |
| 6 | 4 | 2 | 7 | €2,793 | Format auto-detect in UI |
| 9 | 5 | 3 | 13 | €5,187 | Rejection capture; first case study |
| 12 | 3 | 2 | **18** | **€7,182** | €86k ARR, 3 case studies |

Gates: **3mo** ≥2 paying (Operations), 2nd-order delivery without founder debugging, ≥1
testimonial. **6mo** ≥7 paying, MRR ≥€2,500, an Erply/Directo consultant relationship. **12mo**
≥18 paying, MRR ≥€7k, churn ≤5%/mo, CAC ≤€600, ≥1 Integration-tier in pipeline.

---

## 7. Go-to-market

- **Sell to BUYERS first** (they own the pain, pay, decide unilaterally).
- **First niche: IT distributors / wholesalers on Erply/Directo, Estonia-first.**
- **Pitch the stress, not the spreadsheet:** "20–40 min/PO reformatting → 90 seconds."
- **Stay Baltic 12 months** (~200 EE/LV/LT Erply/Directo shops = enough for 30–50 customers).
- **ICP:** Baltic distributor/manufacturer, 50–400 employees, Erply/Directo, 100–500 POs/mo to
  3–20 suppliers, handled by a procurement coordinator in Excel.
- **Persona:** Procurement Coordinator / Purchasing Manager (approves €399 without a committee).
- **Source the first 100:** Estonian Business Register by EMTAK codes (4651/4652/4669/4675,
  2221/2819) × LinkedIn procurement titles. **Multiplier: one Erply/Directo implementation partner
  warm-intros 10–20 customers over one lunch — pursue them before cold DMs.**
- **First-5 offer:** Operations tier, 60-day free pilot (14 is too short), €500 setup waived,
  founder does config — for logo + testimonial + one peer intro.

Outbound (Erply, <120 words): *Subject: Erply → supplier file in 90 seconds.* "Hi {Name} — I build
tools for procurement teams on Erply. When your team sends a PO to a supplier, how long does the
reformatting take? Most Erply shops have someone spending 20–40 min/order. ProcuLink eliminates it
— Erply PO in, supplier-ready file out, delivered automatically. 15-min live demo with one of your
real recent POs — Tuesday or Thursday?"

---

## 8. Architecture critique

**Four structural fixes (target state):**
1. **Kill the 1,166-line `OrderService` god-class** → `IngressService` + `MappingResolverService` +
   `OrderQueryService` + `ExceptionWorkflowService`; introduce a `proculink.eulication` project and
   move `ParseStoredFileAsync` fully into the Worker to dissolve the circular-ref.
2. **Rules-as-data — the anti-spaghetti principle.** *Configurable (versioned rows):* field
   mappings, manipulators, item-code translations, validation rules, output-format-per-supplier,
   SLA windows. *Code (never configurable):* canonical model, pipeline state machine, tenancy,
   security. Add a `ValidationEngine` evaluating `ConditionJson` rows; add a `supplier_format_profiles`
   table (output format must be persisted per supplier for auto-delivery).
3. **Sync vs async:** make `transform` + `test-fire` async (R2 upload / slow endpoints will hit
   Railway's 30s limit); paginate `GET /api/orders`; fan out pollers one-job-per-org.
4. **AI = optional fallback, human-confirmed, never blocks the pipeline.** Keep the current
   deterministic-first / suggest-only pattern; batch the per-line OpenAI calls.

**Security (still open):** SSRF allowlist (P0 before live data), JWT `ValidateAudience=false`,
all-zero AES key in git, and the 4-duplicate-migration consolidation + fresh-DB apply test. (The
cross-tenant `FindAsync` is fixed; verify `CreateFromFileAsync` once more.)

---

## 9. MVP scope

**Definition of done: one real buyer, one real supplier, one real PO delivered intact.**
- **Automate day 1 (already built):** CSV/XLSX → parse → map → HTTP deliver; audit trail; failure
  notification; credential encryption.
- **Concierge (fine for first 5):** mapping config, delivery setup, parse-exception fixes, DPA.
- **FREEZE / SaaS theatre:** Zapier layer, invoice/ASN/DESADV stubs, extra EDI formats, cross-org
  library, RBAC/SCIM, PunchOut, OCR, standards-comparison screen.
- **Only net-new before first pilot:** SSRF fix (P0), Erply/Directo templates, exception dashboard,
  extend pilot 14→60 days.

30-day: W1 SSRF + templates + exception view *and* call Markit + 2 prospects; W2 demo + offline
config; W3 first real PO delivered → case study; W4 live-pilot QA. 90-day: M1 1 pilot/€0; M2 first
paid + 2nd pilot; M3 3 paid, ≥€450 MRR, ≤2h onboarding.

---

## 10. Seed-investor verdict (blunt)

**Would a seed VC wire €250k? No — and you shouldn't want them to.** At a €1–3M ARR ceiling and
EU B2B-integration multiples (4–8×), the best realistic outcome is a €5–20M exit in 6–8 years. A
seed fund needs each check to be able to return the fund; this can't. Institutional venture money
is the *wrong* financing for the *right* business — this is a bootstrappable €1–2M ARR company; keep
the equity. Right capital: **angel/grant (Estonia EAS, EU SME instruments), not a VC round.**

**What would make it venture-fundable (the 3 things to wire €250k):** (1) evidence the mapping
library is a real network effect (customer #8 onboards in 20 min off customers #1–7's anonymized
mappings); (2) a wedge into the e-invoice compliance mandate (be the cheap Peppol on-ramp for
Baltic+Nordic SMEs ahead of Pagero); (3) a repeatable channel-driven motion (≥30% of pipeline from
Erply/Directo partners, <2%/mo churn).

**"Come back in 6 months" triggers:** zero customers, no live deployment, a 5×-overbuilt product —
all signal a founder who avoids selling.

**Traction bars:** *Pre-seed* (€100–300k angel/grant): 3–5 paying, €2–3k MRR, ≥40% pilot→paid,
<30-day cycle. *Seed* (€1–2M): €15–30k MRR, <2%/mo churn, CAC payback <6mo, working partner channel.
**Proof it's not consulting:** 2nd order from a supplier flows with zero founder involvement; gross
margin >75%; onboarding ≤2h; ≥70% suppliers self-configured by customer #5.

**Straight answers:**
- *Weak:* zero customers, zero live runs, pricing 12× too high for small / 2× too low for large.
- *Unclear:* are you a Baltic bootstrap (€1–2M ARR, real) or "the international standard" (a slide)?
  Pick — they require opposite behaviors.
- *Amateur:* the €199/€149 site mismatch, a customers page advertising no customers, EDIFACT
  parsers that throw `NotImplementedException`, an SSRF hole shipping to "production."
- *Strong:* the core engine is real and tested; the design system is distinctive; the GTM docs beat
  most funded seed companies; **you are one phone call from your ideal first customer.**
- *Overthinking:* standards breadth, invoice/ASN, Zapier, mobile polish, RBAC. None gets customer #1.
- *Missing completely:* (1) **Docura already owns your Erply channel** — local, native to your ERP,
  200+ customers, one extension from your use case; (2) your small-supplier segment **can't pay your
  prices** — they're a free viral channel, not a customer; (3) the one feature that could make this
  venture-scale (cross-org mapping-library network effect) is the one you haven't prioritized.

**Highest-EV action in this document:** freeze the codebase, ship the SSRF fix + Erply/Directo
templates this week, and put one real Markit PO in front of one real supplier before writing another
line of feature code.

---

## Sources

Estonia/Baltic/Nordic enterprise counts: HitHorizons, CEIC/Statistics Estonia, Lursoft (LV),
Statistics Lithuania, Nordic Statistics. Size-class split: Eurostat Structural Business Statistics.
EDI/competitor data: SPS Commerce FY2025 SEC filing; Europe EDI market (Research and Markets);
Babelway, Orderful, Celigo, Boomi, MuleSoft, Make, Zapier, n8n, Docura public rate cards; Thomson
Reuters/Pagero acquisition + Pagero–Omniva partnership; GEP/BaltCap OpusCapita/Fitek; EC e-invoicing
mandate pages (EE/LV). Full URLs in the workflow transcript.
