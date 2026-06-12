# Scoring — through the lens of "how fast does a new ops coordinator reach a CONFIDENT first real delivery, and does the tool keep teaching after day one"

**Disputed facts verified against source before scoring** (this changes the scores):

1. `OnboardingController.cs` is exactly the 4-boolean shape, no `IsSample` exclusion (confirmed, lines 35-53). All three designs' premise holds.
2. **Design A's `hasTestFired` claim is TRUE.** `DeliveryService.TestFireAsync` (`C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Infrastructure\Services\DeliveryService.cs:255-267`) writes a real `DeliveryAttempt { OrderId = null, Status = "success"|"failed", AttemptNumber = 0 }`. **Design C's reason for omitting the flag ("no verified persistence") is factually wrong**, and Design B's hedge was unnecessary. `DeliveryAttempts.Any(a => a.OrgId == orgId && a.OrderId == null && a.Status == "success")` is a clean, already-persisted signal.
3. `SupplierDockProfile.tsx:1001` is literally `const [tab, setTab] = useState<Tab>("overview");` — G2a confirmed, the one-line fix all three designs share is real.
4. `SupplierProducts`, `ItemMappings`, `SupplierDeliveryConfigs` DbSets all exist in `ProcuLinkDbContext.cs` — every extended-status query in all three designs needs zero migrations.
5. Sign-up redirect is `fallbackRedirectUrl="/bridge"` at `sign-up/[[...sign-up]]/page.tsx:281` — B's claim accurate. `OnboardingWizard.tsx` is 715 lines.

| Criterion | A (checklist) | B (guided-flow) | C (contextual) |
|---|---:|---:|---:|
| Time-to-first-**confident**-delivery | **72** | 84 | 60 |
| Day-2+ usefulness | 66 | 74 | **88** |
| Honesty | **92** | 78 | 86 |
| Implementation risk (higher = safer) | **88** | 54 | 72 |
| Fit with Group L investments | **90** | 70 | 78 |
| **Total (equal weight)** | **81.6** | 72.0 | 76.8 |

**Scoring rationale (veteran's voice, briefly):**

- **A wins time-to-CONFIDENT over what its raw speed suggests.** B gets a "delivered" badge in 3 minutes, but it's delivered to a cartoon. Thirty years in ops teaches you: confidence is a test payload hitting MY endpoint and the response code coming back. A is the only design that makes config+test-fire the certification (`(hasDeliveryConfig && hasTestFired) || hasDelivery`); B certifies milestone 4 on config alone. B scores 84 on raw speed-to-payoff; A's 72 reflects no rehearsal track — fixed in the synthesis.
- **B's honesty 78:** the sandbox dispatcher is server-guarded and labeled, credit for that — but it writes truthful-looking `delivered` rows from a fake counterparty into the same tables ops views and the BE `first_delivery_succeeded` analytics emitter read, and the design never addresses that blast radius. The north-star rule is "never equate HTTP 200 with acceptance"; a simulated receiver is one mislabeled surface away from violating it. C's 86: maximally honest copy, but it drops a real honest signal (`hasTestFired`) on a false premise.
- **B's risk 54:** ~32 files, a NEW dispatcher registered in API + Worker DI on the production delivery path, a validation change in the config-save path, a cross-route provider state machine, and `SampleOrderService` seeding four artifact types — the most moving parts, several on the money path. Per project memory, seeded circular inserts are exactly where InMemory-green/Postgres-red bites.
- **C's day-2+ 88 is the best single idea in the pile:** state-driven hints that self-resolve, a PER-SUPPLIER catalog probe (keeps teaching on supplier #2 and #5, where A's org-level flag goes silent after supplier #1), the post-save test-fire nudge, and the only worker-down honesty (G9) anywhere. C's time-to-first 60: an impatient coordinator landing cold gets no momentum rail, and the sample's first ending is a failure state — honest, but the framing alone carries the conversion.
- **A's fit 90:** keeps the checklist bones, shrinks rather than deletes the wizard (the direction picker needs a home; rebuilding it on `/start` is B re-spending Group L money), reuses the sample endpoint untouched.

---

# WINNING SYNTHESIS — "Checklist spine, pre-wired rehearsal, screens that keep teaching"

**Skeleton: Design A** (server-truth 6-step checklist + topbar chip + wizard shrink + zero-migration status extension). **Grafted from B:** sample pre-wiring with the one-deliberate-gap teaching beat (catalog + 2-of-3 mappings seeded server-side), the conversion-moment framing at sample end, the shared `upload-formats.ts` constant. **Grafted from C:** per-supplier catalog probe on the review screen, post-save test-fire nudge, retry demotion copy, worker-down escalation (G9), shared `useSampleOrder` mutation, the two cliff help articles. **Explicitly rejected:** B's `SandboxDeliveryDispatcher` (simulated `delivered` rows + delivery-path risk + analytics pollution; the test-fire against the user's real endpoint is the confidence moment and it already exists), B's `/start` page and cross-route `OnboardingProvider` (the checklist hero + chip cover re-entry at a fraction of the risk), C's ban on any aggregate surface (the topbar chip is the cheapest "keeps teaching between screens" mechanism), A's `public/demo-catalog.csv` (superseded by server-side seeding; cheap fast-follow if wanted).

