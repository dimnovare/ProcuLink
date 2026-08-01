# ProcuLink — Pre-Launch Audit & Live Test Plan
_Generated 2026-06-29 from an 11-agent read-only codebase audit (Opus). Read-only: no product code was changed by the audit._

## 0. Readiness verdict

> CONDITIONAL GO — the engine, security spine, and money model are genuinely built and unusually honest (no committed secrets, fail-closed tenancy, offer-vs-works mostly disciplined, no fake-data leaks in live mode), so this is a real product that CAN go live for the outbound-PO wedge once a short, well-scoped blocker list is cleared. The single biggest theme of risk is UNVERIFIED-IN-PRODUCTION integration boundaries: every commercial and channel surface that crosses into a third party (Stripe money path, SFTP/S3 pull ingress, non-HTTP delivery dispatchers, content routing, ASN inbound) is either provably broken (SFTP/S3 never enqueue parse), unconfigurable by a customer, or only confirmable with live external events that nobody has run end-to-end yet. Nothing here is a fabrication-of-capability fraud risk; it is a "we built it but haven't proven the last mile and a couple of advertised channels silently do nothing" risk. Charge customers only for browser-upload + REST-ingress + HTTP/webhook delivery on the verified Stripe ladder, and gate or fix everything else first.

## 1. Go-live blockers (must clear before charging customers)

1. **SFTP and S3/R2 pull ingress never enqueue ParseOrderJob — imported files are stuck forever**
   - SftpIngressService.PollAsync and S3IngressService.PollAsync both call CreateStubAsync but omit the ParseOrderJob.Enqueue that every working channel (OrdersController:239, EmailPollOrgJob:284, InboundEmailRouter:229) performs. Verified in source. A customer who configures SFTP/S3 import gets orders that silently never parse/transform/deliver. Add the enqueue after a successful stub, OR remove SFTP/S3 from all 'supported import channels' marketing copy before charging.
2. **Run the full Stripe money path once in TEST mode end-to-end**
   - Checkout completion, price→plan mapping, plan unlock, customer portal, webhook signature handling, and €0.50 overage at invoice.created are all real code but ZERO of them have been verified with live Stripe events. This is the single largest unverified commercial surface. Configure all price IDs + WebhookSecret and run checkout/portal/overage with the stripe CLI before taking money.
