# ProcuLink Live Readiness Brief

Date: 2026-06-03  
Audience: founder, product/engineering advisor, deep-analysis reviewer  
Purpose: explain the current status, what is working, what is blocked, what is planned next, and how ProcuLink should move toward a live paid product after company registration on 2026-06-09.

---

## Executive Summary

ProcuLink is a B2B outbound procurement bridge for buyer/procurement teams that need to send purchase orders to suppliers in the supplier's required format and delivery channel.

The core product promise is:

> Import a buyer-side order source -> parse and normalize it -> validate supplier-specific requirements -> review only exceptions -> transform into supplier-ready output -> deliver -> audit and learn.

The strongest current wedge is not broad document automation. It is reliable outbound purchase-order processing for buyers/procurement teams that send messy or inconsistent POs to many suppliers.

The product is no longer being treated as a throwaway MVP. The current engineering direction is production hardening: make the primary PO path boringly reliable before adding more breadth.

Current live status:

- Marketing frontend is live at `https://proculink.eu`.
- API health is live at `https://api.proculink.eu/health`.
- Authentication, CORS, public routes, protected-route redirects, and R2 storage are now working.
- Sample-order creation and direct CSV upload now work at API level.
- The current live blocker is background processing: uploaded orders remain `parsing` because the production Hangfire Worker service is not currently running/linked in Railway.

The next critical step is to deploy `ProcuLink.Worker` as a separate Railway service from `Dockerfile.worker`, with the same production environment variables as the API where required.

---

## Product Direction

### What ProcuLink Is

ProcuLink is an AI-assisted order integration middleware platform for B2B procurement.

It sits between:

- buyer/procurement teams,
- suppliers,
- ERPs,
- procurement systems,
- messy order documents,
- delivery channels such as API, webhook, HTTP, email, SFTP, FTP, and future standards.

The product transforms unreliable incoming buyer-side order data into clean supplier-ready outputs.

### Current ICP

The current first customer profile is:

- buyer/procurement teams sending purchase orders out,
- companies with multiple suppliers,
- teams that handle many supplier-specific formats,
- businesses where supplier item-code mapping causes errors,
- companies where manual reformatting and supplier rejections waste time.

This is a sharper wedge than "generic document automation".

### Core Workflow

The product should stay focused on:

```text
Parse -> Normalize -> Validate -> Review exceptions -> Transform -> Deliver -> Learn
```

The most important live path is:

```text
Upload purchase order
-> Parse file
-> Show extracted order
-> Resolve missing supplier mappings
-> Transform to supplier-ready output
-> Deliver or show exact delivery failure
-> Record audit trail
```

---

## Current Architecture

### Repositories

Backend:

```text
C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink
GitHub: dimnovare/ProcuLink
```

Frontend:

```text
C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
GitHub: dimnovare/project-proculink
```

### Backend

Stack:

- ASP.NET Core 8
- EF Core 8
- PostgreSQL
- Hangfire
- Cloudflare R2 storage
- Clerk auth
- Stripe billing code already implemented, but live Stripe activation is pending
- OpenAI-first provider-neutral AI mapping suggestions
- PDF text extraction with OCR provider fallback support

Important backend processes:

- `ProcuLink.Api` handles HTTP/API requests.
- `ProcuLink.Worker` is the Hangfire job runner and must process parse/delivery/background jobs.
- The API intentionally does not run `AddHangfireServer`; production requires a separate Worker service.

### Frontend

Stack:

- Next.js 15 App Router
- TypeScript
- Tailwind
- shadcn/ui
- Clerk via `@clerk/nextjs`
- TanStack Query
- Vercel deployment

Frontend rules:

- Use Next.js App Router only.
- Do not use Vite.
- Do not use React Router.
- Do not use Lovable.
- Use `bun`, not npm/yarn.
- All API calls go through `src/lib/api-client.ts`.
- UI follows the local Bridge Layer design system.

---

## Live Infrastructure Status

### Domain

Main domain:

```text
https://proculink.eu
```

API domain:

```text
https://api.proculink.eu
```

Current status:

- `https://proculink.eu` returns 200 from Vercel.
- `https://www.proculink.eu` returns 200 from Vercel.
- `https://api.proculink.eu/health` returns 200 from Railway.

### Frontend

Working:

- Marketing pages are live.
- Protected routes redirect signed-out users to local sign-in.
- Sitemap and robots are live.
- Clerk handshake middleware fix is deployed.
- Favicon/logo cleanup has already been addressed in earlier passes.

Still needs QA:

- Full authenticated browser path on production from a browser-capable environment.
- Mobile/tablet checks on the final live flow after Worker is running.

### Backend API

Working:

- API health endpoint is live.
- CORS accepts `https://proculink.eu`.
- Authenticated API requests using a real Clerk production session JWT work.
- Tenant auto-provisioning works.
- Billing status endpoint works.
- Supplier creation/listing works.
- Cloudflare R2 upload is now working.
- Sample-order endpoint now returns 200.
- Direct multipart order upload returns 200 and stores the source file in R2.

Current blocker:

- Uploaded orders remain in `parsing` because the Hangfire Worker service is not consuming the parse queue in production.

### Storage

Cloudflare R2 is now configured and working for the API service.

Verified:

- sample-order fixture upload works,
- direct uploaded CSV stores to R2 and returns a `sourceFileKey`,
- previous R2 signature mismatch is resolved.

### Background Worker

Current blocker:

```text
ProcuLink.Worker is not running as a separate Railway service.
```

Evidence:

- API logs show order stubs are created.
- API logs show `ParseOrderJob` is enqueued.
- Orders remain `parsing` for at least 30 seconds.
- API code intentionally does not run Hangfire server.
- Worker code and `Dockerfile.worker` exist and are ready.

Required action:

Deploy a separate Railway service:

```text
Service name: ProcuLink.Worker
Dockerfile: Dockerfile.worker
Public domain: not needed
```

Required Worker environment:

```text
ConnectionStrings__DefaultConnection
ASPNETCORE_ENVIRONMENT=Production
Clerk__Authority
Storage__R2AccountId
Storage__R2AccessKeyId
Storage__R2SecretAccessKey
Storage__R2Endpoint
Storage__R2BucketName
Delivery__EncryptionKey
Ai__OpenAI__ApiKey
```

Optional/depending on enabled features:

```text
Analytics__PostHog__ApiKey
Smtp__*
Stripe__*
Ocr__Azure__*
```

---

## What Is Already Implemented

### PO Intake

Implemented:

- manual browser upload,
- sample order endpoint,
- CSV/XLSX upload path,
- XML routing improvements,
- cXML/UBL/Peppol parser routing,
- PDF text extraction,
- OCR service interface with Azure Document Intelligence fallback when configured,
- inbound REST API for structured order payloads,
- hosted inbound email webhook backend support,
- IMAP polling backend/settings UI,
- SFTP/S3 polling backend with safer default-supplier validation.

Production readiness:

- Manual upload is the primary live path.
- Sample order works at API level after R2 fix.
- Inbound REST/email/SFTP/S3 should be treated as assisted setup until self-service UX and live QA are finished.

### Parsing And Normalization

Implemented:

- CSV parser with common procurement aliases:
  - `po_number`,
  - `PO Number`,
  - `po`,
  - `line_no`,
  - `qty`,
  - `unit_price`,
  - `sku`,
  - `buyer_code`.
- XLSX parser.
- text-based PDF parser.
- cXML parser.
- UBL parser.
- EDIFACT and X12 parser work has been started/implemented in code paths.
- parser factory selection for ambiguous XML.

Production readiness:

- Local end-to-end tests pass.
- Production parse execution is blocked until Worker is deployed.

### Review And Mapping

Implemented:

- mapping preview state,
- unresolved-line review,
- manual supplier-code entry,
- save mapping,
- mapping persistence for future orders,
- AI mapping suggestions through provider-neutral interface,
- OpenAI-first implementation using structured output behavior,
- bulk-accept high-confidence AI suggestions endpoint.

Production readiness:

- Local live QA is green.
- Production live QA is blocked until Worker parses uploaded orders.

