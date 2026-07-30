# Capability Truth Ledger

**Purpose.** Replace the periodic capability audit with a build error. A prose matrix rots between audits; this ledger is the single source that in-app copy, marketing copy and CI all read from.

**Precedent that already works:** `FE/src/lib/standards/catalog.ts` and `FE/src/lib/marketing/format-catalog.ts` already derive `/formats` and the landing-page counts from typed rows, and the build **throws on a typo'd id**. That is the one anti-drift mechanism in the product that holds. This ledger generalises it to channels, feature systems and trust claims.

**Target implementation (`WP-40`):** `FE/src/lib/capability-ledger.ts` — a typed constant, plus a CI check that fails when a user-facing string claims something no row supports.

---

## Schema

| Field | Meaning |
|---|---|
| `id` | stable kebab id, referenced by copy |
| `claim` | the sentence a customer may read |
| `documented` | a help article or marketing page describes it |
| `implemented` | code exists that does it |
| `exposed` | a customer can reach it in the UI — **name the route** |
| `selfService` | a customer can configure and use it with no founder involvement |
| `tested` | a test that would go RED if it broke — **name it**. An env-gated silent return is NOT a test |
| `liveProven` | proven on production — **evidence link required**: order id, attempt id, receiver capture, or dated QA doc |
| `honestLabel` | the in-product wording when any column is false |

**Rule:** `liveProven` without an evidence link fails CI. `exposed && !selfService` must carry an `honestLabel`.

---

## Current state — 2026-07-27 baseline

`✓` yes · `~` partial · `✗` no · `?` unknown, needs WP-03/WP-38/WP-39

### Inbound transports

| id | doc | impl | exposed | self-svc | tested | live | note |
|---|:-:|:-:|:-:|:-:|:-:|:-:|---|
| `in-upload` | ✓ | ✓ | `/upload` | ✓ | ✓ | ✓ | named prod order ids |
| `in-email-hosted` | ✓ | ✓ | `/settings?tab=email` | ✓ | ✓ | ✓ | real mail, ~3 s |
| `in-rest` | ✓ | ✓ | `/settings?tab=api` | ✓ | ✓ | ✓ | supplier-by-name resolution |
| `in-sftp-pull` | ✓ | ✓ | supplier Delivery tab | ~ | ~ env-gated | ✗ | local `atmoz/sftp` only |
| `in-s3-pull` | ✓ | ✓ | supplier Delivery tab | ~ | ~ env-gated | ✗ | local MinIO only |
| `in-imap` | ✓ | ✓ | `/settings?tab=email` | ~ | **✗ dead since `de4ea0e`** | ✗ | never run against a real mailbox |
| `in-webhook-ack` | — | — | — | — | — | — | **RETIRED by founder decision 2026-07-27** (WP-09). Never reachable — the org HMAC secret had no writer, so every callback 401'd. Remove from `/formats`, the help centre and all marketing. Re-introduce only for a named customer |

### Outbound delivery

| id | doc | impl | exposed | self-svc | tested | live | note |
|---|:-:|:-:|:-:|:-:|:-:|:-:|---|
| `out-http` | ✓ | ✓ | supplier Delivery tab | ✓ | ✓ | ✓ | all 6 formats, receiver-verified |
| `out-email` | ✓ | ✓ | supplier Delivery tab | ✓ | ✓ | ✓ | external recipient, Postmark |
| `out-sftp` | ✓ | ✓ | supplier Delivery tab | ~ | **✗ no happy path** | ✗ | + no host-key verification (WP-38) |
| `out-ftps` | ✓ | ✓ | supplier Delivery tab | ~ | **✗** | ✗ | |
| `out-erp-erply` | ✓ | ✓ | supplier Delivery tab | ~ | ~ shape only | ✗ | no sandbox creds |
| `out-erp-directo` | ✓ | ✓ | supplier Delivery tab | ~ | ~ shape only | ✗ | no sandbox creds |
| `out-peppol-as2-as4` | ✗ | **✗** | ✗ | ✗ | ✗ | ✗ | not built, not claimed — keep it that way |

### Formats — inbound parse