## The checklist model (6 steps, all signals server-verified, all sample-excluded)

| # | id | Step | Done when | Locked until | Deep link |
|---|---|---|---|---|---|
| 1 | `supplier` | Add your first supplier | `hasSupplier` | — | `/library/suppliers` |
| 2 | `catalog` | Add the supplier's item codes | `hasCatalog \|\| hasItemMappings` | 1 | `/library/suppliers/{firstSupplierId}?tab=catalog` |
| 3 | `upload` | Upload an order | `hasUpload` | 1 | `/upload` |
| 4 | `resolve` | Match item codes on an order | `hasResolvedMapping` | 3 | `/inbox/{firstActionableOrderId}` (fallback `/inbox`) |
| 5 | `delivery` | Set up delivery and send a test | `(hasDeliveryConfig && hasTestFired) \|\| hasDelivery` | 1 | `/library/suppliers/{firstSupplierId}?tab=delivery` |
| 6 | `send` | Send your first order | `hasDelivery` | 3, 4, 5-config | `/inbox/{firstActionableOrderId}` |

Step 5 intermediate state: "Configured — send a test to finish" / CTA "Send a test". A real delivery already succeeded ⇒ step counts done (no nagging for a redundant test). All copy nouns via `useOrderDirection().labels`. Status-query error ⇒ render nothing new, never a fabricated `0/6`.

## Sample strategy (the contract)

1. **Samples teach, never certify.** `IsSample` orders/suppliers stay quota-exempt and are excluded from every status flag. A sample-only org correctly shows 0/6 — flag this intended flip in the PR.
2. **Pre-wired with ONE deliberate gap (from B).** `SampleOrderService` additionally seeds, idempotently: ~5 catalog rows on the `__sample__` supplier (including the code for fixture line 3, e.g. `SAMPLE-A4-500`) and item mappings covering fixture lines 1–2 only. First run: two lines resolve automatically ("ProcuLink remembered these"), line 3 is the user's one manual rep on the money screen — with catalog-grounded suggestions if an AI key exists, plain typing otherwise.
3. **The sample's honest ending IS the delivery lesson (from C).** No sandbox. Send → `delivery_failed: configuration missing`. The sample banner pre-frames it: *"Practice order — free, doesn't count against your plan. Sending will stop at 'delivery not set up' — that's expected for the practice supplier."* The upgraded `FailedPanels` then shows primary CTA **`Set up delivery`** → this is the conversion moment (B's framing, A/C's mechanics, zero new delivery code).
4. **Test-fire is the real rehearsal of delivery** — framed as "send a test payload to prove the connection", it writes a real `DeliveryAttempt` and ticks step 5, because it's practice against the user's real endpoint, which is exactly what the step certifies. Honesty note rendered with the result: *"A successful test means their endpoint answered — it doesn't mean an order was accepted."*
5. **Entry points:** checklist hero, `/inbox` genuine-empty, `/upload` (existing), Cmd+K — all through one shared `useSampleOrder` hook → `/inbox/{id}?sample=1`.

## Backend needs — ZERO new endpoints, ZERO migrations, 2 changed files + 2 test files

**B1. `ProcuLink.Api\Controllers\OnboardingController.cs`** — extend `GET /api/onboarding/status` additively; existing 4 flags gain sample exclusion:

```jsonc
{
  "hasSupplier": true,            // + DeletedAt == null && !s.IsSample
  "hasUpload": false,             // + !o.IsSample
  "hasResolvedMapping": false,    // + !l.Order.IsSample
  "hasDelivery": false,           // + !o.IsSample
  "hasCatalog": false,            // SupplierProducts joined to non-sample, non-deleted org suppliers
  "hasItemMappings": false,       // ItemMappings, org-scoped, non-sample-supplier
  "hasDeliveryConfig": false,     // SupplierDeliveryConfigs joined to non-sample org suppliers
  "hasTestFired": false,          // DeliveryAttempts: OrgId, OrderId == null, Status == "success"  ← VERIFIED convention, DeliveryService.cs:255-267
  "firstSupplierId": "guid|null", "firstActionableOrderId": "guid|null",
  "supplierCount": 0, "orderCount": 0, "deliveredCount": 0,   // non-sample
  "hasSampleOrder": false, "sampleOrderId": "guid|null"
}
```

