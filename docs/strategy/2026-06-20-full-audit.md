# ProcuLink — Full Product/Engineering Audit (2026-06-20)

> Method: live prod QA (83 real orders, this org) + a 9-agent parallel audit fleet with adversarial verification, building on the 2026-06-19 redesign analysis. Two agents (per-screen UX, delivery-bug hunter) were killed mid-run by upstream API rate-limiting — those areas are partially covered from my own live probes and flagged as gaps.
> Coverage caveat: the audit agents read **local `main`**. Deployed prod showed v3-workshop components live (my DOM probe), but local code still defaults `/inbox/[orderId]` to `SpineReview` (`?workshop=1` for v3). The local↔origin divergence is itself a process risk — verify which review screen is live before acting on `SpineReview`-only copy items.

---

# Executive Verdict

ProcuLink's **engine is genuinely strong and well-tested** (parsers, the OutputNode AST emitter, delivery, idempotency, tenancy — ~2,000 backend tests green). The **trust layer between the engine and the user is broken in exactly the place that matters most: the output/preview/send path** — the founder's #1 complaint, now root-caused to four concrete defects, including a **P0 silent wrong-data delivery** and a **preview panel that is a hand-drawn mockup**, not the real bytes. Verdict stands with the 2026-06-19 call: **partial redesign — converge + delete**, plus a small set of **P0 trust fixes that must ship before anything else.**

# What ProcuLink Currently Is

A B2B procurement order-conversion bridge: receive a PO in any format (CSV/XLSX/PDF/XML/cXML/UBL/X12/EDIFACT/IDoc/API/email/SFTP/S3) → normalize to a canonical order → review/map/resolve item codes → validate/fix → design the supplier-specific output → preview → deliver → reuse the supplier recipe. Core user = a procurement coordinator, **not a developer**. The repo confirms this is the product; the gap is that the UI still speaks like a developer's tool in the deep panels and the money screen.

# The Heart-Piece / Moneymaker Flow

`Import → review detected fields → map → resolve item codes → validate/fix → design output → preview == delivery → send → reuse recipe`. The money screen is the **order-review / workshop** (`/inbox/[orderId]`). It is currently **both the most important and the most broken** surface: 94% of real orders (78/83 live) are stuck in `pending_review`, the manual-code resolution has no working control, and the "what the supplier receives" preview is a mock. The engine downstream of it is fine.

# Biggest Problems Found

1. **Silent wrong-data delivery (P0).** Edit a mapping on an already-transformed order → Send re-ships the **pre-edit artifact**. Directly violates preview==delivery.
2. **The "preview == delivery" panel is fake (P0).** `OutputPreview.tsx` renders a fabricated cXML scaffold / generic 6-row list — it never calls the emitter. The user has likely never seen the real bytes in the main panel.
3. **No way to resolve a line's supplier code in the workshop (P1).** The single most common blocker is a terminal dead-end (the founder's "3 fields to fill, none work"). This is why 78/83 are stuck.
4. **You can only bind 13 fields in the output designer (P0 for the goal).** "Put any incoming field into the output" is impossible in the UI — the literal redesign ask, blocked at both UI and the backend row-bag.
5. **Inferred-but-unmapped output columns silently deliver empty while looking done (P0 data hole).**
6. **Developer jargon + dev/ops error instructions leak to coordinators** (e.g. an onboarding error tells the user to run `dotnet dev-certs https --trust`).

# Bugs Found

Adversarially verified against the real code paths. (`H#` = hunter id.)