| id | impl | exposed | tested | live | note |
|---|:-:|:-:|:-:|:-:|---|
| `parse-csv` | ✓ | ✓ | ✓ | ✓ | EU-locale detection bound to `;` — a comma/tab EU file misreads |
| `parse-xlsx` | ✓ | ✓ | ✓ | ✓ | LLM-primary; deterministic fallback has a `NumberStyles.Any` locale bug and no `NeedsReview` contract |
| `parse-pdf-text` | ✓ | ✓ | ✓ | ✓ | line regex cannot express a grouped EU number — matching lines dropped |
| `parse-pdf-scanned` | ~ | ✓ | ✓ | ~ | every line review-flagged — correct, and honestly stated |
| `parse-xml-generic` | ✓ | ✓ | ✓ | ✓ | |
| `parse-cxml` | ✓ | ✓ | ✓ | ✓ | real fixtures: Coupa, Nasdaq, DPD, Nestlé, KSB/Ariba, Maersk |
| `parse-ubl` | ✓ | ✓ | ✓ | ✓ | |
| `parse-peppol-bis` | ~ | ✓ | ~ | ~ | correctly marked `partial` in the catalogue |
| `parse-edifact-orders` | ~ | ✓ | ✓ | ~ | |
| `parse-x12-850` | ✓ | ✓ | ✓ | ✓ | |
| `parse-idoc` | ✓ | ✓ | ✓ | ✓ | deepest parser — 8 real sanitised SAP docs |
| `parse-ubl-invoice` | ✓ | ✓ | ~ | ✗ | pre-fix naive number reader: `0m` on failure, fabricates today's date |
| `parse-edifact-invoic` | stub | ✓ | ✓ | — | honest "coming soon" |
| `parse-desadv` | stub | ✓ | ✓ | — | 501, honest |

### Formats — outbound emit

| id | impl | exposed | tested | live | note |
|---|:-:|:-:|:-:|:-:|---|
| `emit-xml` `emit-csv` `emit-json` | ✓ | ✓ | ✓ | ✓ | |
| `emit-cxml` | ✓ | ✓ | ✓ | ✓ | delivered as `application/octet-stream` / `.dat` (WP-20) |
| `emit-ubl-peppol` | ✓ | ✓ | ✓ | ✓ | **stamps Peppol BIS 3 conformance ids on a non-BIS-conformant doc** — verify or soften |
| `emit-x12-850` | ✓ | ✓ | ✓ | ✓ | ISA/GS envelope implemented + typed FE, **zero authoring UI** |
| `emit-custom-tree` | ✓ | ✓ | ✓ | ✗ | JSON/XML/CSV only; **not reusable across orders** (WP-12) |
| `emit-scriban` | ✓ | ✓ | ✓ | ✗ | sandboxed, with an in-product tester |
| `emit-edifact` | **✗** | — | — | — | no output transformer. Honestly marked. Do not build |

### Feature systems

| id | impl | exposed | self-svc | tested | live | note |
|---|:-:|:-:|:-:|:-:|:-:|---|
| `supplier-routing` | ✓ | ✓ | ✓ | ✓ 27-cell real-PG | ✓ | strongest journey in the product |
| `item-code-learning` | ✓ | ✓ | ✓ | ✓ | ✓ | 14,713-row catalog proven; case-sensitivity mismatch to fix (WP-14) |
| `ai-suggestions` | ✓ | ✓ | ✓ | ✓ | ✓ | catalog-grounded, confidence-floored, silent when ambiguous |
| `reusable-input-mapping` | ✓ | ✓ | ~ | ✓ | ~ | promote endpoint has **no UI caller** (WP-13) |
| `reusable-output-mapping` | ✓ | **✗** | **✗** | ✓ | ✗ | no editor exists for `PoMappingConfig.Output` |
| `output-designer` | ✓ | ✓ | **✗** | ✓ | ✗ | per-order only — the wedge, unfinished (WP-12) |
| `output-templates-page` | **✗ orphan** | ✓ | ✗ | ~ | ✗ | writes to a table nothing reads; body silently discarded (WP-06) |
| `validation-rules` | — | — | — | — | — | **RETIRED by founder decision 2026-07-27** (WP-09/WP-07). Was working CRUD with zero evaluator. `acceptance-profiles` below is now the ONE rules concept in the product. Do not migrate the six seeded defaults as data — they were never evaluated, so importing them would silently start blocking orders |
| `acceptance-profiles` | ✓ | ✓ | ~ | ✓ | ✗ | **browser-only enforcement**, and none below 1024px (WP-17/18) |
| `revision-pinning` | ✓ | ✓ | ~ | ✓ | **✓ flag ON** | `Connections__RevisionAuthority = true` on BOTH Railway services, verified 2026-07-27. Reproducibility is LIVE. The audit's "inert in production" P0 is **refuted**. Still unproven: that a pinned order does not re-route after a live config edit (WP-21) |
| `replay-impact-diff` | ✓ | ✓ | ~ | ✓ | ✗ | shows impact; cannot re-process (WP-35) |
| `retry-dead-letter` | ✓ | ✓ | ~ | ✓ real PG | ✓ | genuinely strong; any 4xx dead-ends (WP-19) |
| `order-passport` | ✓ | ✓ | ~ | ✓ | ✓ | no bytes, no hash (WP-34) |
| `billing-gates` | ~ **6 of 16** | ✓ | ✓ | ✓ | ✓ | Stripe proven both directions on live infra |
| `tenant-isolation` | ✓ | — | — | ~ 1 of 87 endpoints | — | app-level scoping measured clean: 7 of 207 sites Id-only, all safe |

### Trust & compliance claims

