# ProcuLink — Current Status

_Update this file at the end of every session. Keep it lean — no full code, no long lists._

> **Pruned 2026-07-02.** The founder purged ~143 stale planning docs (commit `9456a08`), and
> this file was cut from ~1,290 lines of session-by-session narrative to the current state.
> Implementation history (Phases 0–6, Groups A–L, Waves 1–4, UI passes 1–15, the June launch
> waves) lives in `git log` — do not re-execute old checklists. The active plan + verified
> capability ground truth is
> [`docs/prompts/2026-07-02-fable5-production-push-master-prompt.md`](docs/prompts/2026-07-02-fable5-production-push-master-prompt.md).

---

## Snapshot (2026-07-24, evening) — wave MERGED

- **All 14 wave PRs are merged and both repos have zero open PRs.** FE #28–#33 (catalog-tab
  polish, inbound-address card, SEO, navbar dedup, assign-supplier UI, catalog-picker scale)
  and BE #45–#53 (PunchOut + auto-detect specs, OPS-1/OPS-2 findings, store:false, log
  levels, email-park-unrouted, upsert batching, set-based clear). Merge train was
  sequential with per-PR conflict weaves on the shared queue docs; two real collisions:
  BE-4's level-pin test vs BE-1's `{Mode}` log template (test updated, all green) and
  #51's InMemory DeleteAsync test vs #53's ExecuteDelete (dropped in favour of the
  Postgres coverage). Item-level detail in the struck queue + bullets below.
- **Open after the train:** FOUNDER P0 — the founder org is `account_status=read_only`
  (Stripe cancel test), every ingest channel dead on it; lift it, then re-fire the parked
  Postmark message. Queue: BE-6 (P1 XML parser drops every second field), the
  schema-fingerprint learning gap (S, P0 in the auto-detect spec), FE `lint:vocab`
  pre-existing red. Founder halves: OpenAI DPA/EU project, OPS-2 creds.
- **2026-07-24: inbound-webhook retry contract fixed — BE PR #56 (open), BE-1 residual
  closed.** OPS-1 measured that 422 does not stop Postmark; the documented policy is 10
  retries over ~10.5 h on any non-200, then the message is filed `Failed` (still re-fireable
  by hand for 45 days), while 200 marks it `Processed` and unrecoverable. The status is
  therefore split by whose fault the failure is, via a new `InboundEmailRejectionKind`:
  **200 `{status:"ignored"}`** for sender-side-permanent (address not ours, unknown tenant
  slug, empty body, missing recipient) and **422 with its retries intact** for
  our-side-transient (org row missing behind a `TenantMapping`, blocked account status —
  a read-only freeze is reversible in minutes, so the retries are the grace window). An
  unlabelled rejection keeps its retries. The false "422 keeps Postmark from retrying"
  comment is deleted; the `rejected_read_only` audit row is pinned by a test.
  **Not fixed, noted:** the CF inbound-verify Worker's 403 on IP-allowlist drift is the one
  status that stops Postmark retrying immediately — stale list ⇒ real mail dropped.

## Snapshot (2026-07-24) — routing/catalog/ops wave queued

- **Active queue: `docs/prompts/2026-07-24-open-queue-handover.md`** — 9 parallel chip
  items (FE: assign-supplier UI [doc: `2026-07-24-assign-supplier-ui.md`], navbar dedup,
  catalog-picker scale, marketing SEO; BE: email-park-unrouted, row-cap raise,
  Responses `store:false`, webhook log level, supplier-auto-detect SPEC; OPS: live
  inbound-email e2e, prod vendor-feed test) + founder actions. Chips run **Opus 4.8
  Extra** per founder. Same-day verification findings: **P0 — CF Email Routing
  forwarding is broken for all 12 addresses ("Destination address not found"; support@
  mail is being lost; only inbound@→Worker Active)**; OpenAI org is an unverified
  Personal account (API-call-logging Disabled, no EU project, no ZDR/DPA); the June CF
  API token is dead (401). Routing truth (code-verified): every channel either requires
  a supplier (upload 400, REST ingress 400) or parks `unrouted` (the three pull channels,
  and — since BE-1 below — the inbound-email webhook, which used to 422-reject);
  BE `assign-supplier` endpoint live at OrdersController.cs:583 with **no FE
  caller** — that's FE-1. Phase 1b enqueue gap: FIXED since `74ac036`+`de4ea0e` (old
  entries below are stale); both routing worktree branches fully merged (CLEANUP-1).
- **2026-07-24: catalog import memory bounded — BE PR #51 (open), BE-2 done.** The row cap
  was already 200k (raised in `efff40e`; the queue's "50k" was stale doc, now corrected in
  3 comments). The real gap was downstream: `UpsertManyAsync` tracked the whole file for one
  `SaveChanges`. Measured on real Postgres with a synthetic 200k-row CSV — single batch:
  615 MB retained, **1.43 GB peak working set**, 200k rows tracked; 5k batches: 24 MB
  retained, 415 MB peak, 0 tracked. Batch size swept (1k/5k/10k → time flat ~46 s, memory
  linear 11/22/38 MB) so it's picked on memory alone; insert costs ~40% more wall time
  (29 s → 41 s), which a background sync absorbs. Each batch detaches only the rows it
  touched — **never `ChangeTracker.Clear()`**, which would detach the tracked
  `SupplierCatalogSource` that `CatalogPullService` writes its sync status to afterwards
  (regression test pins it). Atomicity traded knowingly: the upsert is idempotent by
  (org, supplier, code), and `LastFileHash` has exactly one writer (post-success) so a
  partial import is always re-fetched, never skipped as unchanged.