### Transform And Delivery

Implemented:

- transform services for supplier-ready output,
- delivery state model:
  - `ready_to_deliver`,
  - `delivering`,
  - `delivered`,
  - `delivery_failed`,
  - `rejected_by_supplier`.
- HTTP/webhook delivery dispatcher,
- delivery attempts table/history,
- retry delivery endpoint,
- missing delivery config failure state,
- supplier rejection detail in UI,
- ERP delivery adapters for Erply and Directo,
- SFTP/FTPS/SMTP dispatcher infrastructure.

Production readiness:

- Local happy/error paths are green.
- Production delivery path needs Worker deployment and live test-fire against a controlled endpoint.

### Billing

Implemented:

- billing constants and plan model,
- organisation billing fields,
- Pilot trial logic,
- order/supplier limits,
- Stripe Checkout flow,
- Stripe Portal flow,
- webhook handling,
- billing UI,
- plan-gated upload/supplier handling.

Current plan ladder:

```text
Pilot        internal trial
Growth       paid subscription
Operations   paid subscription
Integration  paid subscription
Enterprise   manual/contact sales
```

Important note:

The public pricing UI has also been exploring/including a Distributor plan. Before Stripe goes live, pricing copy, frontend cards, backend plan constants, Stripe price IDs, and gating logic must be reconciled so there is one authoritative plan ladder.

### Trust, Support, Analytics

Implemented or started:

- support/contact endpoint,
- SMTP/Resend-compatible delivery path planning,
- PostHog analytics integration is present or planned via env,
- cookie consent,
- legal/trust docs in progress,
- Google Search Console setup still needs final live verification after domain is stable.

Production readiness:

- Support email delivery needs final Resend/domain/from-address verification.
- PostHog should be checked after cookie consent and production env are set.
- Search Console should be configured after sitemap is live and domain ownership is verified.

---

## What Is Not Yet Production-Ready

### P0: Worker Service

Until `ProcuLink.Worker` is running in Railway, the live product cannot complete:

```text
upload -> parse -> review -> transform -> deliver
```

Uploads currently store files, but orders remain `parsing`.

### P0: Full Live PO Loop QA

After Worker deployment, verify:

1. Create/login real user.
2. Create or select organisation.
3. Add supplier.
4. Upload CSV order.
5. Confirm it leaves `parsing`.
6. Review extracted order.
7. Resolve mapping.
8. Transform.
9. Deliver or show exact missing-config/delivery failure.
10. Confirm delivery attempt/audit rows are recorded.

### P0: Stripe Before Paid Launch

Stripe should wait until the company is available on 2026-06-09, but the code should be ready before then.

Before enabling live payments:

- decide final public plan ladder,
- create monthly and yearly prices in Stripe for every self-serve paid plan,
- set all Railway and Vercel Stripe env vars,
- configure webhook endpoint,
- verify Checkout success URL,
- verify Portal,
- verify subscription webhook updates organisation plan/status,
- verify failed/cancelled subscription behavior,
- verify Pilot users cannot get repeated free trials.

### P1: Browser Production QA

Current Codex desktop environment cannot launch Playwright Chromium/Chrome/Edge reliably. Authenticated browser production QA should be run from:

- a normal developer machine with browser access,
- a CI environment with Playwright browsers,
- or an already-signed-in real browser session.

### P1: Email And Support

Before presenting support/contact as live:

- verify Resend domain,
- add required DNS,
- set support sender/from address,
- test contact form delivery,
- test Gmail "send mail as" if founder wants to reply as `support@proculink.eu` or `hello@proculink.eu`.

### P1: OCR

Scanned PDFs are possible through the OCR service interface, but production OCR should not be promised until a provider is configured and tested with real scanned POs.

The recommended architecture remains:

```text
OCR provider extracts text/tables
AI interprets/matches/suggests mappings/confidence
human reviews uncertain fields
```

Do not rely on a general LLM as the raw OCR engine for production purchase orders.

---

## Go-Live Plan Toward 2026-06-09

### Phase 1: Make The Live PO Loop Work

Goal:

```text
One real user can upload a CSV PO on proculink.eu and get to review/transform/delivery state.
```

Tasks:

1. Deploy `ProcuLink.Worker` as a separate Railway service.
2. Copy required env vars from API to Worker.
3. Confirm Worker starts and Hangfire consumes jobs.
4. Upload a real CSV from the live UI.
5. Confirm order leaves `parsing`.
6. Resolve mapping and transform.
7. Test delivery failure state if no delivery config exists.
8. Test HTTP delivery against a controlled endpoint.
9. Confirm audit/delivery attempts are visible.

Exit criteria:

- live API upload works,
- live Worker parses,
- live frontend routes to preview/review,
- delivery state is honest and auditable.

### Phase 2: Production UX QA

Goal:

```text
The product feels trustworthy for a first buyer/procurement customer.
```

Tasks:

1. Test desktop, tablet, and mobile.
2. Test first-run empty states.
3. Test upload errors:
   - unsupported file,
   - no supplier,
   - order limit,
   - parsing failure,
   - missing delivery config.
4. Test settings/billing pages.
5. Test pricing CTAs.
6. Test help/support flow.
7. Remove or hide anything that looks demo-only or non-functional.

Exit criteria:

- no visible broken flows,
- no misleading "sent" states,
- no dead CTAs on primary pages,
- mobile is usable for monitoring/review, even if heavy mapping remains desktop-first.

### Phase 3: Stripe Activation On/After 2026-06-09

Goal:

```text
Pilot users can upgrade to a paid plan and the organisation state updates correctly.
```

Tasks:

1. Register company details in Stripe.
2. Create final live products/prices.
3. Decide whether Distributor is part of the official ladder.
4. Add all monthly and yearly Stripe price IDs.
5. Configure Railway Stripe env vars.
6. Configure Vercel env vars if frontend needs price metadata.
7. Configure Stripe webhook endpoint:
   - `checkout.session.completed`,
   - `customer.subscription.updated`,
   - `customer.subscription.deleted`.
8. Run live Checkout with a real low-risk test plan or Stripe test mode first.
9. Verify organisation plan changes.
10. Verify Portal and cancellation/read-only behavior.

Exit criteria:

- Pilot -> paid plan works,
- limits update,
- webhook updates database,
- cancellation does not reset a free trial,
- billing UI reflects reality.

### Phase 4: Customer-Ready Intake And Documentation

Goal:

```text
A non-technical buyer/procurement team understands how to send orders to ProcuLink.
```

Tasks:

1. Polish public/in-app documentation for:
   - browser upload,
   - sample order,
   - inbound REST API,
   - email intake,
   - SFTP/S3 assisted intake,
   - output/delivery channels.
2. Make the docs honest about what is self-service vs assisted setup.
3. Add onboarding copy that explains the first supplier flow in plain language.
4. Add downloadable sample CSV/XLSX templates.

Exit criteria:

- a prospect can understand the flow without a demo call,
- an implementation partner can integrate against the API docs,
- assisted setup boundaries are clear.

---

## Strategic Direction After Go-Live

### Keep The Wedge Sharp

The near-term product should not become "any document to any format" too quickly.

Best next position:

```text
The buyer-side purchase-order bridge for companies whose supplier formats are too messy for Zapier and too small/custom for MuleSoft.
```

Focus on:

- purchase orders first,
- supplier item-code mappings,
- supplier-specific validation,
- transform/delivery reliability,
- audit trail,
- exception review.

### Make Reliability The Differentiator

The product should win because:

- outputs are accepted,
- failures are explainable,
- mappings are remembered,
- rejections are auditable,
- operators review only exceptions,
- suppliers can have different rules/channels.

AI is valuable, but the product should not be positioned as "AI reads a PDF".

Better:

```text
ProcuLink reduces manual PO reformatting and supplier rejection loops by turning messy buyer orders into validated supplier-ready outputs.
```

### Add Breadth Only After The PO Loop Is Solid

Good next expansion order:

1. CSV/XLSX/XML/cXML/UBL PO reliability.
2. Supplier output templates and HTTP/webhook delivery.
3. Email intake and inbound REST API self-service docs.
4. SFTP/S3 assisted intake.
5. Scanned PDF OCR with provider configuration.
6. ERP-specific adapters.
7. Invoices/ASNs/PEPPOL once PO loop is trusted.

---

## Main Risks

### Risk 1: Too Much Breadth Before Reliability

If invoices, ASNs, PEPPOL, every OCR case, and every connector are pushed before the PO loop is live, the product will look broad but fragile.

Mitigation:

Make one PO flow work fully on production first.

### Risk 2: Demo UI That Does Not Persist

If UI actions appear to save/test/deliver but only show local feedback, trust drops immediately.

Mitigation:

Every primary CTA should either:

- persist through API,
- show a clear "draft/local only" message,
- or be hidden until implemented.

### Risk 3: Stripe Plan Drift

There is possible drift between:

- backend billing constants,
- marketing pricing cards,
- Stripe products,
- yearly/monthly prices,
- Distributor plan exploration.

Mitigation:

Before enabling live Checkout, create one authoritative pricing matrix and update code/docs/env to match it.

### Risk 4: Worker/Background Jobs Invisible To Users

If background jobs fail silently, users see stuck `parsing` or `delivering` states.

Mitigation:

Add visible stuck-state guidance and internal monitoring:

- job queue health,
- stuck parsing detector,
- failed job alerts,
- retry/dead-letter views.

---

## Immediate Checklist

### Must Do Before Calling It Live

- [ ] Deploy `ProcuLink.Worker` as a separate Railway service.
- [ ] Copy required env vars to Worker.
- [ ] Verify Worker consumes `ParseOrderJob`.
- [ ] Run live upload -> parse -> preview -> review -> transform -> delivery QA.
- [ ] Verify no primary CTA is dead or misleading.
- [ ] Verify support/contact email delivery.
- [ ] Rotate the live Clerk secret that was pasted into chat.

### Must Do Before Taking Payment

- [ ] Register company in Stripe.
- [ ] Decide final pricing ladder, including whether Distributor exists.
- [ ] Create live monthly/yearly prices.
- [ ] Set Stripe env vars in Railway/Vercel.
- [ ] Configure Stripe webhook endpoint.
- [ ] Test Checkout.
- [ ] Test Portal.
- [ ] Test subscription updated/deleted webhook behavior.
- [ ] Test Pilot expiry/read-only upgrade flow.

### Should Do Soon After

- [ ] Google Search Console verification.
- [ ] Submit sitemap.
- [ ] Configure PostHog production analytics.
- [ ] Add uptime/status page.
- [ ] Add a short product walkthrough video.
- [ ] Improve public API/intake docs for prospects.

---

## Suggested Deep-Analysis Prompt

Use this with ChatGPT or another reviewer:

```text
You are reviewing ProcuLink, a B2B outbound procurement bridge for buyer/procurement teams. The first wedge is reliable purchase-order processing: upload buyer PO -> parse/normalize -> validate supplier-specific requirements -> review exceptions -> transform -> deliver to supplier -> audit/learn.

Current live status:
- proculink.eu and api.proculink.eu are live.
- Auth, CORS, protected redirects, Clerk production API auth, tenant provisioning, supplier API, sample-order creation, direct CSV upload, and Cloudflare R2 storage are working.
- Current blocker: uploaded orders stay parsing because ProcuLink.Worker is not deployed/running as a separate Railway Hangfire worker service.
- Stripe should go live after company registration on 2026-06-09.

Please analyse:
1. What must be done before this can be called live?
2. What must be done before charging customers?
3. What product scope should stay in vs wait?
4. What reliability/UX risks would make a B2B buyer lose trust?
5. How should the pricing and onboarding be simplified?
6. What is the best 30-day roadmap from here?
```

---

## Bottom Line

ProcuLink is close to a real live product for the first buyer-side PO use case, but not live-ready until the production Worker runs and the complete PO loop is verified end to end on the live domain.

The right next move is not another broad feature. It is:

```text
Deploy Worker -> verify live PO loop -> clean UX rough edges -> activate Stripe after company setup -> onboard first pilot users.
```

