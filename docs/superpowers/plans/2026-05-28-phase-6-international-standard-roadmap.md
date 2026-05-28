# Phase 6 — International Standard + Dual-Persona UX

_Created 2026-05-28. Supersedes the Phase 5 production-hardening roadmap as the forward-looking plan. Phase 5 history is preserved in `STATUS.md`._

---

## Direction (the product thesis)

ProcuLink will become the international standard for outbound B2B purchase
order routing: any input format/channel → canonical PO → any output
format/channel. The product must be:

- **Best in class** for 30-year procurement veterans (depth, density, standards visibility).
- **Effortless** for first-time users (wizard, templates, magic mapping preview, AI defaults).
- **Standards-fit** — Cinderella's-shoe-into-any-format for whichever shape a supplier requires.
- **Cost-effective** versus SPS Commerce / TrueCommerce / Babelway / Pagero.

The Learn loop (`Parse → Normalize → Validate → Review → Transform → Deliver →
Learn`) remains the long-term moat. Standards depth + channel breadth +
dual-persona UX are the next 6 months of execution.

Full positioning rationale: `docs/strategy/international-standard-thesis.md`.

---

## 3-Horizon overview

| Horizon | Theme | Timeline | Groups |
|---|---|---|---|
| **1** | Production Ready + Effortless | next 4–6 weeks | J, J2, L (expanded) |
| **2** | Standards Backbone + Channel Breadth | Q4 2026 | M, N, O |
| **3** | Network Effects | Q1 2027+ | P, Q, R, S |

Horizon ordering is deliberate. We do not start expanding standards (Horizon 2)
until the existing happy path is provably reliable end-to-end with real
deployed traffic (Horizon 1). We do not start chasing network effects
(Horizon 3) until we have channel breadth (Horizon 2) — without it, the
network has nothing to route over.

---

## Horizon 1 — Production Ready + Effortless

### Why now

We have working code across Parse → Transform → Deliver, billing/auth wired,
and a UI direction locked. What we do not have yet is:

- live deployed QA against real Stripe / Clerk / R2 / OpenAI / IMAP.
- a first-time user experience that gets a non-technical procurement lead to a
  successful delivery in under 15 minutes.
- production confidence that what a prospect sees is real product, not staged demo data.

If we add more engines on top of that, we will burn our first wave of design
partners on rough edges instead of validating the wedge.

### Group J — live end-to-end QA + deployment hardening (in progress)

**Scope.** Verify Railway API + Worker boot, Clerk login, Stripe Checkout /
Portal / webhook, upload → parse → review → resolve → transform → delivery,
HTTP delivery test-fire, ERP test-fire against sandbox or webhook.site, IMAP
polling against a real mailbox, Sentry capture, CORS on Vercel origin. Code-level
gaps were closed in `2f725cb`, `c9ac1bb`, `9240abd`; live deployed QA is the
remaining work.

**Sequencing rationale.** Live QA gates everything else in Horizon 1 — there is
no point shipping onboarding polish if the underlying happy path silently breaks
in production. Group J must clear before Group L expanded ships.

**ICP fit.** Buyer/procurement teams will not pilot a product whose primary loop
breaks under real-world Stripe/Clerk/IMAP load. This is the credibility gate.

**Success metrics.**
- All checkboxes in `STATUS.md` § "Remaining live QA items" green.
- Sentry shows < 5 unique error fingerprints in a 48h soak.
- A single round-trip (upload → delivery) completes against deployed services
  with no manual intervention.

### Group J2 — purge mock/demo residue from frontend (new)

**Scope.** Frontend currently still references some staged demo state that
prospects can see:

- Hardcoded sample PO `008412` in `/inbox/008412` and any link that bypasses the
  real upload→order id flow.
- Mock dashboard rows / mock crossings / mock supplier health on `/bridge` when
  `NEXT_PUBLIC_USE_MOCK=false`.
- Hardcoded UUIDs in any seeded screen.
- Any "isApiMockMode" branches that render fake data in production builds.

Audit the entire `src/app/(app)/` tree and `src/components/bridge/` for residue.
Either route to live API behind `useQuery` (preferred) or render an explicit
empty state ("No orders yet — try uploading one") instead of fake content.

**Sequencing rationale.** Demo data inside a live deployment is an instant
trust kill for any procurement prospect. This must clear before any sales /
demo call against the deployed Vercel URL.