All org-scoped EF `AnyAsync`/`CountAsync`/`FirstOrDefaultAsync`, no raw SQL, fine for a 30s-stale call.

**B2. `ProcuLink.Infrastructure\Services\SampleOrderService.cs`** — idempotent seeding (existence-checked, Hangfire-idempotent rule): catalog rows + 2-of-3 item mappings on the `__sample__` supplier; pin the fixture line-3 buyer code and the seeded catalog row together with a comment referencing `ProcuLink.Api\Fixtures\sample-order.csv`. **No PO-mapping seed, no delivery-config seed, no dispatcher.**

**Tests:** `ProcuLink.Api.Tests` — sample-only org reports all-false; each new flag true/false; `hasTestFired` from a null-OrderId success attempt; `firstActionableOrderId` ordering; two-org isolation. `SampleOrderServiceTests` — seeding idempotency, **round-trip verified on real Postgres (Testcontainers), not just InMemory** (per project memory: InMemory masks FK/insert-order failures).

## Frontend component/file plan (`C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`)

**New (5 code + 2 MDX):**

| File | Purpose |
|---|---|
| `src/hooks/useOnboardingStatus.ts` | TanStack query, `queryKey ["onboarding-status"]`, `staleTime 30_000`, `refetchOnWindowFocus`, `enabled: isApiMockMode \|\| clerkReady` (clerkReady-starvation rule). Exports `invalidateOnboardingStatus(qc)`. |
| `src/hooks/useSampleOrder.ts` | Shared mutation: POST `/api/onboarding/sample-order` → invalidate status + orders → `router.push("/inbox/{id}?sample=1")`; exposes `isPending`; captures existing `sample_order_started`. |
| `src/components/bridge/SetupProgressChip.tsx` | Topbar pill `Setup 3/6` → `/bridge`; visible only while incomplete AND status loaded; ≥44px tap target. |
| `src/components/review/CatalogHintCard.tsx` | Review-screen hint; **per-supplier probe** via existing `GET /api/suppliers/{id}/catalog` (NOT the org-level flag) so it re-teaches on every new supplier; renders nothing while loading/errored; self-resolves when catalog exists or lines resolve. |
| `src/lib/upload-formats.ts` | `ACCEPTED_UPLOAD_FORMATS` (extensions + human list + dropzone `accept`), mirrors BE whitelist `OrdersController.cs:155-158`; cross-referencing comments in both files. |
| `src/content/help/...` (per existing `help-articles.ts` convention) | 2 MDX articles: `item-codes`, `delivery-setup` (incl. "200 ≠ acceptance"). |

**Changed (16):** `OnboardingChecklist.tsx` (6 steps, self-fetch via hook, step-5 intermediate, completion card with email/API/another-supplier links + sessionStorage one-time celebration, Pilot honesty line, sample CTA via `useSampleOrder`), `BridgeDashboard.tsx` (drop prop threading; wizard trigger unchanged), `OnboardingWizard.tsx` (shrink to direction + first supplier; delete steps 2–4 + resume; fix lime→`#2E8E3A`, `aria-checked`, error copy — net negative LOC), `SupplierDockProfile.tsx` (`?tab=` init at :1001 via `useSearchParams`, validated against `Tab` union, fallback `"overview"`), `SpineReview.tsx` (mount `CatalogHintCard`; sample banner pre-framing copy; first-resolution micro-helper line), `review/SendReadinessCard.tsx` ("Not configured — Set up →" deep link), `review/FailedPanels.tsx` (config-missing variant: primary `Set up delivery`, Retry secondary + "Retry won't succeed until delivery is set up."), `DeliveryConfigEditor.tsx` (post-save success strip + `Send a test now` + verbatim `DeliveryTestResult` + honesty note; invalidate status on success), `InboxView.tsx` (genuine-empty sample CTA), `BridgeTopbar.tsx` (mount chip), `CommandPalette.tsx` (`Getting started`, `Run a sample order`, `Open help` + placeholder fix to what the index actually searches), `UploadWorkbench.tsx` (formats from constant; refactor sample CTA onto `useSampleOrder`), `MagicMappingPreview.tsx` (90s poll escalation → "order processing may be paused — check system health" link to `/operations/health`, poll continues, no false "failed"), `HelpSlideover.tsx` (route map + the 2 articles), `types/procurement.ts` (extend `OnboardingStatus`, new fields optional), `lib/api-client.ts` (mock returns representative mid-progress payload).