- **2026-07-24 FE-1 done — assign-supplier UI, FE PR #32 open (not merged).** The
  `unrouted` park finally has an in-app exit: `apiClient.assignSupplier` (409 = the atomic
  `unrouted → parsing` claim matched no row, i.e. already routed — kept distinct from a 400
  "Supplier not found" via `ApiHttpError`), `AssignSupplierBanner` on the order page keyed on
  `order.status` (NOT the issue count — that screen's badge reads "Needs review" for these
  orders), and the inbox row action in place of the blank supplier name. The banner is a
  banner, not a gate: the extracted lines underneath are the evidence for "whose order is
  this?". `SupplierPicker` extracted from `UploadWorkbench` for reuse; `ord-004` mock fixture
  added (mock mode previously could not reach the flow). Deviation: the inbox action
  NAVIGATES to the order page — `InboxView` has no per-row action cells, so inline assign
  would mean a second copy of the picker + 409 handling. 20 new tests; 869 vitest green
  (90 files); tsc + `bun run build` green; browser-verified at 1440/390 in mock mode.
- **2026-07-24 FE-2 done — double-navbar dedup, FE PR #31 open (not merged).** Real cause
  was NOT a one-tab hub (no hub has <2 tabs): on top-level routes the topbar's context row
  rendered a lone unlinked crumb ("Dashboard") directly under the active nav item of the
  same name. New `isLonePageCrumb()` (breadcrumb.ts) hides that row at `md+` only — where
  the primary nav row is visible; below `md` the nav row is behind the hamburger and the
  crumb is the sole page label (those pages ship `PageHeader titleHidden`). Hub strips and
  ancestor trails ("Workbench / Drafts") untouched. Single-tab-hub guard added anyway
  (`hubShowsTabs`) + `>=2 tabs` invariant pinned in BridgeSidebar.test.tsx. Browser-verified
  at 1440/768/767/390px; 861 vitest green (88 files), `bun run build` green.
- **2026-07-24 FE-3 done — catalog picker scale, FE PR #33 open (not merged).** All three
  gaps land on one shared seam: `src/lib/catalogCodes.ts` (query-key contract + the pure
  `catalogPageClaim` verdict), `useCatalogCodeSearch` (250ms-debounced server-side lookup),
  `CatalogCodeResults` (shared option list + status line). (a) the review picker searched
  only the 1000 rows it had fetched — now `?q=` server-side. (b) `MagicMappingPreview`'s
  manual entry is a combobox over the same lookup, delivering the typeahead the help pages
  already promised (supplier read from the existing `["order", orderId]` cache — the
  mapping-preview payload carries no supplier id). (c) orphaned `CatalogHintCard` mounted in
  `OrderWorkshop`: desktop Issues tab + a new `MobileTriage` `hintSlot`, fed server truth
  (`exceptionCount`, order lines). Search keys extend the empty-query probe's prefix, so the
  existing `["supplier-catalog-codes", supplierId]` invalidation after import/sync/clear
  still sweeps them, and an order view fires one catalog request. Honesty: the "Searched
  only the first N of M" hedge is gone (the server searches the whole catalog), "no catalog
  for this supplier" now requires a settled zero-row page, a full page says "showing the
  first N". Also fixed a mock/API divergence — mock `getSupplierCatalog` returned the MATCH
  count as `total` while the API returns the whole-catalog count (`SupplierCatalogService
  .CountAsync` ignores `?q=`), which is the exact number "no catalog" vs "no match" turns
  on, so mock-mode QA of that copy proved nothing. 875 vitest green (91 files, was 849),
  tsc/lint/build clean. Browser-verified on a 121-row seeded catalog: typing `CROSS` finds
  row 121 — unreachable under the old client-side filter — and a miss reads "No product
  matches". Geometry NOT measured: the browser pane reports zero-width rects while hidden,
  so responsive checks were CSS-reasoned only.