**ICP fit.** Procurement leads doing diligence will open dev tools, follow
network traffic, and notice if dashboard numbers are static. We lose them on
that alone.

**Success metrics.**
- Zero hardcoded order ids, supplier ids, or fake rows in any production build.
- Every list/table screen has an explicit empty state copy.
- Playwright crawl of the live Vercel preview shows no `008412` or `__mock__`
  references in any rendered route.

### Group L (expanded) — onboarding + dual-persona UX

Wave 1 + Wave 2 + Wave 3 of Group L shipped (cookie banner, PostHog SDK
frontend + backend, `org_created` / first-supplier / first-upload /
first-transform / first-delivery / billing event emitters, 4-step onboarding
wizard, sample-order endpoint + button, `/welcome`, `/watch`, `/help`,
`/support` contact form, Pilot Book-a-demo CTAs, dead-code cleanup, DPA /
subprocessors / AUP pages, one-pager). The expanded scope below is what is
**new** for Phase 6 on top of that base.

**Scope (new for Phase 6).**

- **Dual-persona UX rollout** — Default mode (novice) and Expert mode (power user)
  toggle on every operational screen (`/upload`, `/inbox`, `/orders/[id]`,
  `/library/*`, `/operations/*`, `/settings`). Toggle is sticky across sessions
  in `localStorage`. Default mode: wizard, templates, AI defaults, generous
  spacing. Expert mode: density, raw view, standards mapping inline, hotkeys.
  Specification lives in `CLAUDE.md` "Product-level rules" and
  `docs/design-system/00-agent-quick-brief.md` "Dual-Persona UX".
- **Magic mapping preview** — On first upload, show a side-by-side preview of
  source field → canonical field → supplier field before the user has to commit.
  AI-suggested mappings render with visible confidence and provenance. The
  user can accept/edit/reject before the order persists.
- **Per-industry templates** — Bundle starter templates for the four
  industries we expect to land first: industrial distribution, food &
  beverage wholesale, hospitality procurement, healthcare GPO buyers.
  Each template ships with a canonical mapping skeleton, three example
  supplier mappings, and a sample order. Loaded from
  `ProcuLink.Api/Fixtures/templates/<industry>/`.
- **In-app help completion** — `/help` already has 7 MDX articles + Fuse.js
  search (`f09390b`). Phase 6 adds context-aware help links: every screen has a
  Help affordance in `BridgeTopbar` that opens the slideover pre-routed to the
  matching article. Backfill the `/help` set to cover every screen reachable
  from the sidebar.
- **Analytics funnel completion** — PostHog wave shipped; Phase 6 wires the
  Stripe webhook handlers to actually invoke `EmitBillingUpgradedAsync` /
  `EmitBillingDowngradedAsync` / `EmitBillingCancelledAsync` (currently
  defined but webhook wiring is the deferred chip). Confirms billing events
  show up in the funnel and the upgrade attribution path is closed.
- **Trust pack live links** — Wire `NEXT_PUBLIC_STATUS_URL` once Instatus /
  BetterStack is provisioned. Wire `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` once the
  founder records the 60-90s walkthrough. Wire `NEXT_PUBLIC_BOOK_DEMO_URL`
  once a Cal.com slot is live.

**Sequencing rationale.** Dual-persona is upstream of every other Horizon
2/3 feature — adding magic mapping preview, per-industry templates, or
standards-visibility chrome later costs more if the persona toggle is bolted
on rather than baked in. Magic mapping preview is the highest-leverage way to
shorten time-to-first-delivery for novice users. Per-industry templates are
how we cut sales cycle length for a known vertical.

**ICP fit.** A 30-year procurement veteran does not want to babysit a wizard;
they want density, keyboard shortcuts, and direct edit. A first-time user
hired six months ago needs the wizard or they bounce. We cannot win both
without dual-persona.

**Success metrics.**
- Onboarding wizard completion rate ≥ 70% for new orgs in the first 14 days.
- Time from sign-up to first successful delivery < 15 minutes for a novice
  user, < 5 minutes for an expert user.
- Expert mode adoption ≥ 40% on accounts that complete the onboarding wizard
  and run ≥ 5 orders.
- Magic mapping preview accept-without-edit rate ≥ 60% on text-based PDF
  and CSV input.
