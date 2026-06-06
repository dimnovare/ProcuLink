# Handover — founder feedback round (2026-06-06)

Self-contained handover for the four items the founder raised on 2026-06-06, plus the real-PO test
evidence. Repos: backend `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`, frontend
`C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (use **bun**, never npm). Rule: **offer ⇔ works**.

---

## 1. Real-PO testing — DONE this session (with one real bug found)

The founder was right that no test had ever run the **real** `C:\Users\Dmitri.REDACTED-PARTY\Downloads\POs`
corpus (22 real PDFs: Danfoss, ABB, REDACTED-PARTY, Aperam, Veolia, Continental, REDACTED-PARTY, Rheinbahn,
REDACTED-PARTY, ANDRITZ, EXEMPLAR SEAFOOD, LähiTapiola, Somfy, BeCom, SUEZ, …). Two tests were run:

### (a) Extraction benchmark — `~/pl_bench.py` on `gpt-4o-mini` (the PROD model)
- **22/22 documents parsed**, **60 line items**.
- **180/180 numbers verbatim in source = 100%** (anti-hallucination).
- **60/60 qty×price = line amount = 100%** (arithmetic).
- NOK/EUR/DKK/PLN/CZK/GBP, EN/DE/FR/PL/FI.
- This harness mirrors the prod extractor's schema but uses **PyMuPDF**, not the .NET path.

### (b) TRUE .NET pipeline E2E (PdfPig + `OpenAiPdfOrderExtractor`) on 3 real PDFs
Ran the actual local API+Worker with the real OpenAI key (recipe below) and uploaded Danfoss (DKK,
1 line), ABB (PLN, 8 lines), REDACTED-PARTY (DE/EUR, 1 line):
- **All line items, quantities, unit prices, line amounts, totals, currency, doc-type extracted
  correctly** — matched the benchmark exactly.
- All lines correctly went to **`pending_review`** (no supplier-code mapping exists yet → human
  review, as designed).
- **🐞 REAL BUG FOUND — buyer/supplier party roles swapped on 2 of 3 POs.** Danfoss showed
  `buyer=REDACTED-PARTY` (should be Danfoss) and REDACTED-PARTY showed `buyer=REDACTED-PARTY` (should be
  REDACTED-PARTY); ABB was correct (`buyer=ABB`). Numbers are perfect — only the **party-name role
  assignment** is unreliable.
  - **Root cause hypothesis:** `OpenAiPdfOrderExtractor`'s prompt doesn't deterministically define
    which party is the buyer (entity that ISSUED/placed the order) vs the supplier/vendor, especially
    since "Markit" appears on every document. There's also a **product-model nuance**: ProcuLink's ICP
    is "the buyer sending POs OUT", but Markit is *receiving* customer POs (Danfoss/ABB are the
    buyers, Markit is the supplier) — the opposite direction. The extractor may be trying to fit
    Markit-as-buyer.
  - **Fix (TODO):** tighten the extractor system prompt to map "the organisation that issued/placed
    this order" → `buyer_name` and "the vendor/recipient it is addressed to" → `supplier_name`,
    independent of which name is the ProcuLink customer. Add a fixture test asserting buyer≠supplier
    and the correct buyer on 2–3 real POs (anonymise into `ProcuLink.Transform.Tests` fixtures).
  - **Decide with founder:** is Markit using ProcuLink for *inbound* customer-PO processing (supplier
    side) rather than *outbound* buyer POs? That reframes the whole party model and the dashboard
    "buyer → supplier" rail.

### Test recipe (reusable) — run the real .NET pipeline locally
```
# 1) pull the prod OpenAI key (never print/commit it)
$json = railway variables --service ProcuLink --json | ConvertFrom-Json
# 2) API  (Development + QA bypass + real key)
$env:ASPNETCORE_ENVIRONMENT='Development'; $env:PROCULINK_QA_BYPASS_AUTH='true'
$env:ASPNETCORE_URLS='http://localhost:5223'; $env:Delivery__EncryptionKey=<32-byte base64>
$env:Ai__Provider='openai'; $env:Ai__OpenAI__ApiKey=$json.'Ai__OpenAI__ApiKey'
$env:Ai__OpenAI__MappingModel='gpt-4o-mini'; $env:Ai__OpenAI__ExtractionModel='gpt-4o-mini'
dotnet run --project ProcuLink.Api      # background; poll http://localhost:5223/health
# 3) Worker (same env minus URLs/bypass) — MANDATORY (API hosts no Hangfire)
dotnet run --project ProcuLink.Worker
# 4) reuse the existing supplier (Pilot = 1-supplier limit blocks creating a new one):
#    GET /api/orders/upload? no — GET /api/suppliers -> id 943eed7d-... (ProcuLink Sample Supplier)
# 5) POST /api/orders/upload  -Form @{ file=Get-Item <pdf>; supplierId=<id> } -> poll GET /api/orders/{id}
```
Local Postgres is on :5435 (`proculink_dev`, already migrated). See memory
`project-local-golden-path-and-hardening`.

**What "full tests" should become (TODO):** a committed, gated integration test
(`PROCULINK_LIVE_AI_TESTS=1`) that uploads a handful of anonymised real PDFs through the real
extractor and asserts line counts + number fidelity + correct party roles — so this never silently
regresses. The 22 real PDFs are the corpus; anonymise a representative subset into test fixtures.

---

## 2. Mobile UI — needs a proper responsive pass (screenshots in `~/Downloads/1000012563–72.jpg`)

One clear truthfulness bug was **fixed this session** (and pushed): the dashboard "In transit" card
had a fake **"last 10 min ▾"** control (static span + dead chevron implying a time filter that does
not exist) — removed (`BridgeDashboard.tsx`). Everything below is **still TODO** — a dedicated,
careful responsive pass (the founder wants this done *right*, not rushed):

| # | Issue (founder's words) | Component | Fix approach |
|---|---|---|---|
| a | Notifications panel doesn't fit / clips off-screen left | `src/components/bridge/BridgeTopbar.tsx` | On small screens make the dropdown a full-width sheet (`fixed inset-x-2 top-16`, max-h + scroll) instead of an absolute fixed-width panel anchored to the bell. |
| b | Inbox: dead "filters" above the table + cramped | `src/components/bridge/InboxView.tsx` | The status-filter row ("All orders" chip + stray nub) is mis-laid-out on mobile — make the status tabs a horizontal scroll row; give the search full width below. |
| c | Inbox cards: "no understanding where a line begins"; buyer→supplier rail breaks when buyer is empty (stray dashes, misaligned supplier) | `src/components/bridge/InboxView.tsx` | Add clear card separation/elevation; when buyer is missing render a single honest label (not two disconnected dashes); stack the buyer→supplier rail vertically on mobile. |
| d | Order detail "hides text": Save draft / Send to supplier buttons **overlap the PO title** | `src/components/bridge/SpineReview.tsx` (header ~L1700) | On mobile, stack the header: title row, then a full-width action bar (Save draft / Send to supplier) below — don't absolutely position the buttons over the title. Also the "Validate against profile" button clips its 2nd line — let it wrap/auto-height. |
| e | Email intake "IMAP mailbox" header row cramped on small screens | `src/app/(app)/settings/page.tsx` (Email-intake panel) | Stack the icon + "IMAP mailbox" label above the description on mobile instead of a tight 3-column row; let the settings sub-nav grid + currency/region pills wrap cleanly. |
| f | Dashboard "All suppliers →" / supplier-health density; confirm the remaining arrow does something | `src/components/bridge/BridgeDashboard.tsx` | Verify the "All suppliers →" link routes; tighten card density on mobile. |

**Approach:** do this as one focused responsive sweep on a branch, QA each screen on a real small
viewport (375–414px) via the existing `:8082` dev server (DOM/eval, NOT screenshots — see memory
`project-preview-server-contention`), `bun run build`, then merge. Keep the locked Bridge visual
system (navy/violet/rails). Do NOT introduce a user-mode toggle (one great responsive experience).

---

## 3. Owner/admin area — DESIGN (to build next)

The founder (platform owner) wants a private admin area to see **who bought which plan**, **revenue /
MRR / ARR / company health**, and to **generate a manual invoice** (founder-led setup of higher tiers).

### Access model (critical — this is the one legitimately cross-tenant surface)
- A **platform-admin** concept distinct from org membership. Simplest + secure for a solo founder:
  an **env allowlist of admin Clerk user-ids / emails** (e.g. `Admin__UserIds` / `Admin__Emails`),
  checked **server-side** on every admin request. NOT a Clerk org role (admin is cross-org).
- Backend: an `AdminAuthorizationFilter`/policy that 403s anyone not in the allowlist. The
  `AdminController` is the ONLY controller that is **not** org-scoped — so its auth must be airtight
  and covered by tests (e.g. a non-admin user gets 403; an admin sees all orgs).
- Frontend: an `/admin` route gated in middleware + server component (redirect non-admins).

### Data + endpoints
- `GET /api/admin/overview` → MRR, ARR (MRR×12), active/trialing/past-due/cancelled counts, new orgs
  this month, trial→paid conversion. **Compute MRR from active paid orgs × plan price** (PLAN_BY_ID)
  for a fast in-DB number, and/or reconcile against **Stripe** (the SoT for actual collected revenue,
  discounts, proration). Recommend: DB for the live operational view, a "reconcile with Stripe" link.
- `GET /api/admin/organisations` → every org: name, plan, account_status, Stripe customer/subscription
  id, MRR contribution, created_at, last order activity, order volume (30d), supplier count. Sortable.
- `POST /api/admin/invoices` → **create a one-off Stripe invoice** (customer + line items) via the
  Stripe API. Stripe handles the PDF, VAT, payment link, and dunning — far better than rolling our own
  invoice PDF. Use the existing `StripeBillingService`/Stripe client; add `CreateInvoiceAsync`.
- (later) revenue time-series for a chart, churn/cohort, per-plan breakdown.

### Frontend `/admin`
- **Overview**: MRR/ARR/active/trialing headline cards + a simple revenue trend.
- **Customers** table: org · plan · MRR · status · joined · last activity · → Stripe.
- **Create invoice** modal: pick org (→ Stripe customer), add line items (e.g. "Founder-led
  onboarding — Acme GmbH"), amount, send. Confirmation + link to the Stripe invoice.

### Phasing
1. **MVP**: admin auth (env allowlist + tests) · `/admin` customers table (plan/status/MRR) · overview
   headline (MRR/ARR/active) computed from DB. *(This alone answers "who bought what" + "how am I doing".)*
2. **Invoicing**: `POST /api/admin/invoices` → Stripe one-off invoice + the modal.
3. **Polish**: revenue chart, churn, Stripe reconciliation, CSV export.

**Decision for founder:** confirm the admin email(s)/Clerk user-id(s) for the allowlist, and whether
MRR should be DB-computed (fast, approximate) or Stripe-sourced (accurate, the real money).

---

## 4. Pricing copy — DONE this session

The false **"€500/supplier ×3 then €150, waived for design partners"** onboarding-fee claim is retired
(founder, 2026-06-06). Fixed + pushed (frontend `93469c5`, backend `4a5c496`):
- `src/lib/plans.ts` `SETUP_FEE_NOTE` → "hands-on, founder-led supplier onboarding — we configure your
  suppliers with you during setup" (no fee).
- `src/app/(marketing)/pricing/page.tsx` comment, `src/components/marketing/ROICalculator.tsx` fine
  print (ROI math already used `setup=0`), `CLAUDE.md` pricing line.

**Still TODO:** (a) confirm the FINAL onboarding wording with the founder (is founder-led onboarding
free/included, or is there a new fee?); (b) revise the **dated 2026-05-30 strategy memos**
(`docs/strategy/2026-05-30-pricing-proposal.md`, `…-investor-analysis.md`) and
`docs/deployment/stripe-go-live-checklist.md` which still reference the old fee model — left as
historical records, founder to decide whether to revise.

---

## Priority order to make this the best tool

1. **Buyer/supplier role fix** (small, high-trust — wrong party on a delivered PO is a credibility
   killer) + a committed real-PO extraction test.
2. **Mobile responsive pass** (founder uses it on mobile; first-impression quality).
3. **Admin area MVP** (owner needs revenue visibility + manual invoicing for founder-led deals).
4. Confirm onboarding wording + revise dated memos.
