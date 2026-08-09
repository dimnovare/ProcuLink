# Data-residency ground truth — 2026-07-30

**Status:** research artefact. Establishes sourced facts only. **No marketing copy is written or
changed here.** This document is stage 1 of the rewritten WP-10 and blocks all residency copy work.

**Scope of evidence.**

| Ref | What was read |
|---|---|
| BE | Backend repo at `origin/main` (`63b89b5`), read in worktree `.claude/worktrees/residency` |
| FE | Frontend repo `project-proculink` at `origin/main` (`3b0feea`), read via `git show`/`git grep origin/main` |
| VD | Vendor documentation, fetched 2026-07-30 |
| MEAS | A measurement recorded in a prior session |

**Path convention below:** paths beginning `ProcuLink.*`, `docs/`, `STATUS.md`, `README.md`,
`railway.toml` are **backend**; paths beginning `src/`, `next.config.ts`, `vercel.json`,
`sentry.client.config.ts`, `instrumentation.ts`, `.env` are **frontend**
(`C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`). Ambiguous cases are prefixed `FE`/`BE`.

Every claim below is labelled **committed code (file:line)**, **doc (file:line)**, **vendor docs**,
**measurement**, or **inference**. Inferences are never presented as measurements.

**Security note.** No credential-bearing command was run. `railway variables` was not invoked in any
form. No key, token, password, DSN or connection string appears in this document. Where a fact lives
inside a secret-bearing string, only the non-secret shape (e.g. a hostname pattern) is discussed, and
the provenance is stated.

---

## 0. The distinction this document exists to preserve

> **Deploy region** = where a service runs.
> **Egress path** = where its outbound traffic actually leaves from.
>
> These are **different facts** and both can be true at once. A container in Amsterdam can egress
> through a NAT that geolocates to North Carolina. A customer asking *"does our order data cross the
> Atlantic"* is asking about the **path**, because the path is what their data travels.

Previous copy collapsed the two into a single "EU data residency" string. That collapse is the root
cause of the problem this document is fixing. **One value cannot carry both facts** — hence two
separate columns in §1.

---

## 1. Subprocessor inventory — deploy region vs egress path

10 vendors. Sourced from `src/lib/subprocessors.ts:53–115` (FE), cross-checked against code.

`UNSOURCED` means *not establishable from either repo or from vendor documentation* — it needs a
founder action (§4). It does **not** mean "probably fine".

### 1a. Data category actually reaching each vendor (verified in code, not assumed)