- ≥ 3 industries with at least one production-use template in active use.

---

## Horizon 2 — Standards Backbone + Channel Breadth (Q4 2026)

### Why now

Once Horizon 1 is reliable, the next constraint on pilot conversion is:
"can ProcuLink talk to the specific format / channel my counterparty
mandates?" If the answer is no, no amount of UX polish saves the deal.

### Group M — standards depth

**Scope.**

- **UBL 2.1 Order** — parser + transformer. Required for EU mandate where
  buyers ship UBL.
- **Peppol BIS 3.0 Order** — Peppol-conformance profile on top of UBL. Required
  for any customer doing business via the Peppol network.
- **EDIFACT ORDERS d.96A** — real parser, not stub. Evaluate `EdiFabric`
  (commercial) versus open-source alternatives (`EdiWeave`, `Smooks`,
  `bots-edi`) on licence cost, throughput, error reporting, segment-version
  coverage. Decision goes in
  `docs/superpowers/specs/2026-Q4-edifact-library-evaluation.md`.
- **ANSI X12 850** — parser + transformer for the US market. Likely shares the
  same library decision as EDIFACT.
- **Generic JSON / REST PO output** — `JsonTransformService` +
  `OutputFormat.Json`. Canonical PO model emitted as a JSON envelope suitable
  for webhook delivery to suppliers running their own ERP webhook receivers.
- **ISO 20022 reference** — document the mapping from the canonical PO model
  to ISO 20022 PO-equivalent concepts (the standard is finance-led but
  procurement-relevant). No code yet; reference only for future Enterprise
  buyers asking about ISO alignment.
- **In-app standards comparison screen** — new `/standards` route in the app
  for expert-mode users. Side-by-side view of "this canonical field in UBL /
  EDIFACT / X12 / cXML / Peppol BIS" with a live example pulled from any of
  the user's actual orders.

**Sequencing rationale.** UBL/Peppol first (largest EU procurement demand).
EDIFACT/X12 second (gates the largest US/EU industrial customers but needs the
library decision). JSON output is cheap and unblocks several existing pilot
discussions. ISO 20022 is documentation-only for now.

**ICP fit.** Procurement leads at companies with ≥ 50 suppliers virtually
always run into ≥ 2 of these standards. Standards breadth is what gets us
past "interesting tool" into "we can replace TrueCommerce".

**Success metrics.**
- 5 distinct standards parseable end-to-end (UBL, Peppol BIS, EDIFACT, X12, cXML).
- 5 distinct standards transformable end-to-end (same list).
- Standards-mapping inline (expert mode) supported for every field on the
  canonical PO model.
- At least one pilot customer converted on each of UBL, EDIFACT, X12.

### Group N — channel expansion

**Scope.**

- **SFTP out** — dispatcher in `ProcuLink.Infrastructure.Delivery`. Uses
  `SSH.NET` or `WinSCP.NET`; decision in
  `docs/superpowers/specs/2026-Q4-sftp-library-evaluation.md`. Per-supplier
  host/port/credentials/key/path config encrypted with the existing
  `AesGcm`-based `DeliveryEncryptionService`. Test-fire endpoint as for HTTP.
- **FTPS out** — same surface, separate dispatcher.
- **SMTP send-out** — generate a multipart email with the rendered PO as
  attachment and (optional) PO body. Uses MailKit (already pulled for IMAP).
  Per-supplier SMTP credentials encrypted; reuses the email send abstraction
  added in Wave 3 (`IEmailSender`).
- **AS2 / AS4** — required by some industrial and US retail customers.
  **Partner-wrap first**: integrate `mendelson AS2` or `DragonAS2` (open
  source) as the gateway and treat them as a delivery dispatcher. In-house
  AS2/AS4 implementation is a separate Horizon 3 line item if customer
  demand warrants the security audit overhead.
- **PEPPOL Access Point** — required for EU public-sector and increasingly
  for B2B in regulated markets. **Partner-wrap first** via Pagero, Tradeshift,
  or a similar certified Access Point. Migration to an in-house Access Point
  (which requires Peppol Authority approval) is on the Horizon 3 roadmap if
  volumes justify it.
- **Generic HMAC-verified webhook receive** — counterpart to the existing
  `IngressController` (Wave 4 added the API-key + slug ingress). The new
  channel accepts inbound payloads from supplier ERPs / EDI gateways with
  HMAC-SHA256 signature verification, so suppliers can push acknowledgements
  back to ProcuLink without API keys.

