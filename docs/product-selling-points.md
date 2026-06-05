# ProcuLink — Product Selling Points Brief

_Last updated: 2026-05-28. Based on STATUS.md, landing/pricing pages, and Group C–K specs._

---

## 1. One-line pitch

ProcuLink turns any buyer purchase order — CSV, XLSX, PDF, or XML — into a validated, supplier-ready file and delivers it automatically, so your procurement team stops reformatting orders by hand.

---

## 2. ICP (Ideal Customer Profile)

**First buyer:** A procurement manager or purchasing team lead at a B2B company that sends repeat orders to 3–20 suppliers. They currently maintain a spreadsheet or email chain per supplier because each supplier demands a different file format, column name, or item code scheme.

**Their pain:**
- Every new supplier means a new manual conversion ritual.
- A wrong item code or missing field gets the order bounced back — often a day later.
- No audit trail: when a delivery dispute arises, nobody can prove what was sent or when.
- Growing order volume means the problem scales with headcount, not automation.

**Secondary ICP (Integration/Enterprise revenue target):** Operations or IT lead at a company with an ERP (Erply, Directo) who needs to close the loop between internal systems and supplier portals without a full EDI implementation.

---

## 3. Top 5 Selling Points

**1. One upload, supplier-specific output — no code required.**
Per-supplier mapping templates with 8 field manipulators (Replace, DateFormat, Concat, Multiply, etc.) transform any buyer CSV/XLSX/PDF/XML layout into exactly what each supplier expects. Non-developers configure mappings through a visual editor; advanced users can import/export raw JSON. No integration project required.

**2. AI-assisted item code resolution with human review.**
When a buyer item code has no deterministic mapping to the supplier's catalog code, an OpenAI-backed suggestion engine returns a ranked candidate with confidence score, reason, and provenance — pre-filling the review field but never auto-applying it. Buyers resolve exceptions in a single review step instead of hunting through supplier catalogs.

**3. Validated before it leaves — not rejected after.**
A configurable rule engine (error/warning/info severity, entity-scoped) blocks orders with critical violations before dispatch. Catches wrong item codes, missing required fields, and format mismatches at the transform step, not after a supplier bounces the order a day later.

**4. Automatic delivery over HTTP, SFTP, or ERP connector.**
Once mapped and validated, the output artifact is dispatched automatically to each supplier's endpoint — webhook, SFTP drop, or directly into Erply/Directo via native connectors. Credentials are AES-256-GCM encrypted at rest. Every attempt (success or failure) writes an audit row. The full crossing from upload to delivered can take under 2 minutes.

**5. IMAP email ingestion closes the inbound loop.**
On Integration and Enterprise plans, ProcuLink polls a configured mailbox every 5 minutes and imports PO attachments (CSV/XLSX/PDF) automatically. Teams that receive buyer orders by email no longer need to manually upload each one.

---

## 4. Competitive Positioning

**vs. manual email + Excel workflow:**
The status quo requires a human to open the buyer PO, find the right supplier template, rekey or reformat columns, re-map item codes, attach to an email, and hope nothing was missed. ProcuLink replaces every manual step with a configured, audited, repeatable pipeline. The first supplier flow is set up once; the same mapping runs on every subsequent order.

**vs. full EDI / iPaaS (e.g. SPS Commerce, Boomi, MuleSoft):**
EDI requires IT involvement, months of onboarding, and per-document transaction fees that penalise volume. iPaaS platforms are general-purpose and require a developer to build and maintain each integration. ProcuLink is procurement-specific, self-serve in minutes, and priced per subscription — not per document. It targets the mid-market company that cannot justify a six-figure EDI project but has grown past manual reformatting.

**Honest gap vs. EDI:** ProcuLink does not currently support AS2 transport, full X12/EDIFACT message sets, or retail/grocery compliance mandates. For companies under those requirements, it is not a drop-in replacement.

---

## 5. Proof Points (working features, can be shown today)

- Upload CSV, XLSX, PDF, or XML purchase orders — parsing, canonical model, and artifact output all implemented and tested (60 passing unit tests across transform and infrastructure). Text-based PDFs use a text→LLM extractor (PdfPig text layer → OpenAI structured output) with an anti-hallucination safety net (every emitted number must appear verbatim in the source; qty×price must reconcile, else the line is flagged for review). Benchmarked on 22 real Markit POs/invoices: 22/22 parsed, 177/177 numbers verbatim in source, EN/DE/FR/PL/FI and 6 currencies, no templates. Scanned / image-only PDFs (no text layer) fall back to an AI vision extractor (rasterize via PDFtoImage + SkiaSharp → vision-capable OpenAI model, same schema); because there is no text layer to verify numbers, every scanned-PDF line is flagged for human review — assisted, not silent. For customers who cannot send anything to OpenAI, an opt-in self-hosted no-egress OCR engine (RapidOcrNet, Apache-2.0, in-process, no external calls) handles scanned pages on-prem and gates the rest of the AI pipeline off — enterprise/config capability, enabled per organisation by an operator.
- Visual PO mapping editor with 8 field manipulators; mapping stored as JSONB per supplier; import/export as JSON.
- AI item code suggestions via OpenAI structured outputs; confidence + reason + provenance displayed in review UI; never auto-applied.
- Configurable validation rule engine with error/warning/info severity.
- HTTP webhook delivery with AES-256-GCM encrypted credentials, auto-deliver toggle, test-fire, and audit log.
- Erply and Directo ERP delivery connectors (delivery adapter layer implemented).
- IMAP email polling every 5 minutes for Integration+ accounts.
- cXML 1.2 input parser and output transformer (Group K branch, pending merge).
- Stripe billing: Pilot (14-day/20-order trial), Growth (€149/mo), Operations (€399/mo), Integration (€999/mo), Enterprise (contact sales). Self-serve Checkout and Customer Portal wired.
- Full Next.js App Router frontend with Clerk auth, responsive mobile layout, and a "Bridge Layer" topology dashboard showing buyer-to-supplier wire health at a glance.