**Cache invalidation:** call `invalidateOnboardingStatus` after sample-order start, supplier create (wizard), delivery-config save, test-fire success, catalog import success. Everything else self-heals via staleTime + refetch-on-focus.

**Analytics:** reuse `sample_order_started`, `wizard_*`, BE `first_*` emitters; add fire-and-forget `setup_checklist_step_clicked {step}` and `setup_checklist_completed_viewed`. Checklist state never reads analytics.

**Totals: FE ~21 code files + 2 MDX (1 component net-shrunk by ~400 lines), BE 2 changed + 2 test files, 0 endpoints, 0 migrations.**

## Ordered implementation task list (each independently shippable)

| # | Task | Files | Scope |
|---|---|---|---|
| 1 | **BE status extension + tests.** Sample-exclude 4 existing flags; add `hasCatalog/hasItemMappings/hasDeliveryConfig/hasTestFired` (null-OrderId+`"success"` convention), ids, counts. PR note: sample-only orgs intentionally flip to all-false. | `OnboardingController.cs`, `OnboardingControllerTests` | ~12 org-scoped queries + ~10 tests; half day |
| 2 | **BE sample seeding.** Idempotent catalog (5 rows incl. line-3 code) + 2/3 item mappings on `__sample__` supplier; pin codes to fixture with comment; Postgres round-trip test. | `SampleOrderService.cs`, `SampleOrderServiceTests` | half day; verify on Testcontainers Postgres |
| 3 | **FE foundation.** `OnboardingStatus` type (optional fields), mock payload, `useOnboardingStatus`, `useSampleOrder`, `upload-formats.ts`. | `types/procurement.ts`, `api-client.ts`, 3 new files | small, mechanical |
| 4 | **`?tab=` deep-link fix.** Init from `useSearchParams`, validate against `Tab` union. Unblocks every delivery/catalog CTA. | `SupplierDockProfile.tsx` | ~10 lines |
| 5 | **Checklist v2 + dashboard.** 6 steps, gating, step-5 intermediate, completion card, Pilot line, sample CTA; drop prop threading. | `OnboardingChecklist.tsx`, `BridgeDashboard.tsx` | the one nontrivial FE unit; 1 day |
| 6 | **Wizard shrink.** Direction + first supplier only; delete steps 2–4/resume; token/aria/error fixes; closing toast points at the checklist. | `OnboardingWizard.tsx` | net deletion |
| 7 | **Cliff CTAs on the money screen.** `CatalogHintCard` (per-supplier probe) mounted in SpineReview; sample-banner pre-framing; micro-helper line; `SendReadinessCard` link; `FailedPanels` primary CTA + retry demotion. | `CatalogHintCard.tsx`, `SpineReview.tsx`, `SendReadinessCard.tsx`, `FailedPanels.tsx` | careful, additive-only edits to SpineReview — no behavior changes to resolve/send |
| 8 | **Test-fire nudge.** Post-save strip + `Send a test now` + verbatim result + honesty note + status invalidation. | `DeliveryConfigEditor.tsx` | small |
| 9 | **Ambient surfaces.** Topbar chip; inbox empty-state sample CTA; palette actions + placeholder; upload formats constant + first-upload hint. | `SetupProgressChip.tsx`, `BridgeTopbar.tsx`, `InboxView.tsx`, `CommandPalette.tsx`, `UploadWorkbench.tsx` | small, parallelizable |
| 10 | **Worker-down honesty (G9).** 90s poll escalation + `/operations/health` link. | `MagicMappingPreview.tsx` | small |
| 11 | **Help slice.** 2 MDX articles + slideover route map. Deferrable without blocking 1–10. | articles, `help-articles.ts`, `HelpSlideover.tsx` | content; fast-follow OK |
| 12 | **Verification gate.** `dotnet test ProcuLink.slnx` green; `bun run build` clean; live pass under `PROCULINK_QA_BYPASS_AUTH`: (a) new org → sample run → 2/3 auto-resolve → type line-3 code → send → honest config-missing failure → `Set up delivery` CTA lands on delivery tab → test-fire → step 5 ticks → real upload → 6/6 → card graduates; (b) sample-only org → checklist persists at 0/6. | — | the acceptance test IS the persona's journey |

**Explicitly NOT built:** sandbox/simulated delivery (rejected above), `/start` page + cross-route provider/strip, tour/coachmark library, server-side dismissal persistence, `/welcome` rework (founder-config-gated), `demo-catalog.csv` (superseded by seeding; cheap fast-follow), team invites, intake-expansion nudges beyond the completion card, any user-mode flags, new endpoints, migrations. Connections-V1 alignment: when the versioned-connection editor becomes primary, steps 5–6 swap two hrefs and one copy string ("publish your connection") — designed-for, not built.