**Sequencing rationale.** SFTP / FTPS first (lowest implementation cost, high
ICP demand). SMTP send-out second (covers the "my supplier just wants an
email" long tail). AS2/AS4 and PEPPOL third — partner-wrapping reduces time
to first AS2/PEPPOL delivery from "months" to "weeks" and defers the
security audit cost until volume justifies it.

**ICP fit.** Industrial distribution lives on SFTP. Hospitality and SMB
suppliers live on SMTP. Regulated EU customers require Peppol. Large US
retailers require AS2. None of these is optional for the wedge — only the
build-vs-wrap decision is.

**Success metrics.**
- HTTP, SFTP, FTPS, SMTP, AS2 (partner-wrapped), PEPPOL (partner-wrapped),
  webhook-in: 7 channels live.
- At least one production pilot per channel.
- Delivery success rate ≥ 95% per channel over a 30-day rolling window.

### Group O — delivery feedback loop

**Scope.**

- **Retry/replay queue UI** — `/operations/retries` showing failed delivery
  attempts with reason, last error, attempt count, exponential-backoff
  schedule, and a manual "retry now" / "give up and mark exception" control.
  Replaces the implicit Hangfire retry behaviour with a visible operator
  surface.
- **Supplier rejection capture** — two paths: manual (operator pastes / uploads
  the supplier's rejection message), and email-in (the IMAP polling job already
  used for ingress is extended to recognise rejection patterns and link them to
  the originating order via PO number or supplier reference).
- **ACK round-trip** —
  - APERAK (EDIFACT acknowledgement) inbound parser, correlated by EDIFACT
    interchange / message reference back to the original order.
  - MDN (AS2 message disposition notification) inbound handler, correlated by
    AS2 message id.
  - DESADV correlation — when a DESADV (Wave 3 ASN model) is received for an
    order, link it to the originating PO so the operator can see the full lifecycle.
- **Per-supplier SLA timer** — countdown on each in-flight order showing the
  configured SLA (e.g. "supplier must acknowledge within 4h"). Breaches surface
  in the dashboard and (optional) trigger a webhook fired through the existing
  `IntegrationTriggerService`.

**Sequencing rationale.** Retry/replay queue is the most-requested operator
feature in any EDI/messaging product; it must come first. Rejection capture
and ACK round-trip are what turn "we sent the PO" into "we delivered the PO
and the supplier confirmed". SLA timers are how procurement leads convince
their CFO that the spend is worth it.

**ICP fit.** A 30-year procurement veteran does not believe "delivered" until
they have an ACK. SLA visibility is what makes ProcuLink purchasable by ops
managers, not just buyers.

**Success metrics.**
- Retry queue surfaces every failed attempt with non-trivial error context.
- ACK round-trip closed for EDIFACT (APERAK) and AS2 (MDN).
- At least 50% of pilot deliveries land with a captured ACK or DESADV.
- SLA-timer alerts wired into at least one pilot's Slack / email.

---

## Horizon 3 — Network Effects (Q1 2027+)

### Why now

Horizons 1 + 2 give us a usable, reliable, broad product. Horizon 3 is what
turns it from "good outbound tool" into a category-defining network — the
moat that incumbents cannot easily copy because it is built from real
customer data and configuration.

### Group P — RBAC within org

**Scope.**

- Roles: Owner, Admin, Operator, Viewer.
- Per-supplier delegation: an Operator can be scoped to a subset of suppliers
  (useful for large procurement teams where each buyer owns a portfolio).
- Per-user audit log: who created / approved / overrode each mapping decision,
  delivery, and rejection.
- SCIM 2.0 provisioning for Enterprise plan customers integrating with Okta /
  Azure AD / Google Workspace.

**Sequencing rationale.** RBAC blocks Enterprise contracts but is not needed for
the SMB / Growth / Operations / Integration plan ladder. Sequencing it after
Horizons 1 + 2 keeps us focused on wedge depth before enterprise breadth.

**ICP fit.** Procurement teams of ≥ 10 buyers cannot use a shared single login.
Enterprise security teams will not approve a tool without SCIM.

### Group Q — supplier mapping library

**Scope.**

- **Passive accumulation starts in Horizon 2.** Every supplier mapping any
  customer builds is anonymised (strip customer identifiers, keep
  format/channel/field signature) and persisted in a separate
  `mapping_library` table.
- **Public catalog ships in Horizon 3.** A `/library/public` view shows
  community-contributed mappings for each named supplier. A new customer
  onboarding the same supplier can clone the library mapping as a starting
  point.
- Mappings are reviewed and merged manually (no auto-promote) until quality
  signal is established.

**Sequencing rationale.** This is the network-effects flywheel. Every customer
who maps a supplier makes ProcuLink stronger for the next customer who needs
to map the same supplier. We start accumulating data in Horizon 2 but do not
ship the catalog until we have enough data to make it useful.

**ICP fit.** The single biggest source of customer pain is "supplier X requires
a specific field nobody else uses". A pre-built mapping library shortens the
time-to-deliver for that supplier from days to minutes.

### Group R — i18n

**Scope.**

- UI translation for EN, DE, FR, ES, IT, PL — the six languages that cover
  the EU procurement market we expect to land in 2027.
- AI mapping in any language (already partially true via OpenAI; the rule
  is to never assume English in field descriptions or supplier item
  descriptions).
- Per-locale number/date/currency formatting throughout the canonical PO
  model display, not just the marketing pages.

**Sequencing rationale.** i18n cost compounds the longer we wait. Doing it
once Horizons 1 + 2 are stable is cheaper than retrofitting across 50+
screens later. EU customers will not accept English-only tooling for their
non-EN suppliers.

**ICP fit.** EU procurement teams must serve suppliers in the supplier's
local language. Without i18n we are EN-market-only.

### Group S — P2P loop closure

**Scope.**

- **Invoice send** — UBL Invoice 2.1 + Peppol BIS Invoice 3.0 outbound
  transform. Builds on the Wave 3 invoice canonical model (already
  implemented for inbound).
- **DESADV round-trip** — receive supplier ASN, correlate to PO, render in
  the order timeline.
- **3-way match prep** — surface PO → ASN → Invoice matching status per
  order so the operator can see "everything reconciles" or "discrepancy in
  line X". Full 3-way reconciliation is left to the buyer's ERP / AP
  workflow; ProcuLink provides the visibility layer.

**Sequencing rationale.** The P2P (Procure-to-Pay) loop is the natural
expansion from PO-only into invoice/ASN. We have the inbound canonical models
already (Wave 3); Horizon 3 adds outbound and reconciliation. This is what
turns ProcuLink from a PO bridge into a P2P network participant.

**ICP fit.** Procurement leads who buy "outbound PO" first will later want
"close the loop" for the same supplier set. Selling the same buyer twice is
how we get net-revenue retention above 100%.

---

## Anti-scope

Things ProcuLink will deliberately not chase in Phase 6:

- **Full ERP replacement.** ProcuLink integrates with ERP, never replaces it.
- **Accounts payable workflow.** Invoice send-out is in Horizon 3 scope; AP
  approval routing is not.
- **Marketplace / spend analytics.** Different product category; would
  dilute the wedge.
- **Bank / payment rails.** Out of scope.
- **General document automation.** PO and (Horizon 3) Invoice + ASN only.

Anything not in this roadmap defers to a later phase. New requests get
triaged: do they fit a current Horizon group, or do they go to a Horizon 4
parking lot?

---

## Working agreements

- **Source of truth.** This file is the source of truth for the forward
  plan. `STATUS.md` reflects current shipped state. `CLAUDE.md` reflects
  the durable product rules.
- **Updates.** Update this file when a Horizon group ships, when its scope
  changes materially, or when sequencing rationale changes due to learning
  from real customer use.
- **Honesty.** Never describe a Horizon 2 / 3 item as "shipped" or
  "available" in marketing copy. Horizons 2 and 3 are roadmap, not product.

---

## References

- Positioning rationale: `docs/strategy/international-standard-thesis.md`
- Standards matrix (current + planned): `docs/standards-matrix.md`
- Canonical PO model: `docs/canonical-po-model.md`
- Format/channel ground truth: `docs/format-channel-roadmap.md`
- Design system entry point: `docs/design-system/00-agent-quick-brief.md`
- Previous Phase 5 plan (kept for audit trail):
  `docs/superpowers/plans/2026-05-26-production-hardening-roadmap.md`