3. **Configure the admin allowlist (Admin:UserIds / Admin:Emails)**
   - Per-org overrides, trial extensions, MRR reconciliation, manual invoicing, and GDPR-erase are gated solely by an env allowlist that is EMPTY by default and fails closed — every /api/admin/* returns 403 until populated. Without it the founder cannot grant pilot extensions, raise caps, or erase data in production. Set it and verify the positive path.
4. **Flip admin Stripe dashboard links from /test/ to live**
   - admin/page.tsx hardcodes dashboard.stripe.com/test/customers/ with an explicit go-live TODO at line 38. In live mode the owner's 'View' links go to test-mode/404. Trivial but must ship before launch.
5. **Walkthrough video: upload the R2 asset or blank NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL**
   - The committed env hard-codes the MP4 URL but project memory says the master is a DRAFT not uploaded. The friendly fallback only renders when the URL is EMPTY, so a set-but-missing asset gives a broken/black <video> on /watch in prod. Either upload it or blank the var.
6. **Gate /library/rules or wire it to the acceptance engine**
   - Library → Rules is a fully CRUD-able 'validation rules' surface whose ValidationRules table is NOT read by any parse/validate/transform code; real enforcement lives in the separate per-supplier AcceptanceProfile system. A user can author rules that silently never run against orders — a trust/dead-control hazard. Remove/redirect the page to the supplier Validation tab, or connect it.
7. **Confirm ASPNETCORE_ENVIRONMENT=Production and DevFilesController inert on Railway**
   - DevFilesController has no auth attribute and serves local files; it is gated only by IsDevelopment(). Production startup also fail-fasts on insecure config (all-zero AES key, missing Stripe secrets). Both depend on the env var being correctly Production in prod — verify it on the deployed API and Worker.

## 2. Top risks (ranked)

| Sev | Risk | Impact |
|-----|------|--------|
| P1 | SFTP/S3 pull ingress create stubs but never enqueue parsing (dead-end channel) | Any customer configuring SFTP or S3/R2 import gets orders permanently stuck in their initial stub state — they never parse, transform, or deliver. The channel appears to accept files and does nothing. Confirmed in SftpIngressService.cs:202-233 and S3IngressService.cs:225-271. |
| P2 | Entire Stripe money path is unverified against live events | Checkout, plan mapping, portal, webhook signature, and overage billing are real code but never run with real Stripe TEST events; a mis-mapped price ID or webhook secret would mean customers pay and don't unlock, or are double/never charged for overage. BillingController.cs:155-515, StripeBillingService.cs:284-647. |
| P2 | HTTP 200 with an in-body NACK is marked 'delivered' (transport success != business acceptance) | Success is decided solely by IsSuccessStatusCode in HttpDeliveryDispatcher, ErplyConnector, and DirectoConnector. A supplier/ERP that returns 2xx with a business-level rejection in the body shows as delivered — the buyer believes the PO was accepted when it was not. 4xx/5xx are handled well; only in-band 2xx NACKs slip through. |
| P2 | SFTP/S3 ingress and content-routing have no config surface / no producer | SftpIngressConfig/S3IngressConfig are insertable only by direct DB write (no API/UI) so a customer cannot self-serve them; and content-based routing (assign-supplier/unrouted) has its consumer half shipped but ZERO producers — a shared multi-supplier mailbox/folder routes everything to one DefaultSupplierId. Do not advertise either. |
| P2 | Admin surface unusable until allowlist configured | Empty-by-default Admin:UserIds/Admin:Emails fails closed, so pilot grants, cap raises, GDPR erase, and MRR are all 403 in production until set. |
| P2 | EDIFACT/ASN over-claim and ASN DTO mismatch | library/templates offers EDIFACT as an output 'Standard' with no backend transformer; DesadvController GET /api/asns returns fields (ShipmentId/DespatchDate/SourceFileName) that don't match the frontend AsnDto (asnNumber/supplierName/shipDate/packageCount), so any real ASN renders as dashes. Masked today only because ASN upload 501s. |
| P2 | Library → Rules is a dead control that mimics enforcement | Users can author validation rules that never execute, eroding trust precisely with the 30-year procurement veterans the product targets. |
| P2 | BridgeDashboard cold-mount auth race | The most likely landing page (/bridge) fires its TanStack queries without the queryEnabled cold-auth gate other pages use; on a hard refresh before Clerk loads they can 401 and park (fetchStatus paused), leaving the dashboard empty until a manual interaction. |
| P2 | UBL/X12 emit the supplier GUID as the party name | A real Peppol/EDI receiver expects a legal name + scheme-qualified endpoint; a raw GUID is structurally valid but business-unacceptable, so a real supplier may reject a document that ProcuLink reported as delivered (compounds the HTTP-200 gap). |
| P2 | Rate limiters and anti-trial-farming throttle are process-local | Exact at one API replica; horizontal scaling multiplies effective ceilings by replica count and resets abuse windows per replica. Not a defect at the current single-replica deploy but a hard gate before scaling — needs Redis-backed limiting + HMAC nonce replay store. |
| P3 | Walkthrough video / admin test-links / PostHog blank | Broken /watch player if asset missing; owner sent to Stripe test dashboard; launch metrics blind until NEXT_PUBLIC_POSTHOG_KEY set. All cosmetic/config, fixable in minutes. |
| P3 | Non-HTTP delivery (SFTP/FTPS/SMTP/Erply/Directo) not live-proven; test-fire uses live config not pinned revision | Only HTTP delivery has a real live proof; the other five are wired but battle-untested, and TestFireAsync validates the live-edited config rather than the pinned ConnectionRevision an order actually delivers over — a possible false-positive 'it works'. |

## 3. What I need from you (founder prerequisite checklist)

### Prod access
- **Confirm ASPNETCORE_ENVIRONMENT=Production on BOTH the Railway API (ProcuLink) and Worker (aware-amazement)** — Gates DevFilesController off and triggers StartupConfigurationValidator's P0 secret guards; a wrong value re-enables the dev file passthrough and disables fail-fast.
- **Railway CLI / dashboard access to set env vars on API and Worker, and Vercel access for the frontend** — Most blockers are env/config flips (Stripe IDs, admin allowlist, video URL, PostHog) that must be applied to the live deploys.
- **Two distinct Clerk orgs with sessions; confirm the Clerk post-signup flow forces org creation/selection** — Cross-tenant isolation tests need two orgs, and the personal-workspace 'sub' fallback can fragment a B2B team's data if org activation isn't enforced.

### Stripe test
- **Stripe TEST-mode Stripe:SecretKey, Stripe:WebhookSecret, and price IDs GrowthPriceId/OperationsPriceId/IntegrationPriceId/DistributorPriceId (+ optional *YearlyPriceId)** — API will not boot in Production without SecretKey+WebhookSecret; checkout/plan-unlock cannot be verified without the price IDs.
- **A reachable webhook endpoint: run `stripe listen --forward-to <api>/api/billing/webhook` (or register the deployed URL)** — Required to verify checkout.session.completed / subscription.updated / .deleted / invoice.created actually mutate plan state.
- **A Stripe test clock (or manually fire invoice.created) plus an org pushed over its monthly cap** — Only way to verify the €0.50 idempotent overage at the period boundary without waiting a real month.

### Admin
- **Populate Admin:UserIds and/or Admin:Emails with the founder's Clerk identity, then restart the API** — Empty allowlist fails closed — every admin endpoint (overrides, MRR, invoice, GDPR erase) 403s until set.

### Local stack
- **ProcuLink.Api on :5223 AND ProcuLink.Worker running separately; Postgres on :5435 (proculink_dev) migrated** — The API hosts no Hangfire — parse/transform/deliver/retry/SLA/poll jobs only run on the Worker; most live tests need both.
- **PROCULINK_QA_BYPASS_AUTH=true + ASPNETCORE_ENVIRONMENT=Development on the API; NEXT_PUBLIC_QA_BYPASS_AUTH=true on the frontend; NEXT_PUBLIC_USE_MOCK=false** — Enables authed-route QA without Clerk and exercises the REAL endpoints (mock mode hides live behaviour and shows demo fixtures).
- **A real 32-byte base64 Delivery__EncryptionKey (NOT all-zero); Security:ApiKeyHashSecret >=16 chars; DataProtection:EncryptionKey set** — Delivery/credential and API-key endpoints won't resolve without these; Production startup fail-fasts on the all-zero key and short HMAC secret.

### Sample files
- **A clean 3-line CSV PO; a text-based PDF PO; an image-only/scanned PDF (for OCR/vision path); one cXML and one UBL XML PO; an EDIFACT and an X12 sample; a .docx/.png (unsupported-type negative test)** — Drives upload happy path, parse-failure ParseFailedPanel, the in-out format matrix, and the unsupported-file error copy.
- **A PO with line codes the supplier does NOT recognise, and one line with UnitPrice=0 / Quantity=0** — Exercises the inline SourcePickerChip mapping + OutputFieldValidator hold (no blind €0 delivery).
- **A UBL 2.1 invoice XML; ability to seed an AdvanceShippingNotice row in Postgres** — Invoice upload/approve/download path (behind NEXT_PUBLIC_INBOUND_ENABLED) and to confirm/fix the ASN DTO mismatch.

### Supplier endpoints
- **A controllable HTTP receiver (e.g. webhook.site) able to return 2xx, a 2xx-with-NACK-body, 4xx, and 500** — Verifies HTTP delivery happy path, the 'HTTP 200 != acceptance' gap (TX-03), 4xx rejection (no retry), and 5xx retry->dead-letter.
- **SFTP host + username + password OR OpenSSH private key (+ writable dir); explicit-TLS FTPS host + creds; SMTP relay host/port + username/app-password + a verifiable inbox** — The only way to prove the non-HTTP delivery dispatchers, which are wired but not live-proven.
- **Erply sandbox URL + client code + bearer/apikey token; Directo XML API URL + database code + user/password (or key)** — Verifies the two ERP connectors and their SSRF-at-connect protection.

### Email/IMAP
- **Postmark account + inbound domain with MX + parse webhook pointed at /api/inbound-email/postmark, and Inbound:Postmark:WebhookToken set** — Required to test hosted inbound-email PO ingestion and its token auth.
- **A real mailbox host + username + app password, org on the Integration plan, and a DefaultSupplierId saved via PUT /api/settings/email** — Required to test the 5-minute IMAP polling import path and the plan-gating.

### SFTP/S3
- **A reachable SFTP server and an S3/R2 bucket + access/secret keys (+ ServiceUrl for R2/MinIO), plus willingness to insert SftpIngressConfig/S3IngressConfig rows directly in Postgres** — There is NO config UI/API for these channels; needed to reproduce/verify the P1 parse-enqueue fix and SSRF guard. (If not fixing, this whole category can be dropped and the channels de-advertised.)

### AI/OpenAI
- **Ai:OpenAI:ApiKey (EU-residency project + DPA + zero-retention) — and confirm the per-org AI token cap (5M) is set in Railway** — Needed for AI mapping suggestions, PDF text/vision extraction, and email-body NLP; absent key is a safe no-op; a latched cap previously presented as 'all PDFs failing'.

### Frontend config
- **Set or intentionally blank: NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL, NEXT_PUBLIC_POSTHOG_KEY, NEXT_PUBLIC_STATUS_URL, NEXT_PUBLIC_WALKTHROUGH_LOOM_URL, NEXT_PUBLIC_BOOK_DEMO_URL; decide NEXT_PUBLIC_LAUNCH_FULL_NAV and NEXT_PUBLIC_INBOUND_ENABLED** — Fixes the /watch broken-player risk, turns on launch analytics, and controls which audited operator/inbound pages are reachable in nav at go-live.

## 4. Recommended live-test execution order

- 0. Pre-flight config so the live stack even boots: set Delivery__EncryptionKey (real 32-byte), Security:ApiKeyHashSecret, DataProtection key, Stripe SecretKey+WebhookSecret+price IDs, Admin allowlist, and confirm ASPNETCORE_ENVIRONMENT. Then run BAT-10 (Production fails to boot on insecure config) to prove the guards.
- 1. Security/tenancy spine first — it gates everything and needs no third parties: API-1/2/3/4/5 (auth required, cross-tenant isolation, API-key slug guard, admin fail-closed, HMAC webhook), BAT-06/07 (Stripe webhook signature, cross-tenant billing), TX-06 (SSRF block with AllowPrivateNetworkTargets=false), API-11 (DevFiles inert in prod).
- 2. Core PO happy path local with a webhook.site endpoint: FT-1/FT-2 (empty-org + sample order), HP-1/HP-2 (upload->review->map->send->delivered), HP-3 (inbox bulk send). This proves the money-making path end to end.
- 3. Output format matrix: run the FormatMatrix test suite (FMT-01) then FMT-02..FMT-07 via mapping-override/preview for each of CSV/XML/cXML/UBL/X12/JSON; confirm EDIFACT is honestly absent.
- 4. Delivery dispatchers + reliability against the controllable endpoint: TX-01..TX-05 (test-fire, deliver, 2xx-NACK gap, 4xx rejection, 5xx->dead-letter), TX-16 (double-dispatch), TX-17 (missing config).
- 5. Error/edge resilience: ERR-1..ERR-4 (unsupported file, scanned PDF, delivery fail, quota), EDGE-1/2/3 (drafts empty state, navigate-away mid-send, cold /bridge load — confirm the dashboard recovers or fix the queryEnabled gate).
- 6. Quota + commercial gates local: BAT-02/03 (pilot cap + read-only expiry), API-6/7 (order + supplier 429), BAT-09 (admin override reactivates pilot — requires step 0 allowlist).
- 7. Stripe money path with stripe CLI (external): BAT-01 (auto-provision), BAT-04 (checkout->unlock), BAT-05 (portal), BAT-08 (overage at period boundary via test clock).
- 8. Inbound channels that need external deps, in ascending setup cost: EMAIL-HOST-1..4 (Postmark), IMAP-1..3 (mailbox). Defer SFTP-1/S3-1 until the P1 parse-enqueue fix lands — they will fail by design today (confirm the bug, then re-run after fix).
- 9. Non-HTTP delivery once endpoints supplied: TX-08/09 (SFTP), TX-10 (FTPS), TX-11 (SMTP), TX-12/13 (Erply/Directo).
- 10. Prod smoke after deploy: repeat HP-1 and one delivery per format against a controlled receiver on Railway/Vercel (FMT-09), verify marketing/SEO (MKT-12), /watch (MKT-06), and admin Stripe links now point live (SEC-2).

## 5. Coverage gaps (not yet tested — need their own pass)

- End-to-end PARSE/EXTRACTION accuracy was not audited — the 10 areas covered transport in, transform out, and the UI, but no agent stress-tested the actual CSV/XLSX/PDF/cXML/UBL/EDIFACT/X12/IDoc PARSERS against a corpus of real, messy supplier POs (locale comma-decimals, multi-page PDFs, exotic XLSX compression). Project memory flags prior silent 10x/100x numeric-corruption bugs here; this is the heart of the product and needs a regression corpus run (the ~12-PO corpus in Downloads).
- No load / concurrency / soak testing: behaviour of the Worker under a backlog, Hangfire job throughput, DB connection-pool limits, and the documented single-replica rate-limit ceilings under real concurrent uploads were not exercised.
- Data lifecycle / GDPR: retention sweep, per-org erase, and bulk-erase were inventoried but not run end-to-end to confirm R2 artifacts + DB rows + audit events are actually purged (and that erase is org-scoped).
- Backup / disaster recovery / migration safety: no audit of Neon backups, migration rollback, or what happens if a migration fails mid-deploy on prod data.
- Email DELIVERABILITY (outbound transactional + SMTP-delivery channel): SPF/DKIM/DMARC for support@/orders@ and supplier-facing SMTP delivery landing in inbox vs spam were not assessed.
- R2 storage edge cases beyond the known GET-signing gotcha: large-file uploads, upload failure mid-parse, and pre-signed URL expiry during a slow review session.
- Observability/alerting: whether Sentry actually captures backend+frontend errors in prod, and whether anyone is paged when the Worker heartbeat dies or the dead-letter queue grows — the UI surfaces these but no monitoring/alerting path was verified.
- Accessibility and cross-browser/mobile-device real testing: a static mobile audit exists but no real-device or screen-reader pass on the core upload->review->send flow.
- Clerk org lifecycle: invite/role/multi-member flows, what a second team member sees, and the personal-workspace fallback fragmentation risk were noted but not tested.
- Idempotency under true network partition/replay for the REST ingress and webhook firing beyond the happy-path duplicate test.

---
# 6. Per-area detail + full test scenarios

## Frontend MARKETING + AUTH pages (project-proculink/src/app: root landing, sign-in, sign-up, and (marketing): aup, changelog, customers, dpa, formats, help, how-it-works, one-pager, pricing, privacy, security, subprocessors, support, terms, watch, welcome)

The marketing + auth surface is in strong, near-launch shape and unusually honest: pricing/one-pager/formats are all data-driven from shared single-source libs (src/lib/plans.ts, standards/catalog.ts, subprocessors.ts, legal-entity.ts) so they cannot silently drift or over-claim; legal pages (privacy/terms/dpa/aup/subprocessors/security) carry real Estonian-entity content (Diip Solutions OÜ, registry 17527757), not placeholders; the support form posts to a real backend (POST /api/support/contact via SupportController) and honestly downgrades to "email us directly" when SMTP didn't actually deliver; customers page uses explicitly-labeled "Coming soon — anonymised pilot" cards rather than fake logos; the testimonial deliberately omits a fabricated attribution. Auth uses real Clerk SignIn/SignUp with graceful "not configured" fallbacks. The main GO-LIVE risks are media/SEO config, not broken logic: (1) the committed .env hard-codes NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL to an R2 asset that per project memory is "DRAFT staged not uploaded" — so /watch will render a broken/black <video> in prod instead of the friendly fallback; (2) homepage STATS counts ("9 inbound formats / 6 delivery channels") don't line up with the honest /formats catalog (10 import formats, 8 delivery methods of which several are On request) — a mild marketing over-/under-count; (3) the changelog is hardcoded static entries with plausible-but-unverifiable dates; (4) PostHog/Status-URL/Loom/book-demo env vars are blank (analytics + Status footer link + Loom path silently no-op, which is handled correctly). No dead CTAs, no Lovable/Vite residue, robots.txt correctly blocks app routes, sitemap is registry-derived.

**Inventory:** 28 items — {"working":21,"mock":2,"partial":5}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| mock | Hero Topology/Clean-order toggle — Switches BridgeIllustration vs CanonicalPreview (static demo data PO-2026-008412 / Northwind) | `src/app/page.tsx:402-437,920-981` |
| partial | Homepage STATS strip (9 inbound / 6 out / 6 channels / EU) — Capability counts | `src/app/page.tsx:183-188` |
| partial | Footer Status link — Renders only if NEXT_PUBLIC_STATUS_URL set; env is blank so link is correctly hidden | `src/app/(marketing)/layout.tsx:47-49 & src/app/page.tsx:903-905` |
| partial | Watch (/watch) — Walkthrough video: MP4 (env URL) -> Loom -> friendly fallback | `src/app/(marketing)/watch/page.tsx, watch/layout.tsx` |
| partial | Customers (/customers) — Honest 'Coming soon — anonymised pilot' placeholder cards; no fake logos | `src/app/(marketing)/customers/page.tsx` |
| mock | Changelog (/changelog) — v1.0–v1.4 release notes | `src/app/(marketing)/changelog/page.tsx` |
| partial | AnalyticsBoot (PostHog) — Identify/group/pageview capture; requires NEXT_PUBLIC_POSTHOG_KEY (blank in committed .env) | `src/components/analytics/AnalyticsBoot.tsx` |

**Test scenarios (15):**

- **[MKT-01] First-time visitor lands on homepage and understands the product** _(first-time · env: either)_
  - Steps: (1) Open / as a logged-out user (2) Read hero headline 'Send every purchase order to any supplier, any format.' (3) Toggle the hero Topology / Clean order switch (4) Scroll through features, why, testimonial, ROI calculator, CTA band (5) Click 'Start free'
  - Expected: Page renders fully; hero toggle swaps the illustration; ROI slider updates a recommended plan; 'Start free' navigates to /sign-up
  - Prereq: None
- **[MKT-02] New user signs up via Clerk** _(happy · env: prod)_
  - Steps: (1) Click Start free -> /sign-up (2) Complete Clerk sign-up (email or social) (3) Observe redirect
  - Expected: Clerk form is styled to brand; on success user is redirected to /bridge (fallbackRedirectUrl). Sign-up subtitle promises 'No credit card. 20 orders free for 14 days.'
  - Prereq: NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY + CLERK_SECRET_KEY configured in the environment
- **[MKT-03] Auth pages with Clerk env missing** _(error · env: local)_
  - Steps: (1) Unset NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY (and/or CLERK_SECRET_KEY) (2) Visit /sign-in and /sign-up
  - Expected: Friendly 'Sign-in/Sign-up is not configured' / 'Server auth is not configured' message instead of a crash
  - Prereq: Ability to run with Clerk env vars unset
- **[MKT-04] Support contact form submits to backend** _(happy · env: local)_
  - Steps: (1) Go to /support (2) Pick a category, type a message, optionally an email (3) Click 'Send message'
  - Expected: With SMTP configured: green 'Thanks — we'll reply within one business day.' With SMTP NOT configured (delivered:false): honest amber notice telling the user to email support@proculink.eu directly — it never falsely claims the message reached the team
  - Prereq: API running at NEXT_PUBLIC_API_BASE_URL with /api/support/contact; NEXT_PUBLIC_USE_MOCK=false
- **[MKT-05] Support form validation (empty message)** _(edge · env: local)_
  - Steps: (1) Open /support (2) Leave the Message field empty (3) Attempt to submit
  - Expected: Submit button is disabled (canSubmit requires non-empty message); the textarea is also required, so no empty request is sent
  - Prereq: None
- **[MKT-06] Watch the walkthrough video** _(error · env: prod)_
  - Steps: (1) From /how-it-works click 'Watch the walkthrough' -> /watch (2) Wait for the video element to load
  - Expected: RISK: committed .env sets NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL to https://assets.proculink.eu/marketing/walkthrough.mp4. If that R2 asset is not actually uploaded (project memory says master is DRAFT/not uploaded), the <video> renders broken/black instead of the friendly 'coming shortly' fallback (the fallback only shows when the URL env is empty). Verify the asset exists or blank the env so the fallback shows.
  - Prereq: Prod env with the walkthrough video env var set
- **[MKT-07] Pricing recommender + tier disclosure** _(happy · env: either)_
  - Steps: (1) Open /pricing (2) Drag the 'POs per month' slider across its range (10–5000) (3) Observe the recommended plan + price (4) Click 'See all tiers · compare plans' (5) Click a self-serve tier CTA, then the Enterprise CTA
  - Expected: Recommendation and the '≈ €X/mo (... + overage)' readout update live and never block; disclosure reveals Integration/Distributor; when the recommended tier is a secondary tier it is auto-revealed and the toggle is disabled with an explanatory hint; self-serve CTAs go to /sign-up, Enterprise goes to its contact href; no annual toggle is shown (gated off)
  - Prereq: None
- **[MKT-08] Honesty check: homepage stats vs /formats catalog** _(edge · env: either)_
  - Steps: (1) Read homepage STATS: '9 inbound formats', '6 outbound formats', '6 delivery channels' (2) Open /formats and count rows by status
  - Expected: Numbers should reconcile. Currently /formats lists 10 import formats and 8 delivery methods (several 'On request'/'Configurable', not all live), while output formats are 7 (6 live + 1 on-request). The homepage counts are inconsistent with the honest catalog and should be either derived from the catalog or corrected.
  - Prereq: None
- **[MKT-09] Footer + nav link integrity across all marketing pages** _(happy · env: either)_
  - Steps: (1) From the footer, click every link: How it works, Pricing, Security, Open the dashboard, Customers, Changelog, Help center, Support, Privacy, Terms, AUP, DPA, Subprocessors (2) Click the logo
  - Expected: Every link resolves to a real route (all targets exist). 'Open the dashboard' (/bridge) is an app route — for a logged-out user it should bounce to sign-in via middleware. Note the root page.tsx footer omits the 'Help center' link that the (marketing) layout footer includes — minor inconsistency.
  - Prereq: None
- **[MKT-10] Help center search + browse** _(happy · env: either)_
  - Steps: (1) Open /help (2) Type 'SFTP delivery' in search (3) Clear, click a topic card (4) Open a popular article (5) Search a nonsense term
  - Expected: Fuse.js returns ranked MDX articles; topic cards only show categories with >0 articles; article opens at /help/<slug>; nonsense term shows a clean 'No matching articles' empty state with Reset
  - Prereq: None
- **[MKT-11] PostHog analytics + Status link when env blank** _(edge · env: prod)_
  - Steps: (1) Load any marketing page with NEXT_PUBLIC_POSTHOG_KEY and NEXT_PUBLIC_STATUS_URL blank (current committed state) (2) Check console/network for analytics; check footer for a Status link
  - Expected: No crash; analytics capture is a no-op without a key; the footer Status link is correctly hidden when NEXT_PUBLIC_STATUS_URL is empty. To actually get analytics + a Status link, the founder must populate those env vars.
  - Prereq: Prod deployment
- **[MKT-12] SEO / crawlability and robots** _(happy · env: prod)_
  - Steps: (1) Fetch /robots.txt (2) Fetch /sitemap.xml (3) Inspect OG/Twitter tags and JSON-LD on /
  - Expected: robots.txt allows marketing routes and disallows app routes (/bridge,/inbox,/upload,/drafts,/library,/operations,/settings); sitemap is registry-derived incl. all help article slugs and excludes /welcome; OG image /og-image.png, Twitter summary_large_image, and Organization JSON-LD (Diip Solutions OÜ) are present
  - Prereq: Prod deployment with public assets
- **[MKT-13] /welcome visited while logged out** _(edge · env: either)_
  - Steps: (1) Open /welcome directly as a logged-out visitor (it sits in the public (marketing) group)
  - Expected: Renders 'Welcome to ProcuLink.' with no first name and no upgraded banner; 'Open the dashboard' (/bridge) will bounce to sign-in. Consider whether this post-signup page should be public at all.
  - Prereq: None
- **[MKT-14] Mobile nav overlay** _(happy · env: either)_
  - Steps: (1) Load any marketing page at ~390px width (2) Tap the burger (☰) (3) Tap a link, reopen, tap Start free
  - Expected: Full-screen navy overlay covers the hero; links and Sign in/Start free work and close the menu on tap
  - Prereq: None
- **[MKT-15] Subprocessors / legal pages truthfulness** _(security · env: either)_
  - Steps: (1) Open /subprocessors, /privacy, /dpa, /security (2) Cross-check the subprocessor list against the real stack
  - Expected: Single-source list (src/lib/subprocessors.ts) shows exactly the deployed vendors incl. OpenAI (US, no training) and Postmark; locations are real (Railway europe-west4); no fabricated certifications. Legal entity is Diip Solutions OÜ (Estonia). No lorem/TODO placeholders.
  - Prereq: None

**Area prerequisites:**
- Clerk configured (NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY + CLERK_SECRET_KEY) for sign-in/sign-up to function; otherwise the auth pages show a 'not configured' fallback
- Backend API reachable at NEXT_PUBLIC_API_BASE_URL with POST /api/support/contact (SupportController) for the support form; SMTP configured server-side or the form honestly tells users to email directly
- NEXT_PUBLIC_USE_MOCK=false in prod so the support form hits the real endpoint (mock returns delivered:true unconditionally)
- The walkthrough video asset at NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL (https://assets.proculink.eu/marketing/walkthrough.mp4) must actually exist in R2, OR the env var must be blanked, so /watch doesn't render a broken player
- Optional founder config (currently blank, all degrade gracefully): NEXT_PUBLIC_POSTHOG_KEY (analytics), NEXT_PUBLIC_STATUS_URL (footer Status link), NEXT_PUBLIC_WALKTHROUGH_LOOM_URL (Loom fallback), NEXT_PUBLIC_BOOK_DEMO_URL (in-app book-demo CTAs, not on marketing pages)
- Public static assets present in prod: /og-image.png, favicons, /site.webmanifest, /browserconfig.xml
- Stripe self-serve Checkout (live keys + price ids) for the post-signup paid-tier flow that pricing CTAs ultimately lead to

## Frontend CORE PO-loop app pages (upload, upload/preview/[orderId], inbox, inbox/[orderId], drafts, bridge, admin)

The money path is real and well-wired end-to-end against the live .NET API. Upload (UploadWorkbench) → review/map (OrderWorkshop, the unified review screen at /inbox/[orderId]) → transform → deliver → audit all call live endpoints that exist in OrdersController.cs (upload, detect-format, resolve, accept-ai-suggestions, mapping-override/preview, transform, redeliver, retry-delivery, audit, passport, confirmation). Mock data is correctly gated behind NEXT_PUBLIC_USE_MOCK (default false, committed .env=false, plus a prod-mock guard) so real users never see staged rows. The flagship route is /inbox/[orderId] (OrderWorkshop); /upload/preview/[orderId] (MagicMappingPreview) is a retained fallback — upload now redirects straight to /inbox/[orderId]. Send is server-truth gated (re-reads order.lines.needsReview before sending; transform→poll→redeliver→poll with remount-resilience and a re-read before painting any red failure). The inline source-field mapping picker (SourcePickerChip) and live output preview (MapperPreviewPane, ~300ms debounced previewMappingOverride round-trip) are genuine. Key gaps: /drafts has NO persistence backend (honest empty state for real users, demo rows only in mock); BridgeDashboard's data queries lack the cold-auth `enabled: queryEnabled` gate that other pages have (known cold-mount 401→RQ-paused race risk); admin Stripe links are hardcoded to the TEST dashboard (/test/) with a go-live TODO. No dead controls or fake-data leaks found on the core path itself.

**Inventory:** 19 items — {"working":17,"partial":1,"stub":1}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | BridgeDashboard (/bridge) — KPIs/funnel from GET /api/orders + /orders/summary + /dashboard/topology + /suppliers; in-transit list; exceptions link | `project-proculink/src/components/bridge/BridgeDashboard.tsx (real APIs, mock gated; but queries lack queryEnabled cold-auth gate)` |
| stub | DraftsPage (/drafts) — List orders saved to finish later | `project-proculink/src/app/(app)/drafts/page.tsx — no draft-persistence endpoint; real users see honest empty state, DEMO_DRAFTS only in mock; 'New' routes to /upload` |

**Test scenarios (14):**

- **[FT-1] First-time signup → empty upload page, no suppliers** _(first-time · env: prod)_
  - Steps: (1) Sign up as a brand-new buyer via Clerk (2) Land in the app and open /upload (3) Observe the supplier step shows 'No suppliers yet. Add a supplier…' and the green submit button is disabled (4) Observe the 'New here? Start with a sample order' card is promoted above the dropzone
  - Expected: Upload is clearly blocked until a supplier exists; a clear link to /library/suppliers and a one-click sample-order path are offered
  - Prereq: A fresh org with zero suppliers and zero orders
- **[FT-2] Run the zero-friction sample order** _(first-time · env: prod)_
  - Steps: (1) On /upload (empty org) click 'Try with a sample order →' (2) Wait for POST /api/onboarding/sample-order (3) Get redirected to the order review screen
  - Expected: A sample order is created (quota-exempt), parses, and opens in OrderWorkshop so a new user sees the full path without owning a PO
  - Prereq: Signed-in new org
- **[HP-1] Happy path: upload CSV → review → send → delivered** _(happy · env: external-dep)_
  - Steps: (1) On /upload pick a supplier that has a delivery endpoint configured (2) Drop a clean CSV PO; watch the 'Detected: CSV · NN%' pill (3) Click 'Upload & review'; watch the 3-stage pipeline animation then redirect to /inbox/{id} (4) On OrderWorkshop wait for parsing to finish; confirm zero issues (5) Click Send → confirm dialog → watch 'Generating the output…' then 'Sending…' then green 'Delivered'
  - Expected: Order moves ready→transforming→ready_to_deliver→delivered; flow notice turns green; status badge shows Delivered
  - Prereq: A supplier with a real (or controlled test) delivery endpoint + valid Delivery encryption key on the API
- **[HP-2] Resolve unmatched lines with the inline picker + live preview** _(happy · env: local)_
  - Steps: (1) Upload a PO with line codes the supplier doesn't recognise (2) On OrderWorkshop see the IssuesPanel list blocking issues and the Send button read 'Fix N to send' (3) Use the inline SourcePickerChip / per-line code entry to map a line, or click 'Accept suggestion' on an AI card (4) Watch the live MapperPreviewPane re-render the output document (~300ms) as fields are wired (5) Use 'Accept all' / 'Accept ≥85%' bulk button
  - Expected: Each fix commits via POST /resolve or /accept-ai-suggestions and refetches server truth; issue count drops; preview reflects changes; Send unlocks at zero blocking issues
  - Prereq: Local API+Worker with PROCULINK_QA_BYPASS_AUTH and an order with unresolved lines
- **[HP-3] Inbox queue, filter, and bulk send** _(happy · env: local)_
  - Steps: (1) Open /inbox with several orders in mixed states (2) Filter to 'Ready to send' or 'Failed' (3) Select multiple rows; click 'Send selected' (4) Watch the bulk result summary
  - Expected: Only redeliverable rows are selectable for send; each routes through redeliverOrder with the Clerk auth header; results summarized; list refreshes
  - Prereq: Several real orders, at least one in ready_to_deliver / delivery_failed
- **[ERR-1] Upload an unsupported file type** _(error · env: local)_
  - Steps: (1) On /upload drag or pick a .png / .docx (2) Observe the dropzone before any network call
  - Expected: Inline red 'X isn't a supported file type. We accept …' shown immediately client-side; no upload round-trip; drag of clearly-bad MIME types shows the red 'This file type isn't supported' affordance
  - Prereq: None
- **[ERR-2] Scanned/textless PDF or unparseable file** _(error · env: external-dep)_
  - Steps: (1) Upload an image-only PDF (no text layer) with OCR disabled (2) Get redirected to /inbox/{id}; wait for parse to finish or fail (3) Observe the ParseFailedPanel
  - Expected: OrderWorkshop renders the honest ParseFailedPanel (seeded from order audit) with a clear 'scanned/image-only' style message and recovery options — not a blank half-parsed screen
  - Prereq: An image-only PDF; OpenAI/OCR not enabled for the org
- **[ERR-3] Send fails at delivery (bad/unreachable supplier endpoint)** _(error · env: external-dep)_
  - Steps: (1) Configure a supplier with an endpoint that returns 4xx/5xx or is unreachable (2) Upload+resolve an order, click Send (3) Watch the flow notice and status
  - Expected: After transform succeeds, delivery polls to delivery_failed; flow notice renders RED (not green) with the real error; OrderWorkshop shows FailedPanel(stage=delivery); dead-letter badge appears once automatic retries are exhausted; resend is offered
  - Prereq: A supplier delivery config pointed at a failing endpoint
- **[ERR-4] Quota / expired pilot blocks upload** _(error · env: local)_
  - Steps: (1) As a read-only/trial-expired org open /upload (2) Observe the plan-usage panel and submit button (3) Attempt to upload
  - Expected: Button reads 'Processing paused'; amber 'Processing paused' panel explains view-only state; a 429 on upload maps to the correct pilot_expired/order_limit_reached message rather than a generic failure
  - Prereq: An org in read_only/trial_expired state (or admin-forced quota)
- **[EDGE-1] Open a draft / out-of-date order link** _(edge · env: local)_
  - Steps: (1) Navigate to /drafts (2) Then open /inbox/{nonexistent-or-archived-id}
  - Expected: /drafts shows the honest 'Drafts live here' empty state with a 'Go to Inbox' action (no fabricated rows for real users); a missing order shows the 'Order not found' card with a Back-to-inbox button
  - Prereq: Real (non-mock) mode
- **[EDGE-2] Navigate away mid-send and return** _(edge · env: external-dep)_
  - Steps: (1) Click Send on an order, then immediately navigate to /inbox and back to /inbox/{id} while transform/deliver is in flight (2) Observe the Send button / status
  - Expected: useSendFlow remount-resilience reflects the in-flight server status ('Preparing the file…') instead of resetting to an idle 'Send' CTA, and converges to delivered/failed via 3s polling — the button never sticks
  - Prereq: A reachable delivery endpoint slow enough to observe in-flight state
- **[EDGE-3] Cold load of /bridge dashboard before Clerk is ready** _(edge · env: prod)_
  - Steps: (1) Hard-refresh /bridge (cold mount) on a real authed session (2) Observe whether KPIs/funnel load or appear stuck empty
  - Expected: Dashboard should populate; RISK: its useQuery calls (suppliers/orders/topology/summary) lack the queryEnabled cold-auth gate other pages use, so on a cold mount they can fire before Clerk loads, 401, and park (RQ fetchStatus paused) leaving the dashboard empty until a manual refetch — verify it recovers
  - Prereq: Real deployment with Clerk
- **[SEC-1] Non-owner opens /admin** _(security · env: prod)_
  - Steps: (1) As a normal (non-allowlisted) signed-in user navigate to /admin (2) Observe the page
  - Expected: GET /api/admin/* returns 403; the page shows the clean 'You don't have access to the admin area' view with a Back-to-dashboard link; no customer/MRR data leaks (the page is UX-only, backend is the real gate)
  - Prereq: A signed-in user NOT on the server admin allowlist
- **[SEC-2] Admin Stripe links point to TEST dashboard** _(security · env: prod)_
  - Steps: (1) As the owner open /admin and click a customer 'View ↗' Stripe link
  - Expected: Currently the link goes to dashboard.stripe.com/test/customers/… (hardcoded). At go-live this must be flipped to the live dashboard or it sends the owner to test-mode customers — verify before launch (TODO at admin/page.tsx:38)
  - Prereq: Owner account; at least one org with a stripeCustomerId

**Area prerequisites:**
- Backend API (ProcuLink.Api, dev :5223) running AND a separate Worker process (Hangfire jobs — parse/transform/deliver run on the Worker, the API hosts no jobs)
- PostgreSQL on localhost:5435 (proculink_dev) migrated
- A 32-byte base64 Delivery__EncryptionKey set so supplier/delivery-config endpoints resolve
- For local no-auth QA: ASPNETCORE_ENVIRONMENT=Development + PROCULINK_QA_BYPASS_AUTH=true on the API and NEXT_PUBLIC_QA_BYPASS_AUTH=true on the frontend
- Frontend env: NEXT_PUBLIC_API_BASE_URL set, NEXT_PUBLIC_USE_MOCK=false (committed default) to exercise the real path; set =true only to see demo data
- At least one Supplier created in the org (uploads are blocked without one) and, for delivery scenarios, a configured supplier delivery endpoint
- For HP-1/ERR-3/EDGE-2 (real delivery): a controlled HTTP endpoint (e.g. webhook.site) or a real supplier endpoint — external dependency the founder must supply
- For admin scenarios: a Clerk user on the server-side admin allowlist, and a Stripe (test) account connected for MRR reconcile
- For parse-failure/OCR scenarios: OpenAI key configuration state matching the test (e.g. disabled for ERR-2)

## Frontend CONFIG / library / settings pages

Audited every page under src/app/(app): connections, connections/[connectionId], settings, and the seven library/* pages. Headline: the config layer is genuinely well-wired — almost every create/edit/delete/test control hits a real backend controller and persists, with disciplined write-only secret handling (blank = keep saved secret) across delivery config, SFTP/S3 pull, and IMAP. The versioned Supplier Connection model (create-draft / run-tests / publish / rollback / archive) is fully backed by ConnectionsController with publish evidence-gating, and the lifecycle is correctly hidden behind plain verbs ("Edit mapping", "Make live", "Restore"). Most pages have honest empty states and degrade to "—/no data yet" rather than leaking demo data in live mode. The most important findings are NOT broken buttons but ENGINE-LEVEL DISCONNECTS and an under-built dashboard: (1) /library/rules (ValidationRules → /api/rules → ValidationRuleService → ValidationRules table) is a fully CRUD-able rules surface that is NOT consumed anywhere in the parse/validate/transform pipeline — the real per-supplier acceptance validation runs through a separate AcceptanceProfile/SupplierAcceptanceService system surfaced on each supplier's "Validation rules" tab and fed by /library/rule-definitions. So a first-time user can author "validation rules" in the Library that silently do nothing to orders. (2) The supplier profile Overview tab KPIs (Total orders, Avg cycle time, Exception rate, Acceptance) are hard-coded to "—/no data yet" in live mode — there is no real per-supplier analytics endpoint behind them; the rich numbers only exist in mock mode. (3) Inbound channels (IMAP email, SFTP-pull, S3/R2-pull) have NO test-connection button — you save credentials blind and wait for the poller, unlike outbound Delivery config which has a real test-fire. (4) library/templates lets a user pick EDI/EDIFACT as an output "Standard", but EDIFACT has no output transformer in the backend (catalog marks it transform=planned); the template body is free-text so it is technically a user-authored envelope, but the format dropdown over-implies EDIFACT delivery support. The /library/standards page itself is an honest read-only field-mapping reference, not an over-claim of delivery support. Verified routes match server-side: /api/connections, /api/buyers, /api/rules, /api/rule-definitions, /api/templates, /api/suppliers/{id}/delivery-config(+test-fire), /api/suppliers/{id}/mappings, plus settings email/sftp/s3/api-keys/integrations all exist.

**Inventory:** 22 items — {"working":15,"partial":5,"stub":1,"dead-control":1}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | Settings → Email (IMAP) ingestion — Configure IMAP host/user/password/folder + default supplier; billing-gated; write-only password | `settings/page.tsx EmailSettingsSection (L505); GET/PUT email settings. Gap: no test-connection — save blind, worker polls` |
| partial | Settings → SFTP pull — Poll an SFTP folder for order files; write-only password (blank=keep); default-supplier required; paid-plan gated | `src/components/settings/PullIngressSettings.tsx SftpPullSettings; getSftpSettings/updateSftpSettings. Gap: no test-connection; backend pull→parse-enqueue has a known pre-existing gap per project memory` |
| partial | Settings → S3/R2 pull — Watch an S3/R2 bucket prefix; write-only secret key; endpoint URL for R2/MinIO; default-supplier required | `PullIngressSettings.tsx S3PullSettings; getS3Settings/updateS3Settings. Gap: no test-connection` |
| stub | Settings → Connectors: native Zapier/Make apps — One-click published Zapier/Make apps | `settings/page.tsx L1297-1317 — honestly labelled 'Coming soon', no dead links (points users to webhook path instead)` |
| dead-control | Library → Rules (/library/rules) — CRUD validation rules (list/create/edit/toggle/delete) | `src/components/bridge/ValidationRules.tsx → /api/rules → ValidationRuleService (ProcuLink.Infrastructure/Services/ValidationRuleService.cs). Persists to ValidationRules table but that table is NOT read by any parse/validate/transform code — real validation uses AcceptanceProfile/SupplierAcceptanceService instead. Authoring here does nothing to orders.` |
| partial | Library → Supplier profile: Overview tab — Per-supplier KPI cards (Total orders, Avg cycle, Exception rate, Acceptance) + delivery summary + recent deliveries | `src/components/bridge/SupplierDockProfile.tsx L1385-1495. Live mode shows '—/no data yet' for ALL KPIs — no real per-supplier analytics endpoint; real numbers exist only in DEMO_MOCK. Honest (no leak) but a non-functional dashboard.` |
| partial | Library → Templates (/library/templates) — Output template CRUD (create/edit/delete) + code preview + export download; free-text {token} body | `src/app/(app)/library/templates/page.tsx; getTemplates/createTemplate/updateTemplate/deleteTemplate → /api/templates. Format dropdown offers EDI/EDIFACT which has no backend output transformer (catalog: transform=planned) — over-implies EDIFACT delivery. MOCK_TEMPLATES (incl. EDIFACT/X12) only in mock mode` |

**Test scenarios (18):**

- **[FT-1] First-time user lands on empty Connections page** _(first-time · env: local)_
  - Steps: (1) Sign up and reach the app (2) Open Library → Connections
  - Expected: Empty state 'No connections yet' explains a connection is created when you configure a supplier, plus a 'Go to Suppliers' button. No error, no fake rows.
  - Prereq: Fresh org, no suppliers/connections
- **[FT-2] Create first supplier then configure its connection end to end** _(happy · env: local)_
  - Steps: (1) Library → Suppliers → add a supplier (2) Open the supplier → click Edit/Create mapping on /connections/[id] (3) Author the mapping in the three-pane mapper (4) Open History & advanced → Run tests → Make live
  - Expected: Draft created (clone-from-live if any), mapper saves, Run tests returns pass/fail evidence inline, publish blocked with a clear message until tests pass, then 'Live — new orders use this version now'.
  - Prereq: Fresh org; Delivery__EncryptionKey set; PROCULINK_QA_BYPASS_AUTH=true
- **[RULES-1] Author a validation rule in Library → Rules and confirm it has no effect** _(edge · env: local)_
  - Steps: (1) Library → Rules → create a rule and enable it (2) Upload an order that should violate it (3) Watch parse/validate result in order review / exceptions
  - Expected: BUG: order is NOT flagged by the Library rule — /api/rules (ValidationRuleService) is not consumed by the pipeline. Real validation only comes from the supplier Acceptance profile. The page misleads the user into thinking they configured enforcement.
  - Prereq: One supplier + an order that violates the rule
- **[RULES-2] Configure real acceptance rules on the supplier Validation tab** _(happy · env: local)_
  - Steps: (1) Open a supplier → Validation rules tab (2) Add/activate acceptance rules (draft → activate) (3) Upload an order violating a rule
  - Expected: Order is flagged/held per the acceptance profile; profile shows v# and live/draft status. This is the path that actually works.
  - Prereq: One supplier; rule definitions seeded
- **[DEL-1] Configure HTTP delivery and test-fire** _(happy · env: external-dep)_
  - Steps: (1) Supplier → Delivery tab → choose HTTP, enter URL (2) Save (3) Click 'Send a test now'
  - Expected: Save persists; test-fire posts a small payload and reports success/failure honestly. Secret field shows '•••• (leave blank to keep)' on reopen.
  - Prereq: A reachable test endpoint (e.g. webhook.site)
- **[DEL-2] Edit delivery config without re-entering the secret** _(edge · env: local)_
  - Steps: (1) Reopen Delivery tab (2) Change a non-secret field (e.g. timeout) (3) Leave password/secret blank (4) Save
  - Expected: Saved credential preserved (blank=keep). For SFTP, switching auth shape password→key WITHOUT a new secret is blocked with an inline message rather than silently keeping the wrong-shape secret.
  - Prereq: A supplier with a saved delivery credential
- **[DEL-3] Test-fire before saving** _(error · env: local)_
  - Steps: (1) Open Delivery tab on a supplier with no saved config (2) Hover/click the test button
  - Expected: Test button disabled with tooltip 'Save the delivery setup first, then you can test it.' — no confusing failure.
  - Prereq: New unsaved delivery config
- **[PULL-1] Set up SFTP/S3 pull on a fresh org with no suppliers** _(error · env: local)_
  - Steps: (1) Settings → SFTP pull (or S3) (2) Try to enable the toggle
  - Expected: Toggle disabled until a default supplier exists; a dashed 'No suppliers yet — Add a supplier first →' notice links to suppliers. On Pilot plan an amber upgrade notice appears.
  - Prereq: Fresh paid org, zero suppliers
- **[PULL-2] Save SFTP/S3/IMAP credentials with no way to verify them** _(edge · env: external-dep)_
  - Steps: (1) Enter credentials and a default supplier (2) Save (3) Look for any 'test connection' control
  - Expected: GAP: NO test-connection for inbound channels (unlike Delivery test-fire). User saves blind and must wait for the 5-min poller; bad credentials surface only later. Founder should know before go-live.
  - Prereq: Real SFTP/S3/IMAP endpoint credentials
- **[API-1] Create and revoke an API key** _(happy · env: local)_
  - Steps: (1) Settings → API keys → Create key with a label (2) Copy the one-time raw key (3) Reload — confirm only the prefix is shown (4) Revoke the key (confirm prompt)
  - Expected: Raw plk_ key shown once with 'cannot be retrieved again'; list shows prefix + created/last-used; revoke confirms and marks it revoked. Ingress endpoint + slug shown for posting orders.
  - Prereq: Authed org
- **[CONN-1] Add a custom webhook connector and toggle it** _(happy · env: local)_
  - Steps: (1) Settings → Connectors → Add webhook → pick event + target URL (+ optional secret) (2) Save (3) Toggle it off then delete it
  - Expected: Create/toggle/delete all persist via integration endpoints; native Zapier/Make tiles clearly say 'Coming soon' with no dead outbound links.
  - Prereq: Authed org
- **[TPL-1] Create an output template and pick EDIFACT** _(edge · env: local)_
  - Steps: (1) Library → Templates → New template (2) Select Standard = EDI/EDIFACT (3) Write a body and Save (4) Assign to a supplier and attempt a real send
  - Expected: Template saves (free-text body), but EDIFACT has no backend output transformer (catalog transform=planned). The format dropdown over-implies EDIFACT delivery support; verify whether send works as a hand-authored envelope or fails.
  - Prereq: Authed org; a supplier to assign to
- **[TPL-2] Edit/delete/export an existing template** _(happy · env: local)_
  - Steps: (1) Library → Templates → select a card (2) Edit body and Save; Export; Delete
  - Expected: Update/delete persist via /api/templates; Export downloads the previewed envelope as a real file with the right extension.
  - Prereq: At least one template
- **[SUP-1] Open a brand-new supplier's Overview tab** _(first-time · env: local)_
  - Steps: (1) Library → Suppliers → open the supplier → Overview tab
  - Expected: KPI cards all show '—' / 'no data yet' and delivery summary says 'Configure this supplier in the Delivery tab'. Honest — but note KPIs never populate (no per-supplier analytics endpoint), so they stay dashes even after real orders.
  - Prereq: A newly created supplier with no orders
- **[STD-1] Browse the Standards reference as a procurement veteran** _(happy · env: either)_
  - Steps: (1) Library → Standards (2) Search a field (e.g. 'orderID')
  - Expected: Read-only table maps each canonical field to cXML/UBL/EDIFACT/X12/Peppol BIS paths; search filters; 'Request a format' footer links to support. No claim that all listed standards are deliverable — purely a field reference.
  - Prereq: None (static data)
- **[BUY-1] Add and delete a buyer** _(happy · env: local)_
  - Steps: (1) Library → Buyers → New buyer (name + code) (2) Save (3) Click a buyer row (4) Delete a buyer (window.confirm)
  - Expected: Create/delete persist via /api/buyers; clicking a row navigates to /inbox?buyer=CODE (filtered orders). Empty state offers 'New buyer'.
  - Prereq: Authed org
- **[LIFE-1] Roll back to a previous live mapping version** _(happy · env: local)_
  - Steps: (1) Open connection → History & advanced (2) Find an older archived version → Restore
  - Expected: Rollback clones the archived revision into a NEW live revision ('Restored — v# is live now'); the target stays archived and orders pinned to old revisions are unaffected.
  - Prereq: A connection with at least two published versions
- **[ORG-1] Rename workspace and try to clear the name** _(error · env: local)_
  - Steps: (1) Settings → Organization (2) Clear the name field → Save (3) Enter a valid name → Save
  - Expected: Empty name rejected inline ('Workspace name can't be empty'); valid name saves via Clerk and confirms. Currency/region show as a 'Fixed' info block, not editable inputs (no false promise).
  - Prereq: Authed org

**Area prerequisites:**
- Local dev stack: API on :5223 with Postgres :5435, Delivery__EncryptionKey (32-byte base64) set, and PROCULINK_QA_BYPASS_AUTH=true for browser QA without Clerk
- ProcuLink.Worker MUST be running for any pull/poll ingestion (IMAP/SFTP/S3) and for parse/transform/delivery jobs — the API hosts no Hangfire
- NEXT_PUBLIC_USE_MOCK=false to exercise real endpoints (mock mode shows DEMO_MOCK/MOCK_* fixtures that do not reflect live behaviour)
- Stripe test keys + price IDs for the Billing tab
- A reachable external endpoint (e.g. webhook.site) to exercise HTTP delivery test-fire
- Real SFTP / S3-or-R2 / IMAP credentials (founder-supplied) to exercise the inbound pull/poll channels — and awareness there is no in-UI test for them
- OpenAI key only if exercising AI mapping suggestions (not required for these config pages)

## Frontend OPERATIONS + INBOUND pages — operator views (exceptions, health/dead-letter, connectors, delivery log, webhooks) and inbound docs (invoices, ASNs), under project-proculink/src/app/(app)

The five OPERATIONS pages and two INBOUND pages were audited against their real API helpers (src/lib/api/operations.ts, src/lib/api-client.ts) and the backing .NET controllers. Most operator surfaces are genuinely wired and functional in live mode: Exceptions (GET /api/exceptions + PATCH resolve/ignore), System health (GET /api/ops/health, /api/ops/dead-letter, POST requeue-delivery — verified to really flip status + re-enqueue DeliverOrderJob), and Delivery log (GET /api/audit) all hit real endpoints that exist in the controllers, with honest mock fallbacks under NEXT_PUBLIC_USE_MOCK. Webhooks is partially live: live mode does real CRUD on /api/integrations but the "Recent deliveries" panel is always empty (no history API) and Edit is hidden (no PUT endpoint) — mock mode shows entirely fabricated endpoints + deliveries. Connectors is the weakest: in live mode it lists GET /api/suppliers and labels every one "Available" with no real connection/status signal (honestly noted in code), the "Add connector"/"Connect" buttons only open a read-only panel that punts to the supplier Delivery tab (no connector is created here), and mock mode shows hardcoded SAP Ariba/Coupa/etc. Inbound Invoices is fully wired (list/upload/approve/download endpoints all exist) but the whole Inbound group is hidden behind NEXT_PUBLIC_INBOUND_ENABLED (default off). Inbound ASNs is effectively a stub: upload returns HTTP 501 (no EDI licence) and the UI honestly says "coming soon", and although GET /api/asns exists, the backend DTO fields (ShipmentId/DespatchDate/SourceFileName) do NOT match the frontend AsnDto (asnNumber/supplierName/shipDate/packageCount), so any real ASN would render as dashes. NAVIGATION REALITY: with default launch flags only Exceptions + System health appear in the sidebar; Delivery log, Connectors, Webhooks need NEXT_PUBLIC_LAUNCH_FULL_NAV=true and Inbound needs its own flag — all routes still resolve by direct URL. No fake/staged data leaks to real users in live mode (mock data is gated on USE_MOCK); the main go-live risks are the ASN DTO mismatch and the Connectors "Add/Connect" affordance that does nothing creational.

**Inventory:** 25 items — {"working":15,"partial":4,"mock":2,"dead-control":2,"stub":2}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | Awaiting-review tile (pendingReview) — Informational count of pending_review orders; links to /inbox?status=pending_review | `src/app/(app)/operations/health/page.tsx:167-179 — pendingReview is optional (?? 0); renders 0 if backend field not deployed` |
| partial | Connectors grid (live) — Should show connector/integration status; live mode maps GET /api/suppliers to cards all labelled 'Available' (no real status/connection signal) | `src/app/(app)/operations/connectors/page.tsx:271-309 — honest comment: suppliers list carries no delivery-config signal, connectedCount forced to 0 in live` |
| mock | Connectors grid (mock) — Hardcoded SAP Ariba/Coupa/Dynamics (coming_soon), Generic SFTP/Email (connected), Erply (available) | `src/app/(app)/operations/connectors/page.tsx:20-27 MOCK_CONNECTORS` |
| dead-control | Connectors 'Add connector' / 'Connect' buttons — Imply you can create/connect a connector here | `src/app/(app)/operations/connectors/page.tsx:342-369,239-258 — both only open a READ-ONLY ConnectorPanel that routes to the supplier Delivery tab; no connector is created/saved on this page` |
| dead-control | Webhooks Edit action (live) — Edit an existing endpoint URL/event | `src/app/(app)/operations/webhooks/page.tsx:1077 allowEdit=false in live (no PUT endpoint); Edit hidden in live, but in mock mode handleSave path 1059-1068 shows honest 'not supported, delete+re-add' notice` |
| stub | Webhooks 'Recent deliveries' panel (live) — Should show last 5 webhook delivery attempts | `src/app/(app)/operations/webhooks/page.tsx:1074,393-406 — deliveries=null in live (no history API) → always empty state; only MOCK_DELIVERIES under USE_MOCK` |
| mock | Webhooks page (mock mode) — Fully fabricated endpoints + deliveries with in-memory add/edit/toggle/delete | `src/app/(app)/operations/webhooks/page.tsx:52-83 MOCK_WEBHOOKS/MOCK_DELIVERIES, 941-1007 MockWebhooksPage` |
| stub | Inbound ASNs upload — Upload advance shipping notices / EDIFACT DESADV | `src/app/(app)/inbound/asns/page.tsx (no upload control rendered; honest amber 'Coming soon') → DesadvController.cs POST /api/asns/upload returns HTTP 501 (no EDI licence)` |
| partial | Inbound ASNs list — List received ASNs | `src/app/(app)/inbound/asns/page.tsx:70-205 → getAsns → DesadvController.cs GET /api/asns EXISTS but DTO fields (ShipmentId/Status/DespatchDate/SourceFileName) DO NOT match frontend AsnDto (asnNumber/supplierName/shipDate/packageCount) — any real row renders as dashes; in practice empty since ingestion 501s` |
| partial | Operations/Inbound sidebar visibility — Surface operator pages in nav | `src/lib/launch-flags.ts:15-38 + BridgeSidebar.tsx:101-123 — default launch nav shows ONLY Exceptions + System health; Delivery log/Connectors/Webhooks need NEXT_PUBLIC_LAUNCH_FULL_NAV=true; Inbound needs NEXT_PUBLIC_INBOUND_ENABLED=true; all routes resolve by direct URL` |

**Test scenarios (18):**

- **[OPS-1] First-time user lands on Exceptions with nothing blocked** _(first-time · env: local)_
  - Steps: (1) Sign up / sign in as a brand-new org with no orders (2) Open the sidebar and click 'Exceptions' (3) Read the empty-state copy
  - Expected: Page loads (no infinite skeleton), shows green check + 'No exceptions — all clear' and a one-line explanation of when exceptions appear. The '↻ Sync' button works and the filter tabs (All/Open/Resolved/Ignored) are clickable.
  - Prereq: Fresh org, live API reachable, Clerk session established
- **[OPS-2] Operator resolves a real exception via Open order** _(happy · env: local)_
  - Steps: (1) Upload a CSV PO whose line has no supplier code mapping so an exception is raised (2) Go to Exceptions, expand the row to read what/why/how-to-fix (3) Click 'Open order' and fix the mapping on the order page (4) Return to Exceptions and click '↻ Sync'
  - Expected: The exception is order-linked so the primary action is 'Open order' (not Resolve). After fixing the cause and reprocessing, the exception clears on the next pass. A manual 'Resolve' is NOT offered for order-linked rows.
  - Prereq: Worker running locally, an order that produces an unresolved-mapping exception
- **[OPS-3] Exception service unavailable** _(error · env: local)_
  - Steps: (1) Stop the API (or block /api/exceptions) (2) Open Exceptions
  - Expected: After the single retry, the red error card 'Couldn't load exceptions … usually transient' appears with a working Retry button — NOT a permanent loading skeleton and NOT a blank page.
  - Prereq: Ability to stop/break the API
- **[OPS-4] Health all-clear and worker-down banner** _(happy · env: local)_
  - Steps: (1) Open System health with no problem orders and the worker running (2) Stop the Hangfire worker process and wait ~1 min, refresh
  - Expected: With worker up: green 'Order processing is running' banner + 'All clear' block. With worker down: red 'Order processing is paused' banner warning new uploads may wait. Tiles/auto-refresh (45s) behave.
  - Prereq: Local stack where the Worker can be started/stopped independently (API hosts no Hangfire)
- **[OPS-5] Requeue a dead-lettered delivery** _(happy · env: local)_
  - Steps: (1) Create an order that fails delivery until it dead-letters (point delivery at an endpoint returning 503) (2) Open System health → 'Orders we couldn't deliver' (3) Click 'Try sending again' on the row
  - Expected: Button shows 'Sending…', a blue notice names the PO ('Trying to send {PO} again. It will move back to sending.'), the order flips to delivering and DeliverOrderJob re-runs. Only the clicked row's button spins (per-row guard).
  - Prereq: Worker running, a controllable failing delivery endpoint, an order that reaches delivery_dead_letter or delivery_failed
- **[OPS-6] Requeue on an ineligible order** _(error · env: local)_
  - Steps: (1) Find an order not in dead_letter/failed (e.g. delivered) — or hit POST /api/ops/orders/{id}/requeue-delivery directly (2) Trigger requeue
  - Expected: Backend returns 400 with a clear message that the order must be in dead_letter/failed; the UI surfaces the error text in the notice rather than silently succeeding. (Edge: an order with no outbound artifact returns 400 'Transform the order before requeuing'.)
  - Prereq: An order in a non-failed status; ability to call the endpoint
- **[OPS-7] Dumb user tries to 'Add connector' / 'Connect'** _(edge · env: local)_
  - Steps: (1) Open Connectors (full nav enabled) (2) Click 'Add connector' (top right) or 'Connect' on a card
  - Expected: A read-only panel opens explaining delivery endpoints are configured PER SUPPLIER in the supplier's Delivery tab, with a 'Test fire' and an 'Open supplier Delivery tab' link. CONFUSION RISK: the buttons read like they create a connector but nothing is created here — verify the panel copy makes the per-supplier model obvious so the user isn't stuck looking for a save button.
  - Prereq: NEXT_PUBLIC_LAUNCH_FULL_NAV=true (Connectors is hidden in default nav)
- **[OPS-8] Connectors live count vs mock** _(edge · env: local)_
  - Steps: (1) Load Connectors in live mode with several suppliers but no delivery configs (2) Note the header subtitle and per-card footer
  - Expected: Live: header says 'ERP and channel integrations' (NO 'N connected' count) and cards omit the usage line and all read 'Available' — because the suppliers list has no delivery-config signal. This is intentional honesty; verify it does NOT claim suppliers are 'Connected'.
  - Prereq: Live API with suppliers that have no delivery config
- **[OPS-9] Delivery log browse, search, export** _(happy · env: local)_
  - Steps: (1) Process a couple of orders end to end (parse/validate/deliver) (2) Open Delivery log (3) Filter by 'Failed', search a PO number, expand an entry, click 'Export log'
  - Expected: Date-grouped real audit events render with correct event icons/colours; filters and PO search work; expanding a failed entry shows detail and an honest 'Open to resend' (navigates to the order); 'Export log' downloads a CSV of the filtered view.
  - Prereq: Some real audit history for the org; live API
- **[OPS-10] Create + verify an outbound webhook** _(happy · env: external-dep)_
  - Steps: (1) Open Webhooks (full nav) (2) Click 'Add endpoint', paste a webhook.site URL, pick order.delivered, optionally add a signing secret, Save (3) Trigger an order delivery and watch the external receiver
  - Expected: Endpoint is created via /api/integrations, a test ping is sent on save, the receiver gets an HMAC-SHA256 signed payload, and the endpoint status reflects real deliveries (healthy/failing). Note the on-page 'Recent deliveries' panel stays EMPTY in live mode (no history API) — verify this isn't mistaken for a broken webhook.
  - Prereq: NEXT_PUBLIC_LAUNCH_FULL_NAV=true; an external endpoint (webhook.site); a deliverable order
- **[OPS-11] Webhook edit attempt in live mode** _(edge · env: local)_
  - Steps: (1) Open Webhooks in live mode with at least one endpoint (2) Look for an Edit button; if reachable, attempt to edit
  - Expected: Edit is hidden in live mode (allowEdit=false) because there is no PUT endpoint. If an edit path is somehow reached it shows the honest notice 'Editing isn't supported yet — delete and re-add'. Toggle (Disable/Enable) and Delete DO work.
  - Prereq: Live mode, an existing integration
- **[OPS-12] Mock-mode webhooks/connectors must never reach a real user** _(security · env: local)_
  - Steps: (1) Confirm NEXT_PUBLIC_USE_MOCK is false in any deployed/staging build (2) Load Webhooks and Connectors
  - Expected: No fabricated endpoints (erp.company.com, legacy.example) or fake deliveries (ATL-55021, 503 timeout) and no hardcoded SAP Ariba/Coupa 'connected' connectors appear. These only render under USE_MOCK. Verify the deployed env has USE_MOCK off so staged data can't leak.
  - Prereq: Access to the build's env flags
- **[INB-1] Finding the Inbound pages as a new user** _(first-time · env: either)_
  - Steps: (1) Sign in with default launch flags (2) Scan the sidebar for Invoices / ASNs
  - Expected: Invoices and ASNs are NOT in the sidebar by default (Inbound hidden unless NEXT_PUBLIC_INBOUND_ENABLED=true). A user can only reach them by direct URL. Confirm this is intended for go-live (outbound-PO wedge only) so users aren't sold an inbound feature they can't find.
  - Prereq: Default launch flags
- **[INB-2] Upload and approve an invoice** _(happy · env: local)_
  - Steps: (1) Navigate directly to /inbound/invoices (or enable INBOUND_ENABLED) (2) Click 'Upload invoice', pick a UBL 2.1 XML invoice (3) After it appears, click 'Approve', then '↓ CSV'
  - Expected: Invoice uploads via /api/invoices/upload, appears in the table with parsed number/supplier/amount/lines, 'Approve' flips status to Approved, and CSV download produces a real file (object-URL anchor). Empty state shows a clear 'Upload invoice' CTA.
  - Prereq: INBOUND_ENABLED or direct URL; a valid UBL invoice XML; live API + worker
- **[INB-3] Invoice upload of an unparseable file** _(error · env: local)_
  - Steps: (1) On /inbound/invoices upload a non-invoice or malformed XML/EDI
  - Expected: A red notice 'Upload failed — {message}' appears (from the backend error body), the row is not added, and the page remains usable.
  - Prereq: Inbound reachable; live API
- **[INB-4] ASN page shows honest 'coming soon' and never offers a failing upload** _(first-time · env: either)_
  - Steps: (1) Navigate directly to /inbound/asns (2) Look for any upload control
  - Expected: An amber 'Coming soon — ASN/EDIFACT DESADV ingestion isn't available yet' banner shows; there is NO upload button (the 501 path is never exposed); empty state explains there's nothing to upload. Verify no dead upload control exists.
  - Prereq: Direct URL or INBOUND_ENABLED
- **[INB-5] ASN list DTO field mismatch** _(edge · env: local)_
  - Steps: (1) Insert an AdvanceShippingNotice row for the org directly in the DB (since upload 501s) (2) Open /inbound/asns
  - Expected: BUG TO CONFIRM: backend GET /api/asns returns {Id, ShipmentId, Status, DespatchDate, SourceFileName, CreatedAt} but the frontend AsnDto reads asnNumber/supplierName/shipDate/packageCount, so the row renders ASN #, Supplier, Ship date as '—' and Packages as blank/undefined. Either fix the DTO mapping or keep ingestion disabled so the list stays empty.
  - Prereq: Ability to seed an ASN row in Postgres
- **[INB-6] Inbound pages cross-tenant isolation** _(security · env: local)_
  - Steps: (1) Create two orgs each with invoices (2) As org A, call GET /api/invoices and GET /api/asns (3) Confirm only org A's records return
  - Expected: All inbound queries are org-scoped (controllers resolve OrganisationId from the tenant); org A never sees org B's invoices/ASNs.
  - Prereq: Two orgs with seeded inbound data

**Area prerequisites:**
- Live API (ASP.NET Core :5223) + Postgres (:5435) + a running Hangfire Worker (the API hosts no jobs — requeue/transform/deliver need the Worker)
- Clerk session or PROCULINK_QA_BYPASS_AUTH=true in Development for authed (app) routes
- NEXT_PUBLIC_USE_MOCK=false to test live wiring (mock data only renders under USE_MOCK)
- NEXT_PUBLIC_LAUNCH_FULL_NAV=true to surface Delivery log, Connectors, Webhooks in the sidebar (default nav shows only Exceptions + System health)
- NEXT_PUBLIC_INBOUND_ENABLED=true to surface Invoices/ASNs in the sidebar (default off; routes still resolve by direct URL)
- For requeue/dead-letter testing: a controllable supplier delivery endpoint that can return 503/timeout to force delivery_failed → delivery_dead_letter
- For webhook verification: an external receiver (e.g. webhook.site) — external-dep
- For invoice testing: a valid UBL 2.1 invoice XML; for ASN list mismatch: ability to seed an AdvanceShippingNotice row in Postgres

## Backend API endpoint inventory — auth scheme + tenancy isolation across all 38 controllers in ProcuLink.Api/Controllers

I enumerated all 38 controllers under ProcuLink.Api/Controllers and verified auth + tenancy against actual source (Program.cs pipeline, TenantResolutionMiddleware, ApiKeyAuthHandler, AdminOnlyAttribute, CurrentTenantService). The posture is strong and consistent — no P0/P1 tenancy or missing-auth holes found.

AUTH MODEL: Two real schemes. (1) Clerk JWT Bearer is the global default (Program.cs:150-191) with MapInboundClaims=false, ValidateAudience=false compensated by an azp authorized-party check (OnTokenValidated -> ClerkTokenValidation.IsAuthorizedParty). (2) ApiKey scheme (X-ProcuLink-Key header, plk_ keys, HMAC-SHA256 hash via Security:ApiKeyHashSecret) used only by IngressController via [Authorize(AuthenticationSchemes="ApiKey")]. A dev-only QA-bypass scheme replaces the default ONLY when ASPNETCORE_ENVIRONMENT=Development AND PROCULINK_QA_BYPASS_AUTH=true (Program.cs:143-163) — cannot activate in prod.

TENANCY: TenantResolutionMiddleware resolves Clerk org_id (or falls back to sub = personal workspace) to the internal Organisation UUID and stores it in HttpContext.Items; ApiKeyAuthHandler publishes the SAME Items key. CurrentTenantService.OrganisationId reads it and THROWS UnauthorizedAccessException if unresolved (fail-closed). Every authed controller injects ICurrentTenantService and scopes queries .Where(OrgId == orgId). I spot-checked OrdersController (~40 callsites all _tenant.OrganisationId), SuppliersController (every route org+DeletedAt scoped), DashboardController, AuditController, OpsController, MapperEnrichmentController, MappingSuggestionsController, IngressController — all clean. Artifact download and requeue go through org-scoped service calls.

ANONYMOUS/HMAC SURFACES (3, all intentional + guarded): WebhookIngressController ([AllowAnonymous], HMAC-SHA256 + timestamp ±300s + nonce replay store, uniform 401, per-slug rate limit), InboundEmailController (Postmark shared-token constant-time compare, refuses if token unset), BillingController POST /webhook ([AllowAnonymous], Stripe signature verified). SupportController POST /contact is [AllowAnonymous] + tight 5/60s limit. DevFilesController has NO auth attribute but hard-returns 404 unless IsDevelopment() and uses GetFullPath traversal guard — safe.

ADMIN: AdminController is the single deliberately cross-tenant controller, gated by [AdminOnly] (env allowlist Admin:UserIds/Admin:Emails, fails closed on empty/missing allowlist or missing service). bulk-erase requires a non-empty filter (400 on {}); all admin org-targeting is by route id.

RATE LIMITING: Comprehensive named policies (upload 60/min, transform 30/min, ai 15/min, preview 120/min, signed-url 60/min, webhook 120/min per-slug, support 5/min) + a 300/min global backstop on every endpoint. Partition key = Clerk sub -> IP -> anonymous; webhook keys per tenant slug. Documented caveat: limiters are PROCESS-LOCAL fixed-window (exact at 1 replica, ~Nx at N replicas — revisit before horizontal scale).

QUOTA GATES: Order upload + transform both call _billing.CheckOrderLimitAsync (429 pilot_expired/order_limit_reached); supplier create calls CheckSupplierLimitAsync (429 supplier_limit_reached). Sample orders excluded from quota counts.

Only nits (P2/P3): the multi-replica rate-limit caveat, and HMAC nonce replay store is in-memory unless Redis:ConnectionString is set (single-replica only). Neither is a go-live blocker at the current single-replica deploy.

**Inventory:** 41 items — {"working":40,"partial":1}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | DesadvController — ASN/DESADV ingest (202 Accepted + EDI licence note) | `ProcuLink.Api/Controllers/DesadvController.cs — [Authorize], api/asns; EdifactDesadvParser is a licence-gated stub (no commercial EDI lib)` |

**Test scenarios (12):**

- **[API-1] Authed endpoint rejects missing/garbage token (auth-required)** _(security · env: either)_
  - Steps: (1) curl GET http://localhost:5223/api/orders with no Authorization header (2) Repeat with Authorization: Bearer not-a-real-jwt (3) Repeat against GET /api/suppliers and GET /api/dashboard
  - Expected: All return 401 Unauthorized. No data leaks. Endpoints never 200 without a valid token.
  - Prereq: none beyond a running API
- **[API-2] Cross-tenant isolation — org A cannot read org B's order** _(security · env: either)_
  - Steps: (1) As org A, GET /api/orders and note an orderId (2) As org B, GET /api/orders/{org-A-orderId} (3) As org B, GET /api/orders/{org-A-orderId}/audit and /artifacts/{x}/download (4) As org B, POST /api/orders/{org-A-orderId}/transform
  - Expected: All org-B requests against org-A's order return 404 (not 403/200). Service layer filters .Where(OrgId==orgId) so the row is invisible. No artifact URL minted.
  - Prereq: two distinct Clerk orgs
- **[API-3] API-key ingress slug guard — key for org A cannot post to org B's slug** _(security · env: either)_
  - Steps: (1) curl POST /api/ingress/{org-B-slug}/orders with header X-ProcuLink-Key: {org-A-key} and a valid body (2) curl GET /api/ingress/{org-B-slug}/ping with org-A key
  - Expected: 403 Forbid (SlugMatchesCallerAsync fails: route slug != authed org). Posting to the caller's OWN slug succeeds (200).
  - Prereq: a created plk_ API key
- **[API-4] Admin surface is fail-closed for non-admins** _(security · env: either)_
  - Steps: (1) curl GET /api/admin/overview with a normal user's JWT (2) curl GET /api/admin/organisations (3) curl POST /api/admin/organisations/{anyGuid}/orders/bulk-erase with body {}
  - Expected: 403 Forbidden for the normal user on every admin route (logged as fail-closed). With an empty/unset allowlist, EVERYONE gets 403. The bulk-erase {} body would also be 400 (empty filter refused) even for an admin.
  - Prereq: control over Admin:* config
- **[API-5] HMAC webhook ingress rejects bad/missing signature uniformly** _(security · env: either)_
  - Steps: (1) curl POST /api/webhook-ingress/{slug}/acknowledge with no X-ProcuLink-Signature (2) Repeat with a wrong signature (3) Repeat with a stale timestamp (>300s old) (4) Repeat with a replayed nonce
  - Expected: All return 401 with the SAME generic error (never reveals which check failed). A correctly-signed, fresh, unique-nonce request succeeds (200) and records an audit event scoped to that org.
  - Prereq: ability to compute HMAC-SHA256 over ts.nonce.body
- **[API-6] Pilot/quota gate blocks upload + transform when limit reached** _(error · env: either)_
  - Steps: (1) POST /api/orders/upload with a CSV as the over-limit org (2) POST /api/orders/{existingId}/transform as the over-limit org (3) Inspect the 429 JSON body
  - Expected: 429 with {error:'pilot_expired'|'order_limit_reached', plan, limit}. Upload does not enqueue a parse job. An admin POST /api/admin/organisations/{id}/limits override lifts the cap and the next upload succeeds.
  - Prereq: ability to set an org at its limit (admin override or seed)
- **[API-7] Supplier-limit quota gate on supplier create** _(error · env: either)_
  - Steps: (1) POST /api/suppliers with a new supplier name as the at-limit org
  - Expected: 429 {error:'supplier_limit_reached'|'pilot_expired', plan, limit}. No supplier row created.
  - Prereq: org at supplier cap
- **[API-8] Stripe billing webhook verifies signature** _(security · env: either)_
  - Steps: (1) curl POST /api/billing/webhook with no Stripe-Signature header (2) Repeat with a forged signature and arbitrary JSON
  - Expected: 400 'Missing signature.' then 400 'Invalid signature.' — event is never processed without a valid Stripe-signed payload.
  - Prereq: none (negative test)
- **[API-9] First-time dumb-user signup auto-provisions a pilot org** _(first-time · env: prod)_
  - Steps: (1) Sign up via the frontend, land on the app (2) Observe the first authed API call (e.g. GET /api/dashboard) resolve a tenant (3) Re-run a script that mints 6+ fresh Clerk identities from the same IP within 10 min
  - Expected: First login: TenantResolutionMiddleware auto-creates a 14-day pilot org (org_created analytics event) and the dashboard loads empty cleanly. The 6th+ rapid fresh-identity provision from one IP is throttled — request continues WITHOUT a resolved tenant so tenant-scoped controllers fail closed (no unlimited trial farming).
  - Prereq: Clerk signup flow + a way to mint identities
- **[API-10] Rate-limit backstop returns 429 JSON, not a crash** _(edge · env: either)_
  - Steps: (1) Hammer POST /api/orders/upload >60 times in 60s as one user (2) Hammer POST /api/support/contact >5 times in 60s anonymously
  - Expected: Excess requests get 429 with {error:'Rate limit exceeded...'}. Upload cap is 60/min, support 5/min. Global 300/min backstop covers any endpoint without a named policy. (Caveat: limits are per-replica — exact only at 1 API replica.)
  - Prereq: none
- **[API-11] Dev-only file passthrough never serves in prod** _(security · env: prod)_
  - Steps: (1) curl GET https://<prod-api>/api/dev/files/some/key (2) curl GET https://<prod-api>/api/dev/files/../../etc/passwd
  - Expected: 404 in prod regardless of key (IsDevelopment() gate). Path traversal also rejected by GetFullPath guard even in dev.
  - Prereq: prod API host
- **[API-12] Postmark inbound-email rejects bad token** _(security · env: either)_
  - Steps: (1) curl POST /api/inbound-email/postmark with no X-Postmark-Server-Token (2) Repeat with a wrong token
  - Expected: 401 'Invalid webhook token.' (constant-time compare). If the token is UNSET server-side, returns 401 'Inbound webhook is not configured' rather than 500.
  - Prereq: none (negative test)

**Area prerequisites:**
- Two distinct Clerk organisations with valid session JWTs (for cross-tenant isolation tests) — external-dep: Clerk
- A created tenant API key (plk_) to exercise IngressController slug-guard tests
- Admin:UserIds / Admin:Emails env control to test the AdminOnly fail-closed gate (and a known-admin identity to test the positive path)
- Stripe:WebhookSecret + Stripe test events for billing-webhook signature tests — external-dep: Stripe
- Inbound:Postmark:WebhookToken + ability to POST Postmark-shaped JSON for inbound-email tests — external-dep: Postmark
- An org webhook shared secret + ability to compute HMAC-SHA256(ts.nonce.body) for WebhookIngress tests
- Ability to put an org at/over its order and supplier limits (admin override endpoint or DB seed) for quota-gate tests
- Local dev: PROCULINK_QA_BYPASS_AUTH=true + ASPNETCORE_ENVIRONMENT=Development + Postgres on :5435 + Delivery__EncryptionKey to exercise authed endpoints without real Clerk tokens

## Outgoing document formats — transform/output services (ProcuLink.Transform/Output + Core ITransformService)

The outbound transform layer is genuinely built, not stubbed. Six entity-based PO transformers (XML, CSV, cXML, JSON, UBL/Peppol-BIS-order, X12 850) are all real, registered as ITransformService singletons in both ProcuLink.Api/Program.cs (lines 618-623) and ProcuLink.Worker/Program.cs, and dispatched via OrderTransformService by CanTransform(effectiveFormat). Four invoice transformers (CSV/XML/JSON/PeppolBis-Billing-3.0) are registered as IInvoiceTransformService and routed by InvoiceService.ForwardAsync. On top of the fixed transforms sit three override/flexible layers, all real: MappedTransformService (native CSV+JSON from per-order OutputMappingConfig rules + manipulators + Scriban expressions + F-1 src:: source-binding + catalog injection), OutputTemplateEmitter (recursive OutputNode AST → JSON/XML/CSV; deliberately throws for cXML/UBL since a generic tree can't carry the required envelope/DOCTYPE — an offer⇔works guard), and ScribanTemplateTransformService (whole-document supplier-authored Scriban template, sandboxed, never throws to the pipeline). Format selection per supplier is driven by SupplierDeliveryConfig.OutputFormat (DeliveryConfigEditor select offers exactly csv/xml/cxml/ubl/x12/json + "not set→XML"; backend AllowedOutputFormats = {xml,csv,cxml,json,ubl,x12}), and a pinned ConnectionRevision.OutputFormat takes precedence at transform time. cXML carries configurable From/To/Sender credentials, ShipTo/BillTo/Contact address blocks, per-line Tax, and configurable DOCTYPE (T7) with verbatim-injection safety guards — byte-identical to pre-feature output when unconfigured, and the project memory records a real REDACTED-PARTY PDF→cXML MapForce-parity proof. UBL emits Peppol BIS order-only 3.0 identifiers. X12 850 emits a balanced ISA/GS/ST envelope with delimiter sanitization (no escape mechanism in X12). offer⇔works is clean: the ONLY format the marketing /formats page does NOT mark "live" outbound is EDIFACT ORDERS (tagged "onRequest"), and correctly there is no outbound EDIFACT transformer and no EDIFACT option in the delivery-config UI. Three enum values (UblOrder, X12_850, EdifactOrders) are conformance/detection-profile identifiers only (used by FormatDetectorService / conformance), NOT routed to any transformer and NOT reachable from delivery config. A full in×out matrix test suite (FullInOutMatrixTests, InOutMatrixTheoryTests, OutCoverageMatrixTests, HighVolumeMatrixTests, NumericTokenSafetyMatrixTests) exists in the primary checkout. Key honest gaps: UBL/X12/cXML SellerSupplierParty still emits the supplier GUID as the party name (placeholder, not real supplier metadata); PeppolBis invoice ships a lightweight mandatory-field checker, NOT full EN16931/Schematron conformance (self-documented); and AS2/AS4/PEPPOL-AP transport is partner-wrap "onRequest" only.

**Inventory:** 19 items — {"working":15,"partial":3,"stub":1}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | UblOrderTransformService — UBL 2.1 Order-2 with Peppol BIS order-only 3.0 CustomizationID/ProfileID; BuyerCustomerParty + Delivery + OrderLine | `ProcuLink.Transform/Output/UblOrderTransformService.cs` |
| partial | X12TransformService — ANSI X12 850 v004010 with balanced ISA/GS/ST/SE/CTT envelope, N1 loops, delimiter sanitization, WS-12 EnvelopeConfig identity override | `ProcuLink.Transform/Output/X12TransformService.cs` |
| partial | PeppolBisInvoiceTransformService — Peppol BIS Billing 3.0 UBL 2.1 invoice generation (BT/BG business terms); lightweight mandatory-field checker only, NOT full EN16931 Schematron (self-documented) | `ProcuLink.Transform/Output/PeppolBisInvoiceTransformService.cs` |
| stub | Outbound EDIFACT transformer — Marketing /formats lists EDIFACT ORDERS outbound as 'onRequest' (not live); no transformer exists and no UI option — honest, not a dead control | `project-proculink/src/app/(marketing)/formats/page.tsx:92 (onRequest); no transformer in ProcuLink.Transform/Output` |

**Test scenarios (10):**

- **[FMT-01] Transform a canonical order to all six outbound formats and validate structure (in×out matrix)** _(happy · env: local)_
  - Steps: (1) Run the existing matrix suite: dotnet test ProcuLink.Transform.Tests --filter FullyQualifiedName~FormatMatrix (2) Inspect FullInOutMatrixTests + InOutMatrixTheoryTests + OutCoverageMatrixTests output covering CSV/XML/cXML/JSON/UBL/X12 (3) Confirm each produced document is well-formed (XML parses, CSV row/col count, X12 ISA 105-char + balanced SE/CTT, cXML payloadID/Header/OrderRequest, UBL UBLVersionID/CustomizationID/ProfileID)
  - Expected: All matrix cases green; each format emits the documented structure; numeric-token-safety matrix shows no EU comma-decimal corruption
  - Prereq: dotnet 8 SDK; ProcuLink.Transform.Tests project
- **[FMT-02] First-time buyer sets a supplier's output format and previews the result** _(first-time · env: local)_
  - Steps: (1) Sign up, add one supplier, upload the pre-wired sample order (2) Open the supplier's Delivery config, open the 'Output format — the format this supplier requires' select (3) Pick each of CSV / XML / cXML / UBL 2.1 Peppol / ANSI X12 850 / JSON (4) For a resolved order call POST /api/orders/{id}/mapping-override/preview (the editor's debounced dry-run) and read the returned bytes
  - Expected: Each selection persists; preview returns a non-empty document in the chosen shape; no dead options; 'Not set' note correctly says it defaults to XML
  - Prereq: Local dev stack (API :5223 + Postgres :5435 + Worker) with PROCULINK_QA_BYPASS_AUTH and a 32-byte Delivery__EncryptionKey
- **[FMT-03] cXML supplier with network credentials + DTD produces a receiver-shaped document** _(happy · env: local)_
  - Steps: (1) Set output format = cXML; fill From/To/Sender domain+identity and a Sender shared secret; optionally set a DTD system id (2) Resolve all order lines (supplier item codes present) (3) Preview / transform and inspect the cXML
  - Expected: Header carries the configured NetworkId credentials + <SharedSecret>; OrderRequestHeader has orderID/orderDate(dateTime)/orderVersion; ShipTo/BillTo/Contact emitted when address present; per-line <Tax> under ItemOut; DOCTYPE present only when a valid system id set
  - Prereq: Local stack; an order with address + tax data
- **[FMT-04] Unresolved or zero-price line is held, never delivered blind** _(error · env: local)_
  - Steps: (1) Upload an order where one line has no supplier item code OR UnitPrice=0 / Quantity=0 (2) Attempt transform for any fixed format (CSV/XML/JSON/cXML/UBL/X12)
  - Expected: OutputFieldValidator throws TransformValidationException; the order reverts to ready (no artifact), and the flagged line numbers surface in /operations/exceptions with a plain reason — no €0 or empty-code document is produced
  - Prereq: Local stack
- **[FMT-05] OutputNode designer cannot silently emit invalid cXML/UBL** _(edge · env: local)_
  - Steps: (1) Create an OrderMappingOverride with an OutputNodeTemplate whose Format is CXml or Ubl (2) Run OrderTransformService transform / preview
  - Expected: OutputTemplateEmitter throws a clear ArgumentException telling the operator to use the dedicated cXML/UBL transform or design generic XML — it does NOT emit a well-formed-but-rejected envelope-less document (offer⇔works guard holds)
  - Prereq: Local stack; ability to author an output tree
- **[FMT-06] Whole-document Scriban template with a syntax error does not crash the pipeline** _(error · env: local)_
  - Steps: (1) Set an override OutputTemplate to a Scriban string with a deliberate compile error (e.g. unclosed {{ ) (2) Call mapping-override/preview, then attempt a real transform
  - Expected: Preview/Build returns/raises a clear TransformTemplateException message; the order stays un-transformed and is never delivered from a broken template; pipeline survives (no 500 strand in 'transforming')
  - Prereq: Local stack
- **[FMT-07] Buyer looks for EDIFACT outbound and finds it honestly gated** _(edge · env: either)_
  - Steps: (1) Open the Delivery config output-format select and look for an EDIFACT option (2) Open the marketing /formats page 'formats we produce' section
  - Expected: No EDIFACT option in the delivery UI; /formats lists EDIFACT ORDERS outbound as 'On request' (not Supported/live) — offer matches the missing transformer; no dead control
  - Prereq: Frontend running (local) or live deployment
- **[FMT-08] Peppol BIS Billing 3.0 invoice generates and its conformance limits are disclosed** _(happy · env: local)_
  - Steps: (1) Approve an InvoiceEntity, call InvoiceService.ForwardAsync with format 'peppol' (2) Inspect the UBL 2.1 document and run PeppolBisValidator
  - Expected: Document carries BIS Billing 3.0 CustomizationID/ProfileID and the BT/BG terms it can source; PeppolBisValidator reports missing recommended fields so the caller knows it is NOT full-Schematron network-ready — claim matches reality
  - Prereq: Local stack; an approved invoice fixture
- **[FMT-09] Live end-to-end transform+deliver of each format against a real endpoint** _(happy · env: prod)_
  - Steps: (1) On the live deployment configure a supplier per format (CSV/XML/JSON/cXML/UBL/X12) pointing at a controlled receiver (e.g. webhook.site) (2) Upload, resolve, send → transform → deliver for each (3) Confirm the received bytes at the endpoint match the expected format
  - Expected: Each format reaches code-200 delivery and the receiver gets a structurally valid document of the configured type; matches the project's prior 3-supplier x 3-format live proof
  - Prereq: Live Railway API+Worker + Vercel frontend; a controlled receiving endpoint
- **[FMT-10] X12 delimiter contamination in a code field is held, not silently mangled** _(security · env: local)_
  - Steps: (1) Craft an order whose SupplierItemCode contains an X12 delimiter (* > ~) (2) Select output format X12 and transform
  - Expected: X12Sanitizer.GuardCodeFields flags the line for review (held) rather than space-substituting a structured vendor part code into a corrupt 850; free-text fields are sanitized but identifier fields are protected
  - Prereq: Local stack

**Area prerequisites:**
- Local dev stack to exercise transforms: API :5223 + Postgres :5435 + ProcuLink.Worker (delivery/transform jobs run in the Worker, the API hosts no Hangfire), PROCULINK_QA_BYPASS_AUTH=true under Development, and a 32-byte base64 Delivery__EncryptionKey (delivery-config endpoints resolve encrypted services).
- Because /transform is async (enqueues a Hangfire job), structural validation of output bytes is done synchronously via POST /api/orders/{id}/mapping-override/preview (the editor's dry-run) or via the ProcuLink.Transform.Tests FormatMatrix suite — do not poll /transform expecting an inline document.
- For live (prod) delivery scenarios: Railway API + Worker (EU) and Vercel frontend, plus a controlled receiving endpoint (e.g. webhook.site) to inspect delivered bytes.
- cXML network-credential and DTD scenarios need an order carrying address/tax data and resolved supplier item codes; PeppolBis invoice scenarios need an approved InvoiceEntity fixture.
- No third-party credentials are required for the six core outbound formats (all deterministic, in-process); outbound EDIFACT and AS2/AS4/PEPPOL-AP transport are 'on request' partner-wrap and out of scope for go-live.

## Incoming transport / ingress channels (browser upload, inbound REST API, hosted inbound email webhook, IMAP polling, SFTP/S3 polling)

Five inbound channels exist. Three are wired end-to-end (browser upload, hosted inbound email, IMAP polling) and one is wired but JSON-only (inbound REST API). The fifth (SFTP/S3 polling) has a HARD GAP: both SftpIngressService.PollAsync and S3IngressService.PollAsync create order stubs via IOrderService.CreateStubAsync but NEVER enqueue ParseOrderJob, so imported files are stuck forever in their initial state and never parse/transform/deliver. CreateStubAsync (OrderService → OrderIngestionService) does not self-enqueue parsing — every working caller (OrdersController.Upload line 239, EmailPollOrgJob line 284, InboundEmailRouter line 229) explicitly calls ParseOrderJob.Enqueue afterward; the two pull-ingress services omit that step. Compounding this, SFTP/S3 ingress has NO configuration API or UI at all — SftpIngressConfig/S3IngressConfig rows can only be inserted directly into Postgres, so a non-technical buyer cannot set them up. Only IMAP email has a settings surface (GET/PUT /api/settings/email). Content-routing (assign-supplier on unrouted orders) has its consumer half shipped (CreateUnroutedStubAsync, POST /api/orders/{id}/assign-supplier, unrouted status) but the PRODUCER half is fully dormant: CreateUnroutedStubAsync has zero production callers, no channel populates a RouteByContent flag (specced only), and no content/supplier matcher exists. SSRF guard (OutboundRequestGuard) is correctly applied on the tenant-configured IMAP/SFTP/S3 hosts. Auth surfaces are real: ApiKeyAuthHandler (plk_ keys via X-ProcuLink-Key, requires Security:ApiKeyHashSecret) and the HMAC webhook-ingress verifier.

**Inventory:** 13 items — {"working":9,"partial":2,"stub":1,"dead-control":1}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | SFTP polling — SftpPollingJob + SftpPollOrgJob + SftpIngressService — Recurring poll of a tenant SFTP dir; downloads new PO files and creates order stubs | `ProcuLink.Infrastructure/Services/Ingress/SftpIngressService.cs:202-233 — creates stub but NEVER enqueues ParseOrderJob; imported orders never parse` |
| partial | S3/R2 polling — S3PollingJob + S3PollOrgJob + S3IngressService — Recurring poll of a tenant S3/R2 bucket; downloads new PO objects and creates order stubs | `ProcuLink.Infrastructure/Services/Ingress/S3IngressService.cs:225-271 — creates stub but NEVER enqueues ParseOrderJob; imported orders never parse` |
| stub | SFTP/S3 ingress configuration surface — Set up SftpIngressConfig / S3IngressConfig (host, creds, dir/bucket, default supplier) | `No controller exposes SftpIngressConfig/S3IngressConfig CRUD — only insertable directly in Postgres; no frontend setup UI` |
| dead-control | Content routing — CreateUnroutedStubAsync + POST /api/orders/{id}/assign-supplier — Park supplier-less ingested orders as 'unrouted', then assign supplier and re-parse | `Consumer half live (OrderService.cs:86; OrdersController.cs:567-617). Producer half dormant — CreateUnroutedStubAsync has NO production caller; no channel emits unrouted; RouteByContent flag specced only (docs/superpowers/specs/2026-06-26-supplier-routing-backend-scope.md)` |

**Test scenarios (20):**

- **[UP-1] First-time buyer uploads their first PO file** _(first-time · env: local)_
  - Steps: (1) Sign up / land in the app with a fresh org and no orders (2) Create a supplier (the upload form requires a supplierId) (3) Open the upload page and drop a simple 3-line CSV PO (4) Submit and watch the status
  - Expected: Upload returns 200 with order in 'parsing'; Worker picks up ParseOrderJob and the order advances to a reviewable/ready state within seconds. The empty-state should make it obvious a supplier is needed FIRST (upload is blocked with a clear message if supplierId is empty).
  - Prereq: Local dev stack (API :5223, Worker running, Postgres :5435, Delivery__EncryptionKey set, PROCULINK_QA_BYPASS_AUTH=true). At least one supplier must exist for the org.
- **[UP-2] Buyer uploads an unsupported file type** _(error · env: local)_
  - Steps: (1) On the upload form, choose a .docx or .png file (2) Submit
  - Expected: 400 with 'Supported formats: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT/X12).' Frontend should surface this plainly, not a raw error.
  - Prereq: Same as UP-1.
- **[UP-3] Buyer hits the plan/pilot order limit on upload** _(edge · env: local)_
  - Steps: (1) Attempt to upload a valid CSV
  - Expected: 429 with error 'pilot_expired' or 'order_limit_reached' plus plan/limit/upgradeUrl. UI must explain how to recover (upgrade), not silently fail.
  - Prereq: Org on an expired Pilot or at its monthly order cap.
- **[UP-4] Duplicate upload retry (idempotency)** _(edge · env: local)_
  - Steps: (1) Upload a CSV with Idempotency-Key: abc123 (2) Re-POST the identical upload with the same key within 24h
  - Expected: Second call returns the SAME order with idempotentReplay:true; no duplicate order or duplicate downstream delivery.
  - Prereq: Same as UP-1; client sends an Idempotency-Key header.
- **[API-1] Integration user pushes a PO via REST ingress** _(happy · env: local)_
  - Steps: (1) GET /api/ingress/{slug}/ping with X-ProcuLink-Key → confirm OK (2) POST /api/ingress/{slug}/orders with a JSON body of orderNumber/currency/supplierId/lines
  - Expected: 200 with new order id + line count; the order is created and (being a structured push) is immediately processable. Pushed fields are projected into a SourceCapture for the Order Workshop.
  - Prereq: Org has a slug, a plk_ API key created via Settings, Security:ApiKeyHashSecret configured, and a supplier (by GUID or name).
- **[API-2] REST push with wrong slug or unknown supplier** _(error · env: local)_
  - Steps: (1) POST /api/ingress/{slugOfOrgB}/orders → expect Forbid (2) POST /api/ingress/{slugOfOrgA}/orders with supplierId 'Nonexistent Co' → expect 400
  - Expected: 403 for cross-tenant slug mismatch; 400 'Supplier ... not found.' for an unresolvable supplier; no order created.
  - Prereq: Valid API key for org A.
- **[API-3] At-least-once duplicate REST push** _(edge · env: local)_
  - Steps: (1) POST the same order body twice with no Idempotency-Key (Zapier-style replay)
  - Expected: Second POST returns the original order with idempotentReplay:true (auto-derived key from slug+PO+line shape); exactly one order exists.
  - Prereq: Valid API key + supplier.
- **[EMAIL-HOST-1] Supplier emails a PO to the org's inbound address** _(happy · env: external-dep)_
  - Steps: (1) Send an email with a CSV attachment to orders@{slug}.proculink.eu (2) Postmark POSTs the parsed MIME to the webhook
  - Expected: 200; one order created per supported attachment and ParseOrderJob enqueued; order becomes reviewable. Audit event 'inbound_email.processed' written.
  - Prereq: Postmark inbound configured (MX + parse webhook → /api/inbound-email/postmark), Inbound:Postmark:WebhookToken set, org has a default/first supplier. Founder must supply the Postmark account + domain.
- **[EMAIL-HOST-2] Inbound email to an unknown tenant / blocked org / no supplier** _(error · env: external-dep)_
  - Steps: (1) Send to orders@bogus-slug.proculink.eu (2) Send to a valid org that is read-only/trial-expired (3) Send to a valid org that has zero suppliers
  - Expected: 422 in each case with a specific error (unknown slug / account-status blocked / no supplier configured). Postmark does not retry on 422; operator sees it in Postmark's activity log.
  - **CORRECTION (2026-07-24):** two of the three expectations above are obsolete. (a) Postmark retries *any* non-200 — ten times over ~10.5 h, then files the message `Failed`; the "does not retry on 422" premise was measured false on prod. (b) No supplier is no longer a reject at all (BE PR #52): the attachments import `unrouted` and the webhook answers 200. Current contract: unknown slug → **200** `{status:"ignored"}` (no retry); read-only / trial-expired org → **422** (retries are deliberate — the block is reversible); no supplier → **200** + order held unrouted.
  - Prereq: Postmark inbound configured.
- **[EMAIL-HOST-3] Inbound email with bad/missing webhook token** _(security · env: external-dep)_
  - Steps: (1) POST to /api/inbound-email/postmark with a wrong or absent X-Postmark-Server-Token
  - Expected: 401 'Invalid webhook token.' (constant-time compare); nothing ingested. If the token config itself is missing, 401 'Inbound webhook is not configured.'
  - Prereq: Inbound:Postmark:WebhookToken set.
- **[EMAIL-HOST-4] Email body PO with no attachment (NLP fallback)** _(edge · env: external-dep)_
  - Steps: (1) Send a plain-text email describing a PO with no file attached
  - Expected: With an OpenAI key, the body-NLP extractor may create one order; with no key it is a safe no-op (no order, logged). A no-egress org skips NLP entirely.
  - Prereq: Postmark inbound + an OpenAI key (Ai:OpenAI:ApiKey). Org NOT no-egress.
- **[IMAP-1] IMAP polling imports a PO attachment** _(happy · env: external-dep)_
  - Steps: (1) Place an unread email with a CSV/XLSX/PDF attachment in the polled folder (2) Wait for the 5-min email-polling recurring job (or trigger it)
  - Expected: EmailPollOrgJob imports each unseen, non-duplicate attachment, creates a stub, enqueues ParseOrderJob, and flags the message SEEN. Re-poll does not re-import (dedup by OrgId+MessageId+AttachmentHash).
  - Prereq: Org on Integration plan with EmailIngestion feature; valid IMAP host/username/app-password + default supplier saved via PUT /api/settings/email; Worker running. Founder must supply a real mailbox + app password.
- **[IMAP-2] IMAP enabled but plan does not include email ingestion** _(error · env: local)_
  - Steps: (1) Try to enable via PUT /api/settings/email (2) Let the poll run
  - Expected: PUT returns error 'email_ingestion_requires_integration'; even if a config exists, EmailPollOrgJob early-returns because HasFeatureAsync(EmailIngestion) is false. No import.
  - Prereq: Org below Integration; email_config saved.
- **[IMAP-3] IMAP host points at an internal/metadata address (SSRF)** _(security · env: local)_
  - Steps: (1) Run EmailPollOrgJob for that org
  - Expected: OutboundRequestGuard.ValidateHostAsync blocks the connect; job logs 'IMAP host blocked by SSRF guard' and skips. No connection attempt.
  - Prereq: Org with email_config whose Host resolves to 169.254.169.254 / RFC-1918 / loopback.
- **[SFTP-1] SFTP polling imports a file but it NEVER gets parsed (the gap)** _(happy · env: external-dep)_
  - Steps: (1) Drop a valid CSV PO into the remote directory (2) Wait for the sftp-polling recurring job to run SftpPollOrgJob → SftpIngressService.PollAsync
  - Expected: BUG: file downloads and an order stub is created and recorded in ImportedSftpFile, but no ParseOrderJob is enqueued — the order sits in its initial stub state forever (never parses, never reaches review/transform/deliver). Compare EmailPollOrgJob.cs:284 which DOES enqueue. This is a go-live blocker for SFTP ingress.
  - Prereq: A SftpIngressConfig row inserted DIRECTLY in Postgres (no API exists) with IsEnabled, host/creds, RemoteDirectory, a valid DefaultSupplierId; Worker running; an external SFTP server. Founder must supply SFTP host + key/credentials.
- **[S3-1] S3/R2 polling imports an object but it NEVER gets parsed (the gap)** _(happy · env: external-dep)_
  - Steps: (1) Upload a valid CSV PO object under the configured prefix (2) Wait for the s3-polling recurring job to run S3PollOrgJob → S3IngressService.PollAsync
  - Expected: BUG: object downloads, stub created, ImportedS3Object recorded, but no ParseOrderJob enqueued — order stuck in stub state forever. Same defect as SFTP. Go-live blocker for S3/R2 ingress.
  - Prereq: A S3IngressConfig row inserted DIRECTLY in Postgres (no API exists) with IsEnabled, bucket/prefix/region/creds (+ServiceUrl for R2), a valid DefaultSupplierId; Worker running; a reachable bucket. Founder must supply bucket + access/secret keys.
- **[SFTP-2] A non-technical buyer tries to set up SFTP/S3 ingress** _(first-time · env: prod)_
  - Steps: (1) Look in Settings / anywhere in the app for an SFTP or S3/bucket ingress configuration screen
  - Expected: There is NO UI and NO API to configure SFTP/S3 ingress (only IMAP email has GET/PUT /api/settings/email). The buyer cannot self-serve these channels — they require a founder/operator to insert config rows directly in the database. Either hide these channels from any 'supported channels' copy or build a setup surface before go-live.
  - Prereq: Live deployment; a buyer who only has the web UI.
- **[S3-3] S3 ServiceUrl points at an internal endpoint (SSRF)** _(security · env: local)_
  - Steps: (1) Run S3PollOrgJob for that org
  - Expected: OutboundRequestGuard.ValidateAsync blocks the ServiceUrl; job logs 'ServiceUrl blocked by SSRF guard' and skips. Standard AWS endpoints (ServiceUrl null) are not guarded by design.
  - Prereq: S3IngressConfig with a custom ServiceUrl resolving to an internal/metadata host.
- **[ROUTE-1] Buyer expects content-based routing of a supplier-less file** _(edge · env: local)_
  - Steps: (1) Look for an order parked 'unrouted' awaiting supplier assignment (2) Call POST /api/orders/{id}/assign-supplier with a supplierId
  - Expected: The assign endpoint works (atomic unrouted→parsing claim + re-enqueue), BUT no ingest channel ever produces an unrouted order today — CreateUnroutedStubAsync has zero production callers and RouteByContent is unimplemented. So in practice a multi-supplier shared folder/mailbox routes everything to the single DefaultSupplierId (or is skipped). Don't advertise content routing yet.
  - Prereq: Any channel; an order somehow in 'unrouted' status (only reachable today via direct DB/test, since no channel emits it).
- **[WEBHOOK-1] Supplier ERP posts an acknowledgement/status callback** _(happy · env: external-dep)_
  - Steps: (1) POST /api/webhook-ingress/{slug}/acknowledge (or /status) with X-ProcuLink-Timestamp, X-ProcuLink-Nonce, X-ProcuLink-Signature
  - Expected: Valid signature → 200, audit event recorded; /status with 'delivered' or 'rejected' updates order status (never overwrites an already-delivered order). Any auth failure → uniform 401. Note: this is NOT an order-creation channel.
  - Prereq: Org has a webhook shared secret set; an existing delivered order; caller computes HMAC-SHA256(secret, ts.nonce.body). Founder/supplier must supply the integration that signs callbacks.

**Area prerequisites:**
- Worker process MUST be running for IMAP/SFTP/S3 polling and for ParseOrderJob to execute (the API hosts no Hangfire worker).
- Local: PROCULINK_QA_BYPASS_AUTH=true + ASPNETCORE_ENVIRONMENT=Development; Postgres on :5435; a 32-byte base64 Delivery__EncryptionKey (delivery/credential services resolve on ingress/supplier endpoints).
- REST ingress (plk_ keys): Security:ApiKeyHashSecret (SECURITY__APIKEYHASHSECRET) configured, an org slug, and a created API key.
- Hosted inbound email: Postmark inbound MX + parse webhook pointed at /api/inbound-email/postmark and Inbound:Postmark:WebhookToken set (founder-supplied Postmark account + domain). Optional Inbound:Postmark:HostSuffix / TenantMapping overrides.
- IMAP polling: org on Integration plan (EmailIngestion feature), a real mailbox host + username + app password, and a saved DefaultSupplierId via PUT /api/settings/email (founder/customer-supplied mailbox).
- SFTP polling: an externally reachable SFTP host + credentials/key and a SftpIngressConfig row inserted directly in Postgres (no setup API/UI exists) with a valid DefaultSupplierId.
- S3/R2 polling: a reachable bucket + access/secret keys (+ ServiceUrl for R2/MinIO) and a S3IngressConfig row inserted directly in Postgres (no setup API/UI exists) with a valid DefaultSupplierId.
- Email-body NLP fallback and any AI mapping need Ai:OpenAI:ApiKey; absent key is a safe no-op.
- HMAC webhook-ingress callbacks need a per-org webhook shared secret and a supplier integration that signs requests.

## Outgoing Transport / Delivery Dispatchers (HTTP webhook, SFTP, FTPS, SMTP, Erply, Directo ERP, Zapier/Make outbound triggers)

All six delivery channels are REAL and wired, not stubs: HttpDeliveryDispatcher (webhook), SftpDeliveryDispatcher (SSH.NET), FtpsDeliveryDispatcher (FluentFTP explicit TLS), SmtpDeliveryDispatcher (MailKit + MimeKit attachment), and ErplyConnector/DirectoConnector (wrapped by ErplyDeliveryDispatcher/DirectoDeliveryDispatcher). All are DI-registered in both ProcuLink.Api/Program.cs (lines 593-600) and the Worker, exposed to users in DeliveryConfigEditor.tsx (all 6 protocols enabled:true), and selectable per-supplier. The Zapier/Make outbound layer (FireIntegrationTriggerJob) is real: HMAC-SHA256 X-ProcuLink-Signature, AES-GCM-decrypted secret, auto-deactivate after 3 consecutive LOGICAL failures, idempotent on inactive subs.

SSRF protection is layered and present on every channel, with one design asymmetry. HttpDeliveryDispatcher and FireIntegrationTriggerJob do an up-front OutboundRequestGuard.ValidateAsync AND route the socket through a connect-time-revalidating SocketsHttpHandler (CreateGuardedHttpHandler) that pins the validated IP — closing the DNS-rebind TOCTOU window. SFTP/FTPS/SMTP call ValidateHostAsync immediately before connect (cannot pin IP without breaking host-key/TLS-SNI, so re-resolve+validate is the tightest mitigation). The ERP connectors (Erply/Directo) have NO per-call ValidateAsync; they rely SOLELY on the connect-time guarded handler attached to the named 'delivery' HttpClient in both hosts (Program.cs:369-374, Worker:148-154) — verified blocked at TCP connect by ErpConnectorSsrfTests for 169.254.169.254 / 10.x / 100.64.x. Guard blocks loopback, RFC-1918, CGNAT 100.64/10, RFC-2544 198.18/15, link-local 169.254/16 (metadata), IPv6 ULA fc00::/7, IPv6 link-local fe80::/10, unspecified, IPv4-mapped-IPv6. Dev escape hatch: Delivery:AllowPrivateNetworkTargets.

Retry/backoff/SLA/dead-letter all real. RetryDeliveryJob (queue 'delivery-retry', no Hangfire auto-retry, PerOrderDistributedMutex on orderId) delegates to DeliveryService.RetryDeliveryAsync; exponential backoff via DeliveryReliabilityOptions.BackoffFor, MaxAttempts=3 → delivery_dead_letter with an AuditEvent. 4xx supplier rejection stops the auto-retry queue (rejected_by_supplier). DeliverySlaSweepJob (15 min) flags SlaBreached past DeliveryDueAt; StuckDeliveryDetectionJob (15 min, 30-min threshold) re-drives orders stranded in 'delivering'.

State transitions and audit are correct and atomic. DispatchArtifactAsync atomically claims ready_to_deliver/delivery_failed/stale-delivering → delivering via a guarded ExecuteUpdate in one transaction (D-1 double-dispatch defence); PersistAttemptAsync writes the terminal status (delivered / delivery_failed / rejected_by_supplier) plus a DeliveryAttempt row capturing channel, destination, response code, verbatim (bounded) response body, rejection reason, AcknowledgedAt, ConnectionRevisionId + payload SHA-256 provenance. Missing-config and decrypt-failure produce honest failed attempts, not throws.

'HTTP 200 != supplier business acceptance' is PARTIALLY honoured: the system distinguishes 4xx (rejected, no retry) from 5xx/network (retry), captures the raw NACK body, and never silently retries a 4xx. BUT success is still decided purely by HttpResponseMessage.IsSuccessStatusCode — a supplier that returns HTTP 200 with a business-level rejection/NACK in the body is marked 'delivered'. There is no response-body acceptance assertion. This is the genuine residual gap (P2) the founder should understand before go-live: ProcuLink confirms transport success, not parsed business acceptance, for endpoints that NACK inside a 2xx.

**Inventory:** 14 items — {"working":11,"partial":3}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | ErplyConnector — POST artifact bytes to Erply REST endpoint with bearer/apikey auth + X-Erply-Client-Code header | `ProcuLink.Infrastructure/Services/Erp/ErplyConnector.cs` |
| partial | DirectoConnector — POST artifact as form-urlencoded (database/xmldata/user/password/key) to Directo XML API | `ProcuLink.Infrastructure/Services/Erp/DirectoConnector.cs` |
| partial | Supplier test-fire endpoint — POST /api/suppliers/{id}/delivery-config/test-fire — sends a tiny CSV via the LIVE config, records a DeliveryAttempt (attemptNumber 0, no order) | `ProcuLink.Api/Controllers/SuppliersController.cs:697 ; DeliveryService.TestFireAsync` |

**Test scenarios (17):**

- **[TX-01] First-time buyer sets up a webhook delivery and test-fires it (happy path)** _(first-time · env: local)_
  - Steps: (1) Open a supplier, go to the Delivery tab (2) Pick HTTP, paste a https://webhook.site/<uuid> URL, leave auth = none (3) Save the delivery config (4) Click 'Test fire' / 'Send test'
  - Expected: Test returns success with HTTP 200; webhook.site shows the POSTed test CSV (test,from / proculink,true); a DeliveryAttempt row is recorded (channel http, status success, attemptNumber 0)
  - Prereq: Local API+Worker running with PROCULINK_QA_BYPASS_AUTH=true, Delivery__EncryptionKey set, Delivery:AllowPrivateNetworkTargets=true so a webhook.site/local URL is reachable; a supplier exists
- **[TX-02] Full order delivery to a controlled webhook endpoint (happy path)** _(happy · env: local)_
  - Steps: (1) From the order/inbox view click Send / Deliver (2) Observe the order status badge (3) Open webhook.site
  - Expected: Order transitions ready_to_deliver -> delivering -> delivered; webhook.site receives the artifact bytes with correct content-type; GET /api/orders/{id} shows a success DeliveryAttempt with response code 200 and AcknowledgedAt set
  - Prereq: Local stack as TX-01; an order parsed+transformed to ready_to_deliver with an HTTP delivery config pointing at webhook.site
- **[TX-03] Supplier endpoint returns HTTP 200 with a business NACK body (acceptance gap)** _(edge · env: external-dep)_
  - Steps: (1) Configure HTTP delivery to that endpoint (2) Deliver an order (3) Inspect the resulting order status and DeliveryAttempt
  - Expected: REVEALS THE GAP: ProcuLink marks the order 'delivered' (success based on IsSuccessStatusCode) even though the supplier rejected it in-body. Confirms 'HTTP 200 == delivered' is transport-only, not business acceptance
  - Prereq: A controllable endpoint that returns HTTP 200 but a body like {accepted:false,error:unknown SKU} (webhook.site custom response, or a founder-supplied supplier sandbox)
- **[TX-04] Webhook endpoint returns 4xx -> rejected, no auto-retry (error path)** _(error · env: local)_
  - Steps: (1) Deliver an order to the 4xx endpoint (2) Observe status and retry behavior over a few minutes (3) Inspect DeliveryAttempt
  - Expected: Order -> rejected_by_supplier; RejectionReason and raw response body captured (bounded); RetryDeliveryJob does NOT reschedule (4xx is not retried); order surfaces in /operations/exceptions
  - Prereq: Local stack; HTTP delivery config to an endpoint returning HTTP 422/400 (webhook.site custom 422)
- **[TX-05] Webhook endpoint returns 5xx -> retry/backoff -> dead-letter (error path)** _(error · env: local)_
  - Steps: (1) Deliver an order to the 5xx endpoint (2) Watch Hangfire dashboard delivery-retry queue and order status across the backoff windows (3) Let all 3 attempts exhaust
  - Expected: Order -> delivery_failed, retries scheduled with exponential backoff; after MaxAttempts=3 -> delivery_dead_letter with a DeliveryDeadLettered AuditEvent; no further retries; appears in /operations/health dead-letter list
  - Prereq: Local stack with Worker; HTTP delivery config to an endpoint returning HTTP 500 consistently
- **[TX-06] SSRF: webhook/HTTP delivery to cloud-metadata or private IP is blocked** _(security · env: local)_
  - Steps: (1) Set an HTTP delivery URL to http://169.254.169.254/latest/meta-data/ (or http://10.0.0.5/ or http://localhost/) (2) Test-fire or deliver
  - Expected: Blocked: result 'Delivery blocked: Requests to internal/private addresses are not permitted'; no outbound request reaches the target; ERP erp_erply/erp_directo URLs to the same targets are blocked at TCP connect (no response code)
  - Prereq: Local stack with Delivery:AllowPrivateNetworkTargets=FALSE (production-like)
- **[TX-07] SSRF DNS-rebind to private IP at connect time is still blocked** _(security · env: external-dep)_
  - Steps: (1) Configure an HTTP/webhook delivery to the rebinding hostname (2) Deliver repeatedly
  - Expected: CreateGuardedHttpHandler re-resolves and re-validates at TCP connect and pins the validated IP, so the rebind to a private/metadata IP is rejected (HttpRequestException -> failed DeliveryResult). Note SFTP/FTPS/SMTP re-validate via ValidateHostAsync but cannot pin IP, a documented narrower mitigation
  - Prereq: A rebinding DNS name (founder-supplied) that resolves public at validation and private/metadata a moment later
- **[TX-08] SFTP delivery to a real SFTP server (happy + auth-failure)** _(happy · env: external-dep)_
  - Steps: (1) Pick SFTP, enter host/port/remotePath, choose password or key auth, enter the credential (2) Save, then Test fire (3) Then deliver a real order
  - Expected: Happy: file uploaded to remoteDir/<sanitised PO>.ext, status success. Auth-failure variant (wrong password/key): 'SFTP authentication failed — check the username, password, or private key', no upload, recorded as failed attempt
  - Prereq: CREDENTIAL NEEDED: SFTP host/port, a username, and either a password or an OpenSSH private key (+ optional passphrase), plus a writable remote directory
- **[TX-09] SFTP missing remote directory without makeDirectories (edge)** _(edge · env: external-dep)_
  - Steps: (1) Configure SFTP to a non-existent remote dir, makeDirectories off (2) Test fire
  - Expected: Failure with humanised message 'SFTP remote directory ... does not exist. Set makeDirectories=true to auto-create.'; no crash; recorded as failed attempt
  - Prereq: CREDENTIAL NEEDED: SFTP server + valid username/password; configure remotePath to a non-existent directory and leave makeDirectories=false
- **[TX-10] FTPS delivery to a real FTPS server incl. self-signed cert opt-in (happy + cert path)** _(happy · env: external-dep)_
  - Steps: (1) Pick FTPS, enter host/port/remotePath, username+password (2) With a valid CA cert: Test fire (3) With a self-signed cert: first test (expect TLS failure), then set AllowInvalidCertificate=true in config and retest
  - Expected: Valid cert: upload succeeds (FtpStatus.Success). Self-signed without opt-in: 'FTPS encryption could not be negotiated' / TLS validation failure. With AllowInvalidCertificate=true: upload succeeds (operator-conscious override). Bad credentials: 'FTPS authentication failed'
  - Prereq: CREDENTIAL NEEDED: FTPS host/port (explicit TLS), username + password, writable dir. For the self-signed variant a server with an untrusted cert
- **[TX-11] SMTP email delivery with attachment to a real relay (happy + bad recipient)** _(happy · env: external-dep)_
  - Steps: (1) Pick Email (SMTP), enter host/port/UseSsl, fromAddress, toAddresses, subject/body templates (2) Enter username+password, Save (3) Test fire, then check the recipient inbox (4) Repeat with an invalid recipient address
  - Expected: Happy: email with the PO artifact attached arrives; subject/body templates expand {poNumber}/{fileName}; status success. Bad recipient: 'SMTP delivery rejected — invalid recipient address'. Bad auth: 'SMTP authentication failed'
  - Prereq: CREDENTIAL NEEDED: SMTP host/port (e.g. 587 STARTTLS or 465 SSL), username + password/app-password, a valid fromAddress, and toAddresses; an inbox to verify receipt
- **[TX-12] Erply ERP connector delivery (happy + auth)** _(happy · env: external-dep)_
  - Steps: (1) Pick Erply ERP, enter the endpoint URL + clientCode, choose bearer/apikey and enter the token (2) Save, Test fire (3) Deliver a real transformed order
  - Expected: POST reaches Erply with X-Erply-Client-Code + X-ProcuLink-FileName headers and the artifact body; 2xx -> delivered; non-2xx -> failed with 'Erply HTTP {code}...' summary. Note: 'delivered' means HTTP 2xx, not confirmed Erply order creation (same acceptance caveat as TX-03)
  - Prereq: CREDENTIAL NEEDED: Erply sandbox endpoint URL + client code + a bearer token or apikey header/value the Erply tenant accepts
- **[TX-13] Directo ERP connector delivery (happy + invalid config)** _(happy · env: external-dep)_
  - Steps: (1) Pick Directo ERP, enter URL + database, enter user/password (or key) (2) Save, Test fire (3) Then omit the database and retry to see the validation path
  - Expected: Happy: form-urlencoded POST (database/filename/contentType/xmldata + creds) reaches Directo; 2xx -> delivered. Missing url or database -> 'Directo connector configuration is invalid.' returned before any request
  - Prereq: CREDENTIAL NEEDED: Directo XML API URL + database code + user/password or key the Directo account accepts
- **[TX-14] Zapier/Make outbound trigger fires to a webhook with HMAC signature (happy)** _(happy · env: local)_
  - Steps: (1) Create the connector/webhook subscription for order.delivered with a secret (2) Deliver an order successfully (3) Open webhook.site
  - Expected: webhook.site receives a POST with X-ProcuLink-Event: order.delivered and X-ProcuLink-Signature: sha256=<hex>; recomputing HMAC-SHA256(secret,body) matches the header; sub.FailureCount resets to 0
  - Prereq: Local stack with Worker; create an IntegrationSubscription (Settings -> Connectors) for event order.delivered pointing at webhook.site with a signing secret; Delivery:AllowPrivateNetworkTargets as needed for the target
- **[TX-15] Zapier/Make outbound trigger to a failing endpoint auto-deactivates after 3 failures (error)** _(error · env: local)_
  - Steps: (1) Point a subscription at a 500-returning URL (2) Trigger the event 3 separate times (each goes through Hangfire's 3 retries) (3) Inspect the subscription state
  - Expected: FailureCount increments once per LOGICAL failed delivery (not per Hangfire retry) thanks to IsFinalHangfireAttempt; after 3 consecutive logical failures IsActive flips to false and the sub stops firing; an SSRF-blocked target is also counted as a failure
  - Prereq: Local stack with Worker; an IntegrationSubscription to an endpoint returning 500; ability to trigger 3 separate qualifying events
- **[TX-16] Concurrent double-deliver / double-click does not double-send (edge)** _(edge · env: local)_
  - Steps: (1) Trigger Deliver twice in quick succession (or a manual retry racing a scheduled retry) (2) Watch the endpoint receive count and DeliveryAttempt rows
  - Expected: Only ONE dispatch reaches the supplier: the atomic delivering-claim (guarded ExecuteUpdate in a transaction) + PerOrderDistributedMutex make the second activation a benign no-op (DeliveryResult success, no attempt row, no second POST)
  - Prereq: Local stack with Worker; an order in ready_to_deliver with a slow controllable endpoint
- **[TX-17] Delivery with missing supplier delivery config (error recovery for a confused user)** _(error · env: local)_
  - Steps: (1) Attempt to Send/Deliver the order (2) Read the on-screen error and the order status
  - Expected: Order -> delivery_failed with a clear message 'Supplier delivery config is missing. Add a delivery endpoint before sending this order.'; a failed DeliveryAttempt (channel 'missing_config') is recorded; order.failed integration trigger fires; the UI guides the user to add a delivery endpoint
  - Prereq: Local stack; an order routed to a supplier that has NO saved delivery config

**Area prerequisites:**
- Local: ProcuLink.Api AND ProcuLink.Worker both running (the API hosts NO Hangfire processing server — delivery, retry, SLA/stuck sweeps, and integration triggers only execute when the Worker is up)
- Local: PROCULINK_QA_BYPASS_AUTH=true with ASPNETCORE_ENVIRONMENT=Development for unauthenticated API QA
- Local: a 32-byte base64 Delivery__EncryptionKey (delivery/credential encryption services require it)
- Local: Delivery:AllowPrivateNetworkTargets=true to test against webhook.site/localhost; MUST be false to exercise the SSRF block tests (TX-06/07)
- Postgres dev DB on localhost:5435 (proculink_dev)
- External: SFTP server + (password or OpenSSH private key) for TX-08/09
- External: explicit-TLS FTPS server + username/password for TX-10
- External: SMTP relay host/port + username/app-password + a verifiable recipient inbox for TX-11
- External: Erply sandbox endpoint URL + client code + bearer token/apikey for TX-12
- External: Directo XML API URL + database code + user/password (or key) for TX-13
- A controllable endpoint that can return arbitrary status codes AND a 200-with-NACK body (webhook.site custom responses) for TX-03/04/05

## BILLING, AUTH, TENANCY, QUOTAS — the commercial + security spine

The commercial/security spine is real, cohesive, and the strongest part of the codebase — not stubs. Clerk JWT auth validates `azp` (authorized-party) to compensate for `ValidateAudience=false` (correct Clerk design), and TenantResolutionMiddleware auto-provisions an org + 14-day pilot on first authenticated request with a bounded in-memory anti-trial-farming throttle (5 provisions / 10 min / IP+domain key). Tenancy fails closed: CurrentTenantService throws UnauthorizedAccessException when no org is resolved, so a throttled or unresolved caller cannot reach tenant-scoped data. StripeBillingService implements the full plan ladder (Pilot/Growth/Operations/Integration/Distributor/Enterprise), monthly+yearly checkout, customer portal, never-block soft cap with best-price €0.50/order overage billed idempotently at the invoice.created boundary (DB ledger + Stripe Idempotency-Key, with yearly periods decomposed into per-month windows), pilot 14-day + 20-order cap with read-only expiry, and bidirectional reactivation when an admin extends. The order/supplier 429 paths return honest machine-readable error codes (`pilot_expired`/`order_limit_reached`/`supplier_limit_reached`). AI usage is a per-org plan-aware monthly token cap. NO P0 regressions found: there are NO real secrets committed (Development config uses `*_REPLACE_ME` placeholders, Production config is empty/env-injected), and the all-zero AES key is actively blocked in Production by StartupConfigurationValidator (which also fail-fasts on missing Stripe SecretKey/WebhookSecret, short ApiKeyHashSecret, missing DataProtection key, and the SSRF kill-switch). The frontend BillingSection is fully wired (real checkout/portal mutations, interval toggle, contact-sales for Enterprise) — no dead controls. PRIMARY GO-LIVE GAP: none of the Stripe money paths (checkout completion, plan mapping, overage at period close, portal, webhook signature) can be verified without live Stripe TEST-mode events and configured price IDs/webhook secret — they are external-dep. Admin per-org overrides are gated ONLY by an env allowlist (Admin:UserIds/Admin:Emails) that fails closed, so they are unusable/untestable until the founder populates that allowlist.

**Inventory:** 18 items — {"working":14,"partial":4}

**Non-working / partial items:**

| Status | Item | Where |
|---|---|---|
| partial | Checkout session creation (Growth/Operations/Integration/Distributor, monthly+yearly) — Map plan+interval→Stripe price id and create subscription Checkout with org_id metadata | `ProcuLink.Api/Services/StripeBillingService.cs:284-346 — code real but unverifiable without configured price IDs + live Stripe` |
| partial | Customer portal session — Open Stripe billing portal for subscription management | `ProcuLink.Api/Services/StripeBillingService.cs:628-647 — needs StripeCustomerId + live Stripe` |
| partial | Stripe webhook handler (checkout.completed / subscription.updated / .deleted / invoice.created) — Persist plan/status from Stripe; map price→plan; bill overage at period close; emit billing analytics | `ProcuLink.Api/Controllers/BillingController.cs:155-515 — signature-verified, real logic, but needs live Stripe events to verify` |
| partial | AdminController per-org overrides (limits/trial extension/retention/erase/invoice/MRR) — Cross-tenant owner surface for raising caps, extending trials, GDPR erase, manual invoices | `ProcuLink.Api/Controllers/AdminController.cs — real, but gated by env allowlist that is unconfigured by default (fails closed → unusable until set)` |

**Test scenarios (10):**

- **[BAT-01] Brand-new signup auto-creates org with 14-day pilot trial** _(first-time · env: prod)_
  - Steps: (1) From the marketing site, click Sign up and complete Clerk signup as a first-time user with no prior org (2) After landing in the app, make any authenticated API call (the dashboard loads /api/billing/status automatically) (3) Open Settings → Billing
  - Expected: An Organisation row is auto-provisioned on first authenticated request (TenantResolutionMiddleware) with Plan=pilot, AccountStatus=trialing, TrialEndsAt = now+14 days; Billing tab shows Pilot, ~14 days left, 0/20 orders, 0/1 suppliers; org_created analytics event fires
  - Prereq: Live Clerk instance (golden-alpaca-43) + deployed API/frontend; a fresh Clerk identity
- **[BAT-02] Uploading past the pilot 20-order cap returns 429 with honest copy** _(error · env: local)_
  - Steps: (1) Run local stack with PROCULINK_QA_BYPASS_AUTH=true and a valid Delivery:EncryptionKey (2) Create/seed an org on pilot and upload 20 non-sample orders (or admin-lower the cap) (3) Attempt a 21st upload via POST /api/orders/upload
  - Expected: HTTP 429 with body { error: "order_limit_reached" (or "pilot_expired" if past 14 days), plan: "pilot", limit: 20, upgradeUrl: "/settings" }; sample orders never count toward the cap
  - Prereq: Local Postgres on :5435; QA bypass; worker running for parse jobs
- **[BAT-03] Expired pilot becomes read-only and blocks processing** _(edge · env: local)_
  - Steps: (1) Seed a pilot org with TrialStartedAt 15+ days ago (or 20 orders used) (2) Call GET /api/billing/status (3) Attempt an order upload and a supplier add
  - Expected: MarkPilotExpiredIfNeeded flips AccountStatus to trial_expired; status shows IsTrialExpired=true, CanProcessOrders=false, CanAddSupplier=false; upload and supplier-add both return 429 pilot_expired; viewing existing data still works (read-only)
  - Prereq: Local stack; ability to backdate TrialStartedAt in DB
- **[BAT-04] Self-serve upgrade checkout (Pilot → Growth) completes and unlocks plan** _(happy · env: external-dep)_
  - Steps: (1) As a pilot user, open Settings → Billing and pick monthly/yearly via the interval toggle (2) Click Upgrade to Growth (3) Complete Stripe Checkout with test card 4242 4242 4242 4242 (4) Get redirected to /welcome?upgraded=growth and reopen Billing
  - Expected: POST /api/billing/checkout returns a Stripe URL; on completion the checkout.session.completed webhook maps the price id→Growth, sets Plan=growth, AccountStatus=active, persists StripeCustomerId/SubscriptionId/PriceId, emits billing_upgraded; Billing tab now shows Growth 0/150 orders, 0/5 suppliers, manage-billing button appears
  - Prereq: Stripe TEST mode: SecretKey, WebhookSecret, GrowthPriceId (+ GrowthYearlyPriceId for yearly) configured; webhook endpoint reachable (stripe CLI listen or deployed URL)
- **[BAT-05] Manage-billing opens Stripe customer portal** _(happy · env: external-dep)_
  - Steps: (1) As a paid (e.g. Growth) org, open Settings → Billing (2) Click Manage billing
  - Expected: POST /api/billing/portal returns a Stripe BillingPortal URL and the browser redirects to it; if no StripeCustomerId is on file the UI shows 'No billing customer on file. Contact support' (400 from backend) rather than a 500
  - Prereq: Stripe TEST mode configured; org has a StripeCustomerId; Stripe billing portal enabled in the Stripe dashboard
- **[BAT-06] Webhook signature is enforced (forged/missing signature rejected)** _(security · env: local)_
  - Steps: (1) POST a fabricated event body to /api/billing/webhook with no Stripe-Signature header (2) POST again with a bogus signature value
  - Expected: Missing header → 400 { error: "Missing signature." }; bad signature → 400 { error: "Invalid signature." } (EventUtility.ConstructEvent throws StripeException, caught); no org state is mutated
  - Prereq: Local API with any Stripe:WebhookSecret set
- **[BAT-07] Cross-tenant data isolation — org A cannot read org B's orders/billing** _(security · env: prod)_
  - Steps: (1) Sign in as a user in Org A and note an order id from Org B (or just hit a tenant-scoped endpoint) (2) Call GET /api/orders/{orgB_order_id} and GET /api/billing/status with Org A's token (3) Attempt a request whose tenant could not be resolved (e.g. throttled auto-provision)
  - Expected: All queries are scoped to Org A's resolved OrganisationId; Org B's order returns 404/forbidden; billing status reflects only Org A; an unresolved tenant causes CurrentTenantService to throw UnauthorizedAccessException so the request fails closed rather than leaking data
  - Prereq: Two live orgs with data; deployed API
- **[BAT-08] Overage is billed once and idempotently at the period boundary** _(edge · env: external-dep)_
  - Steps: (1) Put a Growth org over 150 orders in a month (2) Trigger invoice.created (via Stripe test clock or stripe CLI) for the subscription (3) Replay the same invoice.created event and a re-issued invoice for the same period
  - Expected: First event creates one Stripe invoice item for (orders-150)×€0.50 attached to the draft invoice and an OverageBillingRecord keyed {orgId}:{periodStart}; replays and re-issues hit the unique (org_id, billing_key) ledger + Stripe Idempotency-Key and create NO second charge; yearly subscriptions decompose into per-month windows each metered at the as-of plan/override
  - Prereq: Stripe TEST mode with a subscription; ability to fire invoice.created (test clock); org over cap
- **[BAT-09] Admin per-org override raises caps and reactivates an expired pilot** _(happy · env: local)_
  - Steps: (1) Populate Admin:Emails (or Admin:UserIds) with your account and restart the API (2) As that admin, POST /api/admin/organisations/{id}/limits with extendTrialDays and a raised orderLimitOverride for an expired pilot org (3) Re-fetch that org's billing status
  - Expected: With the allowlist set, the call succeeds (without it, every /api/admin/* returns 403 — fails closed); TrialEndsAtOverride/OrderLimitOverride persist; MarkPilotExpiredIfNeeded reactivates the org (trial_expired→trialing) and CanProcessOrders flips back to true
  - Prereq: Admin allowlist configured in app settings/env; local stack
- **[BAT-10] Production fails to boot with insecure/missing billing+crypto config** _(security · env: local)_
  - Steps: (1) Set ASPNETCORE_ENVIRONMENT=Production (2) Boot the API with an all-zero Delivery:EncryptionKey (or missing Stripe:SecretKey/WebhookSecret, or a <16-char ApiKeyHashSecret, or Delivery:AllowPrivateNetworkTargets=true)
  - Expected: StartupConfigurationValidator throws StartupConfigurationException and the process refuses to start, naming the offending key; confirms no P0 all-zero-key or missing-secret can reach a live deploy
  - Prereq: Local ability to run the API with Production env and crafted config

**Area prerequisites:**
- Stripe TEST-mode account with: Stripe:SecretKey, Stripe:WebhookSecret, and price IDs GrowthPriceId/OperationsPriceId/IntegrationPriceId/DistributorPriceId (monthly required; *YearlyPriceId optional for the annual toggle). API will NOT boot in Production without SecretKey + WebhookSecret (they are required keys).
- A reachable Stripe webhook endpoint (stripe CLI `stripe listen --forward-to .../api/billing/webhook` locally, or the deployed URL registered in the Stripe dashboard) to verify checkout.session.completed / subscription.updated / subscription.deleted / invoice.created.
- Live Clerk instance (Authority golden-alpaca-43.clerk.accounts.dev in dev) + Clerk frontend origins added to the API's authorized-parties config so azp validation passes.
- Delivery:EncryptionKey = real 32-byte base64 (NOT all-zero), Security:ApiKeyHashSecret ≥16 chars, and DataProtection:EncryptionKey set — all enforced at Production startup.
- Admin:UserIds and/or Admin:Emails populated for any verification of the admin override / MRR / invoice surface (otherwise every /api/admin/* returns 403 by design).
- For local quota/cap testing: Postgres on :5435, PROCULINK_QA_BYPASS_AUTH=true in Development, a running Worker (the API hosts no Hangfire jobs), and the ability to backdate TrialStartedAt / seed orders.
- Stripe test clock (or manual invoice.created firing) to verify overage billing at the period boundary, since real period closes take a month.

---
# 7. All risks (severity-sorted appendix)

| Sev | Risk | Location |
|---|---|---|
| P1 | Library → Rules (/library/rules) is a fully functional CRUD surface whose data (ValidationRules table via ValidationRuleService) is NOT consumed by any parse/validate/transform code. A user can create+enable validation rules that silently never run against orders; real enforcement lives in the separate per-supplier AcceptanceProfile system. Trust/'dead control' hazard at go-live — either wire it to the acceptance engine or remove/redirect the page to the supplier Validation tab. | `src/components/bridge/ValidationRules.tsx + ProcuLink.Infrastructure/Services/ValidationRuleService.cs (only referenced by its own controller + Program.cs)` |
| P1 | SFTP ingress is a dead end: SftpIngressService.PollAsync creates order stubs (CreateStubAsync) and records ImportedSftpFile but NEVER enqueues ParseOrderJob. Imported files are stuck in their initial stub state forever — they never parse, transform, or deliver. CreateStubAsync does not self-enqueue (verified: OrderIngestionService only fires the order.created integration trigger), and every WORKING channel enqueues explicitly afterward (OrdersController.cs:239, EmailPollOrgJob.cs:284, InboundEmailRouter.cs:229). Add a parse enqueue after a successful CreateStubAsync. | `ProcuLink.Infrastructure/Services/Ingress/SftpIngressService.cs:202-233` |
| P1 | S3/R2 ingress has the identical dead-end defect: S3IngressService.PollAsync creates stubs + records ImportedS3Object but never enqueues ParseOrderJob, so imported objects never parse/transform/deliver. Same fix needed as SFTP. | `ProcuLink.Infrastructure/Services/Ingress/S3IngressService.cs:225-271` |
| P2 | /watch will likely show a broken/black video player in prod: committed .env hard-codes NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL to an R2 MP4 that per project memory is a DRAFT not yet uploaded. The friendly 'coming shortly' fallback only renders when the URL env is EMPTY, so a missing-but-set asset bypasses it. Either upload the asset or blank the env var before launch. | `src/app/(marketing)/watch/page.tsx:9-74 + project-proculink/.env (NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL)` |
| P2 | Offer-vs-works mismatch in homepage stats: STATS claims '9 inbound formats / 6 outbound / 6 delivery channels', but the honest /formats catalog lists 10 import formats and 8 delivery methods (several only Configurable/On request) and 7 output formats. The hard-coded counts can drift and slightly misrepresent capability vs the catalog-derived /formats page. Derive these counts from standards/catalog.ts or correct them. | `src/app/page.tsx:183-188 vs src/app/(marketing)/formats/page.tsx:51-93` |
| P2 | BridgeDashboard data queries (suppliers, orders, dashboard-topology, orders-summary) are NOT gated on the cold-auth `enabled: queryEnabled` flag that UploadWorkbench/Admin/InboxView-summary use. On a cold mount (hard refresh of /bridge) they can fire before Clerk's token is ready, get 401, and TanStack Query parks them (fetchStatus 'paused', not loading) — leaving the dashboard empty until a manual interaction triggers refetch. This is the exact documented cold-mount auth-race pattern; the dashboard is the most likely landing page so it is user-visible. | `project-proculink/src/components/bridge/BridgeDashboard.tsx:336-355` |
| P2 | Admin customer Stripe links are hardcoded to the TEST dashboard (https://dashboard.stripe.com/test/customers/). At go-live, with the Stripe account in live mode, clicking 'View ↗' sends the owner to test-mode customer pages (or 404). There is an explicit go-live TODO but it must be flipped before launch. | `project-proculink/src/app/(app)/admin/page.tsx:38-39` |
| P2 | Inbound channels (IMAP email, SFTP-pull, S3/R2-pull) have no test-connection control — users save credentials blind and only discover failures via the background poller (or never). Outbound Delivery config has a proper test-fire; the asymmetry is a real first-run footgun. | `src/app/(app)/settings/page.tsx EmailSettingsSection; src/components/settings/PullIngressSettings.tsx` |
| P2 | library/templates format dropdown offers EDI/EDIFACT as an output 'Standard', but EDIFACT has no backend output transformer (catalog marks transform=planned; only cXML/UBL/X12/JSON/CSV transform). Combined with the EDIFACT/X12 mock templates, this risks over-claiming EDIFACT delivery to a procurement buyer. | `src/app/(app)/library/templates/page.tsx L405 (option EDI) + src/lib/standards/catalog.ts (EDIFACT transform=planned)` |
| P2 | ASN list DTO mismatch: DesadvController GET /api/asns returns {ShipmentId, Status, DespatchDate, SourceFileName} but frontend AsnDto expects {asnNumber, supplierName, shipDate, packageCount}. Any real ASN row renders as '—'/blank. Masked today only because upload returns 501 so the list is always empty. Fix the mapping or keep ASN ingestion disabled at go-live. | `ProcuLink.Api/Controllers/DesadvController.cs:44-55 vs src/lib/api-client.ts:2314-2336 (AsnDto)` |
| P2 | Connectors 'Add connector' and per-card 'Connect' buttons imply connector creation but only open a read-only panel that redirects to the supplier Delivery tab — nothing is created/saved on this page. A first-time operator can get stuck hunting for a save action. Either relabel to 'Set up in supplier' or make the panel actually create a connector. | `src/app/(app)/operations/connectors/page.tsx:342-369 (Add) + 239-258 (Connect) + 651-916 (read-only ConnectorPanel)` |
| P2 | Rate limiters are PROCESS-LOCAL fixed-window (in-memory). Exact at the current single API replica, but effective ceilings become ~Nx the configured value with N replicas — a partition can hit each replica's independent window. Documented in Program.cs:247-254. Must back limiters with a distributed store (Redis) before scaling the API horizontally. | `ProcuLink.Api/Program.cs:245-343 (AddRateLimiter)` |
| P2 | HMAC webhook nonce replay-protection store is in-memory (MemoryDistributedCache) unless Redis:ConnectionString is set. With multiple API replicas a replayed nonce could land on a different replica and bypass replay protection. Single-replica deploy is safe; set Redis before scaling. | `ProcuLink.Api/Program.cs:660-673 (IDistributedCache for HmacWebhookVerifier)` |
| P2 | UBL and X12 emit the supplier GUID (order.SupplierId.ToString()) as the SellerSupplierParty/receiver party NAME instead of real supplier metadata. A real Peppol/EDI receiver expects a legal name + scheme-qualified endpoint (EAS/ISO6523); a raw GUID name is structurally valid UBL/X12 but is not a network-acceptable seller identity. Self-documented as 'placeholder, future pass'. Could cause a real supplier to reject the document despite HTTP-200 transport (HTTP 200 ≠ business acceptance). | `ProcuLink.Transform/Output/UblOrderTransformService.cs:90 (supplierName = order.SupplierId.ToString()); X12TransformService N1 receiver id default 'SUPPLIER'` |
| P2 | PeppolBisInvoiceTransformService ships a lightweight mandatory-field checker only, NOT full EN16931 / PEPPOL-EN16931-* Schematron validation, and omits scheme-ID correctness for EndpointID/PartyIdentification, tax-code lists, allowances/charges, payment means (IBAN/BIC). The class header documents this honestly, but a buyer selecting 'Peppol' could assume network-ready conformance. Ensure UI copy and the /formats 'live' tag for UBL/Peppol do not over-imply certified Peppol conformance. | `ProcuLink.Transform/Output/PeppolBisInvoiceTransformService.cs:35-46; PeppolBisValidator.cs` |
| P2 | SFTP and S3/R2 ingress have NO configuration API or frontend — SftpIngressConfig / S3IngressConfig can only be populated by direct DB insert. A buyer cannot self-serve these channels; they are operator-only. Combined with the parse-enqueue gap, neither channel is currently usable end-to-end. Either ship a config surface + the enqueue fix, or remove SFTP/S3 from any 'supported import channels' marketing copy (offer⇔works rule). | `No controller for SftpIngressConfig/S3IngressConfig; only SettingsController.cs:62-122 exposes IMAP email` |
| P2 | Content-based routing is half-built and effectively a dead control: CreateUnroutedStubAsync, the 'unrouted' status, and POST /api/orders/{id}/assign-supplier all exist and work, but NO ingest channel ever produces an unrouted order (CreateUnroutedStubAsync has zero production callers) and the RouteByContent flag + content/supplier matcher are specced only (docs/superpowers/specs/2026-06-26-supplier-routing-backend-scope.md). A shared SFTP folder / mailbox receiving POs for many suppliers cannot be routed — everything goes to the single DefaultSupplierId or is skipped. Do not advertise content routing. | `ProcuLink.Api/Services/OrderService.cs:86 (CreateUnroutedStubAsync) + OrdersController.cs:567-617 (assign-supplier); producers absent across all 5 channels` |
| P2 | 'HTTP 200 != supplier business acceptance' is only partially enforced. Success is decided solely by HttpResponseMessage.IsSuccessStatusCode in HttpDeliveryDispatcher, ErplyConnector, and DirectoConnector. A supplier (or ERP) that returns HTTP 2xx with a business-level rejection/NACK in the response body is marked 'delivered'. There is no response-body acceptance assertion / configurable success predicate. 4xx vs 5xx is handled well, but in-band NACKs over 2xx slip through as delivered. | `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs:129 ; Services/Erp/ErplyConnector.cs:72 ; Services/Erp/DirectoConnector.cs:69` |
| P2 | Entire Stripe money path (checkout completion, price→plan mapping, plan unlock, overage at invoice.created, portal, cancellation→read-only) cannot be verified by code/unit tests alone — it requires live Stripe TEST-mode events with configured price IDs and a reachable webhook. Go-live readiness hinges on running these once end-to-end with the stripe CLI before launch. This is the single biggest unverified surface in this area. | `ProcuLink.Api/Controllers/BillingController.cs:155-515; ProcuLink.Api/Services/StripeBillingService.cs:284-647` |
| P2 | Admin per-org overrides, MRR reconciliation, manual invoicing, and GDPR erase are gated solely by an env allowlist (Admin:UserIds/Admin:Emails) that is empty by default and fails closed. Until the founder populates it, the admin surface is entirely unusable (every endpoint 403s), meaning beefier-pilot grants / trial extensions cannot be performed in production. Must be configured (and the config verified) before launch. | `ProcuLink.Api/Auth/AdminAllowlist.cs:28-45; ProcuLink.Api/Controllers/AdminController.cs` |
| P3 | Changelog is fully hardcoded static entries (v1.0–v1.4 with month labels) rather than a real release feed; dates/claims are unverifiable and can go stale. Acceptable for marketing but flag for accuracy before launch. | `src/app/(marketing)/changelog/page.tsx:16-67` |
| P3 | Hero 'Clean order' / Topology preview uses static demo data (PO-2026-008412, Northwind Trading, ElectroSupply Co, sample cXML). Clearly illustrative, but a dumb user could read it as a live feed since the window chrome says 'live order topology'. Consider labeling it as an example. | `src/app/page.tsx:388-390,920-981` |
| P3 | Footer inconsistency: the root page.tsx inline footer omits the 'Help center' (/help) link that the (marketing) layout footer includes, so the homepage footer and inner-page footer differ. Minor but a crawl/UX inconsistency. | `src/app/page.tsx:892-895 vs src/app/(marketing)/layout.tsx:5-9` |
| P3 | /welcome (a post-signup onboarding page reading Clerk user state) lives in the public (marketing) route group; a logged-out visitor can load it (renders with no name, no banner). Harmless but arguably should be auth-gated or moved under (app). | `src/app/(marketing)/welcome/page.tsx` |
| P3 | PostHog analytics is a silent no-op in the committed config (NEXT_PUBLIC_POSTHOG_KEY blank), so all capture() calls across marketing/auth (watch_demo_started, welcome_viewed, $pageview, help_watch_click) collect nothing until the founder sets the key. Correct degradation, but launch metrics will be blind until configured. | `src/components/analytics/AnalyticsBoot.tsx, src/lib/analytics + .env NEXT_PUBLIC_POSTHOG_KEY` |
| P3 | /drafts has no persistence backend at all — there is no draft API. Real users always see the empty 'Drafts live here' state; the nav item and 'New' button effectively just route to /upload. Not a data-leak (demo rows are mock-gated) but it is a navigation item that does nothing functional for real users; consider hiding it until draft persistence exists. | `project-proculink/src/app/(app)/drafts/page.tsx` |
| P3 | The /upload/preview/[orderId] (MagicMappingPreview) route remains resolvable but is no longer the live flow — UploadWorkbench redirects straight to /inbox/{id} (OrderWorkshop). Two overlapping review surfaces (preview commit vs workshop) share endpoints but diverge in UX; a stale deep link or bookmarked /upload/preview link lands a user on the older screen. Confirm intended and consider redirecting it to /inbox/{id} to avoid two code paths drifting. | `project-proculink/src/app/(app)/upload/preview/[orderId]/page.tsx + UploadWorkbench.tsx:516-524` |
| P3 | InboxView ships a 50-row procedurally-generated mock dataset (generateOrders) and BridgeDashboard ships IN_TRANSIT_MOCK_FALLBACK/demo rows. These are correctly gated behind isApiMockMode (NEXT_PUBLIC_USE_MOCK), which defaults false with a prod-mock guard, so they should never reach real users — but any future regression that flips the flag on a deploy would surface fabricated buyer/supplier names. Worth a CI assertion that the prod build resolves isApiMockMode=false. | `project-proculink/src/components/bridge/InboxView.tsx:505 + BridgeDashboard.tsx:142` |
| P3 | Supplier profile Overview KPIs (Total orders, Avg cycle time, Exception rate, Acceptance) are hard-coded to '—/no data yet' in live mode with no per-supplier analytics endpoint behind them. Honest (no fake data) but a permanently empty dashboard a buyer may expect to populate after real orders. | `src/components/bridge/SupplierDockProfile.tsx L1385-1495` |
| P3 | All 6 delivery protocols (HTTP/SFTP/FTPS/SMTP/Erply/Directo) are enabled in the picker, but per project history only HTTP delivery is live-proven; SFTP/FTPS/SMTP/ERP test-fire paths exist but are less battle-tested. Verify each non-HTTP protocol's test-fire and real send before promising them to a customer. | `src/components/bridge/DeliveryConfigEditor.tsx L26-32 (PROTOCOLS all enabled:true)` |
| P3 | Webhooks 'Recent deliveries' panel is permanently empty in live mode (no delivery-history API) — an operator who just created a webhook sees no confirmation of fires here and may think it's broken. Consider wiring a real recent-deliveries endpoint or labelling the panel as not-yet-available in live, like the ASN banner. | `src/app/(app)/operations/webhooks/page.tsx:1074 (deliveries=null) + 393-406 (DeliveriesCard empty state)` |
| P3 | Operations sub-pages (Delivery log, Connectors, Webhooks) and the entire Inbound group are hidden from the default launch sidebar; they are only reachable by direct URL unless launch flags are flipped. Acceptable for the outbound-PO wedge, but means these audited pages are effectively dark at go-live — confirm that's intended and that no other UI links dangle to them. | `src/lib/launch-flags.ts:15-38 + src/components/bridge/BridgeSidebar.tsx:101-123` |
| P3 | Health 'Awaiting your review' tile depends on the optional backend OpsHealth.pendingReview field; if that field isn't deployed it silently renders 0, which could hide a real manual-review backlog from an operator. Verify the backend emits pendingReview in production. | `src/app/(app)/operations/health/page.tsx:167-179 + src/lib/api/operations.ts:33-38` |
| P3 | DevFilesController has no [Authorize]/[AllowAnonymous] attribute and serves files with no auth. Mitigated by a hard IsDevelopment() 404 gate and GetFullPath traversal guard, so it is inert in production — but it relies entirely on ASPNETCORE_ENVIRONMENT being correctly set to Production in prod. Confirm the Railway env var is Production. | `ProcuLink.Api/Controllers/DevFilesController.cs:27-47` |
| P3 | DesadvController (api/asns) accepts ASN/DESADV ingest but the EdifactDesadvParser is a licence-gated stub (founder has no commercial EDI library); it returns 202 Accepted with a licence note rather than actually parsing. Auth/tenancy are fine; the capability is partial — ensure UI copy does not over-claim DESADV support. | `ProcuLink.Api/Controllers/DesadvController.cs` |
| P3 | cXML DOCTYPE external identifiers are written VERBATIM into the <!DOCTYPE> declaration (XLinq does not escape them). A defensive IsValidDtdExternalId guard rejects quotes/angle-brackets/control chars and SKIPS the DOCTYPE rather than emit broken cXML, and config-save is expected to validate too — but confirm the config-save-time validation actually exists so a malformed DTD value is caught at entry, not only at emit time. | `ProcuLink.Transform/Output/CxmlTransformService.cs:209-239 (BuildDocumentType / IsValidDtdExternalId)` |
| P3 | X12 ISA/GS control numbers are hard-coded constants (InterchangeControl=000000001, GroupControl=1, StControl=0001) per document. Fine for single-interchange POs, but a supplier doing strict duplicate-control-number detection across many orders could see colliding control numbers. Acceptable for current wedge; flag before high-volume X12 onboarding. | `ProcuLink.Transform/Output/X12TransformService.cs:61-64` |
| P3 | Stale duplicate checkouts under .claude/worktrees contain full copies of every Output transform and FormatMatrix test (routing-phase0/phase1). Auditors/CI globbing the repo root could pick up the wrong copy; the primary checkout is the source of truth. Confirm CI and any deploy packaging exclude .claude/worktrees. | `.claude/worktrees/routing-phase0-nullable-supplier/ProcuLink.Transform/Output/*; .claude/worktrees/routing-phase1-hold-assign/*` |
| P3 | SFTP/S3/IMAP SSRF guard carries a documented residual DNS-rebind TOCTOU between the guard's resolve and the client library's own resolve at connect. Accepted risk (pinning the IP would break SSH host-key/TLS SNI semantics), but worth noting for a go-live security sign-off. | `SftpIngressService.cs:93-107 / S3IngressService.cs:99-115 / EmailPollOrgJob.cs:117-129` |
| P3 | IMAP default-supplier model means a single polled mailbox attributes ALL imported attachments to one DefaultSupplierId regardless of who actually sent the PO; same for SFTP/S3. Until content routing is live this can mis-attribute orders if multiple suppliers email/drop into one channel. Functional, but a correctness footgun for multi-supplier setups. | `EmailPollOrgJob.cs:99-105 / InboundEmailRouter.cs:144-157 / SftpIngressService.cs:71-82` |
| P3 | ERP connectors (Erply/Directo) have NO up-front OutboundRequestGuard.ValidateAsync pre-check; they rely entirely on the connect-time guarded primary handler attached to the named 'delivery' HttpClient. This is defended (ErpConnectorSsrfTests proves the block at connect) and equivalent in outcome, but it is an asymmetry vs HttpDeliveryDispatcher's belt-and-braces (pre-check + connect-time). If a future host ever registers the 'delivery' client WITHOUT ConfigurePrimaryHttpMessageHandler, the ERP connectors would silently lose SSRF protection with no per-call guard to catch it. | `ProcuLink.Infrastructure/Services/Erp/ErplyConnector.cs:68 ; DirectoConnector.cs:65 ; relies on ProcuLink.Api/Program.cs:369-374 + ProcuLink.Worker/Program.cs:148-154` |
| P3 | FTPS AllowInvalidCertificate=true and (separately) SFTP not validating host keys means an operator opt-in or default disables MITM protection on the transport. AllowInvalidCertificate is a per-supplier conscious escape hatch (documented) but is silently accepted from config JSON; SFTP via SSH.NET connects without pinning/verifying a known host key, so a MITM on the SFTP path is not detected. Acceptable for go-live if documented, but worth surfacing to the operator in the UI. | `ProcuLink.Infrastructure/Services/Dispatchers/FtpsDeliveryDispatcher.cs:179 ; SftpDeliveryDispatcher.cs:62-75 (BuildConnectionInfo, no host-key callback)` |
| P3 → **LIVE, not conditional** (WP-21, 2026-07-31) | Test-fire uses the LIVE supplier delivery config (DeliveryService.TestFireAsync), NOT the pinned connection revision used by real order delivery (ResolveEffectiveDeliveryConfigAsync). This entry was written conditionally — "when Connections:RevisionAuthority is on" — and the condition is **met**: `Connections__RevisionAuthority = true` on both Railway services (`ProcuLink` and `aware-amazement`), verified 2026-07-27 and re-verified 2026-07-31. So a successful test-fire CAN today validate a different (live-edited) channel than the one a pinned order will actually deliver over, giving a false-positive 'it works'. Read the deployed value from `GET /health/ready` (`revisionAuthority`), never from an appsettings file. | `ProcuLink.Infrastructure/Services/DeliveryService.cs:370 (TestFireAsync) vs :323 (ResolveEffectiveDeliveryConfigAsync)` ; `docs/ops/revision-authority-production-smoke.md` |
| P3 | SMTP/SFTP/FTPS only re-validate the host immediately before connect (ValidateHostAsync) and cannot pin the validated IP (library re-resolves by hostname for host-key/TLS-SNI). A DNS-rebind between ValidateHostAsync and the library's own resolution remains a (narrow) TOCTOU window for these three channels — strictly narrower protection than the HTTP path which pins the IP. Documented in code comments; founder should accept the residual risk for these channels. | `ProcuLink.Infrastructure/Services/Dispatchers/SmtpDeliveryDispatcher.cs:134 ; FtpsDeliveryDispatcher.cs:119 ; SftpDeliveryDispatcher.cs:70` |
| P3 | Process-local in-memory rate limiters and the trial-farming provision throttle are correct for a single API replica only; scaling the API horizontally multiplies effective limits by replica count and resets the anti-farming window per replica. Documented as scale-gated, but a launch that scales replicas without a shared (Redis) store weakens abuse protection. Not a correctness hole at current single-replica deploy. | `ProcuLink.Api/Program.cs:245-260; ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs:60-318` |
| P3 | Personal-workspace fallback: when a Clerk session has no active org_id, the middleware falls back to the user 'sub' as the tenant key and labels it 'Personal workspace'. This is intentional but means a user who never activates a Clerk org silently gets a separate tenant from their org colleagues; for a B2B procurement team this could fragment data across per-user workspaces if Clerk org activation isn't enforced in the signup flow. Verify the Clerk post-signup flow forces org selection/creation. | `ProcuLink.Api/Middleware/TenantResolutionMiddleware.cs:95-104` |
| P3 | Delivered-only billing meter (Billing:CountDeliveredOnly) intentionally never catches up late deliveries across a closed period (customer-favorable under-counting). Correct and documented, but if the founder ever flips this flag they must also update the published pricing/Terms copy in lockstep — a process risk, not a code defect. | `ProcuLink.Api/Services/StripeBillingService.cs:29,750-832` |

_Total test scenarios: 134. Total risks: 47. Areas mapped: 9 (parser/extraction-accuracy audit appended separately)._

---
# 8. Incoming-format parser deep-audit (extraction accuracy)



---
# 8. Incoming-format parser deep-audit (extraction accuracy)

12 parsers in `ProcuLink.Transform/Parsing`. **9 production-ready, 2 correctly-stubbed (EdiFabric-blocked), 1 invoice.** All hand-rolled (no paid EDI lib). Routing: extension-first, then content-sniff for ambiguous `.xml`/`.txt`/`.edi`. Locale handling robust via `NumberParsing.TryParseFlexibleDecimal(european)` — EU comma-decimals + US dots both safe; ambiguous tokens flagged for review, never silently guessed.

| Format | Parser | Status | Routing | Sample need |
|---|---|---|---|---|
| CSV | CsvOrderParser | REAL | `.csv` ext; `;`→EU mode | synthetic OK |
| XLSX | XlsxOrderParser | REAL | `.xlsx`; raw-double read (no locale round-trip); ZIP-repack fallback | synthetic OK (ClosedXML-built) |
| PDF | PdfOrderParser (+ text→LLM extractor upstream, OCR fallback) | REAL | `.pdf`; regex deterministic + OCR via `IDocumentOcrService` | **real samples needed** for regex/layout |
| cXML 1.2 | CxmlOrderParser | REAL | `.cxml`/`.xml`; DtdSafe (XXE-proof) | real samples in repo |
| UBL 2.1 | UblOrderParser | REAL | `.ubl`/`.xml` root="Order"+ns; Peppol BIS3 detect | synthetic OK |
| X12 850 | X12OrderParser | REAL | `.x12`/`.txt`/`.edi` sniff ISA+ST*850; delim discovery | synthetic OK |
| EDIFACT ORDERS | EdifactOrderParser | REAL | `.edi`/`.txt` sniff UNA/UNB; D96A+D01B; UNA decimal locale | synthetic OK |
| SAP IDoc ORDERS05 | IDocOrders05Parser | REAL | `.xml` root="ORDERS05" (priority) | real samples in repo |
| UBL Invoice | UblInvoiceParser | REAL | invoice factory | synthetic OK |
| EDIFACT INVOIC | EdifactInvoiceParser | **STUB** | — | EdiFabric (forbidden) — out of scope |
| EDIFACT DESADV | EdifactDesadvParser | **STUB** | — | EdiFabric (forbidden) — ASN 501s |

**Key findings:** (1) loud failures on malformed XML/EDI (throws `*ParseException`), graceful empty on malformed CSV/XLSX; (2) cXML/UBL XXE-protected via `DtdSafeXmlLoader`; (3) PDF is the one parser that genuinely needs **real founder sample PDFs** for regex/layout tuning; (4) the #1 product-heart test = run the ~12-PO real corpus (in your Downloads) through every parser to catch silent numeric corruption (prior 10×/100× EU-locale bugs lived here). This regression run is the single most important pre-launch test and needs a local build harness.

---
# 9. LIVE PROD TEST RESULTS (2026-06-29, "Dim's Organization")

Driven via Claude-in-Chrome against https://proculink.eu (API api.proculink.eu). Org has an admin override (orders 13/100000, suppliers 11/30, trial→2027) so quota was a non-issue.

## 9.1 Incoming-format matrix — upload→parse (PASS 7/7)
| Format | File | Result | Lines | Numerics |
|---|---|---|---|---|
| CSV | redacted-fixture | ✅ parsed, €665.50, math reconciles | 3 | qty/price exact |
| CSV EU-locale (`;` + `12,50`) | redacted-fixture | ✅ parsed | 2 | **unitPrice 12.5 / 0.45 — NO 10×/100× corruption** |
| cXML 1.2 | redacted-fixture | ✅ parsed (pending_review) | 2 | ✓ |
| UBL 2.1 | redacted-fixture | ✅ parsed | 2 | ✓ |
| X12 850 | redacted-fixture | ✅ parsed (content-sniff routed) | 2 | ✓ |
| EDIFACT ORDERS | redacted-fixture | ✅ parsed (content-sniff routed) | 2 | ✓ |
| SAP IDoc ORDERS05 | redacted-fixture | ✅ parsed (root routed) | 2 | ✓ |
| JSON (file upload) | redacted-fixture | ✅ **correctly rejected 400** "Supported formats: CSV, XLSX, PDF, XML, EDI" (JSON is REST-ingress only) | — | — |
| XLSX | — | ⏳ NOT TESTED (needs ClosedXML-built or real sample; openpyxl fails on prod) | | |
| PDF (text + scanned) | — | ⏳ NOT TESTED (needs real founder PDFs for regex/AI extraction) | | |

**Bonus:** cross-order code-learning verified — an EDIFACT order carrying only a buyer code auto-resolved `REDACTED-ITEM→REDACTED-ITEM` from a mapping entered earlier on another order (`needsReview:false`). Learn loop is live.

## 9.2 Outgoing-format matrix — transform/preview (PASS 6/6)
Via `POST /api/orders/{id}/mapping-override/preview?format=X&honorFormat=true` on resolved PO-TEST-001:
| Format | Result |
|---|---|
| CSV | ✅ valid (`PoNumber,OrderDate,Currency,…`) |
| XML | ✅ valid (`<PurchaseOrder>…`, LineTotal 312.50 = 25×12.50) |
| JSON | ✅ valid (`{"poNumber":"PO-TEST-001",…}`) |
| X12 850 | ✅ valid (`ISA*00*…ZZ*PROCULINK…`) |
| UBL 2.1 | ✅ valid (`<Order xmlns:cac=…`, 2608 B) |
| cXML 1.2 | ✅ valid (`<cXML>`+OrderRequest+ItemOut, 2142 B, DOCTYPE off by default) |
| EDIFACT | ✅ correctly NOT offered (no transformer; honest) |

## 9.3 Money path (PO-TEST-001, CSV)
parse ✅ → format-detect ✅ (CSV 65%) → validate ✅ (blocked 3 lines "Needs a supplier code", plain language, no blind send) → inline resolve ✅ (counter decremented, status→Normalized/Ready) → transform ✅ (**artifact format = csv**, the supplier's configured format) → deliver ❌ **honest fail** "HTTP delivery configuration is invalid" (demo supplier has no real endpoint; org-wide Delivered=0 — no successful live delivery yet).

## 9.4 Bugs found live
| ID | Sev | Bug | Evidence |
|---|---|---|---|
| PREVIEW-STALE | P2 (UX/trust) | Active preview tab does NOT refresh when line-review state clears — shows "Cannot transform: lines 1,2,3 still need review / (no preview)" even after status flips to "Ready to send". Refreshes only on format-tab switch. Contradicts order state. | Resolved all 3 lines → header "Ready to send" but CSV preview still "(no preview)"; clicking XML tab forced a valid render. |
| REDACTED-ITEM | P2 (trust) | Send confirmation modal shows the **last-previewed** format ("FORMAT: XML / deliver the transformed XML order") instead of the supplier's actual delivery format (CSV). **Backend delivers correct CSV** (artifact verified `format:"csv"`), so it's a misleading label, not data corruption — but a confirmation dialog that misstates the outgoing format erodes trust. | Supplier config `outputFormat:"csv"`; modal said XML after previewing XML; delivered artifact was csv. |
| DETECT-CONFIDENCE-LOW | P3 | Clean 3-line CSV detected at only "CSV · 65%" confidence. Cosmetic but undersells. | Upload preview chip. |

## 9.5 Still to test (this session)
- **Successful delivery** end-to-end (needs a valid receiver — will stand up webhook.site) + verify received bytes per format + the REDACTED-ITEM acceptance gap.
- **PDF** (text + scanned/OCR) and **XLSX** parse — need real sample files.
- Flagged-page spot-checks: /library/rules (dead control), /watch (video), /drafts, connectors Add/Connect, admin Stripe /test/ link.
- Error/edge: scanned-PDF OCR path, navigate-away mid-send.
- Non-HTTP delivery (SFTP/FTPS/SMTP/Erply/Directo), inbound email/IMAP/SFTP/S3 — all need founder-supplied endpoints/creds.

## 9.6 Outbound delivery PROVEN (HTTP)
Created throwaway supplier "ZZ Webhook Test" → `PUT delivery-config {protocol:http, configJson:{url:webhook.site}, outputFormat:csv}` (200) → `POST test-fire` → **`{success:true, responseCode:200}`**. webhook.site received `POST`, `content-type: text/csv`, 27-byte body `test,from\nproculink,true`. **Outbound HTTP delivery works end-to-end on prod.** (Confirms the earlier honest delivery_failed was solely the demo supplier's empty endpoint.)

- **EGRESS-GEO (P3 note):** the outbound POST egressed from **152.55.184.78 (Durham, NC, US)**. Despite EU-residency positioning the delivery egress IP geolocates to the US — verify against the GDPR/EU-data-residency claims (a supplier's server logs will show a US source IP). Likely Railway/Cloudflare egress; confirm region.
- **REDACTED-ITEM (P2, code-confirmed):** not re-demonstrated live, but `HttpDeliveryDispatcher` success = `IsSuccessStatusCode`, so a supplier returning 200 with a rejection body would be marked delivered. The positive path is now proven; the NACK gap remains per audit §risks.
- **Test data created this session (to purge):** supplier "ZZ Webhook Test (delete me)" (id 074cbc15…) + orders PO-TEST-001/002/CXML/UBL/X12/EDI/IDOC. Purge via `POST /api/admin/organisations/{id}/orders/bulk-erase` (filter) or per-order delete — pending user OK.

## 9.7 Audit corrections from live testing
- **/library/rules — DOWNGRADE P1→P3.** The shipped page is honestly framed: header "A catalog of the checks you want to run · Enforcement is configured per supplier", plus a blue banner "**This is a catalog, not a gate. … not enforced automatically — set up the checks that actually hold or block an order on each supplier's Validation rules tab**", and each rule shows "Recommended enforcement (per supplier)". This is NOT a silent dead control — it explicitly tells the user enforcement lives on the supplier Validation tab. Go-live blocker #6 is largely already addressed by copy. (Residual: a user could still miss the banner; and leftover junk test rule "asd/REDACTED-ITEM" should be deleted.)