- **2026-07-24 FE-4 done — marketing SEO, FE PR #30 open (not merged).** Prod-verified
  defects, now fixed: all 33 help articles canonicalised to `/help` (children inherit the
  layout's `alternates.canonical`); pages declaring their own `openGraph` served NO
  `og:image` (a page-level block REPLACES the root's, never merges); `/` + legal/support
  pages had no canonical at all. `src/lib/seo.ts` `pageMetadata()`/`helpArticleMetadata()`
  now drive 50 pages; landing moved into a `(home)` route group so a server layout can
  carry its metadata (route still `/`). Sitemap unchanged + test-pinned against the page
  tree. 964 vitest green (89 files), `bun run build` 77/77 pages. NOTE: `bun run lint:vocab`
  is red on main already — "Proton Bridge" ×2 + "Wiring it from Zapier" in help prose this
  PR did not touch; no CI runs that gate.
- **2026-07-24 — BE-1 done (BE PR #52, open):** the Postmark inbound webhook no longer
  422-rejects a message whose org has no supplier. It imports the attachments via
  `CreateUnroutedStubAsync` + ParseOrderJob (parked `unrouted`, resolvable by FE-1's
  assign-supplier UI) and answers 200; audit `inbound_email.rejected_no_supplier` →
  `inbound_email.unrouted_no_supplier`. 422 kept for unparseable recipient / unknown slug /
  org-not-found / blocked account status. `InboundEmailRouter` is now the FOURTH writer of
  `unrouted` (first PUSH channel) — `OrderStatusConstants` reachability doc updated.
  KNOWN GAP: body-NLP fallback still skipped without a supplier (no supplier-less
  `CreateStubFromParsedOrderAsync`); pinned by a test, not silent.
- **2026-07-24 BE-3 done — Responses API opts out of server-side storage, BE PR #50 open
  (not merged).** `OpenAiProductCodeSearch` (flag-gated product web search) is the only
  Responses API caller, and that API stores request + response payloads by default; the
  prompt carries customer PO line descriptions. Request construction moved to the pure
  `BuildOptions` seam with `StoredOutputEnabled = false` ("store" in the payload); 2 new
  tests pin the storage opt-out and the rest of the request shape. The four Chat
  Completions callers are untouched (that API defaults to store=false). Infrastructure
  1033/1033 + Transform 1218/1218 green; the 25 Api.Tests reds were all Testcontainers
  container-start failures under cross-chip Docker contention, none in this path. Code
  half only — the founder half (DPA, EU-residency project, ZDR) is still open.
- **2026-07-24: supplier auto-detect SPEC (BE-5) — BE PR #48 open, spec only, no code.**
  `docs/superpowers/specs/2026-07-24-supplier-auto-detect-from-document-design.md`. Signal
  audit: header parties EXIST; supplier-name match HALF and VAT match BLOCKED (`Supplier`
  carries no VAT/reg-nr/EDI/domain); catalog overlap needs a cross-supplier query +
  `(OrgId, Code)` index; sender address is SHA-256-only by GDPR design. Strongest signal
  already ships and wasn't on the brief: `SchemaFingerprint.SupplierIdsCsv`. **New defect:**
  that binding never learns from a correction — `assign-supplier` leaves
  `SchemaFingerprintHash` set, so the re-parse short-circuits and the chosen supplier is
  never bound. S-sized P0, worth shipping alone. Six founder decisions in the spec; #1
  (supplier identity columns) and #2 (sender-domain persistence) block signals outright.
- **2026-07-24 done:** FE #28 merged (`a5c2404`, catalog-tab polish); FE PR #29 open
  (inbound address on Email intake tab, 851/851 green); BE PR #45 open (PunchOut L1
  spec + queue strikes); Stripe test coupon deleted (0 remain); FE `feat/design-system-v1`
  deleted per founder (archived at tag `archive/design-system-v1`); **BE-4 done (BE PR #49)** —
  inbound-email webhook log levels: routine per-message narration → Debug (prod runs
  `Default=Information`), blocked-account reject raised Information → Warning, order-created
  stays Information; 5 level tests pin it. `InboundEmailController` needed no change (it
  already logs only rejects/misconfig).
- **2026-07-24 OPS-1 — live inbound-email transport PROVEN with real mail; ingest blocked.**
  Postmark `/email` → `redacted@example.invalid` (MessageID
  `c4fe887c-…`) delivered `250 Ok: queued as 28384453CA4`, and the API logged the routed
  webhook **~2 s later** — so MX → Postmark inbound → `inbound.proculink.eu` CF verify-Worker
  → API, incl. `Inbound__Postmark__ProxySecret`, are all correct end-to-end. **No order was
  created:** the founder org is `account_status=read_only` since 06:35:50 UTC (the Stripe
  cancel test's "frozen Pilot"), so `InboundEmailRouter.cs:136` refuses ingest
  (`inbound_email.rejected_read_only` ×3, 0 orders — nothing to clean up). **P0 for the
  founder:** that org's every ingest channel is dead until the status is lifted; then re-fire
  the still-`Scheduled` message via `POST /messages/inbound/d7dcf55d-…/retry` — no resend.
  Measured side-finding: **422 does not stop Postmark retrying** (3 attempts in 6 min for one
  message), so `InboundEmailController.cs:134`'s "422 keeps Postmark from retrying" comment is
  false — folded into BE-1's scope.
- **2026-07-24 OPS-2 (real vendor catalog feeds on prod): BLOCKED on founder auth** — prod
  is signed out in Chrome and only `sk_test_`/`pk_test_` Clerk keys exist locally, so no
  authed `GET /api/suppliers/{id}/catalog/source` was possible; no prod state was touched.
  Off-prod findings: **P1 defect BE-6** — the generic XML catalog parser silently drops
  every second scalar child (`CatalogXmlParsers.cs:338-364` double-advances;
  repro `a,b,c,d` → `[a|c]`), so element-based XML feeds import with no name/price;
  attribute feeds (100MEGA) and cXML Index unaffected. **Jarltech un-blocked** (was 503,
  now 200 / 19.5 MB / 14,713 items) but must not be enabled until BE-6 lands. **BE-2's
  50k-cap premise is stale** — cap is already 200k + 256 MB. Handoff (per-vendor config
  values + paste-ready read-only prod probe):
  `docs/qa/2026-07-fable5-push/2026-07-24-ops2-vendor-feed-prod-test.md`.

## Snapshot (2026-07-23) — delivery-reliability + UI waves shipped

- **Delivery reliability (BE #28–#36, 2026-07-16→17, all merged + live):** supplier rejections
  land in `rejected_by_supplier` (never re-sent); `DeliveryOutcome {Dispatched, ClaimLost,
  NotRetryable}` retry contract (unbounded-loop fix); Retry pre-flip removed; the
  `ready_to_deliver` rescue sweep discriminates on artifact age (silent-lost-order fix);
  webhook status callbacks require a dispatch MARKER (`IdempotencyKey`/`ArtifactSha256`), one
  shared predicate; **crash-after-ACK now PARKS (`delivery_unconfirmed`) instead of duplicating
  the PO on erp_*/email**. `RedeliverableStatusInvariantPostgresTests` pins the four-list drift.
- **UI wave (FE #19–#26, founder-approved via mockups, all merged + live):** park operator UI;
  retry visibility (no more double-click dead-letters); inbox status truth (`sending`,
  `unrouted`, full failed-bucket — nothing renders as "New" falsely); **order-page chrome 5 rows
  → 2 (~348px → ~148px)**; navbar de-dup + dashboard context line; **Fields|Lines per-line
  mapping view** in the workshop; polish + gate-context pass.
- **Open engineering: the 2026-07 delivery-reliability queue is EMPTY.** Canonical claim
  predicate shipped (#43, `97fd19b` — see below; it also closed the billing-release
  load-then-save window). All three park follow-ups merged: supplier-ACK resolution (#38,
  `2459de1`), billing-held truthful restore (#40, `c85f127`), park race fix (#42, `77820b5`).
  Remaining engineering is the long-deferred list below (RLS, invoice rerouting, …).
  ~~DockerProbe wedged-engine chip~~ DONE (#44, `bbaf2ae`): probe now requires a non-empty
  `{{.ServerVersion}}` response, not just exit 0 — gated tests skip instead of erroring.
- **2026-07-23: the B cut SHIPPED — BE PR #37 (merged, `7052053`).** Ops requeue supersedes
  attempt rows (`CapSupersededAt`) instead of deleting them; `DeliveryAttempt.CountsAgainstCap`
  is the ONE cap predicate (all five sites); numbering ascends across requeues; evidence
  predicate untouched + assert-the-difference tests; refused-rejection re-send P1 CLOSED
  (`CapWithoutErasingEvidencePostgresTests.C2` pins the compound path; KNOWN_GAP deleted —
  it stayed green because its seed never included the erasure step, documented in the PR).
  Suites: Api 1512 / Infra 999 / Transform 1218 green.
- **2026-07-23:** `StatusJourney` errDot — FE PR #27 MERGED (`f590402`) + Vercel prod deploy
  verified Ready (same minute — the morning's webhook drop did not recur): the red X now sits
  on the node that failed (`{ failed: n }` stage variant; bare `failed`→Parse per
  ParseOrderJob.cs:67-73, `transform_failed`→Transform, delivery failures→Deliver);
  845/845 vitest + tsc + build green.
- **2026-07-23: supplier ACK resolves a park — BE PR #38 (merged, `2459de1`), queue item 3.**
  `delivery_unconfirmed` added to `WebhookReportableFrom` (status proxy SOUND for this member:
  sole writer `ParkUnconfirmedAsync` always leaves a marker row, so the evidence half already
  passed); terminal webhook writes now close the SLA window (`DeliveryDueAt`/`SlaBreached`) in
  the same atomic claim; the `unconfirmed` attempt row is never rewritten to success. TDD
  RED-first, 3 InMemory + 1 real-Postgres tests; Api 1514 / Infra 999 / Transform 1218 green.
  Adjacent pre-existing gap found here (dispatch-4xx keeps a live `DeliveryDueAt`) now FIXED —
  see the #39 bullet below.
- **2026-07-23: billing-held park restores truthfully — BE PR #40 (merged, `c85f127`),
  queue item 4.** `PurchaseOrderEntity.HeldFromStatus` (nullable, migration
  `20260723135012`) records a hold's origin on the LIVE row; `ReleaseBillingHeldOrdersAsync`
  now RESTORES a held park to `delivery_unconfirmed` (SLA nag reopened, NO re-drive — the
  auto re-send of an unknown-outcome PO was the duplicate the park exists to prevent) while
  every other hold keeps release+re-drive; `delivery_held → delivery_unconfirmed` added to
  BOTH transition maps. Reverses the old release doc's "human already chose" justification —
  the choice was stale and uncancellable while held (`MarkDelivered` gates on the park).
  TDD RED-first (6 red observed); real-Postgres migration round trip
  (`BillingHeldParkRestorePostgresTests`). Api 1515 / Infra 1002 / Transform 1218 green.
  Pre-existing release-vs-webhook write window named in the method doc + chip'd toward #36.
- **2026-07-23: SLA window closes on supplier rejection — BE PR #39 (merged, `160bd63`).** The
  adjacent gap found during #38: a dispatch 4xx moved the order to `rejected_by_supplier` but
  `PersistAttemptAsync` cleared `DeliveryDueAt`/`SlaBreached` only on success, so once the
  deadline passed the SLA sweep could raise a false "delivery overdue" on a settled order. Now
  closed on `result.Success || isSupplierRejection` (5xx/transient stays open — that order still
  owes a delivery); manual `MarkRejectedAsync` clears the window too; `rejected_by_supplier` added
  to `DeliverySlaService.ExcludedStatuses` belt-and-braces for legacy rows (both the sweep SELECT
  and the atomic claim). TDD RED-first, 2 InMemory + 1 real-Postgres
  (`DeliverySlaConcurrencyPostgresTests.RunAsync_RejectedBySupplierOrder_IsNotFlagged`). Suites on
  the #39 branch green: Api 1512 / Infra 1000 / Transform 1218. NOTE: this worktree's base
  predated #38's code; the squash applied cleanly onto the #38-containing main and #38's webhook
  SLA-close was verified intact post-merge (WebhookIngressController lines 325/347/377).
- **2026-07-23: automatic activation never claims a park — BE PR #42 (merged, `77820b5`),
  queue item 5.** The dispatch claim admitted `delivery_unconfirmed` unconditionally
  (both relational + InMemory branches); a Hangfire refetch (~30-min non-sliding invisibility,
  bare `UseNpgsqlConnection`, Worker Program.cs:133) of a dead automatic activation could
  therefore claim a park the stuck sweep created meanwhile, find no `dispatching` row to
  re-adopt, open a FRESH attempt and SEND — defeating the park. Fix = the handover's candidate,
  verified correct: the claim gates the `delivery_unconfirmed` member on
  `!requireAutoDeliver`, so only an operator activation can claim a park (Redeliver +
  ops requeue are the only `requireAutoDeliver:false` producers, verified exhaustively).
  Bug proven live RED on real Postgres before the fix (`AutomaticParkClaimPostgresTests`);
  refusal shape is benign `Success + ClaimLost` (predicate is the enforcement — no advisory
  pre-read, which the sweep could invalidate anyway). Also corrected `HoldForBillingAsync`'s
  false "automatic queue can never hold a park" justification (billing gate runs pre-claim;
  safe post-#40). Residual documented: a refetch of a dead OPERATOR redeliver may re-execute
  that one accepted human send (pre-existing at-least-once semantics). Api 1519 / Infra 1004 /
  Transform 1218 green. "~50%" mechanism confirmed, probability not measured.
- **2026-07-23: the canonical claim predicate — BE PR #43 (merged, `97fd19b`), #36 done.**
  New `DeliveryClaim` factory (Core): `ClaimableForDispatch(requireAutoDeliver,…)` /
  `ClaimableForRetry`, org-scoping inside the predicate, #42's conditional park member encoded
  in the factory (flattening structurally impossible); five named claim sets on
  `OrderStatusMachine`. Five sites repointed: dispatch relational + InMemory (InMemory now
  enforces the staleness gate), retry relational + InMemory (InMemory previously had NO gate —
  now returns the exact relational lost-claim contract), `HoldForBillingAsync`, Retry
  endpoint's bare literal. Asymmetries preserved + pinned (27 unit tests: operator-vs-auto
  differ exactly by the park; dispatch-vs-retry likewise; subset invariants with non-vacuity
  RAN); 64-case real-Postgres matrix pins Npgsql translation ≡ C# evaluation.
  **Secondary closed: `ReleaseBillingHeldOrdersAsync` per-row atomic claims** — release now
  loses the race to a supplier callback (old overwrite-backwards bug proven RED live via
  deterministic interceptor interleave, `BillingReleaseWebhookRacePostgresTests`); #40 restore
  semantics byte-exact. Suites: Api 1585 / Infra 1031 / Transform 1218 — the chip's full-Api
  run had 28 Testcontainers-contention fails (all re-ran green), so a clean single-run
  1585/1585 was re-verified before merge. Docker-wedge incident → DockerProbe chip filed.
- **Founder gates:** sweep hand-back design call (stuck sweep returns `delivering`+fresh
  timestamp; 4 tests pin it). ~~Preimage relocation~~ DONE 2026-07-23: moved (not deleted) to
  `C:\Users\Dmitri.REDACTED-PARTY\Documents\proculink-private\`, SHA256-verified, tree clean.
- **Ops note 2026-07-23:** GitHub Actions went silent ~07:00–08:15 UTC+3 and Vercel dropped one
  main-push webhook (recovered; interim prod deploy went out via `vercel deploy --prod`).
- **Process rules earned this wave** (durable memory has detail): a comment that JUSTIFIES is a
  proof obligation ("because X" ⇒ verify X); assert-the-difference over comment-the-difference;
  `git merge-base --is-ancestor` LIES about squash-merged PRs (grep main for content instead);
  worktree grep hits are copies of main, not evidence of a separate track.

- **2026-07-24 routing/catalog recon (code-verified) — three STALE claims in this file
  corrected:** (1) the Phase 1b "SFTP/S3 enqueue gap" was FIXED long ago (`74ac036`
  2026-06-30 enqueues ParseOrderJob; `de4ea0e` 2026-07-09 ships unrouted import for
  SFTP/S3/IMAP pull) — the §06-26 line below and the deferred-list entry are outdated;
  (2) the "two routing worktrees in flight" are fully merged (both tips are ancestors of
  main, zero unique commits) — branches are stale pointers, safe to delete; (3) Postmark
  inbound is no longer "token-only, verification deferred": prod sets
  `Inbound__Postmark__ProxySecret`, so the CF verify-Worker edge gate appears deployed
  (verify with one real email). **Known operator gap found:** BE
  `POST /api/orders/{id}/assign-supplier` exists (OrdersController.cs:583) but the FE has
  NO control calling it — an `unrouted` order shows "Needs supplier" with no in-app way
  to resolve it. Also: review-picker catalog typeahead is client-side over the first
  1000 rows only; upload-preview manual entry has no typeahead; `CatalogHintCard` is
  orphaned (never rendered).
- **2026-07-24: queue items 7+8 DONE.** Item 7 — FE PR #28 **MERGED** (`a5c2404`, founder
  grant in-session); item 8 — BE PR #45 open. Also founder-requested cleanup done: the
  Stripe test coupon (`zFUfTMBz` / promo `REDACTED-TAXID`, redeemed 1/1, already inactive)
  deleted via live Stripe API — 0 coupons remain. Item 7 (Catalog-tab
  polish) — FE PR #28: logicom out of the generic protocol picker (offer⇔works held — a
  saved logicom source keeps its tile; keyboard nav follows the visible set), tile labels
  left-aligned, empty-state dashed border 1px→2px (root cause was NOT overlap — geometry
  showed a 12px clear gap; Windows 125% scaling renders 1px as a 0.8px hairline Chromium
  can drop per-edge). 849/849 vitest (4 new, RED first) + tsc + build green; verified
  live at 1440px/390px via computed styles (no screenshots — Browser pane can't composite
  hidden; note: pane DOM/JS tools DO work now, only Playwright CDP stays blocked). Item 8
  (PunchOut L1) — BE PR #45, spec only:
  `docs/superpowers/specs/2026-07-24-punchout-l1-supplier-hosted-catalog-design.md`
  (revision-bundle fit, no-local-code-list AI implications with the allow-list guard kept
  strict, BuyerCookie-correlated browser cart return, ~3.5–4.5 wk estimate, decisions
  D1–D5). Fact-check against the handover pointer: PunchOut exists only as FE copy — no
  vocabulary in `standards/catalog.ts`, no protocol code in either repo.
- **Stripe LIVE webhook verified end-to-end (2026-07-24, founder-present):** real checkout on
  prod with a 100%-forever coupon (`REDACTED-TAXID`, max 1 redemption) — €0.00 invoice paid, webhook
  endpoint `api.proculink.eu/api/billing/webhook` delivered with 0% errors, org flipped to
  Growth (`upgraded to growth via Stripe checkout cs_live_…` in API logs), then cancellation
  reverted it (`subscription cancelled — reverted to frozen Pilot`). BOTH directions of the
  billing pipeline proven on live Stripe with zero money moved. Coupon self-expired (1/1);
  left in Stripe as the audit record. Remaining untested: `amount > 0` invoice branches
  (needs a real charge + refund, ~€4–5 in non-returned Stripe fees).
  **Unintended live side effect, found by OPS-1:** the cancellation left the founder org at
  `account_status=read_only` ("frozen Pilot"), which blocks every ingest channel for that org
  — the OPS-1 test email was refused at the router. Un-freeze the org before any further prod
  live-ops, and treat "cancel a live subscription" as a state-changing act, not a free probe.

## Snapshot (2026-07-04)

- **Production is LIVE** at `proculink.eu` + `api.proculink.eu` (launched 2026-06-09 window).
  Live QA 2026-06-29/30 verdict: **CONDITIONAL GO** — 7 inbound formats, 6 outbound formats,
  and HTTP delivery proven live on prod (locale-safe). `/health/ready` green 2026-07-04
  (DB + migrations + storage + worker all Healthy).
- **Active work:** the Fable-5 production-hardening push (master prompt above) — prove every
  advertised capability live from a clean slate, click-audit the entire UI, consolidate
  design drift, make marketing truthful, fix everything found.
- **Billing:** Stripe is **LIVE** (verified 2026-07-02 via API: `sk_live` key in Railway;
  Growth €149 / Operations €399 / Integration €999 / Distributor €1,499 monthly + all 4
  yearly prices, all active in live mode). Real-money infrastructure — no test checkouts
  against prod. **Annual billing is LIVE** (`ANNUAL_BILLING_ENABLED` defaults ON; verified
  on prod 2026-07-04: pricing toggle Monthly/Annual·save-17% switches to live Stripe yearly
  prices). Remaining billing to-do: verify the live webhook end-to-end on a real subscription
  event (founder — real money).

## Durable identity rule (2026-06-09)

ProcuLink is the product and customer-facing brand. The operating legal entity is
**Diip Solutions OÜ**, registry code **17527757**, registered at Uus-Sadama tn 15-2,
10120 Tallinn, Estonia. Frontend source of truth: `project-proculink/src/lib/legal-entity.ts`
(legal pages, footers, one-pager, JSON-LD consume it). **Never restore the fabricated
"ProcuLink OÜ" / 17477775 / Katusepapi identity.** Do not publish the founder's personal
registry email or invent a VAT number.

## Deployment topology (verified live)

| Piece | State |
|---|---|
| Frontend | Vercel, auto-deploy from FE `main`; `https://proculink.eu` is the single canonical origin (`www` → 308 to apex); `NEXT_PUBLIC_USE_MOCK=false` |
| API | Railway (EU) service `ProcuLink` → `api.proculink.eu`; auto-deploys from BE `main`; EF migrations apply on startup (fail-loud + phantom reconciler) |
| Worker | Railway service `aware-amazement` — the **single** Hangfire worker, GitHub auto-deploy, **mandatory** (nothing parses/delivers without it). Railway CLI linked, project `lucid-generosity` |
| DB | Neon Postgres (also hosts Hangfire) |
| Storage | Cloudflare R2: `proculink` (private order data — pre-signed URL GETs only; SDK chunked GET signing is rejected by R2) + `proculink-public` (marketing assets, `assets.proculink.eu`) |
| Auth | Clerk **production** instance (`clerk.proculink.eu`, `pk_live_…`); org id/slug read from the Clerk v2 `o` claim; force-org-creation (adopt-on-create + softened-resolve) deployed + live-verified 2026-06-30 |
| Inbound email | `{slug}@orders.proculink.eu`: CF Email Routing MX → Postmark → `POST /api/inbound-email/postmark` — proven live with a real email |
| Outbound email | Postmark HTTPS is the **canonical** email delivery path (SMTP is dead on Railway); domain verified (SPF/Return-Path/DKIM via CF API); **Postmark ACCOUNT APPROVED + cross-domain send LIVE-VERIFIED 2026-07-04** (test-fired the `email` delivery channel on prod → clean `{success:true,200}` to an external recipient; the prior 412 gate is cleared). Powers 3 roles single-vendor: outbound `email` delivery, transactional (support/notifications), inbound parse. |
| DNS | Cloudflare — edit **only** via scoped API token (the dashboard SPA won't render in the browser tool) |
| Observability | Sentry capturing (API + Worker + frontend); PostHog EU ingesting; `/health` (liveness) + `/health/ready` (DB + storage + migration checks); Worker heartbeat alert |
| Email auth | SPF + DKIM + DMARC (`p=none`) complete on `proculink.eu` |
| Stripe | **LIVE mode** (`sk_live` verified 2026-07-02); all 8 monthly+yearly price IDs set in Railway and active | 

Prod env vars are fully set in Railway (API + Worker) and Vercel; the required-key list is
enforced by `StartupConfigurationValidator` + `appsettings.Production.json` — verify infra
(`railway variables`, Stripe dashboard) before trusting any doc's gap claim.

## Test / build state

- Backend: **1,029 tests green, 0 failures** (224 Transform + 452 Infrastructure + 353 Api)
  — last count recorded here 2026-06-07 at `main` `216b3fa`. Substantial code landed since;
  run `dotnet test ProcuLink.slnx` for the live count before claiming green.
- Frontend: `bun run build` clean (48 routes at last record). Mock e2e suite green;
  live e2e recipe: `PROCULINK_QA_BYPASS_AUTH` + local PG :5435 + `Delivery__EncryptionKey`
  + Worker running.
- Windows dev, Linux CI/prod — after pushing check `gh run list`; local green ≠ CI green.

## What happened 2026-06-09 → 2026-07-02 (summary; detail in git log + memory)

- **North Star pivot (06-09):** versioned Supplier Connection platform (draft → test →
  publish → archive, `ConnectionRevisionId` pinning, replay/impact diff) — V1–V10 shipped,
  plus confidence-calibration (per-org accept-rate buckets).
- **Output-layer restructuring (06-15):** 100% complete — trust P0s, `OutputNode` AST +
  emitters + output designer (IncludeWhen conditionals, format presets, namespaces),
  cXML DTD, plain-language validation messages.
- **Order Workshop (06-18 → 06-21):** unified 3-column order review (`OrderWorkshop`) with
  inline source picker + "bind any source field" flexible mapping — live; layout now locked.
- **Hardening + audits (06-16 → 06-24):** four-track push (idempotency, retry, GDPR erase,
  AI-usage atomic), strand-race fix, full 5-lens audit (0 P0), workbench UX + mobile audits.
- **cXML address blocks (06-25):** ShipTo/BillTo/Contact emission, MapForce byte-identical,
  proven on a real REDACTED-PARTY PDF→cXML round trip.
- **Supplier routing (06-26):** Phase 0 (nullable SupplierId + `unrouted` status) + Phase 1
  (hold + assign-supplier re-parse) shipped; producer dormant; Phase 1b blocked on the
  SFTP/S3 enqueue gap. Two routing worktrees in flight — see master prompt operating rule 8.
- **Design (06-26 → 06-29):** design-system v1 branch `feat/design-system-v1` review-gated;
  a redesign handoff spec + 228 screenshots were produced for Claude Design (the spec was
  removed in the 07-02 docs purge; recover from git history at `4f0855f` if needed).
- **Prod launch QA + fixes (06-29 → 06-30):** pre-launch audit → CONDITIONAL GO; Postmark
  HTTP email channel (PR #15 overage retry, #14 Clerk `o` claim, #11 force-org-creation)
  merged + deployed; inbound email live end-to-end on a real org.
- **07-01:** shared `useConfirm()` dialog primitive replaced all native confirms; mapper
  toolbar/API-key polish. **07-02:** docs purge (`9456a08`) + master prompt (`93a5b10`) +
  this CLAUDE.md/STATUS.md cleanup.

## Known issues / limitations (honest capability edges)

- **EDIFACT INVOIC / DESADV are stubs** (no commercial EDI licence — EdiFabric rejected);
  DESADV upload returns 501. UI must present these as "coming soon", never as errors.
- **ERP connectors (`erp_erply` / `erp_directo`):** no live ERP sandbox creds — verified via
  unit/request-shape tests + mock REST test-fires only; label honestly.
- **Scanned/image-only PDFs:** every extracted line is review-flagged (no text layer to
  verify numbers against); illegible scans fail with an honest message. Assisted, not silent.
- **Postgres RLS not implemented** — final-deferred by design (post-revenue redesign);
  app-level `.Where(OrganisationId == …)` scoping enforces isolation everywhere.
- **Postmark:** account APPROVED 2026-07-04 — outbound customer email delivery is unblocked
  and live-verified (cross-domain send returns 200). Inbound webhook signature verification
  is still deferred (needs a CF Worker).
- Design-system drift (duplicate primitives, e.g. `UnifiedStatusBadge` ×2) — inventory in
  master prompt Appendix C; fix in the push's Phase 3.

## Open items — founder / ops gates (not code)

1. ~~Stripe live-mode swap~~ **DONE** (verified 2026-07-02: `sk_live` + live `whsec_` + all
   8 live price IDs in Railway). Remaining billing to-do is engineering: verify the live
   webhook end-to-end with a real subscription event and wire the annual toggle.
2. **Rotate chat-exposed secrets** — Clerk, R2, ElevenLabs, Cloudflare API token; delete
   `~/.proculink-cf-creds.env`. Deletable cruft: the `proculink-livetest` delivery Worker + KV.
   **2026-07-02 addition:** supplier catalog feed credentials (Ingram, Also/Actebis, 100MEGA,
   REDACTED-PARTY, Logicom, Jarltech URLs) were pasted in chat — rotate/re-issue with those vendors
   after the push, and store only in encrypted catalog-source config.
3. **A real PO to a real supplier's endpoint** — controlled-endpoint deliveries are proven
   live (code 200, verified at receiver); an actual third-party supplier remains untested.
4. **Monitored support/ops mailbox + alert destinations** (Sentry/ops alerts need a real
   destination the founder watches).
5. **OpenAI compliance for customer data** — EU-residency project + DPA + zero-retention
   before real customer PO text flows through extraction at scale.
6. **Google Search Console** — use the Domain property `proculink.eu`, submit
   `https://proculink.eu/sitemap.xml` (apex, not www).
7. **The actual selling.**

## Open items — engineering (deferred by design / next up)

- Postgres RLS (needs a real-Postgres two-org test harness + Hangfire/migration role
  exemptions before it can land green).
- Invoice-pipeline rerouting (needs a PO↔invoice link migration + relational test; the
  original plan doc was purged — re-plan if picked up).
- Frontend `api-client.ts` split, retry-consolidation, denormalize/partition — audit-flagged
  counterproductive pre-revenue; don't do without a fresh reason.
- Neon pooler + `DataRetentionSweepJob` enablement — env-only flips; both dormant safe-by-default.
- Full app CSP (script/style/connect — needs Clerk/Stripe/PostHog/Sentry testing); per-page
  SEO metadata on the remaining marketing pages; Sentry stale-issue resolve.
- Supplier-routing Phase 1b (SFTP/S3 enqueue gap) + integrating the two in-flight routing
  worktrees (`routing-phase0-nullable-supplier` @ `056aff6`, `routing-phase1-hold-assign`
  @ `2fed48e`).
- "Your inbound address" card in Settings (the `{slug}@orders.proculink.eu` address now
  exists but isn't surfaced in-app).
- Design consolidation per master prompt Appendix C; `feat/design-system-v1` branch is
  review-gated, not merged.

## Open items — founder configuration gaps (feature works, config missing)

| Area | Action | Where | Effect when missing |
|---|---|---|---|
| ~~Clerk post-signup redirect~~ | **Resolved — no change.** Redirect is code-controlled (SignUp `fallbackRedirectUrl` → `/onboarding/select-organization` → `/bridge`). New sign-ups correctly land on `/bridge`, which hosts the onboarding checklist. `/welcome` is the **post-checkout** (paid) confirmation page, not the signup landing — routing new (unpaid) signups there would be wrong. | — | — |
| Status page | Host a status board, set the URL | `NEXT_PUBLIC_STATUS_URL` (Vercel + `.env`) | Footer link hidden |
| Book-a-demo CTA | Create Cal.com/Calendly slot | `NEXT_PUBLIC_BOOK_DEMO_URL` | Pilot book-a-demo cards hidden |
| ~~Support-form delivery~~ | **Resolved** — `IEmailSender` resolves to `PostmarkEmailSender` whenever `Email:Postmark:ServerToken` is set (it is, in prod), so the support form now delivers via Postmark (approved). Optional: send one real test to confirm the ops mailbox receives it. | — | — |
| DPA counter-signature | Staff `legal@proculink.eu`, sign DPAs within 5 business days as committed on `/dpa` | Operational | Trust commitment becomes false |
| Subprocessor notifications | Maintain the subscriber list; 30-day advance notice per `/subprocessors` | Operational | Trust commitment becomes false |
| Cookie banner copy | Review live banner tone (incognito) | Browser smoke test | Cosmetic |

Closed since this table was first written: PostHog (ingesting live), `Frontend:Url` (set to
prod domain), walkthrough video (real R2-hosted video live on `/watch` — the Loom env var is
superseded).

---

## Archive note

Everything before June 2026 — Phase 0–3 build-out, the Next.js migration, commercial Groups
A–L, Waves 1–4 (invoice/ASN + Zapier/Make layer), Group I UI passes 1–15, Group J/K/L, the
2026-06 launch waves (0–8 + Wave D), and the per-session narratives that used to live here —
is implemented history. See `git log` (both repos) and the memory files. Treat all of it as
shipped unless a section above explicitly reopens it.
