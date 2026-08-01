# Work Packets — WP-01 … WP-41

Every packet is self-contained: an agent should be able to execute it from this entry plus the named files, without re-exploring the repo. That is deliberate — re-exploration is where the token budget goes.

**Repo shorthand:** `BE` = `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink` · `FE` = `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`

**Every packet carries, without exception:**
- a RED-first test that fails before the change and passes after (R2)
- a named regression test that would catch this exact defect returning
- `superpowers:verification-before-completion` before any "done" claim
- `code-review:code-review` before merge

**Legend:** `S` ≤ half a day · `M` 1–3 days · `L` 4–8 days · `XL` > 8 days

---

# WAVE 0 — Ground truth & guardrails

## WP-01 · CI runs the tests we already wrote — `S` — risk: none
**Why:** `.github/workflows/ci.yml` has exactly two jobs, `build` and `test-e2e`. The strings `bun run test` and `bun run lint` appear nowhere. **105 vitest files / 1096 assertions gate nothing.**
**Files:** `FE/.github/workflows/ci.yml`
**Do:** add a `test-unit` job — `bun run test`, `bun run lint`, `bun run check:pageshell --strict`, `bun run lint:vocab` — on the same push/PR triggers as `build`.
**AC:** a deliberately-broken assertion fails the PR check. All four commands run on every PR.
**Tests:** the proof is the broken-assertion run itself; capture the failing check URL in the PR body.
**Deps:** none. **Do this first.** **Skills:** none needed.

## WP-02 · No test may pass vacuously — `M` — risk: low
**Why:** every live-transport test is an env-gated silent `return` that CI counts as PASSED. `Live_ImapIngress` has been dead since `de4ea0e` and "2 skipped" hid it. 26 of ~85 Playwright tests are double-gated; one skips itself mid-body.
**Files:** `BE/ProcuLink.Api.Tests/**/Live*Tests.cs`, `BE/**/Services/Ingress/*`, `BE/**/Services/Catalog/*`, `FE/tests/**`, `FE/playwright.config.ts`
**Do:** convert every env guard to the **static-skip attribute** already used correctly by the 155 real-Postgres tests across 47 classes. Delete or repair `Live_ImapIngress`. Remove the mid-body skip.
**AC:** `dotnet test ProcuLink.slnx` prints an explicit skip **with a reason** for every un-runnable test; zero tests return early while reporting Passed.
**Tests:** a meta-test asserting no test method body contains a bare early `return` on a missing env var.
**Deps:** none. **Skills:** `superpowers:test-driven-development`.

