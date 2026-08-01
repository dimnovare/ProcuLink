# Post-wave adversarial regression audit — 2026-07-31

**Scope.** The sixteen packets merged 2026-07-29 → 2026-07-31 across six parallel sessions, several
labelled *green-but-ungated* by their own authors.

**Audited at:** BE `origin/main` **`504d9cc`** · FE `origin/main` **`478b809`** · zero open PRs in either
repo. (The brief named BE `c61fe30`; two commits — `49dd828` docs, `504d9cc` test — landed since.)
Both mains green: BE CI run `30626700557`, FE CI run `30626697343`.

**Method.** Seven verifiers, each on a different packet with a distinct attack lens, plus a main-thread
lens on the capability ledger. Mutation testing wherever the suite was safe to run
(`ProcuLink.Transform.Tests`, FE vitest); analytic per-test reading where it was not
(`ProcuLink.Api.Tests` / `ProcuLink.Infrastructure.Tests` are hard-barred — one Postgres container per
test). **Every finding below is labelled `ran-it` or `analytic`.**

**Standard applied.** Default to refuted. Every claim cites file:line, a test name, or a CI run id.
Before reporting an inference drawn from two verified facts, the step between them was named and
checked — that check is recorded in each finding. §7 lists the claims that died on that step,
including four of my own.

---

## 1. Ranked findings — by whether a real customer hits it today