| id | claim as shipped | true? | action |
|---|---|:-:|---|
| `eu-residency` | "All order data is processed and stored in EU-region infrastructure" | **NO — and one value cannot carry the answer** | Superseded by the residency table below. See AUDIT-2026-07-27.md section 11b. **WP-10 must establish both columns before asserting anything.** |

### Residency — DEPLOY REGION and EGRESS PATH are two different facts

Collapsing them into one value is how the copy became false. A container deployed in Amsterdam can
egress through a NAT that geolocates to Durham, NC — **both measurements are true simultaneously**, and
the customer asking "does our order data cross the Atlantic" is asking about the **egress path**, because
that is what their data actually travels. One value cannot carry both facts, so the ledger carries two.

| Leg | Deploy region | Source | Egress path | Source |
|---|---|---|---|---|
| Railway (API + Worker) | **EU West "Metal" = Amsterdam, Netherlands**. Identifier is **`europe-west4-drams3a`** | Railway regions doc, fetched 2026-07-30 | **UNKNOWN** — measured once at `152.55.184.78` (Durham, NC, US) | `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md:1065`, filed again `:1090`, never resolved |
| Neon (Postgres) | **AWS `eu-central-1`, Frankfurt, Germany** | the region is in the host itself: `ep-…-c-3.eu-central-1.aws.neon.tech`. ⚠️ read from the `railway variables` call that leaked three keys on 2026-07-27 — **do not re-derive it that way**; use the Neon console or a host-only grep | UNKNOWN | — |
| Cloudflare R2 | **UNKNOWN** — `R2Endpoint` is empty at `appsettings.Production.json:21` | — | UNKNOWN | — |
| Postmark | **US** — and it carries the **OUTBOUND PO** as an attachment, not just inbound mail | `EmailApiDeliveryDispatcher.cs:109`; its test asserts the attachment is `PO-1.xml` | US | same |
| OpenAI | **US** — receives PO line text | `appsettings.Production.json` sets `Ai:Provider=openai`; no endpoint override exists in the repo | US | same |

**Corrections logged against this table.** "`europe-west4`" is not a Railway identifier — the real one is
`europe-west4-drams3a`, so the shipped copy quotes a string the vendor does not use. And this plan
previously asserted Railway "runs on AWS": **retracted** — Railway's docs name no cloud provider for any
region and describe them as its own hardware. That AWS evidence was real but belonged to **Neon**; two
providers were conflated inside one sentence. Same failure shape as the rest of the correction log, with
the unchecked step being *which subject each fact belonged to*.
| `subprocessor-dpa` | OpenAI DPA + SCCs | ✓ | corrected in `c315a76` (#66). **Audit claim was wrong — do not re-open** |
| `customers-pilots` | two sized pilot profiles | **✗** | contradicted by production inventory. Replace (WP-10) |
| `format-counts` | hero "10 inbound / 6 outbound / 6 channels" | ✓ | derived from the catalogue, build-time checked |
| `no-fabricated-stats` | no invented metric, testimonial or logo wall | ✓ | earlier cleanup; has held. **Keep it that way** |
| `plan-ladder` | prices, order limits, supplier limits | ✓ | FE `plans.ts` matches `PlanConstants` exactly — **`CLAUDE.md §11.5` is the stale one** |
| `self-hosted-no-egress` | "runs entirely in your environment" | **✗** | not a product a customer can buy or enable. Remove or build |

---

## Open unknowns — resolve, then update this file

| # | Unknown | How to settle | Packet |
|---|---|---|---|
| 1 | ~~Is `Connections:RevisionAuthority` on in production?~~ | **RESOLVED 2026-07-27 — YES, `true` on both the `ProcuLink` API and the `aware-amazement` Worker.** ⚠️ run `railway variables` filtered through `grep`; an unfiltered call leaked three live secrets | ~~WP-03~~ done |
| 2 | Is `unrouted` reachable on production since `3a12f22`? | one test email with the org default cleared | WP-03 |
| 3 | Do SFTP / FTPS / ERP delivery work **at all**? | one real transfer each, SHA-256 compared at the receiver | WP-38 |
| 4 | Does the authenticated production UI behave as the code suggests? | recorded pass through all 12 journeys, both viewports | WP-39 |
| 5 | Is the ICP outbound or inbound? | the only two real customer POs in the repo (`real-cxml-1.2-ariba-punchout-mpn-differs.xml`, `real-cxml-1.1-mpn-equals-supplier-part.xml`) are orders **received by** the ProcuLink user. Ask the customer | founder |
| 6 | Does UBL output actually satisfy Peppol BIS 3? | validate an emitted doc against the BIS 3 schematron | WP-40 |

Unknown 4 is the largest evidence gap in the entire audit: **every UI finding in this plan is code- or mock-derived**, because production could not be reached with an authenticated session and `docs/design-system/current-ui-screenshots-2026-06-26/` is empty.