---

## 6. Gaps (claims that cannot yet be made honestly)

- **Live end-to-end QA not complete.** Clerk auth, Stripe Checkout/webhooks, upload-to-delivery pipeline, IMAP polling, ERP connectors, and CORS have not been verified against deployed Railway/Vercel services. Group J is in progress. Do not tell prospects the product is in production until Group J passes.
- **Stats on the landing page are fabricated.** "84% auto-processed", "1m 42s avg crossing time", "€4.20 cost per crossing", and "99.7% uptime SLA" have no empirical basis. Remove or replace with honest claims before any external promotion.
- **No customer proof points yet.** Zero published case studies, testimonials, or logos. The "Real results from teams" section header is false — there are no teams using this in production yet.
- **Scanned-PDF support: cloud vision by default, self-hosted no-egress OCR available opt-in.** By default, scanned / image-only PDFs are supported via the AI vision fallback, which rasterizes pages and sends the images to OpenAI (same EU-residency/DPA/zero-retention considerations as the text path). A self-hosted, no-egress OCR engine (RapidOcrNet 2.0.0 — PP-OCRv5 via ONNX Runtime, Apache-2.0 code and weights, in-process, no GPU, no external network calls) is now **shipped** and can be enabled per organisation. For a no-egress org the entire ingest/parse pipeline stays on-prem: scanned pages are OCR'd locally instead of via OpenAI vision, and AI SKU mapping, email-body extraction, and AI schema-inference are all gated off — so no document data leaves the customer's environment. This is an **enterprise / config capability enabled by an operator, not a self-serve UI toggle**, and scanned/image-only lines are still flagged for human review (no text layer to verify numbers against). Illegible scans still fail with "This PDF looks scanned or image-only — we couldn't extract any text." No Azure provider — Azure Document Intelligence was removed.
- **SFTP/FTP dispatchers deferred.** Code exists but live QA has not been done. Do not position SFTP delivery as ready without a live test.
- **UBL/PEPPOL BIS Order input not yet implemented.** Group K has cXML; UBL and PEPPOL are next.
- **No onboarding flow, demo data, or trust/security pages.** Group L is planned but not started. First-time users hit an empty workspace with no guidance.
- **ERP connectors are "Enterprise only" in pricing copy, but Erply/Directo adapters are already built.** Decide whether to surface these earlier in the funnel.

---

## 7. Recommended Copy Changes

**Landing page — remove fabricated stats immediately.**
The stats strip ("84% auto-processed", "1m 42s", "€4.20", "99.7% SLA") is the highest-risk copy on the site. If a prospect asks for the source, there is none. Replace with honest, capability-based statements: e.g. "CSV, XLSX, PDF, XML — one upload format" / "8 field manipulators, no code" / "Every delivery attempt audited" / "AI-suggested item code resolution".

**Landing page — "AI extraction" feature card overpromises.**
"PDFs, emails, EDI, XLSX — our engine pulls structured data from any format with per-field confidence scores" is not accurate. Text-based PDFs use a text→LLM extractor that validates numbers against the source and flags suspect lines for review, and item-code AI suggestions carry confidence/provenance — but "any format" overstates it (scanned/image-only PDFs are supported only via the AI vision fallback or the opt-in self-hosted no-egress OCR engine, with every line review-flagged; EDIFACT/X12 input is reached only by content-sniffing, see the runbook). Rewrite: "CSV, XLSX, text-based PDF, and XML purchase orders parsed into a canonical model — PDF numbers checked against the source — with AI-suggested item code resolution when codes don't match."

**Landing page — "One-click crossing" feature card.**
"cXML, EDI, or API — delivered to the supplier dock" overstates EDI readiness. Change to "cXML or HTTP webhook — delivered to the supplier dock."

**Pricing page — Pilot CTA says "Start Pilot" but landing page says "Start for free".**
The Pilot is free for 14 days. Align both CTAs. Either both say "Start free" or both say "Start Pilot — free for 14 days." The current mismatch will cause friction.

**Pricing page — Growth features list omits email ingestion and ERP connectors entirely.**
This is accurate (both are higher-tier features) but the page should visually indicate what's added at each tier — currently readers cannot scan the delta between tiers. Add a brief "Everything in Growth, plus..." pattern.

**Both pages — remove "Real results from teams" framing.**
The "Why procurement teams choose ProcuLink" section implies social proof that does not exist. Rename to "Why this matters" or "The problem we solve" until there are real customer quotes to cite.