## WP-03 · Two production truth-checks — `S` — risk: none — **DO BEFORE WAVE 3 PLANNING**
**Why:** two P0-severity findings hinge on facts nobody has read. Either answer may delete work.
**Do:**
1. `railway variables | grep -i RevisionAuthority` — is `Connections:RevisionAuthority` set on the API and Worker services? `EffectiveConnectionConfigResolver.cs:18,32,39-40` gates the entire versioning story on it; `appsettings.Development.json:46` sets it true and production config does not.
2. With the org default cleared, send one real email to `{slug}@orders.proculink.eu` and confirm the order parks `unrouted`. `3a12f22` (#68) removed the oldest-active-supplier fallback in code but this has never been re-measured live.
**STATUS 2026-07-27 — check 1 DONE, check 2 OPEN.**
Check 1 result: **`Connections__RevisionAuthority = true` on BOTH Railway services** (`ProcuLink` API and `aware-amazement` Worker). Revision authority is ON in production; the audit's P0 is refuted and WP-21 is rescoped accordingly.
⚠️ **Run `railway variables` FILTERED.** An unfiltered call on 2026-07-27 printed the live OpenAI API key, the PostHog project key and the Neon Postgres password into a session transcript — all three now need rotating. Always pipe through `grep` for the single key you need.
**AC:** both answers written into `04-CAPABILITY-TRUTH-LEDGER.md` with a date.
**Deps:** none. Read-only except the test email. **Skills:** none.

## WP-04 · Orphan guard — `M` — risk: low
**Why:** R1 needs teeth. Three navigable surfaces reached production writing to stores nothing reads.
**Files:** new `BE/ProcuLink.Api.Tests/Architecture/OrphanGuardTests.cs`; new `FE/src/test/route-reachability.test.ts`
**Do:** (a) BE test — every `DbSet<T>` on `ProcuLinkDbContext` has at least one reader outside its own CRUD service/controller, or appears in an explicit `KnownWriteOnly` allowlist with a written reason. (b) FE test — every `page.tsx` route is reachable from the nav registry, a hub tab, a redirect, or an explicit `KnownDeepLinkOnly` allowlist.
**AC:** the guard is RED today on `OutputTemplate`, `ValidationRule`, `/drafts`, `/upload/preview/[orderId]`, `/library/rule-definitions` — and green after Wave 1.
**Tests:** the guard is the test. Prove it catches a synthetic orphan.
**Deps:** none. **Skills:** `superpowers:test-driven-development`.

## WP-05 · Mock/real parity harness — `M` — risk: low
**Why:** mock mode is what CI exercises. `mockTransformOrder` (`api-client.ts:716-736`) teleports `transforming → delivered`, skipping `ready_to_deliver` and `delivering` — the exact boundary customers hit. `mockUploadPurchaseOrder` never returns `parsing`, never returns `unrouted`, never throws 400/413/429. `mockValidateOrder` (`api-client.ts:2719-2722`) returns `passed:true` on an empty result set; `realValidateOrder` (`:2746-2748`) calls that **not** passing.
**Files:** `FE/src/lib/api-client.ts`, new `FE/src/lib/api-client.parity.test.ts`
**Do:** make each mock traverse the same state sequence and emit the same error shapes as its real counterpart. Add a parity test per mocked endpoint asserting the state sequence and the error taxonomy match the documented real contract.
**AC:** the upload failure surface (400/413/429/`unrouted`) and the transform→deliver boundary are exercised in CI.
**Note:** `mockValidateOrder` is only worth fixing if WP-07 keeps validation. Sequence after the WP-07 decision.
**Deps:** WP-01. **Skills:** `superpowers:test-driven-development`.

---

# WAVE 1 — Stop lying

## WP-06 · Retire `/library/templates` and the `OutputTemplate` entity — `M` — risk: low
**Why:** the strongest single finding in the audit. `git grep OutputTemplates -- '*.cs' ':!*Migrations*'` returns only the DbSet, its own CRUD service, its controller, its own test. Nothing in parse/mapping/transform/delivery/revision reads `ConfigJson`. The "N suppliers" count is a fabricated join on matching format strings (`OutputTemplateService.cs:33-35` — the comment admits it). And it does not even save: `api-client.ts:2071-2080` posts `config`; `OutputTemplatesController.cs:99-103` binds `ConfigJson`; no case-insensitive JSON option at `Program.cs:399`. The footer says *"{tokens} are filled from the order at delivery time."* — false on three independent counts.
**Files:** delete `BE/ProcuLink.Core/Entities/OutputTemplate.cs`, `BE/ProcuLink.Core/Services/IOutputTemplateService.cs`, `BE/ProcuLink.Infrastructure/Services/OutputTemplateService.cs`, `BE/ProcuLink.Api/Controllers/OutputTemplatesController.cs`, the `DbSet` at `ProcuLinkDbContext.cs:42`, the DI line at `Program.cs:613`, the test file; delete `FE/src/app/(app)/library/templates/**`; remove the tab at `FE/src/components/bridge/layout/HubTabs.tsx:49`; remove the client fns in `api-client.ts`. Also fix the stale doc comment at `BE/ProcuLink.Core/Entities/SupplierConnectionRevision.cs:69` (it claims to snapshot `OutputTemplate.ConfigJson`; `ConnectionBackfillService.cs:161` fills it from `SupplierPoMapping`).
**Do:** ship a **drop migration** and a permanent redirect `/library/templates` → the supplier output surface (R4).
**AC:** WP-04's orphan guard goes green on `OutputTemplate`. `/library/templates` 308s. No help article links to a dead page.
**Tests:** redirect route test; migration up/down on real Postgres; a help-link crawl asserting zero 404s.
**Deps:** WP-04. Ideally sequence **after** WP-12/WP-13 so the redirect target exists — otherwise redirect to `/library/suppliers` in the interim.
**Skills:** `superpowers:brainstorming` (deletion scope), `code-review:code-review`.

## WP-07 · Retire the duplicate rules engine — `M` — risk: medium — **DECISION 1 ANSWERED: Path A, retire**
**Why:** `/library/rules` is working CRUD over `ValidationRule`, seeded with six default rules including "Order total mismatch" marked error/auto-block — and the **only** consumer of `IValidationRuleService` is its own controller. The drawer even reports "Triggered 0 times". Meanwhile `SupplierAcceptanceRule` is the engine that runs. Two rule engines is the confusion.
**Files:** `BE/ProcuLink.Infrastructure/Services/ValidationRuleService.cs`, `BE/ProcuLink.Api/Controllers/ValidationRulesController.cs`, `FE/src/app/(app)/library/rules/**`, `FE/src/app/(app)/library/rule-definitions/**`
**RULING (2026-07-27): retire.** Delete `ValidationRule`, `ValidationRuleService`, `ValidationRulesController`, `/library/rules` and `/library/rule-definitions`. Ship the drop migration. Permanent redirect `/library/rules` → the supplier **Validation rules** tab (R4). `SupplierAcceptanceRule` becomes the single rules concept in the product.
**Before deleting `RuleDefinition`, verify it separately** — it may or may not have a live consumer; `/library/rule-definitions` has **no inbound link anywhere**, which is why it is in scope. If `RuleDefinition` *does* have a consumer, keep the entity and delete only the orphan page.
**AC:** exactly one rules concept remains in the UI, and toggling it changes what happens to an order. Prove with a RED-first test: a rule that blocks, blocks. Orphan guard (WP-04) goes green on `ValidationRule` and `/library/rule-definitions`. No help article or search result 404s.
**Do NOT** carry the six seeded default rules forward as data — they were never evaluated, so migrating them would import six rules that silently start blocking orders. If the equivalent defaults are wanted, they are a separate, deliberate seeding decision on `SupplierAcceptanceRule`.
**Deps:** WP-04; sequence before WP-17. **Skills:** `superpowers:brainstorming`, `superpowers:test-driven-development`.

## WP-08 · Retire the dead routes — `S` — risk: low
**Why:** `/drafts` is a nav destination that can never contain anything. `/upload/preview/[orderId]` is unreachable, backed by a 1,539-line component; its only consumer `parseStall.ts` has one importer.
**Files:** `FE/src/app/(app)/drafts/**`, `FE/src/app/(app)/upload/preview/**`, `FE/src/components/bridge/BridgeSidebar.tsx:58`, `FE/src/lib/parseStall.ts` and its importer `MagicMappingPreview.tsx:30,408,717`
**Do:** delete both routes with permanent redirects (`/drafts` → `/inbox`, `/upload/preview/[id]` → `/inbox/[id]`). Keep `parseStall` only if `MagicMappingPreview` genuinely uses it; otherwise delete both.
**AC:** orphan guard green; both redirects test-pinned; no nav item points at an empty destination.
**Deps:** WP-04. **Skills:** none.

## WP-09 · Retire webhook ingress — `M` — risk: low — **DECISION 2 ANSWERED: Path B, retire**
**Why:** `Organisation.WebhookSecretEncrypted` doc-comments "Set/rotated by org admins"; `git grep` on `origin/main` finds **4 non-test hits, all reads**. No writer, no endpoint, no UI. Every supplier callback 401s forever.
**RULING (2026-07-27): retire.** No customer has asked for it and it has been unreachable since it shipped. Re-introduce only when a named customer needs it — at that point the writer is a half-day.
**Delete:** `WebhookIngressController`, the `Organisation.WebhookSecretEncrypted` column (drop migration), `HmacWebhookVerifier` **only if** it has no other consumer — verify first; the outbound webhook subscriptions are a *different* feature and must keep working. Remove the channel from the FE, `/formats`, the help centre, the capability ledger and every marketing surface.
**AC:** the channel is gone from every claim surface, outbound webhook subscriptions still fire with a valid HMAC, and the orphan guard goes green on `WebhookSecretEncrypted`.
**Careful:** inbound *email* and inbound *webhook* are separate channels sharing similar names. Do not touch `InboundEmailController`, the Postmark path, or the CF verify-Worker.
**Deps:** WP-04. **Skills:** `superpowers:test-driven-development`.

## WP-10 · Marketing truth — `S` — risk: none
**Why:** `/security:40-41` claims *"All order data is processed and stored in EU-region infrastructure. No data leaves the region without an explicit, contracted subprocessor agreement"* while four named subprocessors are US and `appsettings.Production.json` sets `Ai:Provider=openai` with no endpoint override (`OpenAiPdfOrderExtractor.cs:259,294`) — PO line text reaches `api.openai.com`. `/customers:28,34,42` ships two invented pilot profiles ("Mid-market wholesaler · ~120 POs/month") contradicted by the production inventory.
**Files:** `FE/src/app/(marketing)/security/page.tsx:40-41`, `FE/src/app/(home)/page.tsx:203,940`, `FE/src/app/(marketing)/pricing/page.tsx:330-332`, `FE/src/app/(marketing)/customers/page.tsx:28,34,42`
**Do — IN THIS ORDER. Stage 1 is not optional (rewritten 2026-07-30).**

**Stage 1 — ESTABLISH THE FACTS. Write no copy until this is done.** The original version of this packet told you to assert "EU-region (Neon, R2, Railway europe-west4)". **Do not.** That replaces a vague false claim with a more specific and more confident one, which is worse. The evidence does not support it: `R2Endpoint` is **empty** at `appsettings.Production.json:21`; nothing in either repo records the Neon region; and the only in-repo support for "Railway europe-west4" is `docs/qa/2026-06-29-...:193` citing `src/lib/subprocessors.ts` — the page citing itself. `europe-west4` is a GCP region name and Neon runs on AWS, so the label is wrong on its face. Record **TWO COLUMNS per leg — deploy region AND egress path — each with its own source.** One value cannot carry both facts: a container deployed in Amsterdam can egress through a NAT that geolocates to Durham NC, and both are true. `04-CAPABILITY-TRUTH-LEDGER.md` already has the table started; two of eight cells are now sourced (Railway deploy = Amsterdam, identifier `europe-west4-drams3a`, per Railway's regions doc; Neon deploy = AWS `eu-central-1` Frankfurt, per the host string). R2 deploy, and **every** egress cell, are still UNKNOWN. Fill them or mark them unknown in the copy — do not infer one from the other.

**Stage 2 — RESOLVE `EGRESS-GEO`, or say in the copy that it is unresolved.** `docs/qa/2026-06-29-prelaunch-audit-and-test-plan.md:1065` records the outbound delivery POST egressing from `152.55.184.78` (Durham, NC, US) — "a supplier's server logs will show a US source IP" — and `:1090` files it again as open. `git grep EGRESS-GEO origin/main` returns those two lines and nothing else. No residency sentence can be written honestly while the egress region is unknown. **Reframe the question**: it is not "which region is the service in" but "which path does the outbound PO take" — a supplier's server logs record the egress IP, not the deploy region, and that is the fact a customer's DPA question is actually about.

**Stage 3 — FIX THE CATEGORY ERROR.** Every version of this copy calls the US email category "inbound email". **Postmark also carries the OUTBOUND PURCHASE ORDER**: `EmailApiDeliveryDispatcher.cs:11` ("Sends the generated artifact as an email"), `:109` attaches it, and its test asserts the attachment is `PO-1.xml`. `email` is a live-proven delivery channel, so a US subprocessor receives the complete PO document. This propagates into `/dpa`, which incorporates the subprocessor list by reference — so `/dpa` is in scope for this packet even though it is not in the file list above.

**Stage 4 — then write the copy**, asserting only what stages 1-3 established and naming what is still unresolved rather than omitting it. Keep the "EU" hero stat ONLY if it links to that explanation. `/customers`: an honest "no public references yet" placeholder, or a real dated engagement.
**AC:** every sentence on both pages maps to a ledger row.
**Tests:** extend the existing DPA/subprocessor regression test to pin the residency wording.
**Note:** the audit's `/subprocessors` "OpenAI DPA + SCCs" P0 was **refuted** — `c315a76` (#66) already corrected it. Do not re-open.
**Deps:** none. **Skills:** `superpowers:verification-before-completion`.

## WP-11 · Billing gate honesty — `M` — risk: medium
**Why:** four gates return an error code naming Integration while the real minimum is Growth (`PlanConstants.cs:286`) — and three tests pin the wrong string. **Ten of sixteen `BillingFeature` gates are never enforced**, so the paid ladder's differentiators are unlocked on every plan. REST ingress has no billing gate at all — a frozen Pilot can still push orders. SFTP and S3 pull skip the gate the IMAP job applies. `/pricing` does not disclose that cancelling freezes the org read-only and kills every ingest channel.
**Files:** `BE/ProcuLink.Core/Constants/PlanConstants.cs`, `BE/ProcuLink.Api/Controllers/IngressController.cs`, the SFTP/S3 polling jobs, `FE/src/app/(marketing)/pricing/page.tsx`, `FE/src/lib/plans.ts`
**Do:** correct the four error codes and their tests; enforce or delete each of the ten unenforced gates (a gate that is not enforced is a lie about the ladder); add the ingress + pull-job gates; disclose the cancel behaviour.
**AC:** a test per `BillingFeature` proving the gate fires at the documented plan boundary. No feature is sold at a tier that does not gate it.
**Note:** `FE/src/lib/plans.ts` currently matches `PlanConstants` exactly including the Integration 1,000→1,500 raise and the Distributor tier — **CLAUDE.md §11.5 is the stale one.** Fix the doc, not the code.
**Deps:** WP-01. **Skills:** `superpowers:test-driven-development`, `code-review:code-review`.

---

# WAVE 2 — The wedge

## WP-12 · Carry `OutputTree` through promotion — `L` — risk: medium — **HIGHEST VALUE IN THE PLAN**
**Why:** the visual designer is the differentiator and its output dies with the order. `grep OutputTree PromoteMappingService.cs` = **0**. `PoMappingConfig.Output` is the flat rules map only. A per-order override lives inside `purchase_orders.canonical_json` and nothing copies it forward.
**Files:** `BE/ProcuLink.Core/Services/Mapping/PoMappingConfig.cs` (add `OutputNodeTemplate? OutputTree`), `BE/ProcuLink.Infrastructure/Services/PromoteMappingService.cs:143-186`, `BE/ProcuLink.Api/Services/Orders/OrderTransformService.TryReadSupplierPromotedOutputAsync`, `BE/ProcuLink.Api/Services/ConnectionBackfillService.cs:161`, `BE/ProcuLink.Core/Entities/SupplierConnectionRevision.cs`
**Do:** additive JSONB on the existing `SupplierPoMapping.ConfigJson` column — **no migration**, exactly the pattern `Output` already uses. Promote the tree; consume it below the per-order override and above the fixed transformer; snapshot into the revision bundle.
**AC:** design a tree on order A → promote → upload an identical file → order B renders **byte-identically** with zero designer interaction. A malformed promoted tree falls back to the fixed transformer and logs a warning — never throws.
**Tests:** unit round-trip (serialise/deserialise preserves namespaces and `IncludeWhen`); real-Postgres promote-then-transform; a **byte-parity assertion** between A and B; a malformed-tree fallback test.
**Risk:** precedence interaction with the per-order override. Mitigate by asserting the full precedence ladder in one table-driven test before touching anything.
**Deps:** none. **Skills:** `superpowers:brainstorming`, `superpowers:writing-plans`, `superpowers:test-driven-development`.

## WP-13 · Wire the promote control — `S` — risk: low
**Why:** `MapperWorkbench.tsx:952` renders "Save mappings" only when `onSaveMappings` is passed, and **no mount site passes it** (`OrderWorkshop.tsx:805`, `MappingPanel.tsx:201`, `ConnectionDetail.tsx:272`). `promoteMapping()` at `api-client.ts:1877` has **zero call sites**. `/help/output-mapping-editor` tells users to click a button that does not exist.
**Files:** `FE/src/components/bridge/workshop/OrderWorkshop.tsx`, `FE/src/components/bridge/workshop/WorkshopStatusBar.tsx`, `FE/src/lib/api-client.ts:1877`
**Do:** thread `onSaveMappings` → `promoteMapping()`; render `PromoteMappingResult.Message` so the user learns exactly what was saved.
**AC:** the control renders on `/inbox/[orderId]`, calls the endpoint, reports what it saved; the help article becomes true.
**Tests:** RTL — control present, endpoint called, message rendered, disabled in read-only.
**Deps:** WP-12. **Skills:** `frontend-design`.

## WP-14 · Widen the canonical output row — `M` — risk: low
**Why:** `MappedTransformService.cs:321-336` exposes **10 header** names and `:394-412` exposes **11 line** names. `PurchaseOrderEntity` has ~45 business header fields and `PurchaseOrderLineEntity` ~25. **No ShipTo, BillTo, Contact, Incoterms, BuyerTaxId, ManufacturerPartNumber, Unspsc, DiscountPercent, TaxAmount can be emitted by any custom output.** For a physical PO, ship-to is not optional. The FE picker is narrower still — 5 header + 8 line (`types.ts:331-332`).
**Files:** `BE/ProcuLink.Transform/Output/MappedTransformService.cs:321-336,394-412`, `FE/src/lib/api/types.ts:331-332`
**Also fix here:** learned `ItemMapping` matches case-sensitively while the catalog matches on a normalised key — the same code resolves in one path and not the other.
**AC:** a CSV output binding `ShipToCity` emits the parsed city; a tree binding `ManufacturerPartNumber` emits it; the FE picker offers every exposed name grouped by scope.
**Tests:** one assertion per newly-exposed field; a **completeness test** that fails when an entity business field has neither a row key nor an explicit written exclusion — so this gap cannot silently reappear.
**Deps:** none (parallel with WP-12). **Skills:** `superpowers:test-driven-development`.

## WP-15 · Designer depth I — `L` — risk: medium
**Why:** the designer cannot reorder nodes (delete-and-re-add only, so CSV column order and XML element order are unchangeable in place); every JSON leaf emits as a string so numeric/boolean/null is impossible; the 8 manipulators in `MANIPULATOR_TYPES` (`types.ts:319-328`) are not surfaced — only the two format presets are; CSV delimiter, quoting, encoding and **line ending** are hardcoded, and the line ending is whatever OS the server runs on.
**Files:** `FE/src/components/bridge/OutputStructureDesigner.tsx`, `FE/src/components/bridge/outputNamespaceModel.ts`, `BE/ProcuLink.Transform/Output/**` (CSV dialect threading, typed leaves)
**Do:** node move up/down (keyboard-accessible); typed JSON leaves; expose Trim/Replace/DateFormat/Concat/Fallback/Split/Multiply/Divide per node; a CSV dialect panel (delimiter, quote policy, encoding, line ending) threaded to the emitter.
**AC:** an operator with no code produces a CSV with a chosen column order, CRLF endings and a concatenated field, and a JSON doc with a real numeric quantity — and the preview equals the delivered bytes.
**Tests:** emitter tests per dialect option; a reorder model test; a typed-leaf round trip.
**Deps:** WP-12, WP-14. **Skills:** `frontend-design`, `ui-ux-pro-max`, design brief **DB-2**.

## WP-16 · Designer depth II — `L` — risk: medium
**Why:** conditionals are raw Scriban predicates typed into a text box; namespaces are hand-typed prefixes and URIs with no presets; the tree path **skips `OutputFieldValidator`**, losing the checks every fixed transform runs; `designerFormat()` (`OutputStructureDesigner.tsx:44-51`) silently rewrites a cXML/UBL/X12 tree to generic `xml` on save.
**Files:** same as WP-15, plus `BE/ProcuLink.Transform/Output/OutputTemplateEmitter`, `OutputFieldValidator`
**Do:** structured conditional builder emitting the same `IncludeWhen` predicate ("include when *[field]* *[is / is not / is empty]* *[value]*", with a raw-expression escape); namespace preset dropdown (UBL 2.1 / cXML 1.2 / Peppol BIS 3 / custom); route the tree path through `OutputFieldValidator`; make the format rewrite explicit and consented instead of silent.
**AC:** a non-developer builds a conditional section and a namespaced XML doc without typing an expression or a URI; a tree that would emit an invalid document fails loudly at design time, not at delivery.
**Tests:** predicate round-trip (structured ↔ raw); namespace preset emission; a validator test proving the tree path now rejects what the fixed path rejects; a format-preservation test.
**Deps:** WP-15. **Skills:** `frontend-design`, `ui-ux-pro-max`, design brief **DB-2**.

---

# WAVE 3 — Enforcement & recovery

## WP-17 · Server-side acceptance gate — `M` — risk: medium
**Why:** `ISupplierAcceptanceService.ValidateOrderAsync` has exactly two production call sites, **both HTTP controllers** (`OrdersController.cs:1563`, `MapperEnrichmentController.cs:143`). `ParseOrderJob`, `TransformOrderJob` and the delivery path never call it. Enforcement is browser-only, so a USD order configured to block on non-EUR goes out via inbox bulk-send, auto-deliver, REST ingress, or email ingest. The supplier profile UI tells the operator error rules block delivery.
**Files:** `BE/ProcuLink.Api/Services/Orders/OrderTransformService.cs` (pre-transform claim), `BE/ProcuLink.Api/Services/SupplierAcceptanceService.cs`
**Do:** evaluate the effective acceptance profile inside the transform claim; refuse when any row is `severity=error` / `BlockOnFail=true`; provide an explicit, audited operator override.
**AC:** a blocked order refuses to transform via **all four** entry paths, with a plain-language reason and a visible override.
**Tests:** real-Postgres, one per entry path, plus a **negative control** proving the gate is what blocks (not a coincidental precondition).
**Deps:** WP-07. **Skills:** `superpowers:test-driven-development`, `superpowers:brainstorming`.

## WP-18 · Validation at every breakpoint — `S` — risk: low
**Why:** the only live caller of `GET /api/orders/{id}/validation` is `useMapperModel.ts:269-276`, inside `MapperWorkbench`, which `OrderWorkshop.tsx:802` mounts in a `hidden lg:flex` container. **Below 1024 px no acceptance rule is evaluated at all**, and the send gate falls back to line-level `needsReview` only. `useAcceptanceValidation` is dead code — its confirm dialog and fix-queue branches are unreachable.
**Files:** `FE/src/components/bridge/workshop/OrderWorkshop.tsx`, `FE/src/components/bridge/mapper/useMapperModel.ts:269-276`, `FE/src/components/bridge/review/hooks/useAcceptanceValidation.ts`
**Do:** hoist the validation query into `OrderWorkshop` so it runs at every breakpoint; fold its blocking count into `canSend`; wire or delete `useAcceptanceValidation`.
**AC:** at 390 px, 768 px and 1440 px the send gate reflects acceptance results identically.
**Tests:** RTL at three viewports asserting the same gate decision.
**Deps:** WP-17. **Skills:** `frontend-design`.

## WP-19 · Split 4xx; end the dead end — `M` — risk: medium
**Why:** `DeliveryService.cs:764-772` classifies **every** 400–499 as `RejectedBySupplier`; `OrderStatusMachine.cs:99` gives that status **no outgoing edges**; `:146-147` excludes it from Redeliver. An expired API key (401), a moved endpoint (404) or a rate limit (429) permanently dead-ends the order with no control in the product that can move it.
**Files:** `BE/ProcuLink.Infrastructure/Services/DeliveryService.cs:764-772`, `BE/ProcuLink.Core/Constants/OrderStatusMachine.cs:99,146-147`, FE failure panel
**Do:** route 401/403/404/408/429 to `delivery_failed` (retryable, with a config CTA); reserve `rejected_by_supplier` for a genuine business rejection (422, or 400 carrying a supplier reason). Give that status a documented operator exit.
**AC:** each of those codes lands somewhere the operator can act on, with copy naming the likely cause.
**Tests:** one per status code; plus a **state-machine invariant test asserting no non-terminal status has an empty edge set** — this is the regression guard for the whole class.
**Deps:** WP-03 (informs nothing here, but sequence after Wave 0). **Skills:** `superpowers:test-driven-development`.

## WP-20 · Content type and filename — `S` — risk: low
**Why:** cXML, UBL and X12 artifacts are delivered as **`application/octet-stream` named `PO-xxx.dat`** because `DeliveryService` re-derives the content type from the format string and its switch only knows xml/json/csv. Many receivers reject on content-type or extension. Over SFTP/FTPS the filename is the **bare PO number with overwrite enabled** — two orders sharing a PO number silently clobber.
**Files:** `BE/ProcuLink.Infrastructure/Services/DeliveryService.cs`, the SFTP/FTPS writer
**Do:** a single table-driven format → (mime, extension) map; SFTP filename gains an order-id or timestamp suffix; overwrite off by default with an opt-in.
**AC:** cXML → `application/xml` + `.xml`; X12 → `application/EDI-X12` + `.x12`; two same-PO orders both land intact.
**Tests:** table-driven mime/ext test; an SFTP test proving no clobber.
**Deps:** none. **Skills:** none.

## WP-21 · Prove revision authority — `M` — risk: low — **RESCOPED 2026-07-27: the flag is ALREADY ON**
**What changed:** the audit's P0 said the versioning subsystem was inert in production because `Connections:RevisionAuthority` is set true only in `appsettings.Development.json:46`. **That was a wrong reading.** Verified 2026-07-27: `Connections__RevisionAuthority = true` on **both** Railway services — `ProcuLink` (API) and `aware-amazement` (Worker). Reproducibility is live. Path B (retire the subsystem) is off the table; it is load-bearing.
**Do:**
1. A production smoke proving a pinned order does **not** re-route after a live config edit. The behaviour is live and has never been observed — this is the packet's real deliverable.
2. Correct every doc and code comment describing the flag as Development-only, including the audit's own claim and anything in `STATUS.md`.
3. Add a startup assertion (or a `/health/ready` line) that surfaces the flag's effective value, so it can never again be a fact nobody can read.
4. Confirm the flag is set on any future service that resolves an effective config.
**AC:** an operator edits a supplier's delivery config while an order is pinned to an earlier revision, and that order still delivers under its pinned bundle — proven on production with the order id recorded in the ledger.
**Deps:** none (WP-03 is done). **Skills:** `superpowers:verification-before-completion`.

## WP-22 · Ingest duplicate prevention — `M` — risk: medium
**Why:** Postmark PUSH inbound email has **no message-id or content dedupe** while the IMAP PULL path does. REST ingress idempotency is **check-then-create, not an atomic claim** — concurrent duplicates create two orders. The three pull channels already do this correctly (claim-first against a unique index); copy that pattern.
**Files:** `BE/ProcuLink.Api/Controllers/InboundEmailController.cs`, `BE/ProcuLink.Api/Services/.../InboundEmailRouter.cs`, `BE/ProcuLink.Api/Controllers/IngressController.cs`
**AC:** the same Postmark message replayed twice creates one order; two concurrent identical `Idempotency-Key` requests create one order.
**Tests:** real-Postgres concurrency test with a deterministic interleave, mirroring the existing pull-channel dedupe tests.
**Deps:** none. **Skills:** `superpowers:test-driven-development`.

## WP-23 · `resolve` status guard — `S` — risk: low
**Why:** `POST /api/orders/{id}/resolve` has no status guard and performs transitions **both state maps declare impossible**.
**AC:** the endpoint rejects a resolve on a status the machine forbids, with a 409 and a plain message.
**Tests:** one per forbidden source status; assert against `OrderStatusMachine` rather than a hand-written list.
**Deps:** WP-19. **Skills:** `superpowers:test-driven-development`.

## WP-24 · Recovery UI — `M` — risk: low
**Why:** `transform_failed` recovery exists in the backend but is unreachable from the UI — the failure panel's only CTA links to itself. **Every `/operations/health` deep link is inert** because `InboxView` never reads the `status` query param. The Worker-outage stall escalation was built then orphaned on a route nothing links to. The dead-lettered order page instructs the operator to click a button that returns 400.
**Files:** `FE/src/components/bridge/InboxView.tsx` (read `status` from the query), the order failure panel, `FE/src/app/(app)/operations/health/page.tsx`
**AC:** every failure state on every screen has a control that performs a real recovery, and every health deep link filters the inbox.
**Tests:** RTL per failure state asserting the CTA calls a real endpoint; a link-integrity test over the health page.
**Deps:** WP-19. **Skills:** `frontend-design`, design brief **DB-6**.

---

# WAVE 4 — Concepts & UI

> Every packet in this wave is **rename / merge / hide / relayout only**. Code identifiers, routes-as-redirects, and the Bridge Layer aesthetic do not change (R10).

## WP-25 · Concept reduction — `XL` — risk: medium
**Why:** the UI teaches ~50 nouns for a 9-concept job. "Mapping" means three different things and the UI apologises in body copy (`SupplierDockProfile.tsx:1639`: *"For per-item code translations, use the Mappings tab instead."*). "revision" appears 450× in app source, "canonical" 963×, "passport" 72×, "replay" 97×, "dead-letter" 52×.
**Target vocabulary (9):** Order · Supplier · Item code · Order layout · Output · Delivery · Rule · Issue · Workspace.
**Renames:** supplier `Mappings` + `Catalog` → **Item codes** (one tab) · `PO Mapping` → **How we read their files** · `Output templates` → **What we send them** · revision → **version** · passport → **order record** · dead-letter → **needs your attention** · inbox `ready` / "Normalized" → **Ready to send**.
**Files:** `FE/src/components/bridge/SupplierDockProfile.tsx:105-115`, `HubTabs.tsx:32-65`, `UnifiedStatusBadge.tsx:95`, `InboxView.tsx:102,431-435`, plus a copy sweep.
**Also:** extend `scripts/check-vocabulary.mjs` — today `RETIRED` (`:50-65`) polices only the metaphor words (`bridge, crossing, dock, lane, spine, wire, traveller`). **The guard rail is aimed at the wrong vocabulary.** Add the engineering-jargon list scoped to user-facing strings.
**AC:** the extended vocab gate is green; no screen teaches a noun outside the nine without a tooltip defining it in plain procurement language.
**Deps:** WP-06, WP-07, WP-08 (fewer nouns to rename). **Skills:** `ui-ux-pro-max`, `frontend-design`, design brief **DB-1**.

## WP-26 · Nav restructure — `L` — risk: medium
**Why:** six top-level items; only "Inbox" names something an operator does. "Rules & formats" is a bucket of four unrelated engines, two of them inert. `/operations/webhooks` and Settings → Connectors are the same data under two nouns. `/connections` is a nav tab *and* the supplier History tab.
**Target:** **Orders · Suppliers · Activity · Settings**.
**Files:** `FE/src/components/bridge/BridgeSidebar.tsx:52-96`, `FE/src/components/bridge/layout/HubTabs.tsx:40-65`, `BridgeTopbar.tsx:495`
**AC:** every current route stays reachable (redirect or nested tab) — the existing nav test that pins legacy-route reachability must stay green. Four top-level items.
**Tests:** extend `BridgeSidebar.test.tsx`'s reachability invariant to the new tree.
**Deps:** WP-25. **Skills:** `ui-ux-pro-max`, design brief **DB-1**.

## WP-27 · Onboarding that completes in one sitting — `L` — risk: medium
**Why:** first run is 6+ screens and **dead-ends at delivery configuration** because every terminal channel needs supplier cooperation. The sample order cannot complete the loop its own docstring advertises — no delivery config is seeded. "Practice order" framing depends on a URL query param, not the `IsSample` flag.
**Files:** `FE/src/components/bridge/OnboardingWizard.tsx:417`, `FE/src/components/bridge/buildChecklistSteps.ts:97-173`, `BE/ProcuLink.Api/Controllers/SampleOrderController.cs`
**Do:** add a terminal delivery channel needing zero supplier cooperation — **"Email the supplier-ready file to an address"** and **"Download it"** — and make it the default for the delivery step. Seed the sample order with it. Drive the practice framing off `IsSample`.
**AC:** a brand-new account reaches a **delivered** supplier-ready file without contacting a supplier, in ≤3 screens after sign-up.
**Tests:** a Playwright journey from fresh org to delivered, running in CI against mock.
**Deps:** WP-26. **Skills:** `ui-ux-pro-max`, `frontend-design`, design brief **DB-3**.

## WP-28 · Order Workshop density — `M` — risk: low — **LAYOUT LOCKED**
**Why:** up to **seven stacked chrome bands** before any order data; the issue list hides behind a collapsible column; an emoji is used as an icon (`OrderWorkshop.tsx:746` 🟢).
**Do:** compress chrome to at most two bands (the 2026-07 wave already did five→two once — finish it); promote the issue list to always-visible on desktop; replace the emoji.
**AC:** order data begins within 160 px of the content top at 1440 px; issue count is visible without a click.
**Deps:** WP-25. **Skills:** `frontend-design`, `design-review`, design brief **DB-4**.

## WP-29 · Inbox: make the valuable state visible — `M` — risk: low
**Why:** the zero-touch recurring order — the product's entire economic premise — is labelled **"Normalized"** (`InboxView.tsx:102`, `UnifiedStatusBadge.tsx:95`), has **no filter chip** (`FILTER_CHIPS:431-435`), and is the least actionable row. Separately, the dashboard and inbox print **different numbers under the identical label "Ready to send"** (`BridgeDashboard.tsx:643-644` sums `ready`+`ready_to_deliver`; `InboxView.tsx:433` maps to `ready_to_deliver` only). The Pipeline column is five unlabelled dots in 184 px with no legend or accessible name. A `transforming` order is labelled "Extracting" and lights the wrong node.
**Files:** `FE/src/components/bridge/InboxView.tsx`, `UnifiedStatusBadge.tsx`, `BridgeDashboard.tsx:642-644,947,964`
**AC:** one label = one number everywhere; `ready` is a first-class filter chip with a primary send action; the pipeline column has a legend and an accessible name; the stage label matches the lit node.
**Tests:** a cross-screen count-parity test (dashboard vs inbox for the same fixture) — this is the regression guard.
**Deps:** WP-25. **Skills:** `frontend-design`, design brief **DB-5**.

## WP-30 · Design-token enforcement — `M` — risk: low
**Why:** **788 raw hex literals and 185 per-page palette constants** under `src/app`; the CI lint `11-unified-page-rules.md §Enforcement` specifies was never written. The landing page hardcodes 9 stale palette values and ships a **2.93:1** amber tile — below AA for text and below 3:1 for non-text. Two `ConfidenceChip` implementations use **different thresholds**, so the same AI score is green in one pane and amber in another. Three implementations of the file-format chip, one failing AA on XLSX/API/EDI.
**Files:** `FE/src/app/(home)/page.tsx:26-42`, the chip implementations, new `FE/scripts/check-tokens.mjs`
**AC:** the hex lint is green and wired into CI (WP-01's job); one `ConfidenceChip`; one format chip; zero AA failures on text and non-text.
**Note:** all three Appendix C drift claims (duplicate `UnifiedStatusBadge`, `SettingsPrimitives` inline styles) are **already fixed**. Do not re-open them.
**Deps:** WP-01. **Skills:** `web-design-guidelines`, `design-review`.

## WP-31 · Accessibility — `M` — risk: low
**Why:** **11 of 17** hand-rolled `aria-modal` dialogs have no focus trap; the onboarding wizard has no Escape handler; the marketing hero SVG **animates forever under `prefers-reduced-motion`** (in-app reduced-motion handling is correct — only marketing is wrong); form controls below the 44 px tap floor and the 16 px iOS zoom floor, including the two shared input styles on Settings and Webhooks.
**AC:** every dialog traps focus and closes on Escape; zero controls below either floor; the hero respects reduced-motion.
**Tests:** a shared dialog-behaviour test applied to all 17; a tap-target lint.
**Deps:** WP-30. **Skills:** `web-design-guidelines`.

## WP-32 · Degraded-state pattern — `M` — risk: low
**Why:** **when Clerk JS fails to load the app spins on "Loading…" forever** — a repo-wide grep finds no Clerk load-failure fallback anywhere. Every data query gates on `isApiMockMode || clerkReady`, and `clerkReady` never becomes true. An ad-blocker, a corporate proxy, or a Clerk outage looks identical to a hang. (Reproduced live during the audit.)
**Files:** `FE/src/app/(app)/layout.tsx`, `FE/src/hooks/useQueriesEnabled.ts`
**Do:** bounded timeout → an honest "sign-in service unavailable" card with retry and a status link. Generalise as the standard degraded-state for any hard external dependency.
**AC:** with `clerk.proculink.eu` blocked, the app shows an explanatory card within 10 s, not a spinner.
**Tests:** a test that blocks the Clerk script and asserts the card.
**Deps:** none. **Skills:** `frontend-design`.

---

# WAVE 5 — Self-running

## WP-33 · Auto-send when clean — `L` — risk: high — **DECISION 3 ANSWERED: automation, dry-run first**
**Why:** `TransformOrderJob.Enqueue` has exactly **one caller** — `OrdersController.cs:1475`, the manual transform endpoint. No order advances past `ready` without a human click. The 100th identical PO that auto-resolves every line still needs an operator to open it and press Send. The recurring-order case — the commercial premise — saves mapping work but saves no clicks.
**RULING (2026-07-27): ProcuLink is an automation product.** Build it — but ship it in three stages, and do not skip stage 1.

**Stage 1 — dry run (ships first, on for one week).** The switch exists, defaults OFF, and when ON it **logs the PO it would have sent and does not send it**. An audit row per would-be send: order id, supplier, artifact SHA-256, the channel it would have used, and the reason it was considered clean. One week of this data is what earns the right to stage 2.

**Stage 2 — live, one supplier.** Flip a single supplier the founder chooses. Watch `/operations/log`.

**Stage 3 — generally available**, still per-supplier opt-in, still default OFF, with an **org-level kill switch** that stops every automatic send immediately without touching per-supplier config.

**Build:** a per-supplier `AutoTransform` flag, or reuse `AutoDeliver` as one "auto-send when clean" switch — decide from the code, and say which and why. Enqueue from parse completion when the status lands `ready` **and** no blocking issue exists **and** the supplier has a delivery config. A visible per-supplier indicator so an operator can always see which suppliers are automatic.
**AC:** with the switch on, a fully-resolved recurring PO goes ingest → delivered with **zero human interaction**, and every such send is audited as automatic.
**Tests:** real-Postgres end-to-end; a negative test proving an order with any blocking issue never auto-sends; an idempotency test proving a Hangfire refetch cannot double-send.
**Risk: high** — this sends real POs unattended. The three-stage rollout above is the mitigation and is not optional. Stage 1 (dry run) must run a full week and its log must be read before stage 2.
**Deps:** WP-17, WP-19. **Skills:** `superpowers:brainstorming`, `superpowers:writing-plans`, `superpowers:test-driven-development`.

## WP-34 · Prove what was sent — `M` — risk: low
**Why:** `GET /api/orders/{id}/artifacts/{artifactId}/download` exists (`OrdersController.cs:2137`) and `getDownloadUrl` wraps it (`api-client.ts:905`) — with **zero callers**. The passport shows a file *key* and no SHA-256, so an operator disputing a supplier claim cannot retrieve or fingerprint the bytes.
**Files:** `FE/src/components/bridge/OrderPassport.tsx:388-392`, `FE/src/lib/api-client.ts:905`, passport DTOs
**Do:** a "Download what we sent" action on the delivery-attempts section; surface `ArtifactSha256` on the artifact and on each attempt.
**AC:** an operator downloads the exact delivered bytes and sees a hash matching the attempt record.
**Tests:** RTL for the control; a backend test asserting the served bytes hash to the recorded value.
**Deps:** none. **Skills:** `frontend-design`.

## WP-35 · Replay that re-processes — `L` — risk: medium
**Why:** replay produces a real per-order impact diff and correctly prioritises orders that currently pass but would start failing — then **nothing can actually re-process a historical order**.
**AC:** from the replay result an operator re-processes a selected historical order under the new configuration, with the new artifact stored alongside the old and both retained in the record.
**Tests:** real-Postgres replay-then-reprocess asserting both artifacts exist and the old one is untouched.
**Deps:** WP-21 (a replay that vets a bundle which does not govern delivery is worse than none). **Skills:** `superpowers:test-driven-development`.

## WP-36 · Every failure has an obvious action — `M` — risk: low
**Do:** enumerate every terminal and failure status from `OrderStatusMachine`; for each, assert in a test that the order screen renders a control that performs a real recovery or an honest "nothing to do, here is why".
**AC:** 100% of failure statuses covered; measured by a test that iterates the machine, not a checklist.
**Deps:** WP-19, WP-24. **Skills:** `superpowers:test-driven-development`.

## WP-37 · Page the founder — `M` — risk: low
**Why:** you are alone. Today: Sentry captures, `/health/ready` is good, the Worker heartbeat is monitored — but a stuck queue, a spiking failure rate, or a dead pull channel does not reach you.
**Do:** alerts on Worker heartbeat loss, dead-letter growth, delivery failure-rate spike, pull-channel last-success age, and AI token-cap latch (a known incident pattern). Route to one destination you actually watch.
**AC:** each condition fires a real notification in a staged test.
**Deps:** none. **Skills:** none.

---

# WAVE 6 — Prove it

## WP-38 · SFTP host keys + live channel proof — `L` — risk: **highest variance in the plan**
**Why:** a repo-wide grep for `HostKey|known_hosts|knownhosts|TrustedHostKey|ed25519|HostKeyAlgorithm` across `origin/main` returns **zero hits** — no host-key verification on SFTP delivery or SFTP pull. An in-path attacker captures POs and credentials. Separately, **SFTP, FTPS and ERP outbound have no happy-path test and no production proof**; the host-key gap suggests nobody has run them against a real server.
**Do:** host-key pinning with a UI to accept-on-first-use and a stored fingerprint; then a real transfer per channel with a SHA-256 comparison at the receiver.
**AC:** an unknown host key blocks the transfer with an actionable message; each channel has a dated live-proof row in the ledger.
**Risk:** if a channel turns out to be broken, its fix is unscoped work. Run WP-38's *proof* half early (week 1, read-only against a throwaway server) so the variance surfaces before Wave 6.
**Deps:** WP-20. **Skills:** `superpowers:test-driven-development`, `superpowers:systematic-debugging`.

## WP-39 · Recorded authenticated production pass — `M` — risk: low — **BIGGEST EVIDENCE GAP**
**Why:** the audit had no production session; Clerk JS would not load in the browser pane, so **every UI finding is code- or mock-derived**. The screenshot directory `docs/design-system/current-ui-screenshots-2026-06-26/` is **empty (0 files)**.
**Do:** one recorded authenticated pass through all 12 journeys on production, capturing a screenshot per screen at 1440 px and 390 px. Commit the captures to that directory so it stops being a phantom reference.
**AC:** 12 journeys, both viewports, every finding in this plan either confirmed or struck with evidence.
**Deps:** Wave 4 complete. **Skills:** `design-review`.

## WP-40 · Reconcile the ledger — `M` — risk: low
**Do:** every row in `04-CAPABILITY-TRUTH-LEDGER.md` gets a live-proof link or an honest in-product label. CI fails if a marketing string claims something no ledger row supports.
**AC:** zero rows claim `live-proven` without an evidence link.
**Deps:** WP-38, WP-39. **Skills:** `superpowers:verification-before-completion`.

## WP-41 · Accessibility + visual-regression CI — `M` — risk: low
**Why:** no automated a11y or visual check anywhere, and exactly **one** mobile-viewport assertion runs in CI.
**Do:** axe in Playwright on the ten core screens; visual snapshots at 390/768/1440; mobile viewport presets in `playwright.config.ts`.
**AC:** an introduced contrast or focus regression fails a PR.
**Deps:** WP-31, WP-01. **Skills:** `web-design-guidelines`.

---

## Dependency spine (critical path)

```
WP-01 ─┬─> WP-04 ─> WP-06/07/08 ─┐
       └─> WP-02, WP-05          │
WP-03 ───────────────────────────┼─> WP-21 ─> WP-35
                                 │
WP-12 ─> WP-13 ─┐                │
WP-14 ──────────┴─> WP-15 ─> WP-16 ─> WP-25 ─> WP-26 ─> WP-27 ─> WP-39 ─> WP-40
                                 │
WP-07 ─> WP-17 ─> WP-18          │
WP-19 ─> WP-23, WP-24 ─> WP-36   │
WP-20 ─> WP-38 ───────────────────┘
```

**Longest path:** `WP-01 → WP-04 → WP-06 → WP-12 → WP-15 → WP-16 → WP-25 → WP-26 → WP-27 → WP-39 → WP-40`.
That path is the timeline. Everything else parallelises around it.