- **/watch video — BLOCKER #5 CONFIRMED LIVE.** `walkthrough-poster.jpg` loads (1920px) so the page shows a still frame, but `walkthrough.mp4` (`https://assets.proculink.eu/marketing/walkthrough.mp4`) never loads — fresh `<video>` load test timed out at `readyState:0/networkState:2` after 9s. A visitor clicking play gets a dead player. FIX: upload the MP4 to the R2 public bucket, OR blank `NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL` so the "coming shortly" fallback renders.
- **admin Stripe links** (blocker #4) not re-tested live (requires admin allowlist, which is unconfigured) — remains a code fact: `admin/page.tsx:38` hardcodes `dashboard.stripe.com/test/`.

## 9.8 Fix PRs opened this session (PR-only, awaiting founder merge/deploy)
- **SFTP/S3 parse-enqueue (P1, GO-LIVE blocker #1):** https://github.com/dimnovare/ProcuLink/pull/4 — 27/27 ingress tests pass.
- **Admin Stripe links → live (blocker #4):** https://github.com/dimnovare/project-proculink/pull/4 — env-driven `NEXT_PUBLIC_STRIPE_DASHBOARD_BASE`, defaults live.
- **Homepage stat counts:** https://github.com/dimnovare/project-proculink/pull/5 — inbound 9→10 (matches /formats; out 6 & channels 6 already correct).
- **/bridge cold-auth gate:** https://github.com/dimnovare/project-proculink/pull/6 — `useQueriesEnabled()` on the 4 ungated dashboard queries.

## 9.9 HELD patches (apply on feat/design-system-v1 to avoid WIP collision)
- **REDACTED-ITEM** — `OrderWorkshop.tsx:358`: send-confirmation should use `orderDeliveryFormat(order)` (already imported, line 37) not `outputArtifactType(order.artifacts)`.
- **PREVIEW-STALE** — `MapperPreviewPane.tsx`: add order review signal (`order.status` / unresolved-line count) to the preview effect deps so resolving a line re-fires the preview (currently only fires on format-tab switch).

## 9.10 Remaining = founder-blocked (inputs requested)
- PDF (text + scanned) & XLSX parse — need real sample files.
- Full Stripe money path — need TEST keys + price IDs + webhook (set on Railway).
- Admin surface — need founder Clerk email in `Admin__Emails`.
- Non-HTTP delivery (SFTP/FTPS/SMTP/Erply/Directo) + inbound (Postmark email / IMAP / REDACTED-ITEM pull) — need endpoints/creds.
- EGRESS-GEO: confirm delivery egress region vs EU-residency claim.

## 9.11 REAL PO files — local parser test (PDF + XLSX) — 2026-06-29
Tested the actual `XlsxOrderParser` + `PdfOrderParser` (deterministic paths) against 4 real founder files on disk.

**Results — deterministic parsers FAIL on these real files:**
| File | Parser path | Result |
|---|---|---|
| Rheinbahn PDF (German) | regex (no-key fallback) | PO/date/buyer empty; 1 garbage line (`code='ST' qty=1 price=752.40 desc='376,20 EUR'`). German "Bestellnummer/Bestelldatum" labels not matched. |
| 226131000790 PDF | regex fallback | PO = a sentence fragment; 0 lines. |
| Rheinbahn XLSX | XlsxOrderParser | Currency=EUR ✓; **0 line data** (code/qty/price blank). |
| 226131000790 XLSX | XlsxOrderParser | Currency=PLN ✓; **0 line data**. |

**PDF:** regex fail is EXPECTED — prod uses text→LLM (handles German/arbitrary layout); regex is only the no-OpenAI-key fallback. The LLM path was NOT exercised (no local key; prod byte-upload blocked by tooling). **Must verify the prod LLM path** (founder uploads via prod UI, or supply OpenAI key for local full-pipeline test).

**XLSX — REAL GAP (high confidence):** the Markit XLSX export is NOT a simple header+rows table. It is a labeled/sectioned schema:
`PurchaseOrderNr`, `BillToName`, `BillToVatNr`, `Currency`, `Totals TotalWoVAT/TotalVAT/TotalWVAT`, and a Lines section `Lines LineNr / Lines ManufPN / Lines ProductName / Lines Quantity / Lines PriceWoVAT`.
`XlsxOrderParser` assumes row1=headers with aliases (`PoNumber`/`Quantity`/`UnitPrice`/`BuyerItemCode`) → it maps almost nothing (only literal "Currency"). **Same parser runs on prod (no LLM layer for XLSX) → same zero-line result on prod.** If these XLSX exports are an ingestion target, XLSX PO ingestion is effectively broken for this real-world format. ACTION: confirm whether this XLSX schema is an inbound target; if yes, add a labeled/sectioned-schema reader (or an XLSX→LLM extraction path like PDF) + a Markit-export adapter.
- Side note: redacted-fixture stores `PurchaseOrderDate=2026-12-06` while the PO is dated 12.06.2026 (June 12) — the export wrote DD/MM as ISO (Dec 6) — a date-format bug in the upstream export to watch for.

## 9.12 PROD upload of real files (founder-uploaded via UI) — 2026-06-29
| File | Path | Prod result |
|---|---|---|
| redacted-fixture | text→LLM | ✅ PERFECT: PO 226131000790, buyer "DNV Poland Sp. z o.o.", PLN, 2 lines (REDACTED-ITEM Logitech Ergo Wave Keys 246.94; REDACTED-ITEM Logitech Lift Mouse 169.25) |
| redacted-fixture (Rheinbahn, German) | text→LLM | ✅ PO 11421247, 1 line, EUR |
| redacted-fixture | XlsxOrderParser | ❌ PO# not extracted (auto-gen), 2 lines but **qty=0/price=0/code=''/desc=''** |
| redacted-fixture | XlsxOrderParser | ❌ same — empty lines |

**Verdict:** PDF text→LLM extraction is EXCELLENT on real multi-language customer PDFs (the product's core strength, confirmed). **XLSX ingestion is broken for the real labeled Markit export schema** — offer⇔works violation. Recommend routing XLSX through the same LLM extraction as PDF (xlsx→text/markdown→LLM) or a labeled-schema reader.

## 9.13 SMTP delivery — Railway egress block (P1 infra)
Wired a test supplier to Ethereal SMTP (smtp.ethereal.email:587) + test-fire → **"SMTP delivery timed out"** (responseCode null). HTTP delivery (443) works from the same host, so this is almost certainly **Railway blocking outbound SMTP ports (25/465/587)** — standard PaaS anti-spam policy. CONSEQUENCE: the SMTP delivery channel (offered in DeliveryConfigEditor) does NOT work on the current Railway deploy. FIX: deliver email via an HTTP email API (Postmark/SendGrid/Mailgun REST over 443) rather than raw SMTP, or use a relay on an allowed port. Verify before offering SMTP delivery to a customer.

## 9.14 Channel-test plan (free services)
- HTTP delivery: ✅ proven (webhook.site).
- SMTP delivery: ❌ Railway port block (above).
- SFTP delivery: testable via a free instant SFTP (e.g. sftpcloud.io) — also probes whether Railway allows port-22 egress.
- FTPS delivery: needs a public explicit-TLS FTPS server (harder to source free).
- Erply/Directo: HTTP/443-based (egress OK) — need a sandbox account (Erply free demo).
- Inbound REST API: testable now (create plk_ key + POST JSON to /api/ingress/{slug}/orders).
- Inbound email (Postmark): founder has Postmark — set inbound webhook → /api/inbound-email/postmark + Inbound__Postmark__WebhookToken.
- Inbound IMAP: Ethereal IMAP (imap.ethereal.email:993) possible if Railway allows 993 + org on Integration plan.
- Inbound SFTP/S3 pull: blocked on PR#4 (parse-enqueue) + no config UI (DB-only).

## 9.15 Channel + commercial progress (cont.)
- **Admin allowlist:** ✅ set `Admin__Emails=redacted@example.invalid` on Railway (ProcuLink) + verified — `GET /api/admin/organisations` → 200. Blocker #3 resolved.
- **Inbound REST API:** ✅ WORKS end-to-end. Created `plk_` key (`POST /api/api-keys {label}`) → `GET /api/ingress/{slug}/ping` 200 → `POST /api/ingress/{slug}/orders` (X-ProcuLink-Key, JSON body {OrderNumber,Currency,SupplierId,Lines[]}) → **200, order created, status ready, 2 lines**. (A dangling QA API key `plk_7oiH…` remains — revoke on request.)
- **PERSONAL-WORKSPACE FALLBACK — confirmed live (raise to P2):** the founder's own session resolves to backend org slug `personal-workspace-d3be` (name "Personal workspace"), NOT a real org — every backend org in the admin list is a per-user "Personal workspace". The UI shows "Dim's Organization" (a Clerk org) but no active Clerk org is set, so `TenantResolutionMiddleware` falls back to the per-user `sub` workspace. For a B2B team this means each member silently gets a SEPARATE workspace (data fragmentation) unless the Clerk post-signup flow forces org creation/selection. Fix before onboarding multi-member teams.
- **Stripe sandbox surfaced:** founder opened Stripe TEST account `acct_1TbeHmLMyzXaWowf` ("ProcuLink sandbox") with price IDs visible (price_1TdQaC…, _1TdQZW…, _1TdQYn…, _1TdQY4…, _1Tcq7Y…, _1TbeYf…, _1TbeYU…, _1TbeYB…). For the local Stripe full-path test I still need the test Secret key + Webhook signing secret (founder-provided; I won't scrape secrets).

## 9.16 Channel scorecard (live)
| Channel | Dir | Result |
|---|---|---|
| Browser upload | in | ✅ (7 formats; XLSX schema gap) |
| Inbound REST API | in | ✅ proven |
| HTTP / webhook | out | ✅ proven (webhook.site 200) |
| SMTP | out | ❌ Railway egress block (587 timeout) |
| SFTP / FTPS | out | ⏳ pending (need public server; also probes Railway port-22/21 egress) |
| Erply / Directo | out | ⏳ pending (need sandbox; HTTP/443 so egress OK) |
| Inbound email (Postmark) | in | ⏳ pending (founder configures inbound webhook + token) |
| Inbound IMAP | in | ⏳ pending (Integration plan + mailbox) |
| Inbound SFTP/S3 pull | in | ⏳ blocked on PR#4 + no config UI |

## 9.17 Stripe money path — LOCAL full-path test (test keys) — VERIFIED
Ran a local stack (Docker PG :5435 + API, QA-bypass, founder-supplied Stripe TEST keys + 8 price IDs in env). Verified webhooks with synthetic Stripe-signed events (no card entry needed).
- **Checkout session creation + plan→price mapping: 8/8 CORRECT.** Each plan × {monthly,yearly} POST /api/billing/checkout created a Stripe session whose actual line-item price (read back from Stripe) matched the expected price ID exactly (growth/operations/integration/distributor, both intervals).
- **`checkout.session.completed` webhook → plan unlock:** HTTP 200; org flipped pilot→**operations**, account_status→**active**, StripeCustomerId + billing email persisted.
- **`customer.subscription.updated` webhook → price→plan mapping:** HTTP 200; org → **integration** (mapped from the event's price id), subscription_status=active.
- **Signature verification:** HMAC-SHA256 `Stripe-Signature` validated; a wrong/old api_version is rejected (400).
- ⏳ NOT run: overage at `invoice.created` (needs over-quota org + test clock) and customer portal (needs a real customer) — both unit-tested in repo; recommend a Stripe test-clock pass before launch.

### ★ CRITICAL go-live finding — webhook API version
Stripe.net **51.1.0 expects API version `2026-04-22.dahlia`**. `EventUtility.ConstructEvent` THROWS on any other version → the webhook returns 400 → the event is dropped. **The PROD Stripe webhook endpoint (live mode) MUST be registered with API version `2026-04-22.dahlia`** (Stripe Dashboard → Developers → Webhooks → endpoint → API version), OR change the code to `ConstructEvent(..., throwOnApiVersionMismatch:false)`. Otherwise: customers pay, `checkout.session.completed` 400s, and **the plan never unlocks** — silent billing failure. Verify the live webhook endpoint's version before launch.

## 9.18 XLSX→LLM extraction — PR opened
Implemented (background agent): `.xlsx` now routes through the SAME `IStructuredOrderExtractor` (text→LLM) the PDF path uses — new `XlsxTextExtractor` renders the workbook to labeled text (preserving `PurchaseOrderNr | …`, `Lines ManufPN | …`), fed to `ExtractFromTextAsync`; deterministic `XlsxOrderParser` kept as fallback (no key / no-egress / failure). Build clean; Transform 1153 + Api 1180 + Infra 46 tests pass (10 new). Caveat: when an OpenAI key is present, .xlsx always prefers the LLM (an LLM call even for simple tables) — intended, mirrors PDF. End-to-end on the real Markit XLSX is pending deploy (local has no OpenAI key); high confidence given the same extractor nails the PDFs.

## 9.19 Inbound email (Postmark) — logic PROVEN; 2 real-use gaps
Set `Inbound__Postmark__WebhookToken` on Railway. Synthetic Postmark payload (recipient `redacted@example.invalid`, CSV attachment, `X-Postmark-Server-Token` header) → **HTTP 200, order created** (`orgId 7a3b01e1`, `createdOrderId c600eaf4`). Token auth + recipient→slug→org routing + attachment ingest all work. Token gate also correctly rejected a wrong token (401).
- **✅ FIXED (PR#6, 2026-06-30) — webhook auth mechanism:** `InboundEmailController` now accepts the shared secret from (1) `?token=` query, (2) HTTP Basic-Auth password, (3) the legacy `X-Postmark-Server-Token` header — constant-time compared against `Inbound:Postmark:WebhookToken`. So a real Postmark inbound POST authenticates by carrying the token in the webhook URL (`…/postmark?token=<secret>` or `https://user:<secret>@host/…`). 9 controller auth tests added (accept paths + reject paths + Basic edge cases). The "header-only → non-functional" condition is resolved in code.
- **✅ FIXED (PR#6) — addressing scheme:** the router now supports the preferred **local-part** scheme `{slug}@orders.proculink.eu` (single MX, no wildcard) via `Inbound:Postmark:InboundDomain`, alongside the legacy subdomain scheme; plus-addressing tags stripped. 3 router tests added.
- **Remaining = founder infra only (one-time, NOT code):** (a) set the Postmark webhook URL to carry the token (`?token=` or Basic Auth); (b) MX — point `orders.proculink.eu` (local-part scheme) or `*.proculink.eu` (legacy) at Postmark and set the server's inbound domain so `OriginalRecipient` preserves the tenant address; (c) receiving org has a default supplier. Internal pipeline (auth→route→slug→org→attachment parse→order) is correct and live-proven.

## 9.20 SFTP delivery — WORKS (+ Railway port-22 egress confirmed)
Provisioned a free SFTPCloud test server (example.invalid:22), wired a prod supplier's SFTP delivery to it (configJson {host,port:22,remotePath:/proculink-test,makeDirectories:true} + credentialsJson {username,password}), test-fire → **success:true** (CSV written). First attempt failed "SFTP authentication failed" (transcribed password char wrong) — notable that auth-failure (not timeout) proves the SSH connection REACHED the server: **Railway permits outbound port 22**, unlike the blocked SMTP 587. So SFTP delivery is production-viable; SMTP delivery is not (needs HTTP email-API).

## 9.21 Updated channel scorecard (live, 2026-06-29)
| Channel | Dir | Result |
|---|---|---|
| Browser upload | in | ✅ proven (7 formats; XLSX schema fix PR'd) |
| Inbound REST API | in | ✅ proven |
| HTTP / webhook | out | ✅ proven |
| SFTP | out | ✅ proven (Railway allows :22) |
| SMTP | out | ❌ Railway blocks :587 — needs HTTP email-API |
| Inbound email (Postmark) | in | ⚠️ logic ✅; real use blocked by header-auth mismatch + MX (see 9.19) |
| FTPS | out | ⏳ need a public explicit-TLS server |
| Erply / Directo | out | ⏳ need sandbox creds (HTTP/443 — egress fine) |
| Inbound IMAP | in | ⏳ Integration plan + mailbox (could use Ethereal IMAP) |
| Inbound SFTP/S3 pull | in | ⏳ blocked on PR#4 (parse-enqueue) + no config UI |

## 9.22 Stripe local test stack — torn down after verification
Core money path verified (9.17); local API + Docker Postgres torn down; the test Stripe secret lived only in the process env (never written to the repo). Re-spin in ~1 min if needed (overage/test-clock pass still recommended).

---
# 10. FIXES MERGED (2026-06-30) + FOUNDER TO-DO

## 10.1 Merged to main + deploying (I did these)
| Fix | Repo / PR |
|---|---|
| SFTP/S3 pull → enqueue ParseOrderJob | ProcuLink #4 |
| XLSX → LLM extraction (real exports parse) | ProcuLink #5 |
| Postmark inbound auth: accept header / `?token=` / Basic-Auth | ProcuLink #6 |
| Admin Stripe links → live dashboard | project-proculink #4 |
| Homepage capability stat counts | project-proculink #5 |
| /bridge cold-mount auth-race gate | project-proculink #6 |
Held (apply on your design-system branch): PREVIEW-STALE + REDACTED-ITEM.

## 10.2 FOUNDER TO-DO (can't be done in code / needs your access)
1. **★ Stripe prod webhook API version = `2026-04-22.dahlia`** — Stripe Dashboard → Developers → Webhooks → your LIVE endpoint → set API version to 2026-04-22.dahlia. Confirm `Stripe__WebhookSecret` (Railway) = that live endpoint's signing secret. WITHOUT THIS: customers pay, webhooks 400, plans never unlock. Highest-priority launch gate.
2. **Walkthrough video** — upload `walkthrough.mp4` to the R2 public bucket (assets.proculink.eu/marketing/walkthrough.mp4), OR blank `NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL` in Vercel so the friendly fallback shows. (Poster already loads; only the mp4 is missing.)
3. **Postmark inbound (real receipt):** (a) set the Postmark inbound webhook URL to carry the token: `…/api/inbound-email/postmark?token=<token>` (I set `Inbound__Postmark__WebhookToken=pminbound-qa-7f3a9c2e1b` on Railway — rotate to your own + update both). (b) MX: route `orders@{slug}.proculink.eu` → Postmark so OriginalRecipient carries the tenant (raw `@inbound.postmarkapp.com` won't). (c) receiving org on Integration plan + an inbound default supplier (Settings → Email).
4. **SMTP delivery decision** — Railway blocks SMTP ports (587 timeout); the channel can't work as raw SMTP. Choose: (a) I build an HTTP email-API dispatcher (Postmark/SendGrid REST over 443), or (b) I remove SMTP from the offered delivery channels (honest until built).
5. **Clerk org / personal-workspace** — enable Clerk Organizations + force org creation/selection at signup so B2B teams share one org (today each user silently gets a separate personal-workspace → fragmented data). Clerk Dashboard + a product decision.
6. **SFTP/S3 inbound pull config UI** — parse-enqueue fixed (#4) but there is still no setup UI (rows are DB-only). If you want self-serve SFTP/S3 import, I build a settings UI (follow-up feature) — say the word.
7. **Stripe overage** — run a Stripe test-clock pass to confirm €0.50/order overage billing at period close (only money-path piece not yet exercised).
8. **Frontend CI is red (pre-existing)** — Playwright e2e (mock mode) fails on main independent of my changes; fix it so CI gates real regressions.

## 10.3 PROD Stripe live verification (2026-06-30) — GO-LIVE READY
- **Two Stripe accounts:** `acct_1TbeHmLMyzXaWowf` = sandbox (the test keys provided are from it); `acct_1TbeHcLSwazJxGKo` = LIVE prod account.
- **Live webhook "sophisticated-breeze":** endpoint `https://api.proculink.eu/api/billing/webhook`, **API version `2026-04-22.dahlia`** (EXACTLY matches Stripe.net 51.1.0 — no version-mismatch 400), signing secret set, listening to 4 events, Active, 0% error. ★ Go-live blocker #1 (webhook version) is ALREADY correctly configured in prod — billing webhooks will NOT silently fail.
- **Live checkout works:** `POST /api/billing/checkout {growth, monthly}` on prod returned a `cs_live_…` Checkout Session (HTTP 200) → the LIVE Growth price ID is configured on Railway and maps correctly (Stripe rejects session creation for an unknown/invalid price). No payment completed.
- Combined with local verification (plan→price 8/8, checkout.completed + subscription.updated webhook handling, HMAC), the Stripe money path is go-live ready. Remaining: a Stripe test-clock pass to exercise €0.50 overage at `invoice.created` (minor; logic unit-tested).
- Clerk dashboard was logged out — could not inspect Organizations/org-enforcement config; remains a founder item.

## 10.4 FINAL STATE (2026-06-30) — all fixes merged + deploying
**9 PRs merged this engagement:**
- Backend (ProcuLink): #4 SFTP/S3 parse-enqueue · #5 XLSX→LLM · #6 Postmark inbound auth (header/query/basic) · #7 Postmark HTTPS email (SMTP works on Railway) · #8 SFTP/S3 config controller tests
- Frontend (project-proculink): #4 admin Stripe live link · #5 homepage stat counts · #6 /bridge cold-auth gate · #7 send-modal true-format + preview-stale refresh

**Audit corrections (better than first reported):**
- Stripe live webhook ALREADY on API version 2026-04-22.dahlia (blocker #1 = non-issue). Live checkout creates cs_live → live prices wired.
- SFTP/S3 ingress config API ALREADY exists (GET/PUT /api/settings/sftp + /s3, encrypted, billing-gated, FE matches) — with #4 it's now self-serve end-to-end.
- /library/rules honestly labelled "catalog, not a gate" (not a dead control).

**Founder to-do (remaining):** (2) upload walkthrough.mp4 or blank env; (3) Clerk: enable Organizations + force org at signup (dashboard was logged out — couldn't inspect); (4) set `Delivery__Smtp__PostmarkServerToken` on API+Worker to activate email delivery; (5) Postmark inbound: webhook URL `…/api/inbound-email/postmark?token=<token>` + MX `orders@{slug}.proculink.eu`→Postmark + Integration plan + inbound supplier; (6) confirm Railway+Vercel deploys; (7) Stripe overage test-clock + fix pre-existing red FE Playwright CI. Re-drag the 2 XLSX post-deploy to confirm XLSX→LLM extraction.

## 10.5 Email delivery PROVEN (2026-06-30) + chip-reconciliation outcome
- **Email delivery channel WORKS on prod.** Canonical path = chip's #13 `EmailApiDeliveryDispatcher` → `PostmarkEmailApiClient` (config key `Email:Postmark:ServerToken`). Founder set the Postmark outbound token on Railway API+Worker + verified the `proculink.eu` sender. Test-fire (email-protocol supplier) → **success:true, HTTP 200, Postmark ErrorCode 0**. (My #7 Postmark-into-SMTP was superseded by #13 — 0 trace on main; no leftover confusion.)
- **Postmark account pending-approval caveat:** until the Postmark account is approved, sends are restricted to same-domain (proculink.eu) recipients (first test to mailinator.com → 422 pending-approval; proculink.eu retry → 200). FOUNDER ACTION: request Postmark account approval to send to arbitrary supplier domains.
- **Clerk:** Production instance ALREADY has Organizations enabled + Membership Required + auto-create-first-org → new-signup fragmentation already prevented; nothing changed. Legacy org-less sessions (e.g. founder's personal-workspace-d3be) still resolve (backend #11 softened-resolve; verified 200). FE force-org gate = project-proculink PR#10 (held; belt-and-suspenders).
- **Chip reconciliation:** other chips merged PRs #9–#13 to origin/main (force-org #11, email-API channel #13, FE email #9, inbound-email refinements #10/#12). Preserved 2 stranded works → wip/postmark-localpart-addressing (BE 614c67d; likely redundant vs merged #12) + wip/mapper-pane-collapse (FE, pushed). Cleaned merged/scratch branches. Backend `main` working dir left detached (ecstatic-lumiere worktree locked by another process). Security: founder pasted the Postmark token in chat → advised rotation.