| # | Vendor | Data category that actually reaches it | How known |
|---|---|---|---|
| 1 | **Railway** | **Everything.** API + Worker run here; every order file, parsed order, supplier credential (AES-GCM at rest, decrypted in-process for dispatch) and generated artifact passes through this compute. | committed code — `railway.toml`, `Dockerfile`, `Dockerfile.worker`; `STATUS.md:873` |
| 2 | **Neon** | All persisted relational data: orders, canonical JSON, line items, buyer/supplier records, connection revisions, audit trail, **and the Hangfire job store**. | doc `STATUS.md:875` (`Neon Postgres (also hosts Hangfire)`); committed code `ProcuLink.Api/Program.cs:87–89` |
| 3 | **Cloudflare (R2)** | Raw uploaded source files + generated output artifacts, bucket `proculink` (private). Separately bucket `proculink-public` = marketing media only, no customer data. | committed code `ProcuLink.Api/appsettings.Production.json:20`; `ProcuLink.Infrastructure/Storage/R2StorageService.cs:10–14, 26`; doc `STATUS.md:876` |
| 4 | **Cloudflare (Worker + DNS)** | **Inbound email webhook bodies only** — the Postmark inbound payload (which contains the customer's order attachment) transits one Worker. Hard-gated to a single path. | committed code `docs/infra/postmark-inbound-verify-worker/worker.js:65–67, 181–189, 208–211` |
| 5 | **Vercel** | **No order data.** Marketing site + app shell (HTML/JS). Zero route handlers, zero server actions, `src/app/api/**` does not exist. The only server-side backend call is a boolean admin allow/deny carrying a Clerk JWT. Middleware inspects session identity, not order content. | committed code FE `src/lib/api/core.ts:29`; `src/lib/api-client.ts:370–374`; `src/lib/admin-guard.ts:52–56`; `src/middleware.ts:4–15` |
| 6 | **Clerk** | Authentication identity: user id, org id, email, name, session tokens. | committed code FE `src/middleware.ts:58–74`; BE `ProcuLink.Api/appsettings.Production.json:13–14` |
| 7 | **OpenAI** | **Order content.** PDF extracted text; scanned-PDF page images (vision fallback); XLSX/CSV workbook data; source column headers; line descriptions + buyer part codes; inbound email bodies; schema samples. Suppressed entirely for `SelfHostedOcr` / no-egress orgs. | committed code `ProcuLink.Infrastructure/Services/Ai/OpenAiPdfOrderExtractor.cs`, `OpenAiEmailBodyOrderExtractor.cs`, `OpenAiSchemaInferencer.cs`, `Services/OpenAiMappingService.cs:188, 590`, `OpenAiProductCodeSearch.cs`; gate `ProcuLink.Core/Entities/Organisation.cs:90–92`, `ProcuLink.Api/Services/Orders/OrderIngestionService.cs:1573, 1641, 1858` |
| 8 | **Stripe** | Billing identity + subscription state. No order content. | committed code `ProcuLink.Api/Program.cs:72–81`; `Services/StripeSubscriptionReconciliationService.cs:32` |
| 9 | **Postmark** | **BOTH directions, and the outbound direction carries the purchase order itself.** See §2 — this is the single most consequential finding. | committed code `ProcuLink.Infrastructure/Services/Dispatchers/EmailApiDeliveryDispatcher.cs:10–14, 34, 104–113` |
| 10 | **PostHog** | Pseudonymous product analytics. Client-side: `autocapture:false`, `disable_session_recording:true`, `mask_personal_data_properties:true`. Server-side: events tagged with the org group. | committed code FE `src/lib/analytics.ts:27–34`; BE `ProcuLink.Infrastructure/Services/PostHogAnalyticsService.cs:26` |
| 11 | **Sentry** | Error/exception telemetry from API, Worker and browser. | committed code `ProcuLink.Api/Program.cs:44–47`; `ProcuLink.Worker/Program.cs:33–43`; FE `sentry.client.config.ts:34`, `instrumentation.ts:12` |

> Rows 3 and 4 are both **Cloudflare** — `/subprocessors` lists Cloudflare once, covering "R2 object
> storage … and DNS". The inbound-email Worker is a *third* Cloudflare surface not named on the page.

### 1b. THE TABLE — deploy region and egress path as separate facts

| # | Vendor | **Deploy region** (where it runs) | **Egress path** (where our traffic to/from it actually leaves from) | SCCs per `/subprocessors` |
|---|---|---|---|---|
| 1 | **Railway** (API + Worker) | Claimed `europe-west4`, Netherlands — doc `src/lib/subprocessors.ts:57`, `privacy/page.tsx:63`, `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md:193`. **The identifier `europe-west4` is not a real Railway region id** — vendor docs list EU West Metal as **`europe-west4-drams3a`, Amsterdam NL** (vendor docs, railway.com/reference/regions). **No region is pinned in any committed file** (`railway.toml`, `nixpacks.toml`, both Dockerfiles, `docker-compose.yml`, all three workflows) → dashboard-only. **UNSOURCED** as an actual deploy fact. | **UNMANAGED — the app does nothing to control it.** Every outbound HTTP delivery ends at a bare socket: `OutboundRequestGuard.cs:257–263` `new Socket(...)` + `ConnectAsync(new IPEndPoint(ip, port))`. No proxy anywhere in the solution (zero hits for `WebProxy`, `IWebProxy`, `HttpClientHandler`, `HTTP_PROXY`, `HTTPS_PROXY`, `UseProxy`, `DefaultProxy`). The `ConnectCallback` design (`OutboundRequestGuard.cs:184–191`) would **bypass** an env proxy even if one were set. **One measurement: `152.55.184.78` = Durham, North Carolina, US** — `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md:1065`. Never resolved (`:1090`). | — (`Railway DPA`) |
| 2 | **Neon** | **AWS `eu-central-1` = Frankfurt, Germany** — *interpretation* is vendor-documented (neon.com/docs: hostname is `ep-<id>.<region-slug>.aws.neon.tech`; "the region is the segment before `.aws.neon.tech`"; `aws-eu-central-1` = Europe (Frankfurt)). **The hostname VALUE itself is UNSOURCED from any non-secret artefact** — see §3.2. `/subprocessors` says only `EU region` (`subprocessors.ts:63`). | Traffic originates from the Railway containers → row 1's unmanaged egress. Destination is EU. **No path measurement.** | — (`Neon DPA`) |
| 3 | **Cloudflare R2** (`proculink`, private) | **UNSOURCED.** `R2BucketName: "proculink"`, `R2Endpoint: ""` — `ProcuLink.Api/appsettings.Production.json:20–21` (empty in committed config; injected at runtime). Zero occurrences of `jurisdiction`, `locationHint`, `location_hint` or `CreateBucket` in **either** repo. `R2StorageService.cs:31–33` sets only `ServiceURL`, `ForcePathStyle`, `AuthenticationRegion="auto"` — `auto` is R2's universal signing region and carries **no** location meaning (vendor docs, R2 S3 API compatibility). See §3.1 for what would settle it. | Same as row 1 (Railway containers → unmanaged egress) for writes/reads. **Read path additionally uses pre-signed URLs fetched by the browser** (project memory: R2 rejects SDK chunked GET). **No path measurement.** | — (`Cloudflare DPA`) |
| 4 | **Cloudflare Worker** (inbound email verify) | **UNSOURCED.** Hand-deployed; **no `wrangler.toml` is committed in either repo**. Cloudflare Workers run at the edge PoP nearest the request by default. | **INBOUND ONLY. Cannot carry outbound deliveries** — 404s every path but `/api/inbound-email/postmark` (`worker.js:65–67, 181–189`), 405s every non-POST, and its forward target is hardcoded to ProcuLink's own origin (`worker.js:208–211`). Origin also remains directly reachable (`worker.js:30–32`: `api.proculink.eu is DNS-only to Railway`), so inbound is **not** universally CF-fronted. | — |
| 5 | **Vercel** | **UNSOURCED.** `vercel.json` is three lines with no `regions`. Zero `preferredRegion`, zero `export const runtime`, zero `regions` in any segment. Default Vercel region applies; source cannot name it. | Browser → Vercel for HTML/JS. **Order data never transits Vercel** (§1a row 5) — it goes browser → `api.proculink.eu` cross-origin. So Vercel's own egress is not on the order-data path at all. | **Yes** (`Vercel DPA + SCCs`) |
| 6 | **Clerk** | `/subprocessors` says `US, EU data residency available` (`subprocessors.ts:81`). **Whether EU residency is actually ENABLED on the production instance is UNSOURCED.** `clerk.proculink.eu` is a vanity Frontend-API domain, **not** a residency guarantee — it is derived at build time from the base64 payload of the publishable key (`FE src/lib/security/csp.ts:72–86`), not from any region setting. | Browser → Clerk FAPI directly (CSP `connect-src` includes the Clerk origin, `csp.ts:154–169`). API validates JWTs against `Clerk:Authority` (`appsettings.Production.json:13–14`), egressing via row 1. | **Yes** (`Clerk DPA + SCCs`) |
| 7 | **OpenAI** | **US.** `api.openai.com`. **Established negatively and definitively: there is no base-URL override anywhere in the solution.** All five call sites construct the SDK's two-arg client — `new ChatClient(_model, apiKey)` at `OpenAiEmailBodyOrderExtractor.cs:124,162`, `OpenAiPdfOrderExtractor.cs:259,294`, `OpenAiSchemaInferencer.cs:135,176`, `OpenAiMappingService.cs:143,177`, plus `OpenAiProductCodeSearch.cs` (`OpenAI.Responses`). **Zero occurrences of `OpenAIClientOptions` or any `Endpoint =` assignment.** No override → SDK default `https://api.openai.com`. **EU-residency project is NOT provisioned** — dashboard-verified 2026-07-24 ("no EU-residency project; no ZDR"), still open at `STATUS.md:573, 956`. **DPA IS signed** (same source, corrected 2026-07-26). | Railway containers → row 1 unmanaged egress → US endpoint. Transatlantic by construction. | **Yes** (`OpenAI DPA + SCCs`) |
| 8 | **Stripe** | `US, EU establishment` (`subprocessors.ts:93`). No region config in code; SDK default `api.stripe.com`. | API container → row 1 egress. Checkout/Portal are `window.location.href` redirects — **`stripe.js` is never loaded**, so Stripe needs no CSP directive (FE `docs/reports/2026-07-27-csp-and-sentry-hygiene.md:29–32`). | **Yes** (`Stripe DPA + SCCs`) |
| 9 | **Postmark** | **US — vendor-confirmed and not configurable.** postmarkapp.com/eu-privacy: *"Postmark's primary data and servers are hosted at Deft's data center (located outside of Chicago), and Amazon Web Services (AWS)"* and *"We currently don't have plans to add servers in the EU"*. Their DPA *"includes the new Standard Contractual Clauses (SCCs) for cross border transfers"*. | **Outbound:** API/Worker container → `https://api.postmarkapp.com/email` (`PostmarkEmailApiClient.cs:28`) → Postmark US → supplier. **Inbound:** `{slug}@orders.proculink.eu` MX → Postmark US → CF Worker → `api.proculink.eu`. **Both directions carry order content.** | **Yes** (`Postmark DPA + SCCs`) |
| 10 | **PostHog** | **EU cloud — established in code, both stacks.** BE default `Host = "https://eu.posthog.com"` (`PostHogAnalyticsService.cs:18`), committed prod config agrees (`ProcuLink.Api/appsettings.Production.json:78–80`). FE default `?? "https://eu.posthog.com"` in **two** places (`src/lib/analytics.ts:25`, `src/lib/security/csp.ts:108`), committed `.env:6` and `.env.example:40` agree. **Runtime env override is UNSOURCED** (Vercel/Railway env can override the default). | Browser → `eu.posthog.com` **directly** — `next.config.ts` has **no `rewrites()`** (`next.config.ts:27`: *"API lives on a separate origin — no rewrites needed"*), so there is no reverse proxy. Server events: Railway container → row 1 egress → EU endpoint. | — (`PostHog DPA`) |
| 11 | **Sentry** | **UNSOURCED.** DSN is env-only in all three runtimes: BE `Sentry:Dsn` empty in committed prod config (`ProcuLink.Api/appsettings.Production.json:54`, `ProcuLink.Worker/appsettings.Production.json:34`), read at `ProcuLink.Api/Program.cs:47` / `ProcuLink.Worker/Program.cs:43`; FE `process.env.NEXT_PUBLIC_SENTRY_DSN` (`sentry.client.config.ts:34`, `instrumentation.ts:12`). **Zero `sentry.io` hostnames in the backend repo.** The only region-bearing string in either repo is a **unit-test fixture**: `example.invalid` (FE `src/lib/security/csp.test.ts:13`) — `.de.` is Sentry's EU region, but its key segment is an obvious placeholder (`abc123def456`), so it is **a fixture, not proof of the production DSN**. | Browser → Sentry ingest directly (**no `tunnelRoute`** — zero hits repo-wide). API/Worker → Sentry ingest via row 1 egress. | — (`Sentry DPA`) |

---

## 2. Postmark carries the outbound purchase order — precise characterisation

This is not "email ingestion" as `/subprocessors` describes it. Confirmed in code:

- **`email` is an offered delivery protocol.** `EmailApiDeliveryDispatcher.Protocol => DeliveryProtocolConstants.Email`
  — `ProcuLink.Infrastructure/Services/Dispatchers/EmailApiDeliveryDispatcher.cs:34`.
- **The generated artifact is attached and sent to the supplier.** `:104–113` builds
  `EmailApiMessage(..., Attachments: new[] { new EmailApiAttachment(attachmentName, contentType, content) }, ...)`
  where `content` is the `byte[]` artifact passed into `DispatchAsync`. Default subject is
  `"Purchase Order " + fileNameWithoutExt` (`:81–83`); default body is
  `"Please find the attached purchase order (…)."` (`:85–89`).
- **It goes over Postmark's US HTTPS API.** `PostmarkEmailApiClient.cs:28` —
  `private const string SendUrl = "https://api.postmarkapp.com/email";`. The class doc at `:12`
  states the same. This is deliberate: Railway blocks outbound SMTP, so HTTPS-to-Postmark is the
  canonical email path (`EmailApiDeliveryDispatcher.cs:12–14`).
- **Registered in BOTH hosts**, so both the API and the Worker can send:
  `ProcuLink.Api/Program.cs:540` + `:643`; `ProcuLink.Worker/Program.cs:253` + `:258`.
- **Also carries transactional mail** — support contact form and notifications via
  `PostmarkEmailSender` (`ProcuLink.Infrastructure/Services/PostmarkEmailSender.cs:8–14`).
- **Inbound too**: `{slug}@orders.proculink.eu` → Postmark inbound → webhook. Docs disagree on one
  hop — `docs/infra/postmark-inbound-verify-worker/README.md:15–21` draws
  `Cloudflare Email Routing MX → Postmark inbound`, while a 2026-07-24 live DNS check
  (recorded in a handover prompt since deleted for carrying live production identifiers)
  found `orders.proculink.eu MX → inbound.postmarkapp.com` directly. Either way the mail lands in
  Postmark US; the discrepancy only affects whether Cloudflare also touches it.

**Net:** for every org using the `email` delivery channel, the purchase order **leaves the EU by
design**, through a US processor, on the way to the supplier. `/subprocessors` describes Postmark's
purpose as *"Inbound email ingestion (orders emailed to your ProcuLink address)"*
(`src/lib/subprocessors.ts:99`) — that is **materially incomplete**, and it is the strongest
contradiction of the `/security` line *"No data leaves the region without an explicit, contracted
subprocessor agreement."* An SCC-backed DPA exists, so the sentence is arguably technically
satisfiable — but a reader will not understand from it that their PO is emailed via Chicago.

---

## 3. The two open jurisdiction questions

### 3.1 Cloudflare R2 — what the repos can and cannot establish

**Can establish:**
- Two buckets: `proculink` (private, order data) and `proculink-public` (marketing media, custom
  domain `assets.proculink.eu`). `STATUS.md:876`; FE `scripts/demo-video/HELP-INTEGRATION.md:81–90`
  (*"Never the private `proculink` order-data bucket"*).
- The app **never creates a bucket** — zero `CreateBucket` calls, so no `LocationConstraint` and no
  `jurisdiction` is expressible from code. Both buckets were created by hand in the dashboard.
- `R2StorageService.cs:31–33` sets `AuthenticationRegion = "auto"`. Per vendor docs this is R2's
  universal SigV4 signing region and **means nothing about physical location**.
- `R2Endpoint` is empty in committed prod config (`appsettings.Production.json:21`,
  `ProcuLink.Worker/appsettings.Production.json:17`) and injected at runtime.

**Cannot establish:** the private bucket's **location hint** or **jurisdiction**. Zero occurrences
of `jurisdiction` / `locationHint` / `location_hint` in either repo.

**Why this matters — vendor docs draw a sharp line** (developers.cloudflare.com/r2/reference/data-location/):
- **Location Hints** are *"a best effort and not a guarantee"* — a performance optimisation.
- **Jurisdictional Restrictions** *"guarantee objects in a bucket are stored within a specific
  jurisdiction"*, explicitly *"when you need to ensure data is stored and processed within a
  jurisdiction to meet data residency requirements, including local regulations such as the GDPR"*.
- The `eu` jurisdiction is real; supported jurisdictions are `eu` and `fedramp`.

**The decisive tell is in the endpoint hostname.** Vendor docs: an EU-jurisdiction bucket **must** be
addressed at `https://<ACCOUNT_ID>.eu.r2.cloudflarestorage.com`; a non-jurisdiction bucket uses
`https://<ACCOUNT_ID>.r2.cloudflarestorage.com`. So the live `Storage__R2Endpoint` value already
answers the question — **but only its hostname shape is needed, never the account id**.

`/subprocessors` currently states `EU-region bucket` (`subprocessors.ts:69`) and `/privacy` states
`Cloudflare R2 (EU-region bucket)` (`privacy/page.tsx:62`). Both are **UNSOURCED** today. Note that
even if a *location hint* were set to an EU value, "EU-region bucket" would be a best-effort
statement, not a residency guarantee — the wording currently implies more than a hint delivers.

### 3.2 Neon — how the Frankfurt claim is (and is not) confirmed

- **Interpretation: vendor-documented.** neon.com/docs — the compute hostname is
  `ep-<endpoint_id>.<region-slug>.aws.neon.tech`, and *"the region is the segment before
  `.aws.neon.tech`"*. `aws-eu-central-1` = **Europe (Frankfurt)**. So *if* the hostname contains
  `eu-central-1`, Frankfurt follows.
- **The hostname value itself: NOT independently re-confirmed.** I grepped both repos for
  `neon.tech`, `eu-central-1`, `aws.neon`, `ep-`, `pooler`. **There is no Neon hostname anywhere in
  either repository.** `ConnectionStrings:DefaultConnection` is empty in committed prod config
  (`ProcuLink.Api/appsettings.Production.json:11`); the only literal connection strings are
  local-dev `Host=localhost;Port=5435` with throwaway credentials matching `docker-compose.yml:16–17`.
  Nothing to redact, and nothing to confirm from.
- The repos say only `Neon Postgres` with no region (`STATUS.md:875`) and `EU region` as prose
  (`subprocessors.ts:63`). **I could not re-confirm the region from a non-secret source.**
  Per the task brief the original hostname reading came from the leaked `railway variables` call and
  must not be re-derived that way.
- Related operational note: `STATUS.md:970` records the Neon **pooler** endpoint as dormant, so the
  live hostname is the direct (non-`-pooler`) form.

---

## 4. Established / unsourced / founder action

### 4.1 ESTABLISHED (safe to build copy on)

| # | Fact | How known |
|---|---|---|
| E1 | The app exercises **zero control over its egress IP**. No proxy, gateway, tunnel, NAT config or bind address exists in the solution; outbound delivery terminates in `new Socket(...)` + `ConnectAsync(IPEndPoint)`. A `ConnectCallback` would bypass an env proxy even if one were configured. | committed code `OutboundRequestGuard.cs:184–191, 257–263`; `HttpDeliveryDispatcher.cs:60–64`; exhaustive negative grep across the solution |
| E2 | **No egress-IP allowlist documentation exists** anywhere. Every `allowlist`/`firewall`/`source IP` hit in the repo is inbound-direction (Postmark → us). | committed code + docs, exhaustive grep |
| E3 | The one Cloudflare Worker is **inbound-only** and structurally cannot carry outbound deliveries. | committed code `worker.js:65–67, 181–189, 208–211` |
| E4 | **No deploy region is pinned in any committed file** in either repo — not `railway.toml`, `nixpacks.toml`, either Dockerfile, `vercel.json`, or any route segment. Region is a dashboard-only setting everywhere. | committed code, exhaustive read |
| E5 | **`europe-west4` is not a valid Railway region identifier.** The real EU West Metal id is `europe-west4-drams3a` (Amsterdam, NL). Railway docs name **no** underlying cloud provider. | vendor docs (railway.com/reference/regions) |
| E6 | **OpenAI traffic goes to `api.openai.com` (US).** No base-URL override exists at any of the 5 call sites; `OpenAIClientOptions` appears zero times. | committed code, 9 constructor sites listed in §1b row 7 |
| E7 | **OpenAI receives real order content** — PDF text, page images, workbook data, column headers, line descriptions, buyer part codes, email bodies — unless the org is flagged no-egress. | committed code, gate at `Organisation.cs:90–92` + `OrderIngestionService.cs:1573, 1641, 1858` |
| E8 | **OpenAI EU-residency project and ZDR are NOT provisioned** (dashboard-verified). The **DPA is signed**. | dashboard verification 2026-07-24; `STATUS.md:573, 956` |
| E9 | **Postmark carries the outbound purchase order as an email attachment**, over `https://api.postmarkapp.com/email`, registered in both API and Worker. | committed code — §2 |
| E10 | **Postmark is US-only and says so**; its DPA includes SCCs. There is no EU-residency option to enable. | vendor docs (postmarkapp.com/eu-privacy) |
| E11 | **PostHog is EU by default in both stacks**, hardcoded in three places and matching committed env. No reverse proxy — the browser talks to `eu.posthog.com` directly. | committed code BE `PostHogAnalyticsService.cs:18` + `appsettings.Production.json:80`; FE `analytics.ts:25`, `csp.ts:108`, `.env:6`, `next.config.ts:27` |
| E12 | **Vercel does not process order data.** No route handlers, no server actions, no `src/app/api/**`. Uploads and all order traffic go browser → `api.proculink.eu` cross-origin. | committed code FE `core.ts:29`, `api-client.ts:370–374`, `middleware.ts`, `admin-guard.ts:52–56` |
| E13 | **`/security` renders all 10 subprocessors, not 7** — the card is `SUBPROCESSORS.map(...)` from the single source list, so it structurally cannot diverge from `/subprocessors`. | committed code FE `security/page.tsx:3, 74, 211` |
| E14 | **R2 jurisdiction and location hint are absent from both repos**, and the app never creates a bucket, so neither is expressible from code. | committed code, exhaustive grep |
| E15 | Cloudflare's own docs draw the line: **location hint = "best effort and not a guarantee"; jurisdictional restriction = "guarantee"**. Only the latter is a residency control. | vendor docs (r2/reference/data-location) |
| E16 | Neon's hostname **encodes** the region; `aws-eu-central-1` is Frankfurt, Germany. | vendor docs (neon.com/docs) |

### 4.2 UNSOURCED (must not appear in copy as fact)

| # | Unknown | Current copy that asserts it anyway |
|---|---|---|
| U1 | **The actual Railway deploy region of the API and Worker services.** | `subprocessors.ts:57` `EU (europe-west4, Netherlands)`; `privacy/page.tsx:63` |
| U2 | **The actual egress geography of outbound deliveries today.** One measurement (US) exists; it is 13 months old and was never re-taken. | implied by every `EU data residency` string |
| U3 | **The private R2 bucket's jurisdiction / location hint.** | `subprocessors.ts:69` `EU-region bucket`; `privacy/page.tsx:62` |
| U4 | **The public bucket `proculink-public` jurisdiction.** (Marketing media only — low stakes, but equally unsourced.) | not claimed |
| U5 | **The Neon hostname value** (hence the region) from a non-secret source. | `subprocessors.ts:63` `EU region`; `privacy/page.tsx:64` |
| U6 | **The production Sentry DSN region.** Only a test fixture shows `.de.`. | `subprocessors.ts:112` `EU region`; `privacy/page.tsx:65`; `dpa/page.tsx:123` |
| U7 | **Whether Clerk EU data residency is actually enabled** on the production instance. | `subprocessors.ts:81` correctly says only `available` — no over-claim here |
| U8 | **The runtime `NEXT_PUBLIC_POSTHOG_HOST` / `Analytics:PostHog:Host`** actually set in Vercel/Railway (defaults are EU; an override is possible). | `subprocessors.ts:105` `EU (eu.posthog.com)` |
| U9 | **The Vercel deploy region** for functions/middleware. | `subprocessors.ts:75` `Global CDN, source data EU` — vague enough to survive |
| U10 | **Which Cloudflare edge PoPs run the inbound-email Worker.** | not claimed anywhere |

### 4.3 FOUNDER ACTIONS — exact steps to settle each

| # | Settles | Exact action | Non-secret? |
|---|---|---|---|
| **FA-1** | U3, U4 | Cloudflare dashboard → **R2 object storage** → bucket `proculink` → bucket detail shows **Location** and **Jurisdiction**. Repeat for `proculink-public`. Equivalent CLI: `wrangler r2 bucket info proculink`. Record the literal Jurisdiction value (`eu` / none) and the Location. | Yes — no secret involved |
| **FA-2** | U3 (cross-check) | Read **only the hostname shape** of the Railway variable `Storage__R2Endpoint` and report whether it contains `.eu.r2.cloudflarestorage.com` or plain `.r2.cloudflarestorage.com`. **Do not print the account id.** Use a filtered read, never an unfiltered `railway variables`. | Partially — filter, and report only the `.eu.` yes/no |
| **FA-3** | U1 | Railway dashboard → project → service `ProcuLink` → **Settings → Region**; repeat for Worker `aware-amazement`. Record the full identifier (expect `europe-west4-drams3a`, not `europe-west4`). Confirm **both** services, not just the API — a Worker in a different region would be invisible today. | Yes |
| **FA-4** | U2 — **the highest-value action** | Re-measure the delivery egress IP with founder authorisation. Method: configure a throwaway HTTP delivery target you control (or an echo endpoint), fire one real delivery from production, read the source IP from your own server logs. Do this from **both** the API and the Worker (Worker sends most deliveries). Geolocate. Record IP + date + which service. Repeat after any FA-3 region change. **Not attempted here — this requires a live production request.** | Yes |
| **FA-5** | U5 | Read **only the host portion** of the Neon connection string — from the **Neon dashboard** (Project → Connection details), not from Railway. Confirm whether the segment before `.aws.neon.tech` is `eu-central-1`. Neon's dashboard also shows the region name directly, which avoids touching the connection string at all. **Prefer the dashboard region label.** | Yes via dashboard region label |
| **FA-6** | U6 | Read the **host portion** of `NEXT_PUBLIC_SENTRY_DSN` from the Vercel project env and `Sentry:Dsn` from Railway (filtered), or simply read the Sentry org's region in the Sentry dashboard (Settings → shows EU/US). `.ingest.de.sentry.io` = EU. The Sentry dashboard route avoids secrets entirely — **prefer it**. | Yes via dashboard |
| **FA-7** | U7 | Clerk dashboard → instance → check whether EU data residency is enabled. If it is not, `/subprocessors`' `US, EU data residency available` remains accurate and no copy change is needed — but `/privacy`'s framing should be read alongside it. | Yes |
| **FA-8** | U8 | Read the **names and values** of `NEXT_PUBLIC_POSTHOG_HOST` (Vercel) and `Analytics__PostHog__Host` (Railway, filtered). A host is not a secret. Alternatively curl a production page and read `connect-src` in the CSP header — it is derived from the same variable. | Yes |
| **FA-9** | U9 | Vercel → Project Settings → **Functions → Region**. | Yes |
| **FA-10** | E8 follow-through | Decide whether to provision an OpenAI EU-residency project + ZDR, or to state plainly that AI extraction is US-processed under DPA + SCCs. This is a **product decision**, not a research gap. | Yes |
| **FA-11** | §2 | Decide whether the `email` delivery channel stays on Postmark (US) or moves to an EU-hosted transactional provider. Until then, `/subprocessors`' Postmark purpose string must be corrected to name outbound PO delivery. | Yes |

---

## 5. Do `/security` and `/subprocessors` agree?

**Structurally, yes — by construction.** `/security`'s subprocessor card is
`SUBPROCESSORS.map((s) => [s.name, s.purpose])` (FE `security/page.tsx:74`, rendered `:211`), so the
vendor *set* and *purposes* can never diverge. The card shows name + purpose only; location lives one
hop away at `/subprocessors`.

**The task's premise that the card names 7 is not true of `origin/main`.** It names all 10. If a
7-vendor card exists it is in an uncommitted draft, not in the shipped page. **This is good news** —
there is no vendor-set contradiction to fix.

**But the two pages contradict each other semantically, in four places:**

| # | `/security` says | `/subprocessors` says | Verdict |
|---|---|---|---|
| C1 | *"All order data is processed and stored in EU-region infrastructure. No data leaves the region without an explicit, contracted subprocessor agreement."* (`security/page.tsx:41`) | Four vendors are **US**: Clerk `US, EU data residency available` (`:81`), OpenAI `US` (`:87`), Stripe `US, EU establishment` (`:93`), Postmark `US` (`:99`) | **Contradiction in effect.** The escape clause technically covers it — all four carry `DPA + SCCs` — but "all order data … EU" followed by "OpenAI: US" processing real order content (E7) reads as a contradiction to any careful customer. |
| C2 | Same line 41 | Postmark's purpose is stated as **inbound only** (`:99`), concealing that the outbound PO transits US (§2) | **Contradiction of fact.** The purpose string is materially incomplete. |
| C3 | — | Railway `EU (europe-west4, Netherlands)` (`:57`) | **Factually wrong identifier.** `europe-west4` is not a Railway region id (E5). The file's own rule at `subprocessors.ts:7–14` says *"Locations must be checkable … write the real region, never a guessed city"* — the file violates its own rule. |
| C4 | — | Cloudflare `EU-region bucket` (`:69`), Neon `EU region` (`:63`), Sentry `EU region` (`:112`) | **Unsourced** (U3, U5, U6). Not necessarily wrong; simply unevidenced. |

**Wider drift beyond these two pages** (same claims, more places to fix once settled):
`privacy/page.tsx:59–66` (six per-vendor location lines, the most specific claims on the site);
`dpa/page.tsx:76–81, 123`; `pricing/page.tsx:137, 330–331`; `one-pager/page.tsx:93`;
home hero stat `(home)/page.tsx:203` (`EU` / `Data residency`); footers
`(home)/page.tsx:940` and `(marketing)/layout.tsx:68`; sign-in/sign-up `:139`;
in-app `settings/page.tsx:328–330` (`Workspace region: EU`, hardcoded).

**One already-corrected drift, for the record:** a stale committed artefact
`pricing_audit.html:170` (a saved render of the live `/pricing` page) says
*"All order data is stored in the EU (Frankfurt)"* and *"EU data residency (Frankfurt)"*.
"Frankfurt" appears **nowhere else in the backend repo** and nowhere in `src/` in the frontend. If
that string is still live anywhere it is a **precise claim with no source** — exactly the failure
mode this document exists to prevent. (Neon *is* plausibly Frankfurt per E16, but compute is claimed
Netherlands — naming one city for "all order data" collapses two different vendors' locations.)

---

## 6. What the copy may honestly claim TODAY

Given only §4.1, and **pending FA-1 through FA-9**.

### 6.1 Defensible today

- *"Your order data is stored in the EU."* — **only after FA-1 and FA-5 come back EU.** Not yet.
- *"We use a fixed, published list of subprocessors, with 30 days' notice before any change."* —
  fully backed by `subprocessors.ts:27–39` + `subprocessors/page.tsx:100–121`, and enforced by a test
  (`legalCommitments.test.tsx:56–80`). **This is the strongest honest claim on the page and it is
  currently underplayed.**
- *"Product analytics run on PostHog's EU cloud."* — E11. The most solidly sourced residency fact in
  the entire stack. (Add "by default" if FA-8 has not been run.)
- *"Vercel serves our website and app shell; your order data never passes through it — uploads and
  order traffic go directly to our EU API."* — E12. Precise, verifiable, and stronger than the
  current vague `Global CDN, source data EU`.
- *"Every subprocessor is under a DPA; those outside the EEA are covered by Standard Contractual
  Clauses."* — matches `dpa/page.tsx:76–81` and is independently true for Postmark (E10) and OpenAI
  (E8, DPA signed).
- *"Orgs on our no-egress mode have their documents processed entirely without sending anything to
  OpenAI."* — E7's gate is real code with tests asserting `Times.Never`.
- *"Supplier delivery credentials are encrypted with AES-256-GCM and never written to logs."* —
  unrelated to residency, already true, already on the page.

### 6.2 Must NOT be claimed today

- ❌ **`europe-west4`** — not a real identifier (E5). Do not replace it with `europe-west4-drams3a`
  either until FA-3 confirms; substituting a *more precise* wrong string is the exact failure this
  work exists to prevent.
- ❌ **Any city name** — "Frankfurt", "Amsterdam", "Eemshaven". Compute and database are different
  vendors in different countries; one city name cannot describe both.
- ❌ **"EU-region bucket"** for R2 — U3. And even a confirmed *location hint* would not license it
  (E15); only a confirmed **`eu` jurisdiction** would.
- ❌ **"Sentry EU region"** — U6.
- ❌ **"All order data is processed and stored in EU-region infrastructure"** as an unqualified
  sentence — contradicted in effect by OpenAI (E6/E7) and Postmark (E9/E10).
- ❌ **Anything implying outbound traffic leaves from the EU.** E1 + the single US measurement (U2)
  mean the honest position today is *silence on egress*, not a claim.
- ❌ **Any named AWS/GCP provider for Railway** — Railway's docs name none (E5).

### 6.3 The one sentence the page is missing

Nothing in `/security` or `/subprocessors` currently distinguishes **storage** from **transit**. A
truthful page needs a sentence to the effect of: *data at rest lives in EU infrastructure; some
processing and delivery steps involve non-EU subprocessors under SCCs, named individually on
`/subprocessors`; the network path by which we reach your supplier is determined by our hosting
provider and is not currently pinned to a region.*

That last clause is uncomfortable, and it is also the honest answer to *"does our order data cross
the Atlantic"*. **A precise false claim is harder to defend than a vague one — and an honest
limitation is easier to defend than either.** If FA-4 comes back US again, that sentence is not
optional.

---

## 7. Single most important finding

**Nothing in the codebase can control, pin, or predict where ProcuLink's outbound traffic leaves
from — and one measurement says it left from the United States.**

The delivery path terminates in a bare `new Socket(...)` + `ConnectAsync(new IPEndPoint(ip, port))`
(`OutboundRequestGuard.cs:257–263`) reached through a `ConnectCallback` that would **bypass an
environment proxy even if one were configured** (`:184–191`). There is no proxy, gateway, tunnel or
egress relay anywhere in the solution. The Cloudflare Worker that exists is inbound-only and
structurally incapable of carrying a delivery (`worker.js:65–67, 181–189, 208–211`).

Two consequences that copy must respect:

1. **Changing the Railway region would move the egress IP but would not make it stable or
   publishable.** There is no code path today that could give a supplier a fixed IP to allowlist;
   that would be new engineering work, not a config flip.
2. **A supplier reading their own server logs sees whatever source IP the host's NAT presents** —
   which, on the only occasion anyone looked, was Durham, North Carolina
   (`docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md:1065`, refiled unresolved at `:1090`,
   13 months ago).

This is why deploy region and egress path must stay as two columns. **FA-4 is the single highest-value
founder action in this document** — it is the only one that answers the question a customer is
actually asking.