| ID | Severity | Bug | Evidence | Repro | Fix |
|---|---|---|---|---|---|
| **MV-1** | **P0 CRITICAL** | Send re-delivers the STALE artifact after a mapping edit (preview ≠ delivery) | `useSendFlow.ts:85` short-circuit (`artifacts.length===0 && status!=="ready_to_deliver"`) → `redeliverOrder`; `OrderMappingOverrideService.UpsertAsync:64-69` never touches status/artifacts; `useMapperModel.ts:472` save only `setQueryData(["mapper-override"])`, never invalidates `["order"]` nor resets status; `OrdersController:1653` Redeliver ships newest artifact; `OrderStatusMachine:67` `RedeliverableFrom` includes `ReadyToDeliver` | Transform an order (→`ready_to_deliver`, artifact exists) → edit a mapping in the workshop → Send → the OLD artifact is delivered | On override upsert, invalidate the artifact / reset status to `ready`; OR in `confirmSend`, re-transform whenever the override is newer than the latest artifact. **Changes sending behavior → needs approval.** |
| **MV-2/3** | **P1 HIGH** | `manual-code` blockers are terminally unfixable in the v3 workshop (no inline input; "Where →" jumps nowhere) | `OrderWorkshop.tsx:84` `fixAction` only for `ai-suggestion`; `:106` `nodes:[]` → only `acceptSuggestion` wired; `useResolveActions.ts:142-172` `startLineEdit`/`commitLineCode` built but unconsumed; `OrderWorkshop:81` `ref:c.lineId` (bare GUID) vs `MapperWorkbench:196` output-path keys → `scrollIntoView` no-op | Open any `pending_review` order needing a supplier code → no way to enter it | Wire the existing manual-code machinery into the issue cards (task #123 / Phase-0 spec). |
| **IN-5** | **P1/P2** | EDIFACT EU-thousands numbers silently ~1000× under-read, no review flag | `EdifactOrderParser.cs:555-564` `ParseDecimal(value)` never receives the UNA-declared `Delimiters.Decimal` (`:479`). Empirically: `1.000` → `1.0`; `1.234,56` → null (fails to parse) | Parse an EDIFACT file whose quantities use EU grouping (`1.000`) | Pass the UNA decimal mark into `ParseDecimal`; flag ambiguous values for review. Money correctness. |
| **MV-4** | P2 | Live preview defaults to CSV for non-revision / pre-transform orders | `MapperPreviewPane.tsx:64,68` `defaultFormat ?? "csv"`; `OrdersController:880` format swap only `IsRevision` | Request json/xml preview on a non-revision order → get CSV | Drive preview format from the supplier's delivery format unconditionally. |
| **MV-5** | P2 | `ready_to_deliver` orders poll the API every 3s forever | `useOrderReview.ts:62` `refetchInterval` returns 3000 for `parsing\|transforming\|ready_to_deliver`; the last is a resting state | Open a `ready_to_deliver` order, watch network | Drop `ready_to_deliver` from the polling set. **Safe.** |
| **MV-6** | LOW | Mobile "Map on larger screen" button is a no-op | `MobileTriage.tsx:471` → `onFocusField`; desktop mapper is `hidden xl:block` (not mounted below xl) | On mobile, tap it | Replace with copy/guidance or wire a mobile resolution path. |
| **IN-1** | P2 | Preview throws for cXML/X12 OutputTree while delivery succeeds (inverted) | `OrdersController:779` Mode-0 fires for ANY `OutputTree!=null` → `OutputTemplateEmitter:68` throws for cXML/X12; transform guards via `treeIsFixedFormat`. NOT designer-reachable (designer offers json/xml/csv only) — only WS-12 envelope trees | Configure an envelope-carrying cXML connection tree, open preview | Mirror the transform's `treeIsFixedFormat` guard in the preview path. |
| **IN-2** | P2 | OutputTree with no delivery format ships JSON/CSV bytes labelled `xml` | `OrdersController:1256` default `"xml"`; `OrderTransformService:479` records `Format=effectiveFormat`; `DeliveryService:183` derives content-type/filename from `artifact.Format` (R2 bytes are correct; the DB `Format` + delivery metadata diverge) | Deliver an OutputTree order with no explicit format | Derive the recorded `Format` from the tree's actual format. |
| **IN-3** | P2 | `.txt` EDIFACT/X12 parses into a real order but captures ZERO source fields | `OrderParserFactory:75,85` routes `.txt`→X12/EDIFACT; `OrderIngestionService:894` capture gate excludes `.txt`; `SourceTokenizer:71` no `.txt` case | Upload an EDI file named `.txt` | Add `.txt` to the capture gate + tokenizer, or refuse `.txt` EDI honestly. |
| **IN-4** | P2 | XLSX source capture re-introduces locale number corruption | `SourceTokenizer.cs:127,155` `cell.GetString().Trim()` on numeric cells, vs the parser's locale-safe `GetNumericColumnValue` | Capture-tokenize a comma-decimal XLSX under a non-invariant culture | Use the locale-safe numeric reader in the tokenizer too. |

**Live-observed (my probes):**
- `GET /api/dashboard` → **404** (route doesn't exist; the dashboard is built off `/api/orders/summary`). Dead/legacy endpoint reference risk.
- **Honesty gap:** `/api/ops/health` reports `totalProblemOrders: 0` while **78 orders are stuck in `pending_review`**. "Stuck in review" isn't counted as a problem, so the health view says all-clear while the core flow is jammed.
- `/api/orders/summary` = `{pending_review:78, ready:3, delivered:2}`; `/api/exceptions` = 79 `unresolved_mapping` warnings — consistent with the manual-resolution dead-end.

# UI/UX Problems Found

> Per-screen agent re-ran successfully on 2026-06-20 (19 screens). Full detail in the "Per-Screen Appendix" at the bottom of this doc. Headline cross-cutting themes:
> 1. **4+ screens fight over "orders in trouble"** (Dashboard strip, Inbox needs-review/failed, Exceptions, Health, Delivery-log) — all funnel to `/inbox/{id}`. Pick ONE primary triage surface; make the rest thin links.
> 2. **Validation split across 3 screens, only 1 enforces** — `/library/rules` (inert `Active` toggle + trigger counts that IMPLY enforcement but don't — a trust bomb, confirmed `ValidationRules.tsx:9-15`), `/library/rule-definitions` (read-only catalog), supplier "Validation rules" tab (the only enforcer).
> 3. **Mapping split + ambiguously named** — supplier "Mappings" (SKU) vs "PO Mapping" (columns) vs standalone `/library/mappings` (duplicate). Rename to "Item codes" + "File layout".
> 4. **Two webhook surfaces, same API** — Operations→Webhooks and Settings→Connectors.
> 5. **Dashboard duplicates Inbox+Suppliers+Exceptions** — make it a thin "what needs me + headline counts".
> 6. **#114 cutover half-done** — workshop flag ON in prod but `SpineReview` (2,588 lines, two sub-views, two mobile layouts) is still the mount point and still ships → every fix lands in two diverging places. Delete it.
> 7. **Status vocabulary in ≥3 places, disagreeing** ("Ready"/"Normalized"/"Ready to send"/"Sending") while a canonical `UnifiedStatusBadge` exists unused.
> 8. **Raw machine codes leak** — Exceptions "Code" column, Health `lastResponseCode (504)`/`(host_not_allowed)`.
> 9. **Jargon in user copy** — append-only/immutable/crossing (Delivery-log), dead-letter/requeue/Worker/heartbeat (Health), HMAC-SHA256/ingress (Settings/Webhooks), canonical (Standards/Templates/Passport), Passport/Conformance, SFTP/S3 "pull".
> 10. **Decorative/always-empty controls (offer⇔works smell)** — supplier Overview 4 "—" KPI cards; Upload `Size` permanent "—"; Connectors "0 connected" + dead Add/Connect/Test-fire; Webhooks empty "Recent deliveries"; Delivery-log "Retry delivery" that only navigates; **Drafts = an entire nav-level promise with no implementation** ("Go to Inbox" CTA dead-ends).
> 11. **Mock data renders as convincing fact** under `isApiMockMode` across most screens — gating looks correct, but the rich fake numbers set an expectation the real empty states can't meet (worst: supplier Overview).
>
> **Best-in-class to preserve:** Inbox empty/loading/error; Upload's whole flow + rate-limit-vs-quota messaging; Settings error copy; Exceptions empty state; and the honest "Sent — acceptance unconfirmed (a 2xx is not business acceptance)" framing — that trust posture is the model.

(Earlier live-probe + prior-analysis notes:)
- **Money screen:** duplicate review surfaces ("Fix these to send" + the new send-strip both render); blocker chips dead; no inline fix; preview is a mock (F-4); two disconnected format controls (F-5); the OutputZone format `<select>` changes nothing.
- **Dashboard/health:** says "0 problems" while 78 orders are stuck — erodes trust.
- **Deep panels** (OrderPassport, ConformancePanel, ReplayPanel) are dense with developer jargon (see Copy).
- **Mobile:** the desktop mapper is `hidden xl:block`, so below `xl` there is no mapping/resolution at all — the mobile "Map on larger screen" button is a dead-end.
- **Cognitive load:** tabs like Passport / Conformance / Triage|Full document expose internal concepts the coordinator shouldn't need.

# Backend Problems Found

- **MV-1** (stale-artifact send) — the headline trust bug.
- **Silent fallbacks / loud-fail gaps:** the OutputTree emit-failure → revert path is only happy-path tested (M3) — the top of the 7-branch precedence chain is the most likely "silently delivered the default" regression.
- **Validation numeric culture bug (M4):** `SupplierAcceptanceService:469` parses rule values with `NumberStyles.Any, InvariantCulture` → `"73,22"` becomes `7322`; a `max:5000` rule silently passes a non-conforming value. Validation-side mirror of the known parse-side corruption.
- **SourceCapture coverage (M5):** the live async-parse capture path is only proven for CSV; XLSX/XML/cXML/X12/EDIFACT/PDF capture-write is unverified → source-tokens/mapper silently degrade to empty for those formats.
- **`.txt` EDI capture gap (IN-3); XLSX tokenizer corruption (IN-4); EDIFACT decimal (IN-5).**
- **Format-list drift:** `OrdersController:167` says "EDI (EDIFACT/X12)"; `ParseFailureExplain:21` says "EDI (EDIFACT)"; the infer message says "json, csv, xml" while the code also accepts cXML/UBL — three different "supported" lists.
- Org scoping & idempotency are well-covered (tenancy tests + `TransformIdempotency*`); no scoping holes found (but the dedicated delivery-bug agent was rate-limited — treat delivery idempotency as "not re-audited this round").

# Frontend Problems Found

- **Two output models / two editors live (F-6):** editing in the flat `OutputMappingEditor` while an `outputTree` exists saves edits that **never deliver** (tree wins by precedence) — no warning.
- **Preview is a mock (F-4)** — the single biggest FE defect.
- **Dead controls:** OutputZone format `<select>` (F-5); mobile map button (MV-6); blocker chips (MV-2/3).
- **`api-client.ts` `statusText` pattern (~20 sites):** user-facing throws leak "Bad Request"/"Forbidden" with no cause/fix; several reach the UI (e.g. webhooks page shows "Failed to add endpoint — Failed to list connections: Bad Request").
- **No cold-auth regression test (M6):** the `authHeader` Clerk-loaded race (prod P0 `48cea6e`) has zero coverage.
- **Inline-style-vs-responsive-class double-render** is a recurring class (no breakpoint test).

# Copy / Naming Problems Found

36 verified rendered-text leaks (full table in the audit appendix). The founder's bridge/dock/crossing purge is **confirmed done in copy** (survives only in internal names). Remaining leaks, highest-leverage first:

| Where | Current | →  Proposed |
|---|---|---|
| Onboarding error `OnboardingWizard.tsx:271` | "run `dotnet dev-certs https --trust`… set the Railway `Frontend:Url` env var" | "We can't reach ProcuLink right now — wait a moment and try again. If it keeps happening, contact support." |
| `OrderPassport.tsx:328/331/341` | "Order passport" / "Full provenance" / "Download acceptance proof" | "Order history" / "Full history" / "Download order record" |
| `ConformancePanel.tsx:223/186` | "Conformant / Not conformant" / "Couldn't run the conformance check" | "Matches the standard / Doesn't match" / "Couldn't run the standards check" |
| `SpineReview.tsx:2184/2185` (if live) | tabs "Passport" / "Conformance" | "Audit trail" / "Standards check" (already done in the v3 drawer — back-port) |
| `ReplayPanel.tsx` (13 items) | "Replay & impact preview", "Revision to test", "draft/published/archived", "Run replay" | "Test against past orders", "Version to test", "Draft/Tested/Live/Previous", "Run test" |
| `CrossingsLog.tsx:392` | "Append-only · immutable" | "A permanent record of every parse, edit, validation and delivery" |
| `OutputMappingEditor.tsx:532` | "Render the whole document from one **Scriban** template" | "Build the whole document from one **custom template**" |
| `app/page.tsx:129/149/430` | "canonical structure / canonical view / Canonical order" | "one consistent order / cleaned-up order / Clean order" |
| Help MDX (`connections`, `validation-rules`, `mapping-basics`) | revision/replay/test pack/conformance/snapshot/immutable/manipulators/Scriban throughout | rewrite around version / make live / test against past orders / standards check / transforms |

**Single highest-leverage move:** fix the three **shared** panels (OrderPassport, ConformancePanel, the standards-profile sentence) once — both the v3 drawer and the legacy SpineReview consume them, so the jargon leaks even after a tab is renamed.

# Output Designer Problems Found

The engine works (the saved tree IS the highest-precedence delivery mode). The blockers are UI + preview:
- **F-4 (P0):** the workshop preview panel is a static HTML mock that ignores the designed tree — it fabricates a cXML scaffold or a generic 6-field list, never calls the emitter. **This is the root of "preview won't switch format / I can't design it."** The real preview fn (`previewMappingOverride` → Mode-0 emitter) exists; the panel just doesn't call it.
- **F-1 (P0 for the goal):** only the 13 hardcoded canonical fields are bindable; the BE row-bag (`MappedTransformService.Build*Row`) carries no source token, so arbitrary incoming fields (GLN, EAN, ShipToCode…) **cannot** reach the output. Calculated fields: engine honors `Expression`, but there's **no UI input** for it.
- **F-2 (P0 data hole):** a pasted-sample column the heuristic can't map → `FixedValue=""` → renders as a **bound** violet pill → silently delivers an empty string forever, looking "done."
- **F-6 (P1):** flat editor edits are silently dead when a tree exists (precedence, no warning).
- **F-5/F-7/F-8 (P1/P2):** dead format select; conditional authoring is raw Scriban that fails open with no UI signal; arrays give no "repeats per line" legibility.

# Validation / Error Problems Found

16 verified message problems (full list in appendix). Top:
- **P0:** onboarding error leaks `dotnet`/Railway/CORS to the coordinator.
- **P1:** raw machine codes shown verbatim on Settings (`host_not_allowed`, `sftp_ingestion_requires_integration`); raw parser exception text leaked (`"'>' is an unexpected token… Line 14"`); delivery-failure panel dumps the supplier's raw HTTP body with no cause/fix.
- **P2:** infer message "supports json, csv, and xml" undercounts (code accepts cXML/UBL too) — an offer⇔works lie; `api-client.ts` `statusText` throws leak HTTP semantics; "Something went wrong" / "Unknown error" dead-ends.
- The good standard already exists (`AcceptanceMessages.ForFail`, `RuleCatalog` titles, `ParseFailureExplain.ForEmptyLines`) — the gap is the controller `ex.Message`/raw-code bodies and the FE `statusText` pattern that bypass it.

# What To Keep

OutputNode AST + emitter · paste-sample inference · date/number/currency format presets · `InvariantValidator` fail-closed · loud-fail revert · honest cXML/UBL refusal from the generic tree · catalog-guarded AI SKU suggestions · locale-safe CSV decimal parser · SourceCapture token capture · pinned `ConnectionRevisionId` · the strong backend test core (transform/delivery/idempotency/tenancy). The `AcceptanceMessages`/`RuleCatalog`/`OrderDetailsDrawer`-humanized-copy layer is the standard to extend.

# What To Remove Or Hide

The flat `OutputMappingConfig` as a persisted mode + the 13-field `EffectiveEntityResolver`/`Build*Row` wall · the 220-line preview twin (make preview call the emitter) · two of the three review screens + the Triage|Full toggle · the static `OutputPreview` mock · the dead OutputZone format select · raw "Scriban"/"revision"/"conformance"/"passport"/"replay" wording from rendered copy · the `dotnet`/Railway onboarding instruction · the dead `/api/dashboard` reference. Hide raw `includeWhen` Scriban behind a structured builder + "Advanced."

# Best Simplified User Flow

`Upload (file first → suggest supplier) → Review (one screen: received values + auto-run checks, each fixable in place) → Output (one tree-backed designer, bind any field, real live preview = the bytes) → Send (gate on real errors only) → History → reuse recipe`. One happy path; power via progressive disclosure.

# Recommended Money View Design

One screen. Left: "What we received" (real values). Center: issues to fix — **each with an inline control** (type/pick the supplier code; edit a header; pick a currency) — no jump-to-nowhere. Right: "What {supplier} receives" = the **real emitter output** for the supplier's delivery format, byte-identical to send, with validation inline. One "Edit output" affordance opening the tree designer (same artifact). Send gates only on real errors. No Passport/Conformance/Triage tabs in the user's face.

# Prioritized Fix Plan

**P0 — trust / data-loss (must fix first):**
- MV-1 stale-artifact send (re-transform/invalidate on override change). *files: useSendFlow, OrderMappingOverrideService, OrderTransformService. risk: M. test: M1+M3-style. **needs approval (sending behavior).** complexity: M.*
- F-4 real preview in the workshop (call `previewMappingOverride`, drop the mock). *files: OutputPreview, OutputZone. risk: M (most-seen panel). test: U5. complexity: M.*
- F-2 inferred-unmapped → `null` not `""` (stop silent-empty). *files: OutputNodeTemplateInferrer, OutputStructureDesigner. risk: L-M (touches emitted output for inferred templates → parity-gate). complexity: L.*

**P1 — money-view + correctness blockers:**
- MV-2/3 inline per-line resolution (task #123 spec). *complexity: M.*
- F-1 bind any source field + custom + calculated (UI + row-bag). *risk: M (byte-parity gate). complexity: H.*
- F-6 "which editor wins" guard (warn when a tree exists). *complexity: L (mitigation) / convergence (real fix).*
- IN-5 EDIFACT decimal; M4 validation comma-decimal (money). *complexity: M.*

**P2 — simplification + comfort:**
- MV-4 preview format source of truth; MV-5 polling fix; IN-1/IN-2/IN-3/IN-4 honesty/loss fixes; format-list drift unification; dashboard/health "stuck-in-review = a problem."

**P3 — copy/error polish:** the 36-item rename table + 16 error rewrites (start with the 3 shared panels + the onboarding P0 + the `api-client` statusText pattern).

**P4 — future:** output-model convergence (delete flat path), losslessness provenance edge, mobile resolution path, help-MDX rewrite.

# Tests To Add

**Must-have:** M1 dead-chip focusability (the test that would've caught #123); M2 inline manual-code commits a code; M3 OutputTree emit-failure reverts status + creates no artifact; M4 validation `73,22` ≠ 7322; M5 async SourceCapture per format (theory); M6 `authHeader` waits for Clerk.loaded.
**Useful:** U1 preview content parses as the requested format; U2 full E2E for a structured format (X12/cXML/XLSX) to a delivered artifact; U3 tokenizer degrades to empty on malformed XLSX; U4 accept-all count==applied; U5 designer tree-edit == previewed == delivered.
**Low:** L1 responsive single-render; L2 GetSourceTokens download-failure branch; L3 IDoc token golden; L4 acceptSuggestion hook test.

# Safe Fixes You Can Implement Now

(No delivery bytes, no migration, no auth/scoping, no route deletion, no sending-behavior change.)
1. **MV-5** — drop `ready_to_deliver` from the 3s poll set (`useOrderReview.ts:62`).
2. **Error wording** — the onboarding `dotnet`/Railway leak; `host_not_allowed`/`*_requires_integration` → human text; the infer message "json, csv, xml" → add cXML/UBL (fixes an offer⇔works lie); strip `statusText` from FE throws.
3. **Copy renames** — the shared-panel jargon (OrderPassport, ConformancePanel) + ReplayPanel + CrossingsLog + the marketing "canonical" + "Scriban"→"custom template".
4. **F-6 mitigation** — when an `outputTree` exists, the flat editor shows "a structure design is active; edits here won't apply."
5. **Format-list drift** — one canonical supported-formats string.

# Fixes That Need Approval

- **MV-1** (changes sending behavior).
- **F-4** (rewrites the core preview panel — technically preview-only/no bytes, but highest-visibility; treat as approval-gated for care).
- **F-1 / F-2** (touch emitted output → byte-parity-gated).
- **IN-5 / M4** (numeric semantics → money correctness, needs golden tests).
- The output-model convergence (Phase 1 of the redesign).

# Final Recommendation

Ship the **P0 trust trio first** (MV-1 stale-send, F-4 real preview, F-2 silent-empty) — these are the difference between "looks like it works" and "is trustworthy," and they're the founder's exact complaint. Then the **P1 money-view cluster** (inline resolution #123, bind-any-field F-1, EDIFACT/validation money bugs). The **safe batch** (polling, errors, copy, F-6 guard, format-list) can ship immediately on a go — pure text/config, no bytes, no migrations. Keep the engine; fix the trust layer and the money view. Do not flip more flags or add screens until MV-1 and F-4 are closed.

**Coverage honesty:** the per-screen UX audit was re-run successfully (2026-06-20, appendix below). The dedicated delivery-idempotency bug hunt is still being re-run — don't treat delivery/idempotency as cleared until it lands.

---

# Per-Screen Appendix (2026-06-20, 19 screens, read-only)

Persona: procurement coordinator, not a developer. **Nav gate:** the launch nav (`launch-flags.ts:13,26`) shows only Dashboard/Upload/Inbox/Suppliers/Connections/Exceptions/Health/Admin/Settings/Help; Mappings/Rules/Rule-definitions/Templates/Standards/Buyers/Drafts/Connectors(ops)/Delivery-log/Webhooks are URL-only until `NEXT_PUBLIC_LAUNCH_FULL_NAV=true` — fix or delete the misleading ones BEFORE flipping it.

| # | Screen | Top problems (remove / rename / fix) |
|---|---|---|
| 1 | Dashboard (`BridgeDashboard`) | worse copy of Inbox+Suppliers+Exceptions; `IN_TRANSIT_MOCK_FALLBACK` fake rows; "Export report" truncates to 100 silently; 2 lifecycle vocabularies; 3 near-synonym counts on 3 time bases. Lead with the exception strip, cut tabs/topology. |
| 2 | Inbox (`InboxView`) | TWO columns show the same lifecycle (Pipeline stepper + Status pill); `generateOrders(50)`+SEED mock; dead `assigned`="—"; session-only Columns menu; j/k nav. Wire `UnifiedStatusBadge`; default to "Needs review". |
| 3 | Drafts | **offer⇔works: describes a save-draft feature that doesn't exist** (no save control, no endpoint); always empty for real users; `DEMO_DRAFTS`→`/inbox/d1` 404; CTA "Go to Inbox" wrong; no loading/error. Remove from nav until real. |
| 4 | Upload (`UploadWorkbench`) | **best screen.** `Size` column permanent "—"; `DEMO_RECENT`; detection pill shows raw "cXML/UBL/EDIFACT/X12" acronyms; "Route" vs Suppliers' "Channel". |
| 5 | Order review (`SpineReview`+`OrderWorkshop`) | **most over-built; #114 cutover half-done** — `page.tsx:14` still mounts the 2,588-line SpineReview which forks to the workshop; 3 redundant "ready?" surfaces in ~100px; 2 progress models (5 vs 4 stages); "Received/47 fields" dumps raw `cell:r2c3`; `DocumentAnatomy` fake-PO; orphaned-but-shipped `ReceivedZone`/`OutputZone`; Passport/Conformance/canonical jargon. Delete SpineReview. |
| 6 | Suppliers (`SupplierDockList`) | subtitle leaks "versioned integration lives in Connections"; "Connection ›" column re-enters revision jargon. |
| 7 | Supplier detail (`SupplierDockProfile`) | **"Mappings" vs "PO Mapping" tabs are indistinguishable** → rename "Item codes" / "File layout"; Overview 4 KPI cards permanent "—"; Delivery summary mock-only; raw fieldPath/operator strings. |
| 8 | Mappings (`MappingEditor`) | must pick supplier first → reads as broken; `MOCK_ROWS`; **no fetch-error branch**; "Inherited" chip unexplained; duplicates the supplier Mappings tab. |
| 9 | Rules (`ValidationRules`) | **MOST MISLEADING: inert `Active` toggle + "Triggered 30d" counts imply enforcement but the service is never called by transform/delivery** (`:9-15`). Trust bomb. Kill the toggle+counts or make read-only. |
| 10 | Rule definitions | "Rule definitions" vs "Rule catalog" vs "Validation rules" — 3 synonyms; raw code/fieldPath/operator + UBL/X12 refs. Hide under Advanced. |
| 11 | Templates | right pane = raw cXML/UBL/EDIFACT envelope with `{token}`s — exactly what a coordinator avoids; card preview always renders canned illustration, never the saved body (can diverge). |
| 12 | Standards | clean reference; rename "Canonical field"→"Field"; duplicates the per-field popovers + rule-definitions + supplier bindings (same data ≥4 places). |
| 13 | Buyers | clean; `MOCK_BUYERS`; hardcoded inbound copy, doesn't use `useOrderDirection` (inconsistent); really an inbox filter. |
| 14 | Connectors (ops) | **always "0 connected" in live mode**; every supplier = identical "Available API (REST)" card; **dead Add/Connect/Test-fire** (just open a panel telling you to go to the supplier Delivery tab); `MOCK_CONNECTORS`; overlaps Settings→Connectors. |
| 15 | Exceptions | strong empty/error; but "Code" column leaks `unresolved_mapping`; "Stage" pipeline jargon (disagrees with detail "Step"); a "Resolve" button branch that never renders + 2 tooltips for it. |
| 16 | Health | confirmed "All clear" while stuck-in-review exists (no tile); **dead-letter/requeue/Worker/heartbeat/`'parsing'` jargon**; "Dead-letter" tile links to the page you're on; raw `(504)`/`(host_not_allowed)` in rows; 3 tiles → same `/inbox?status=failed`. |
| 17 | Delivery log (`CrossingsLog`) | "Append-only · immutable · crossing" jargon; **"Retry delivery" button that only navigates** (no retry API); `MOCK_LOG` convincing fake corpus. |
| 18 | Webhooks | developer screen on an ops menu; event codes/HMAC-SHA256/signing-secret; **"Recent deliveries" always empty in live**; "Edit" hidden in live (half-CRUD); duplicates Settings→Connectors (same API). |
| 19 | Settings | best error copy in the app; mixed audience — group SFTP/S3/API-keys/Connectors under "Developers & integrations"; raw `ingress` URL + `X-ProcuLink-Key`; "SFTP/S3 pull"→"folder"/"cloud storage". |

**Safe-batch candidates from this appendix** (text/hide-dead-control only, no bytes/migration/auth/route-delete): #9 kill the deceptive Active toggle+counts (or label "reference only"); #17 relabel "Retry delivery"→"Open order"; #16 fix the dead-letter self-link + de-jargon the Worker banner; #14 hide dead Add/Connect/Test-fire; jargon renames (#1,6,7,11,12,16,17,18,19); #7 rename the two ambiguous tabs. Higher-risk (defer): deleting SpineReview (#5/#114), removing routes (#3 Drafts), wiring `UnifiedStatusBadge`.