| # | Finding | Sev | Packets | Provenance |
|---|---|---|---|---|
| 1 | Output designer silently discards a design on any format mismatch — and WP-12 **changed delivered bytes** for orders that previously worked | P1 | WP-12 | analytic (parent diff verified) |
| 2 | `rejected_by_supplier` recovery: backend opened the exit, frontend pins it shut, and a test forecloses the fix | P1 | WP-19 × WP-24 | **ran-it** + analytic, 2 independent verifiers |
| 3 | WP-24's `rejected_by_supplier` primary CTA is inert — links to the page it is already on | P1 | WP-24 | **ran-it** |
| 4 | WP-17's refusal names a remedy that has no frontend at all | P1 | WP-17 | analytic |
| 5 | WP-12's headline capability has **no UI trigger**, and two help pages tell customers to click it | P1 | WP-12 | analytic |
| 6 | Help articles teach the pre-WP-25 status vocabulary **inverted**; a WP-24 test pins a stale nav name | P2 | WP-25/26 × WP-24 | analytic |
| 7 | "Two numbers, one label" was **relocated by WP-25, not fixed** — and sharpened | P2 | WP-25/26 | analytic |
| 8 | WP-25's headline rename is guarded by **nothing** — full suite + both vocab modes green with it reverted | P2 | WP-25 | **ran-it** |
| 9 | Scriban artifacts: stored envelope ≠ delivered envelope (WP-20's own bug class, power-user path) | P2 | WP-20 | analytic |
| 10 | No SSH host-key verification is *possible* anywhere; SSH.NET accepts any key | P2 | pre-existing / WP-38 | **ran-it** (decompiled + executed) |
| 11 | `/security` claims a self-hosted mode that runs in ProcuLink's environment, not the customer's | P2 | pre-existing | analytic |
| 12 | WP-17's gate consumes WP-19's re-transform exit → `transform_failed` loop | P2 | WP-17 × WP-19 | analytic |
| 13 | Preview's tree gate never receives the live delivery-format swap | P2 | WP-12 | analytic |
| 14–20 | See §5 (P3s: guard sensitivity, stale mirrors and comments, inert `&from=`) | P3 | various | mixed |

---

## 2. The four that a customer hits today

### 1 — The output designer silently discards the design, and WP-12 changed delivered bytes · P1 · WP-12

`d233409` introduced a **format-equality gate** that did not exist in its parent. Verified first-hand
against `3878c0c`:

```
3878c0c  var treeIsFixedFormat = outputTree is { Format: OutputFormat.CXml or OutputFormat.X12 };
         var useOutputNode     = outputTree is not null && !treeIsFixedFormat;      // no format check

504d9cc  var useOutputNode     = TreeDrivesTheDocument(perOrderTree, effectiveFormat, orderId, …);
         // TreeDrivesTheDocument returns FALSE when tree.Format != effectiveFormat, and only LogWarning()s
```

The writer-side UI was not brought into agreement with the new gate:

- `OutputStructureDesigner.tsx:147` seeds `defaultTree("json")` whenever no tree is saved.
- `:33-36` / `:287-295` offer a free json/xml/csv format radiogroup.
- `:230` previews via `previewMappingOverride(orderId, override, tree.format)` — `honorFormat` defaults
  false, so the preview's own gate (`OrdersController.cs:1000`) **always matches** and the tree always
  renders — under the in-file label at `:222`: *"Live preview (debounced) — exactly what will be delivered."*
- `git grep outputFormat` over `OutputStructureDesigner.tsx` → **zero hits**. Its `<OutputSourcePicker>`
  mount (`:724-733`) omits the `outputFormat` prop — the one prop that drives the honest
  "This connection delivers X" note at `OutputSourcePicker.tsx:410`.

**What the customer does:** on a CSV-delivering supplier, opens the designer (it opens on JSON), designs
a layout, watches the docked preview render their document above the words *exactly what will be
delivered*, saves, gets a success. Delivery ships the fixed CSV and discards the tree, leaving only a
server-side log line.

**And the regression proper:** any order carrying a mismatched tree was **delivering that tree at
`3878c0c` and stopped at `d233409`** — a silent change in delivered bytes, with no migration, no notice
and no UI signal.

The gate itself is *correct* (artifact format, content type and filename all derive from the connection
post-`1c47fc0`; shipping JSON bytes named `.csv` would be worse). The defect is that the designer and
the preview were left contradicting it.

### 2 — `rejected_by_supplier`: the exit exists, the UI pins it shut · P1 · WP-19 × WP-24

**Found independently by two verifiers using different methods.** Re-verified on the main thread.

Backend (`504d9cc`): `OrderStatusMachine.cs:176` `[RejectedBySupplier] = Set(PendingReview, Ready,
Transforming)`; `:406` `TransformableFrom = Set(Ready, TransformFailed, RejectedBySupplier)`; `:232`
`DeclaredTerminal = Set(Failed)` **only**. `OrdersController.cs:1601` reads `TransformableFrom`, so
`POST /api/orders/{id}/transform` **answers 202 for a rejected order today**.

Frontend (`478b809`): `problemActions.ts:41` `transformOrder: new Set(["ready","pending_review",
"transform_failed"])`, above a comment at `:38` — *"`rejected_by_supplier` and `failed` appear in none
either — both are terminal."* `problemCopy.ts:353` — *"rejected_by_supplier has NO outgoing
transitions: it is terminal. So there is no post action here at all"*; `:363` tells the operator
*"Nothing is queued. Sending the same file again would be refused the same way."*

**`problemActions.ts` declares itself a mirror of the backend** — *"ProcuLink.Core: `transformOrder` ←
OrderStatusMachine: `transforming` accepts these entry statuses."* It is not one, and it has drifted in
**both** directions (it also admits `pending_review`, which `TransformableFrom` excludes).

**The fix is red before it ships.** `problemContract.test.ts:110-113` — titled *"the guard mirror itself
matches the backend's sets"* — loops **every** op asserting
`OP_ALLOWED_FROM[op].has("rejected_by_supplier")).toBe(false)`. Mutation, `ran-it`: adding
`rejected_by_supplier` to `OP_ALLOWED_FROM.transformOrder` (i.e. correcting the mirror) turns that test
**RED**. `recovery.test.tsx:320` independently asserts `expect(api.transformOrder).not.toHaveBeenCalled()`.

Backend's own `OrderStatusMachineTests.RejectedBySupplier_ExitsThroughACorrectionLoop_NotARedelivery:713-716`
calls this *"the operator's one-click exit."* **There is no click.**

Not fully stranded — the panel is a banner over the live workshop and `OrderResolutionService` has no
from-status guard, so a line edit still walks the order out. That is why it is P1, not P0. But the copy
tells the operator the opposite, and the only signposted route is re-upload as a **new order**,
discarding the `ConnectionRevisionId` pin, the per-order override and audit continuity.

### 3 — WP-24's rejected-order CTA is inert · P1 · WP-24

`problemCopy.ts:372` → `/inbox/${c.orderId}?details=response`. `OrderWorkshop.tsx:306-312` reads
**`?tab=`** only, and does so inside a `useState` initialiser (once, on mount). Nothing in `src` reads a
`details` param.

The panel is a banner **already rendered at `/inbox/{orderId}`**. The customer clicks the primary green
CTA "See their reply" and lands on the same screen, drawer closed, nothing revealed. This is verbatim
WP-24's own D1 defect — *"its only CTA was a Link to `/inbox/{id}` — the page it had already replaced"* —
reintroduced by WP-24.

**Why the packet's own link walk missed it:** `src/test/appRoutes.ts:66-70` `normalizePath` strips `?`
before matching, so the contract walk is **query-blind by construction**; and
`problemContract.test.ts:74` explicitly asserts `resolvesToLivePage('/inbox/ord-1?details=response')`
is `true`, holding it up as the example of a live destination.

Mutation, `ran-it`: changing it to the working `?tab=response` turns `recovery.test.tsx:343` **RED**
(`expected [ /inbox/ord-1?tab=response, … ] to include /inbox/ord-1?details=response`) while
`problemContract.test.ts` stays 65/65 green. **The test pins the broken value.**

Note the fix is not a param swap: `detailsTab` is a `useState` initialiser, so a same-route push would
not reopen the drawer. The CTA needs `openDetails('response')`, not a `Link`.

### 4 — WP-17 refuses an order and names a remedy that does not exist · P1 · WP-17

`IAcceptanceGate.cs:193-194` — `AcceptanceGateMessage.Compose` returns *"… Fix the order and send it
again, **or record an override saying why it should go anyway**."* That string is returned verbatim as
the 409 body of the manual-send endpoints (`OrdersController.cs:190-195`) and as the `transform_failed`
`errorMessage` (`OrderTransformService.cs:487-499` → `FailTransformAsync:783`). Pinned by
`AcceptanceGateTests.cs:120`.

The override surface is `OrderAcceptanceGateController.cs:74-106`,
`POST /api/orders/{id}/acceptance-gate/override`. At FE `478b809`:
`acceptance-gate|acceptanceGate|acceptance_gate` → **0 hits**;
`recordOverride|overrideReason|acceptanceOverride` → **0 hits**.

Reachable by default: `SupplierDockProfile.tsx:440` defaults a **new** acceptance rule to
`blockOnFail: true`.

Compounding it — the `transform_failed` recovery control re-enters `TransformAsync`, which re-evaluates
the same gate and re-writes `transform_failed`. A click loop whose documented exit has no UI.

---

## 3. WP-12, the wedge — the engine is sound; nothing can reach it

### 5 — Promotion has no UI trigger, and the help centre says it does · P1

The only writer of `PoMappingConfig.OutputTree` is `PromoteMappingService.cs:207-212`, inside
`PromoteAsync` (`:78`), whose only caller is `OrdersController.cs:1383`
(`POST /api/orders/{id}/mapping-override/promote`).

FE `478b809`: the sole client is `api-client.ts:1925` `promoteMapping()` — `git grep promoteMapping
origin/main -- src` returns **only its own definition**. The sole UI affordance is
`MapperWorkbench.tsx:953-958`, rendered under `{onSaveMappings && (…)}`; all three mount sites
(`MappingPanel.tsx:201`, `OrderWorkshop.tsx:792`, `ConnectionDetail.tsx:272`) pass **none** of
`onSaveMappings` / `saveMappingsLabel` / `savingMappings`.

The other supplier-config writer cannot carry a tree either: the FE `PoMappingConfig` interface
(`src/lib/api/types.ts:14-19`) has only `hasHeaderRecord/separator/header/lines` — no `outputTree` — and
`src/lib/api/mapping.ts:20-27` serialises exactly that.

**Unchecked step, checked:** the *pinned* path could have had its own writer.
`ConnectionBackfillService.cs:155` sets `InputMappingJson = poMapping?.ConfigJson`, and both consumers
(`OrderTransformService.TryReadPinnedOutputTree:1018-1030`, `ReplayService.DeserializeSnapshotTree:546-552`)
deserialise a `PoMappingConfig` from that same string. With `ConfigJson.outputTree` never written, **the
pinned layer is inert too.** `SupplierConnectionService.cs:142` only copies revision→revision.

**Live today:** `help/output-mapping-editor/page.mdx:70` and
`help/guides/map-supplier-po-fields/page.mdx:145` both instruct *"Save mappings for this supplier on the
review screen."* The button does not render.

This is the known WP-13 gap. What the audit adds: the pinned path is *also* inert, the FE DTO
structurally cannot carry a tree, and **the help centre is actively wrong today** — that last part is
customer-facing now, not future work.

### The WP-12 engine itself held up under attack

- **`OutputTreeRenderabilityTests` is NOT vacuous.** Seven mutations, each rebuilt and re-run against a
  1406-pass baseline: `OutputTreeFormats.cs:46` → 8 FAIL · `OrderMappingOverrideReader.cs:141` → 1 FAIL ·
  `OutputTemplateEmitter.cs:99` → 1 FAIL · `:312-314` → 1 FAIL · `OrderMappingOverrideReader.cs:168` →
  7 FAIL · `:150` → 5 FAIL · `OutputTemplateEmitter.cs:66` → 12 FAIL. **Zero tests stayed green with
  their own fix reverted.** (`ran-it`)
- **The plan's own claim that "the real-Postgres jsonb proof was SKIPPED" is FALSE at origin/main.** CI
  run **`30578073101`** (headSha `d233409`, success) contains
  `Passed …PromotedOutputTreePostgresTests.DesignOnOrderA_Promote_OrderBRendersByteIdentically_ThroughRealPostgres [164 ms]`,
  `…PromotedTree_SurvivesTheJsonbRoundTrip_WithNamespacesAndPredicateIntact [34 ms]`,
  `…PromotedTree_ProducesAStableConfigDigestAcrossOrders [183 ms]`. CI runs `dotnet test ProcuLink.slnx`
  on ubuntu-latest where Docker is live, so `[DockerRequiredFact]` does not skip. **Correct the plan.**
- **Order B needs no per-order override** for a promoted tree to apply — traced end-to-end through
  `OrderTransformService.TransformAsync` (`:175 → :187 → :195-199 → :211-217 → :263 → :270 → :275-281`),
  and CI-proven byte-identical on real Postgres.
- **The pinned revision genuinely snapshots the tree** — promoting again does not retro-change an
  already-delivered pinned order. `TryReadPinnedOutputTree` reads the revision's own column and never
  touches `_poMappings`.
- **Preview and delivery share one renderer and one renderability predicate** — both route through
  `RenderTreePreviewAsync` (`OrdersController.cs:1226-1258`) calling the same emitter with the same
  tokens and catalog; proven inseparable by mutations M1/M6/M7. The renderability half of
  "two call sites can diverge" could **not** be broken. Only the format-equality half diverges (§2.1, §5).
- **`SuppliersController` no longer wipes a promoted tree** — `MergePreservingPromotedOutput:552-577`;
  clearing requires the explicit `DELETE`. Both guard tests Passed in run `30578073101`.
- **All five pre-merge defects are fixed at `d233409`** — none merged unfixed.
- **`PromoteMappingService` cannot promote a layout delivery would refuse** —
  `IsHonestForThisSupplier:290-292` re-checks an already-stored tree on every promote.

---

## 4. What held up under attack

- **`delivery_unconfirmed` cannot be re-sent in one click.** The data-integrity invariant holds. All
  seven historically-drifting lists enumerated and agreeing: `RedeliverableFrom` (`:265-266` →
  `OrdersController.cs:1987`), `ClaimableForDispatchFrom` (`:300-301`),
  `ClaimableForAutomaticDispatchFrom` (`:313-314` — no park), `ClaimableForRetryFrom` (`:323-324` — no
  park), `RetryableFrom` (`:358-359` — no park), `HoldableForBillingFrom` (`:348-349`),
  `ManuallyDeliverableFrom` (`:423-424`). Belt: `DeliveryService.cs:1169` refuses a parked order by name
  with `NotRetryable`. **WP-19 and WP-24 added the park to nothing.** FE half: `inboxSend.ts`
  `BULK_SELECTABLE_STATUSES` excludes it, `bulkSendNeedsDuplicateConfirm()` returns true for the park
  *and* for unknown-status ids, `deliveryUnconfirmed.test.tsx:155` pins it.
- **WP-14's four named pre-merge defects are genuinely fixed, and the tests are load-bearing.**
  `EffectiveEntityResolver.Clone` carries all 19 header + 8 line columns — mutation-proven, deleting both
  blocks turned **5** tests RED (baseline 1406/0, restored 1406/0) (`ran-it`). Write path case-folds
  (`FindExistingAsync`, all three writers). Batch dictionary seeded `Ordinal` (B-1/b-1 closed).
  `SupplierSuggestionService` now agrees with `OrderServiceShared.cs:119-123`. A **third** comparer site
  was found (`SupplierCatalogService.cs:77-97`) and is a deliberate, documented, registered exception —
  folding there would collapse two supplier SKUs and delete a product.
- **WP-20's media-type table is exhaustive and the emitters are genuinely bound to it** — 9 enum
  members, 9 rows; mutation-proven in two passes, including the two-part D7 mutation (`ran-it`). The
  pre-merge overwrite-test vacuity was fixed by **adding** `FileDropOverwriteWiringTests` (drives real
  `DispatchAsync` against a real config row), not by repairing the original.
- **WP-34's fingerprint is over the bytes actually sent** — `DeliveryService.cs:321-324` downloads once
  into `content`; `:345` hashes that array; `:392-399` dispatches that array; `:426` persists it. No
  re-derivation. Survives both known hazards: a pre-dispatch marker writes no `IdempotencyKey` and
  `ArtifactSha256 = null`, so `PassportService.cs:201-204` offers no download; and the key is
  artifact-scoped, so a re-transform mints a new key rather than re-pointing an old attempt.
- **WP-19's 4xx split is sound.** 401/403/404/408/429 → Retryable + a distinct operator sentence →
  `delivery_failed`; 422 and bare-400-with-body → `rejected_by_supplier`. One predicate
  (`SuppressesAutomaticRetry:244-246`), three consumers — they cannot disagree. `Retry-After` survives
  wire → dispatcher → `DeliveryService` → job (the record `with`-copy at `:417` preserves it, which is
  why `git grep RetryAfter -- DeliveryService.cs` returning zero is a red herring).
  `DeliveryOutcome` semantics hold: `NotRetryable` never reschedules, `ClaimLost` reschedules on the
  retry leg. `rejected_by_supplier` appears in **no** delivery claim set, so WP-19 cannot cause refused
  bytes to be re-shipped.
- **WP-11's billing honesty is real.** `BillingFeature` is now 10 members; WP-11 **deleted** the five
  that had no enforcement point rather than faking gates for them. Every remaining member has a named
  site and all resolve (`SettingsController`, `SuppliersController`, `AuditController`,
  `SupplierAcceptanceController`, and the three `ProcuLink.Worker/Jobs/*PollOrgJob`).
- **The Wave-1 retirement migration is safe.** `20260730094527_Wave1RetireDeadSubsystems` drops **tables
  only** and explicitly refuses to drop `organisations.webhook_secret_encrypted` (`:98`) — the column
  drop that would break the Worker, which never migrates.
- **`src/lib/standards/catalog.ts` was NOT swept by WP-25/26** — absent from `478b809`'s 50-file
  changelist. The one anti-drift mechanism that works is intact.
- **WP-14's FE mirror is exact** — `BINDABLE_HEADER_FIELDS` = 33, `BINDABLE_LINE_FIELDS` = 20, identical
  names *and order* to `CanonicalRowFields.cs` at `504d9cc`.
- **The audit's "`getDownloadUrl` has zero callers" is refuted at `478b809`** — `OrderPassport.tsx:338`
  calls it, rendered via `OrderDetailsDrawer.tsx:200`, with 503 lines of tests.
- **`20d5239` papered over nothing behavioural** — a 2-line test-only fix for a `tsc` error surfaced the
  day typecheck became a required gate.
- **The inbound-mode pin is not vacuous** — `BridgeSidebar.test.tsx:336-339` exercises the *post-rename*
  component; WP-25's nav rename routed through `counterpartyPlural`, not a literal. The founder's
  binding constraint on WP-25/26 was respected.
- **BE CI is a trustworthy gate** — `dotnet test ProcuLink.slnx` with **no `--filter`**, so all three
  suites run; and the `cancel-in-progress` fix is correct (group by SHA on main pushes, by ref on PRs).

---

## 5. The guards — three new bypasses, and two old traps confirmed closed

Every bypass below was **run end to end**, not reasoned about: a real orphan page or a real synthetic
DbSet was created, the real guard was executed, and the result recorded. Worktrees removed afterwards.

### Both plan TRAPS are CLOSED as merged — verified by planting real orphans, not by reading regexes

- **TRAP 7 (`page.mdx` blindness) — closed.** `route-reachability.test.ts:229`
  `PAGE_FILE_RE = /^page\.(tsx?|mdx)$/`; `appRoutes.ts` `PAGE_FILES = ["page.tsx","page.mdx"]`. A
  planted orphan `page.mdx` **went red**.
- **TRAP 6 (phantom guide targets) — closed.** `REGISTRY_FILES` (`route-reachability.test.ts:193-200`)
  excludes `guides.ts` / `help-articles.ts` from the raw text scan and re-credits guides only through
  `linkedGuides()` (`guides.ts:320`, `status === "live"`). Both previously-bypassing pages —
  `admin/guides/unfreeze-a-pilot-workspace` and `help/guides/set-up-your-workspace` — **were reported**.
  The other href-carrying registries were checked for the same shape and are clean.

### G1 · P1 · A `//` after a colon is not stripped — **FIXED, PR #66**

`src/test/sourceScan.ts` `stripComments`: `if (c === "/" && next === "/" && prev !== ":")`. `prev` is
the last **non-whitespace** character emitted, so every colon in the language took the URL-scheme
exemption — `key: // …`, `case "x": // …`, a ternary, a TS annotation — and the rest of the line was
handed to both guards as live code.

**Proven end to end:** an orphan page whose only referrer was `ready: // <Link href="/…">` passed
`route-reachability.test.ts` **18/18**; deleting the comment file alone flipped it to
`1 route(s) exist but nothing navigates to them`. Four live instances on origin/main
(`catalogSourceHelpers.ts:135`, `sourcePickerModel.ts:84` among them), **none currently holding a link**.

Direction: **blind / false-green** — the dangerous one. Symmetrically the crawl will not blame such a
comment for a 404. See §8.

### G2 · P1 · A markdown link inside an MDX code fence is credited as navigation

`stripComments`'s mdx branch strips only `{/* … */}` and `<!-- … -->`; there is **no fence state**.
`MD_LINK_RE` then runs against comment-stripped text. Ran it: a ```` ```ts ```` fence containing
`// [old](/zz-A16)` yields `["/zz-A16"]` for both guards. End-to-end: a ```` ```md ```` fence appended
to `help/billing-faq/page.mdx` cleared a planted orphan — the run reported only the *other* orphan,
never this one.

The help centre is 46 `page.mdx` files full of code fences. **Latent, not live** — all 46 scanned with
real fence tracking, 0 markdown links currently sit inside a fence or inline-code span. Note the
asymmetry: `scripts/check-vocabulary.mjs`'s `mdxScanner` **does** track fence state. The link guards do
not. Precedent for the fix already exists in-repo.

### G3 · P2 · `extractRaw` runs its `re` patterns against **unmasked** text

The anchor branch uses `pattern.anchor.exec(masked)`; the `re` branch uses `pattern.re.exec(code)`.
So literal masking — the protection the module header advertises — applies only to anchor patterns.
Ran it: `const msg = "see [docs](/zz-A14) for more";` yields `["/zz-A14"]`, and a tuple-shaped path
inside a single-quoted JSON blob yields `["/zz-A15"]`. A toast body, a JSON fixture or a docs snippet
confers reachability. Lower severity only because the construction is less natural than G1/G2.

### G4 · P2 · BE orphan guard: naming a service in a **string literal** discharges the consumer obligation

`OrphanGuardTests.HasConsumerBeyondItsOwnCrud` does
`file.Text.Contains(stem) && referenceRegex.IsMatch(file.Text)` over **comment-stripped** text — and
`StripComments` preserves string literals verbatim **by design** (that is `7ed0961`'s whole fix). The
check cannot tell a literal from code.

Driven through the real public `OrphanDetector.FindOrphans` from a console harness (xunit never
invoked): baseline corpus gives `orphans=[ZzTelemetry]` **caught**; then a
`_logger.LogInformation("ZzTelemetryService is deprecated…")` in an unrelated file gives `orphans=[]`
**passed**; an `[Obsolete("superseded by ZzTelemetryService")]` attribute — passed; a raw string
literal — passed; a `#region ZzTelemetryService notes` — passed. Harness fidelity confirmed in the same
run: a real repo scan produced `dbsets=53 files=506` and **exactly** the six `KnownWriteOnlyStores`
entries.

The shape is pervasive: `DeliverOrderJob.cs` names itself in six log strings, `ParseInvoiceJob.cs` in
two. **Not introduced by `7ed0961`** — the old regex pair also preserved literals; `7ed0961` fixed the
opposite direction. But the commit's own thesis ("prose looks like use and proves nothing") should
have covered it.

### G5 · P2 · The FE allowlist is not shrink-only in any enforced sense

`route-reachability.test.ts:134` states *"This list SHRINKS. Adding to it is a decision with a name on
it."* The three tests that follow check reason **quality** (`:886`, `:893`) and route **existence**
(`:917`) — no baseline, no count, no shrink assertion. Ran it: planted a genuinely unlinked page plus a
`KNOWN_DEEP_LINK_ONLY` entry whose reason was copied *in shape* from the guard's own passing fixture at
`:909` — **18/18 passed**. `"the allowlist cannot rot"` passes precisely because the route exists; it
catches the opposite failure. The BE side at least has a `MayOnlyEverShrink` test; the FE side has a
comment.

### G6 · P3 · The BE shrink-only baseline is a literal 60 lines below the list it guards

`OrphanGuardTests.cs:139` `AllowlistBaselineAt_2026_07_30` is an in-file `HashSet`; `:265` asserts no
entry outside it and `count <= baseline count`. Both assertions were evaluated verbatim by reflecting
the private static fields out of the rebuilt assembly, after adding a name to **both** literals:
`added=[]` passes, `7 <= 9` passes. The class doc says *"nothing may ever be added … this array is what
makes that non-negotiable."* The mechanism is a two-literal edit in one file with no external pin. P3
because the commit body concedes it ("move the baseline deliberately").

### G7 · P3 · The vocabulary gate cannot see copy inside a JSX expression container

`visibleSpansTsx`'s `TEXT_RE = />([^<>{}]+)</g` excludes braces by construction, and `looksLikeProse`
rejects any line containing `=` or a quote. Control first — `<p>Pick a revision to test</p>` gives
**FAIL, exit 1** (the gate is genuinely blocking). Then five jargon strings in one component: a `const`
rendered as `{HEADING}`, an escaped string in a container, a template literal, and an
`aria-description` — **only the aria attribute was blocked**. Everything inside a `{…}` container
passed. **Latent** — 164 non-exempt `.tsx` scanned, 1 hit and it is a CSS keyframe name, not copy.

### The guards that held

- **Outbound dead-link detection works** — a planted 404 from a component nothing imports was caught by
  file and path.
- **FE typecheck gate genuinely fails the build** — CI run **`30566069490`** (push, main) FAILED at
  `Typecheck (tsc --noEmit)`. Runs on both `pull_request` and `push: [main]`, no `continue-on-error`.
- **FE vitest gate genuinely fails the build** — CI runs **`30540383479`** (push, main — WP-04's own
  post-merge red) and **`30545135934`** (PR).
- **BE gate genuinely fails** — CI run **`30579652873`** (PR `wp17/server-side-acceptance-gate`) FAILED
  at `Test`.
- **The WP-01 mock-env leak is closed** — the workflow-level `NEXT_PUBLIC_USE_MOCK: 'true'` is
  overridden to `'false'` in the `test-unit` job's own env block.
- **Vocabulary gate is blocking, not advisory** — it ends by exiting non-zero on failure, proven by
  exit code against a planted violation.
- **Pre-commit hooks neither duplicate nor contradict CI** — and they earned their keep during this
  audit: they caught a `no-useless-escape` in the first draft of the PR #66 regex.
- **BE `StripComments` (`7ed0961`) survived nine adversarial inputs** — a verbatim string ending in a
  backslash, a raw string with an unbalanced quote, an escaped-quote char literal, an interpolated
  URL, a comment inside `#if false`. In all nine the comment was removed **and** the following code
  survived.
- **`504d9cc` removed the weaker copy, not the stronger one** — verified by diffing what was deleted:
  the removed private stripper was two regex passes; the survivor is the single left-to-right
  literal-aware scanner from `#92`, strictly stronger than either regex ordering.
- **No lenient / false-RED case found in the FE stripper** — 12 real-link fixtures all extracted
  correctly (link after an `https://` literal, after a slash-pair regex, after a regex containing a
  block-comment opener, after a nested template, after JSX text with an apostrophe, after `sftp://`
  prose, after a division expression, plus ternary and template hrefs).

### Guard scope gap, stated as scope rather than as a defect

`OrphanGuardTests.cs` is the only orphan guard in the backend and is **DbSet-only by construction**
(`DbSetsOf` reflects `DbSet<T>` off `ProcuLinkDbContext`). A controller, DTO or service with no consumer
is outside every guard in either repo.

---

## 6. Claims that outran their evidence

### C1 · P2 · `/security` claims a self-hosted mode that runs in ProcuLink's environment

`src/app/(marketing)/security/page.tsx:84`, the "Responsible AI" card:

> "Enterprise customers can opt into a self-hosted mode where document extraction — including
> scanned-PDF OCR — **runs entirely in your environment: documents never leave your region**, and
> nothing is sent to OpenAI."

What is implemented: `Organisation.SelfHostedOcr` (`ProcuLink.Core/Entities/Organisation.cs:85`), read
at exactly **three** non-test sites, **all** in `ProcuLink.Api/Services/Orders/OrderIngestionService.cs`
(`:1580` PDF routing, `:1645` XLSX routing, `:1868` web product search), plus `Program.cs:586-588`
gating a self-hosted OCR engine that runs **inside ProcuLink's own deployment**.

Unchecked steps, checked: (a) no customer-installable artifact exists — `git ls-tree -r origin/main`
returns only `Dockerfile`, `Dockerfile.worker`, `docker-compose.yml`, the dev stack; no chart, no
installer, no on-prem docs. (b) The flag gates **nothing beyond ingestion** — grepped every non-test
`.cs`. (c) FE has **zero** references to `SelfHostedOcr`/`noEgress` under `src/**`, so it is
operator-only, not something a customer can "opt into".

Consequence for the second clause: because the flag does not gate delivery, a no-egress org using the
`email` channel still sends the **complete PO as an attachment** through Postmark (US) —
`EmailApiDeliveryDispatcher.cs:109`, established in `AUDIT-2026-07-27.md §11b`.

The product's own help page states it correctly — `help/ai-suggestions/page.mdx:46` says only "never
send order data to OpenAI". The marketing page over-claims relative to it. This survived WP-10
(`214b3f3`) **and** its follow-up correction (`ac27353`): the residency fix was applied to one
sentence, not to the claim.

Residual honest uncertainty: an on-prem offering could exist as a contract not represented in the repo.
Founder-answerable. Nothing in code supports it.

### C2 · P2 · No SSH host-key verification is *possible* anywhere — proven, not asserted

**Library default proven twice.** Decompiled (`ilspycmd`, against the exact pinned
`ssh.net/2024.2.0/lib/net8.0`):

```csharp
protected bool CanTrustHostKey(KeyHostAlgorithm host) {
    var h = this.HostKeyReceived;
    if (h != null) { var e = new HostKeyEventArgs(host); h(this, e); return e.CanTrust; }
    return true;                       // no subscriber -> trusts, never inspects the key
}
```

`HostKeyEventArgs`' constructor sets `CanTrust = true` unconditionally. Confirmed by live execution
against assembly `Renci.SshNet, Version=2024.2.0.1` with a real 2048-bit RSA `KeyHostAlgorithm`:
no-subscriber gives `True`; a subscriber setting false gives `False`.

`HostKeyReceived` has **zero hits repo-wide**, and verification is **structurally impossible**: there is
nowhere to store an expected key. `SupplierDeliveryConfig` has no fingerprint column; `SftpConfig` is
Host/Port/RemotePath/MakeDirectories/TimeoutSeconds; `SftpCredentials` is
Username/Password/PrivateKey/PrivateKeyPassphrase; FE `DeliveryConfigEditor.tsx:70`
`MANAGED_CONFIG_KEYS.sftp` has no fingerprint input. Every repo-wide `fingerprint` hit is the unrelated
`SchemaFingerprint` feature.

Affects **both** delivery (`SftpDeliveryDispatcher.BuildConnectionInfo:359-378`) and ingress polling
(`RenciSftpClientFactory.cs:17`).

**The contrast is what makes this an omission rather than a decision:** FTPS does it properly —
`FtpsDeliveryDispatcher.cs:144` disables blanket acceptance, `:145-147` installs a real
`ValidateCertificate` handler, and a per-supplier `AllowInvalidCertificate` defaulting false is
surfaced as an operator checkbox at `DeliveryConfigEditor.tsx:1008-1020`. SFTP has no equivalent at any
layer.

Ranked P2 rather than higher because SFTP delivery has no live proof and no known user, so exposure
today is small. Already tracked as WP-38; this upgrades it from "no host-key verification" to a proven
default-accept with the mechanism pinned. A prior internal audit accepted it at P3.

### C3 · P3 · The capability ledger has not been reconciled since sixteen packets merged

`04-CAPABILITY-TRUTH-LEDGER.md` is still the 2026-07-27 baseline. Rows for `output-designer`,
`emit-custom-tree`, `retry-dead-letter`, `order-passport`, `acceptance-profiles`, `item-code-learning`
and `billing-gates` all describe pre-packet state. Doc staleness rather than a customer-facing defect —
but WP-40 intends to make this file CI-authoritative, so it should be reconciled first.

### C4 · P3 · `format-catalog.ts` protects only one of its three tables

The file's own rule (`:20-21`): *"nothing is 'live' … unless it works in production today."* Its
anti-drift mechanism — `parseStatus()`, which throws the static build on a typo'd id — covers **only
document standards**. `IMPORT_METHODS` and the delivery-channel statuses are hand-written literals with
no guard. `:53` marks IMAP polling `status: "live"`; the ledger records it as never run against a real
mailbox, and PR #79 converted `LiveImapIngressTests.cs` to a **declared skip** — honest, still never
run. **I could not prove IMAP is broken**, only that no production evidence exists for a badge whose own
file requires it. The structural gap is the durable point, not the single badge.

---

## 7. Refuted — claims that died on the unchecked step

Recorded so nobody re-raises them. Four are my own.

| Claim | Verdict | The step that killed it |
|---|---|---|
| "SFTP/S3 pull have no config surface, so the `live` badge is false" | **REFUTED** (mine) | `SettingsController.cs:126-180` ships GET/PUT `sftp` and `s3`, plan-gated, with a save-time SSRF pre-check. The 2026-06-29 QA doc saying otherwise is a month stale; `STATUS.md:1086` already flags it. *Is the doc still true?* — it was not. |
| "WP-11 left 10 of 16 billing gates unenforced" | **REFUTED** (mine) | `BillingFeature` is now **10** members. WP-11 **deleted** the five with no enforcement point (`Xml`, `Pdf`, `MappingLibrary`, `DeliveryHistory`, `SlaOnboarding`); two more went with their retired surfaces. Deleting an unenforceable gate is the honest fix. |
| "Cxml / WebhookDelivery / ErpConnectors have no gate — `HasFeatureAsync` never appears in `UpsertDeliveryConfig`" | **REFUTED** (mine) | `SuppliersController.cs:758` gates via a resolved `gated` object surfaced at `:776`. *Is `HasFeatureAsync` the only gate mechanism?* — it is not. |
| "The three poll jobs named in `EnforcedBy` do not gate" | **REFUTED** (mine) | They live in `ProcuLink.Worker/Jobs/`, not `ProcuLink.Infrastructure/`. My grep path was wrong. `EmailPollOrgJob.cs:109`, `S3PollOrgJob.cs:49`, `SftpPollOrgJob.cs:51`. |
| "WP-12's real-Postgres jsonb proof was SKIPPED" (**the plan's own claim**) | **REFUTED** | CI run `30578073101` (headSha `d233409`) shows all three `PromotedOutputTreePostgresTests` as **`Passed`** with timings. CI runs the full solution on ubuntu-latest where Docker is live, so `[DockerRequiredFact]` does not skip. |
| "`getDownloadUrl` has zero callers" (audit §3) | **REFUTED** | `OrderPassport.tsx:338` calls it, rendered via `OrderDetailsDrawer.tsx:200`, with 503 lines of tests. |
| "`7ed0961` introduced the BE literal blind spot" | **REFUTED** | The pre-`7ed0961` regex pair also preserved literals. `7ed0961` fixed the opposite direction (a false ORPHAN from a swallowed composition root). |
| "The WP-14 comparer defect has a third live site" | **REFUTED** | `SupplierCatalogService.cs:77-97` is a deliberate, documented, registered exception — folding there would collapse two supplier SKUs and delete a product. |
| "`Retry-After` is never honoured — grepping `DeliveryService.cs` returns zero hits" | **REFUTED** | The record `with`-copy at `:417` preserves it; both jobs read the returned result. |
| "WP-20 widened the resend set via `ResendSafety`" | **REFUTED** | The `1c47fc0` edit is a single doc-comment hunk; enum members byte-identical. The behavioural change **parks an extra case**. |
| "WP-17's gate blocks the ONLY exit WP-19 added" | **DOWNGRADED P1→P2** | WP-19 added **two** edges; the gate touches only the transform leg. The resolve exit survives — `OrderResolutionService.IsFinished:70-71` refuses only `{failed}`. |
| "The output designer's preview and delivery renderers can diverge" | **REFUTED** | Both route through one `RenderTreePreviewAsync` and one renderability predicate; proven inseparable by three mutations. Only the *format-equality* half diverges (§2.1). |

---

## 8. The anti-vacuous guard itself — its GREEN is a narrow claim

`ProcuLink.Api.Tests/Meta/VacuousTestPassScanner.cs` shipped in PR #79 (`0184261`) on 2026-07-30 and a
whole audit round leans on it. It is genuinely well built — Roslyn-based rather than regex, correctly
excluding returns nested in lambdas and local functions, and it even documents and defends the
"relative vs absolute worktree exclusion" self-trap with a companion
`TheGuardActuallyReadsTheTestProjects` assertion.

But its **only** rule is: a *valueless* `return` belonging directly to a test method, not preceded by an
assertion. Nine bypass shapes were executed against the real scanner (copied verbatim into a standalone
Roslyn console harness). **The canonical offender was correctly flagged — the control run proves the
harness discriminates.** Of the other eight, **eight scanned clean**, and every one would be recorded by
xUnit as `Passed` while verifying nothing:

| Shape | Result | Why the scanner cannot see it |
|---|---|---|
| `if (env is not null) { asserts }` — no return at all | **clean** | no `ReturnStatementSyntax` to iterate |
| `foreach (var c in cases) { asserts }` over a possibly-empty collection | **clean** | same |
| assertion inside a lambda *defined before* the guard | **clean** | `AssertionPrecedes` is purely positional over `body.DescendantNodes()` — it counts a never-invoked lambda's `.Should()` as "preceding" |
| `if (env is null) { Assert.True(true); return; }` | **clean** | a content-free tautology satisfies `IsAssertion` |
| `goto Done;` exit | **clean** | not a `ReturnStatementSyntax` |
| `do { … break; } while (false);` exit | **clean** | not a `ReturnStatementSyntax` |
| `return Task.FromResult(true);` | **clean** | `IsValuelessReturn` is exact string equality against four literals |
| `return ValueTask.FromResult(0);` | **clean** | only `Task.FromResult(0)` is listed, not the `ValueTask` spelling |
| `await Task.CompletedTask; return;` in a real `async Task` test | **flagged** | C# forbids a value return there, so it is a bare `return;` |

Note the asymmetry the third row exposes: `BelongsDirectlyTo` correctly excludes lambda **returns**, but
`AssertionPrecedes` does **not** exclude lambda **assertions**.

**Live instances already in the suite** (all `foreach`-over-an-unguaranteed-collection, Candidate B):

- `OrdersControllerRedeliverTests.cs:193` `Redeliver_FromInvalidStatus_ErrorMessage_ListsEveryRedeliverableStatus`
  — mitigated cross-project by `OrderStatusMachineTests.cs:163`, which pins `RedeliverableFrom`'s exact
  three elements.
- `AcceptanceGateMatchesValidationTests.cs:128` `BlockerMessages_areTheSameSentences_thePanelShows` —
  the entire body after two live service calls, with no non-emptiness assertion. Mitigated by a sibling
  in the same class (`:90-98`) that does `Assert.NotEmpty(blockers)` against the identical fixture.
- `ConnectorManifestCatalogTests.cs:69,108,126` — lower confidence; siblings cover most of it, but
  `All_And_ByKey_AreConsistent` checks only the `All → ByKey` direction, so `All` shrinking while
  `ByKey` stays populated slips past every sibling.

**Coverage gap.** `TestSourceFiles` globs `*.Tests` top-level directories. **`ProcuLink.TestSupport` is
compiled into two of the three test assemblies** — `ProcuLink.Api.Tests.csproj:34` and
`ProcuLink.Infrastructure.Tests.csproj:33` both `<Compile Include="..\ProcuLink.TestSupport\*.cs" />` —
while sitting outside the glob entirely. It holds only attribute definitions today (zero `[Fact]`), so
the gap is latent; but any test added there would run in CI and be permanently invisible to the guard.
All other non-`.Tests` top-level directories were grepped and hold no real xUnit tests.

**How to read a green run from this guard:** *"no bare early return before an assertion"* — not
*"every test asserts something."*

---

## 9. Fix shipped

**FE PR [#66](https://github.com/dimnovare/project-proculink/pull/66) — "A comment after a colon is
still a comment"** (`54fee0d`). Closes §5 G1.

Test-infrastructure only; no product code, no route or navigation change. The scheme exemption now
requires the colon to be immediately adjacent to the slash pair and to terminate a scheme-shaped
identifier, matched against raw preceding text so whitespace defeats it.

Guard tests cover the four leaking shapes plus an over-correction control. **Mutation-checked:**
restoring `prev !== ":"` with the new tests in place fails on `/orphan-obj-key` — the regression test is
not vacuous. Full suite **1582/1582**, `tsc --noEmit` clean, vocabulary and PageShell gates clean.

Nothing else was fixed. Everything above this line is reported, not changed — several of the findings
are product decisions (which recovery control to offer, whether to build the override UI) rather than
defects with an obvious correct patch.

---

## 10. What to do next, in order

1. **WP-12's format contract (§2.1).** Highest value and the only item that has already changed
   delivered bytes. Pass the connection's delivery format into `OutputStructureDesigner`, default the
   tree to it rather than to JSON, and either preview at the delivery format or say plainly that the
   design will not be applied. The gate is right; the UI has to stop contradicting it.
2. **Reconcile `problemActions.ts` with `OrderStatusMachine` (§2.2), and fix the test that forecloses
   it.** The file declares itself a mirror; making it one is not a redesign. Sequence it with item 3,
   because giving the operator a re-transform button that the WP-17 gate then refuses (§ finding 12)
   trades one dead end for a loop. Deliberately **not** fixed in this audit: which control to offer from
   a supplier-refused order is a product decision.
3. **Either build the acceptance-override UI or stop promising it (§2.4).** One sentence of copy is the
   cheap half; the endpoint already exists and is tested.
4. **WP-24's inert CTAs (§2.3, §5).** `?details=response` needs `openDetails('response')`, not a `Link`.
   And teach the contract walk to see query parameters — it is query-blind by construction, which is why
   its own fixture holds the broken value up as correct.
5. **The two remaining guard bypasses (§5 G2, G3).** MDX fence state has a working in-repo precedent in
   `check-vocabulary.mjs`'s `mdxScanner`; the `re`-vs-`masked` asymmetry is a one-line change.
6. **Vocabulary drift in the help centre (§ finding 6).** The status articles teach the pre-WP-25 names
   *inverted*, and `helpCopyMatchesShipped.test.ts:59-60` currently pins a stale nav name in place —
   fix the test in the same change or it blocks the correction.
7. **Widen the anti-vacuous scanner (§8)** to no-return conditional/loop bodies, scope-aware
   `AssertionPrecedes`, and non-`return` exits; add `ProcuLink.TestSupport` to its glob.
8. **Reconcile `04-CAPABILITY-TRUTH-LEDGER.md` (§6 C3)** before WP-40 makes it CI-authoritative.
9. **Founder items.** The `/security` self-hosted claim (§6 C1) needs a yes/no on whether an on-prem
   offering exists at all. SSH host keys (§6 C2) stay tracked at WP-38.

---

## Method notes worth keeping

- **The stale-build trap fired during this audit and was caught.** Restoring a mutated file from a
  `.bak` via `Move-Item` preserves the backup's **old** mtime, so MSBuild skips recompilation and the
  "restored" run reports the **mutated** result. `git status --short` showing clean is not sufficient.
  Any mutation recipe in this repo needs an explicit `touch` after restore.
- **Two verifiers reached §2.2 independently** — one analytic from the backend side, one by mutation
  from the frontend side. That is genuine convergence with independent provenance, not one source
  echoed twice.
- **Refuting is most of the work.** Twelve claims died on the unchecked step, four of them mine, and
  one was the plan's own. The ratio is the point: the standard is what makes the surviving findings
  worth acting on.
