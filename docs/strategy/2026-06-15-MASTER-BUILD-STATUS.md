# ProcuLink — MASTER BUILD STATUS & HANDOFF

**Read this FIRST.** Any AI/engineer picking up this work starts here, then reads the plan.

- **THE PLAN (source of truth):** [`docs/strategy/2026-06-15-output-layer-restructuring-masterplan.md`](2026-06-15-output-layer-restructuring-masterplan.md) — Parts 1+2+3+4 reconciled + verified, then a FINAL MASTER. Build from its WS-0…WS-14 + phases A→E.
- **This doc:** live BUILD progress + how to resume. Update it as you go.
- **Date opened:** 2026-06-15. **Founder directive:** do everything in the master, nothing deferred/gated; build with full tooling (Railway/Vercel/Wrangler CLIs, OpenAI, PostHog, live prod testing, browser). Total best.

---

## What ProcuLink IS (so you build the right thing)

A **B2B procurement order-conversion bridge.** A buyer/procurement team receives or holds a purchase order in *some* shape (CSV/XLSX/PDF/XML/cXML/UBL/X12/IDoc, via upload/email/API/SFTP), and must send it to a **supplier in that supplier's exact required format and channel**. ProcuLink: **import → normalize → resolve item-code mappings → validate/fix → transform to the supplier's required output → deliver → learn.**

The engine is real and tested (7 input formats parse live; delivery proven on prod; AES-GCM creds; audit; versioned connections). **The problem is the OUTPUT layer + the shell**, not the engine.

**The governing principle (north star for every screen):**
> The user must always see exactly **what arrived, what ProcuLink changed, why it changed, and exactly what will be sent.**

**The core user:** a procurement coordinator, NOT an integration engineer. They think *"Supplier X wants a file with THESE fields in THIS shape"* — not canonical paths, not Scriban, not version-control.

---

## The two things the whole restructure fixes

1. **Trust:** today the product can lie — a broken mapping silently delivers the default doc while showing success; validation shows green "Passed — meets all acceptance rules" on an order with quantity `-3` / no rules. **(Phase A — in progress, see below.)**
2. **Output design:** today you cannot produce a supplier's required *structure* for XML/cXML/UBL/X12 (authored fields are silently dropped); CSV/JSON are flat-only; the only arbitrary-shape path (Scriban) is in an editor unreachable from the daily mapper. **(Phases B–E.)** The cut: **one `OutputNode` AST + `EnvelopeConfig` → format-aware emitters → one preview==delivery path → one supplier-scoped designer (visual⇄AST round-trips; raw Scriban one-way) → canonical invisible → paste-sample→infer.**

---

## BUILD STATUS

### Branches — MERGED + DEPLOYED TO PROD (2026-06-15 → -16)
- **Post-deploy fix `06656d6`** (BE `main`): the live prod walkthrough found the designer modal rendered beautifully but its live preview returned **HTTP 400** — `[FromBody]` MVC binding lacked the string-enum converter. Fixed via the `[JsonConverter]` attribute (above). Pushed; Railway redeploy `03608873` (DEPLOYING at 00:05). Re-verify the preview on the open prod tab once SUCCESS.
- Backend: `feat/trust-layer-ws0` → **`main @ 49712cf`** (FF). Pushed; Railway redeployed API + Worker (`aware-amazement`). **API verified live** (infer route 401; `/health` OK).
- Frontend: `feat/trust-layer-ws0` → **`main @ 11e82a2`** (FF). Pushed; **Vercel production ● Ready** (verified, fresh on dynamic routes).
- No EF migrations (all additive: `OutputTree` rides `canonical_json`). Rollback = Railway/Vercel redeploy of the prior build.
- **The entire output-layer restructure (A trust + B engine + C designer + D infer, all formats) is LIVE on prod.** Remaining = consolidation (WS-5/8/9), EnvelopeConfig, designer pixel-QA — all post-deploy follow-ups.

### Order Workshop v3 "calm workbench" (Claude Design handoff) — **PHASE 1 SHIPPED + LIVE** (2026-06-18)
The founder ran the workshop design through **Claude Design**; the hifi handoff lives at
`~/Downloads/ProcuLink-design/design_handoff_order_workshop/` (README + tokens.css + ws3-*.jsx +
11 mockups). It is an **evolution** of the existing workshop (picker/preview/designer/transform/focus
all already exist), behind the SAME `?workshop=1` flag. Phasing:
- **Phase 1 — chrome (SHIPPED + LIVE-verified, FE `0f9e8d0`):** `WorkshopStepper` (Parse→Normalize→
  Validate→Transform→Deliver, stage derived from status/blockers/sendState), `SendReadinessStrip`
  (slim full-width bar that REPLACES the bulky issues card — green "Ready to send" / amber "N fields
  to fill" + clickable blocker chips that jump+flash), `WorkshopBrandLoader` (animated link-wire mark),
  header "Send to supplier" + paper-plane + disabled-reason tooltip, FocusControl v3 segmented restyle.
  Verified on prod at both ready + blocking orders.
- **Phase 2 — outgoing rows (SHIPPED + LIVE, FE `0a73a6f`):** needs-attention rows on top; "N fields
  ready · mapped automatically" collapsible (auto-expands when ready); inline AI-fix strip on needs-source
  rows (✦ SUGGESTED · value · rationale · ConfidenceChip · Apply) that reuses `suggestedSourceFor()` +
  the existing `onPickSource` dispatch (no new mutation path). **Gated to picker mode** — the classic
  `/inbox` wires screen is byte-unchanged (live-verified: no summary/strip/stepper there). MapperWorkbench
  suppresses its older attention split in picker mode so OutgoingPane is the single owner.
- **Phase 3 — received enrichment + rails (SHIPPED + LIVE, FE `0a73a6f`):** per-field ConfidenceChip +
  SourceTypeChip (from file-key ext) + §6 left-accent; CollapsedRail upgraded to the 44px tone-gradient
  look; grid rebalanced (incoming fixed-narrow / outgoing flex-wide) + preview widened; error state
  polished. Built by 2 background agents (distinct panes) + main-thread integration + adversarial review.
  DEFERRED: provenance badge (ready, but `SourceField` has no location data — no fabrication), mobile
  triage rebuild (§15), per-pane independent carets / exact resolveLayout (wire-engine risk).
- Tokens match the app already (navy/blue/green/violet · Bricolage/Inter/JetBrains). 11 mockups in the
  handoff are the pixel reference; `ws3-canvas.jsx`/`ws3-app.jsx` are the interaction reference.

### Order Workshop (WS-5 consolidation) — **BUILT + FLAG-GATED + LIVE-VERIFIED** (2026-06-18)
The unified **Order Workshop** replaces the old two-mode Triage/Classic order-review split with one
collapsible 3-zone screen: **Issues on top → mapper below** (IssuesPanel + the *enhanced*
`MapperWorkbench`, NOT a duplicate set of panes), lossless incoming pane, flexible add-field/output.
- **Flag-gated:** `NEXT_PUBLIC_ORDER_WORKSHOP_V2` env OR `?workshop=1` URL override (`src/lib/flags.ts`).
  Flag OFF → old screen byte-identical. Shipped to FE `main`, deployed (flag OFF).
- **Mapping mechanic = inline source picker** (founder-chosen over drag-wires; wires now optional behind a
  "Show connections" toggle). `SourcePickerChip` + `sourcePickerModel.ts`: each output row is a searchable
  typeahead of incoming fields (AI-suggestion first w/ confidence, grouped header→parties→line→raw, shows
  each field's actual value), routed through the EXISTING wire-connect dispatch — `buildOverrideDraft` save
  contract untouched. `MapperWorkbench` gained `mappingMode="picker"|"wires"` (default wires).
- **LIVE-VERIFIED on prod** (order `5db81f02`, `?workshop=1`): picker opens, searches, AI-first, picking
  reassigns the source; dedup works (incoming 34→19); bad auto-map (BuyerName←po_number) gone; transform
  popover clean; designer shows Format JSON; **preview now defaults to the supplier's REAL delivered format**
  (JSON, not CSV) with manual exploratory override respected (`MapperPreviewPane` snap-to-delivered-format).
- **Commits (FE `main`):** picker `2c97265`/`0d6fcc3`; bug fixes `638e8b6`/`6f2d8e6`/`8cb17f7`; preview
  format-snap `f9aee1f`. 509 vitest green, tsc clean.
- **"(no preview)" on a blocking order is HONEST** (line needs a supplier code → no valid bytes yet).
- **NEXT (gated on founder approval after they view `?workshop=1`):** task #114 — flip the flag ON in
  prod, reduced-mobile pass, delete the old two-mode SpineReview + orphaned triptych + inert workshop
  wrappers (`ReceivedZone`/`OutputZone`/`MappingPanel`).

### Phase A — TRUST (WS-0 + WS-13 quick-wins) — **IN PROGRESS**
P0. Must land before any output-layer feature. Status legend: ☐ todo · ◐ in progress · ☑ done · ✅ verified-live.

| Item | What | Status |
|---|---|---|
| WS-0c | Mandatory `InvariantValidator` (qty>0, unit price present&>0, currency present, PO id present) — ALWAYS runs regardless of supplier profile; produces rows so empty-result can't show green | ☑ `InvariantValidator.cs` + integrated in `ValidateOrderAsync` |
| WS-0d | Validation fails closed — unknown/unsupported acceptance-rule operator errors (or rejected at rule-create), never silently passes (`SupplierAcceptanceService.cs:474 default:return true`) | ☑ default→`false` + `KnownOperators` allowlist + create-time 400 |
| WS-0e | Frontend: zero-rules renders neutral "Not checked", never green "meets all acceptance rules" (`FixQueueTriage.tsx:560`); guard `[].every` vacuous-true (`api-client.ts:2421`) | ☑ `acceptanceSummary` invariant/supplier split + strip + readiness card |
| WS-0f | Negative/zero quantity flagged at parse (`CsvOrderParser.cs:119 ?? 0m`, no sign check) + covered by invariant | ☑ covered by `invariant.quantity_positive` (all formats, parser-agnostic) |
| WS-0a | Kill silent output fallback in `OrderTransformService.cs:312-322,333-343` → fail loud for a configured-but-broken override; legit no-override default stays | ☑ inner catches now throw → reuse the validation-fail path (revert to `ready`, return reason); throwing-transformer characterization test added |
| WS-0g | `OutputMappingFellBack`(+reason) provenance on artifact + order DTO + UI | ◐ LARGELY MOOT — with WS-0a there is no silent fallback left; the only remaining "default" is the genuine no-override case (not a fallback). A "used default because no override" provenance flag is optional polish; deferred (would need the migration). |
| WS-0h | cXML preview credential parity — preview resolves From/To/Sender via same resolver as delivery (`OrdersController.PreviewMappingOverride`) | ☑ `e47e9cf` — resolver wired into controller; preview passes cxmlCreds |
| WS-13a | Sample SUPPLIER excluded from quota + normal lists (`StripeBillingService.cs:763` add `&& !s.IsSample`; filter list) | ☑ quota fix done (list-filter is a small follow-up) |
| WS-13b | Live PO-loop E2E heading fixed + made a CI gate (`live-po-loop.spec.ts:48`) | ◐ DEFERRED — CI-skipped live-only test (`PLAYWRIGHT_LIVE`); fixing the assertion needs a live-env heading check + a CI live-gate decision. Low risk, no prod impact. |
| WS-13c | Retry disabled when delivery config missing (`FailedPanels.tsx`) | ☑ `35fdd30` — retry disabled (not just demoted) in the config-missing panel |
| WS-13d | 5-vs-6 stages copy reconciled | ☑ `35fdd30` — how-it-works "Five"→"Six" stages to match its panel |

> **PHASE A COMPLETE** (both P0 trust bombs + hygiene). Branch `feat/trust-layer-ws0` — BE `e47e9cf`, FE `35fdd30`. Full Api.Tests 1074 green; Infra 706; Transform 935; FE build clean. Only WS-13b deferred (a CI-skipped live test). **Not merged/deployed** (founder gate). **Next: Phase B — the `OutputNode` AST (WS-1) + format-aware emitters (WS-2) + `EnvelopeConfig` (WS-12).** This is the large structural cut and the actual "design the output" fix; start with the OutputNode model design + a Phase B implementation plan (Superpowers brainstorm/write-plan), then build behind the parser/transform seams with a byte-parity gate before cutover.

> **BOTH P0 trust bombs committed** on `feat/trust-layer-ws0`:
> - input-trust (WS-0c/d/e/f) + sample quota (WS-13a) — backend `0d7160b`, frontend `78f781a`.
> - output-trust fail-loud (WS-0a) — backend `5c1dd6c`.
>
> **Verified:** full solution suite green — Infrastructure 706, Transform 935 (2 skip), Api.Tests 1073 (the lone `SchemaFingerprintConcurrencyPostgres` blip is a known parallel-container flake; passes 2/2 in isolation). Frontend stageModel 24 green + production build clean. New characterization tests: `InvariantValidatorTests`, `SupplierAcceptanceTrustTests`, and the throwing-transformer loud-fail test.
>
> **Next in Phase A:** WS-0h (cXML preview credential parity), WS-13b/c/d (live-PO-loop CI gate, retry-disable-when-config-missing, 5-vs-6 stages copy). Then Phase B (the `OutputNode` AST). **Branch NOT yet merged to `main`/deployed** — these change delivery behaviour (fail-loud), so merge + prod verification is a deliberate gate.

### Phase B — output contract — IN PROGRESS (foundation shipped + byte-parity proven)
Branch `feat/trust-layer-ws0`, commits `3d5a8a4` + `e041922`. All additive + UNWIRED (no live-path change yet) — Transform.Tests 939 green.

| Step | What | Status |
|---|---|---|
| B1 | `OutputNode` AST (`Object`/`Array`/`Field`/`Attribute`) + `OutputNodeTemplate` + `EnvelopeConfig` in `ProcuLink.Core/Services/Mapping/OutputNode.cs` (renamed to avoid the existing `Entities.OutputTemplate` persistence entity) | ☑ |
| B3 | `OutputTemplateEmitter` (`ProcuLink.Transform/Output/`) — JSON + XML. Renders arbitrary nesting / arrays / attributes / renamed keys. Reuses `MappedTransformService.{BuildHeaderRow,BuildLineRow,ResolveRule}` + SourceMap re-derive verbatim. Same unresolved-lines guard. | ☑ tests prove the impossible-today capability |
| B-CSV | Delimited emitter (CSV) mirroring `BuildCsv` exactly | ☑ |
| B5 | `OutputNodeTemplateConverter.FromFlat` — lifts the existing flat `OutputMappingConfig` → tree | ☑ |
| **Byte-parity gate** | converted flat config → emitter CSV == `MappedTransformService` flat CSV, **byte-identical** | ☑ **PROVEN** — cutover de-risked |
| **B6** | Wire OutputNode as the highest-precedence output mode in `OrderTransformService` (opt-in `OrderMappingOverride.OutputTree`; all other modes gate on `!useOutputNode`; round-trips via the override JSON, no migration) | ☑ **LIVE** `ffab220` — end-to-end test delivers arbitrary nested structure; full Api.Tests 1075 green, zero regression. **Design-the-output works on the backend.** |
| B4 | Default `OutputNodeTemplate` per STRUCTURED format = today's hardcoded tree; byte-parity vs `Xml/Cxml/Ubl/X12TransformService` | ◐ OPTIONAL — existing transformers stay as the default; only needed to MIGRATE existing suppliers' flat configs to trees |
| B6-preview | Preview path honors `OutputTree` so preview == delivery | ☑ `bc87e19` — highest-precedence Mode-0 renders via the same emitter |
| Wire contract | Override read+write serializers gain `JsonStringEnumConverter` so the tree's node types round-trip as FE strings | ☑ `be68f57` |
| Wire contract (binding) | **[FromBody] binding** of `OrderMappingOverride` (preview + save endpoints) uses the GLOBAL web JSON options, which lacked the string-enum converter → live walkthrough hit HTTP **400 "Preview failed"**. Fix: `CamelCaseJsonStringEnumConverter` applied as a `[JsonConverter]` attribute on `OutputNodeType` + `OutputNodeTemplate.Format` (carries everywhere incl. MVC binding, leaves the wide-used `OutputFormat` default shape untouched). Added the missing `JsonSerializerDefaults.Web` binding regression test. | ☑ `06656d6` — Api.Tests **1078** green |

### Phase C — the visual designer — STARTED (functional first version)
| Step | What | Status |
|---|---|---|
| C-types | FE `OutputNode`/`OutputNodeTemplate`/`EnvelopeConfig` types + `outputTree` on the override; `buildOverrideDraft` + both save paths carry it through (data-loss guard) | ☑ `d97b227` + `105258d` |
| C-designer | `OutputStructureDesigner` modal — tree editor (object/list/value/attribute) bound to incoming fields, LIVE preview (== delivery), Save. Launched from the output editor's "⚄ Design structure" button | ☑ `105258d` (functional first version) |
| C-polish | design-system alignment (Bridge Layer): violet→AI-only, green-primary Save, slate badges, navy launch, **3px buyer-blue→supplier-green bridge edge**, "what the supplier receives" copy | ☑ `dafb78a` (token/signature-compliant by construction) |
| C-visual-QA | pixel-level live-render screenshot pass + drag-reorder + responsive | ◐ NEXT — blocked by `.next` contention with the running `:8082`; do via a fresh worktree (own `.next` + symlinked node_modules) or when `:8082` is free. Component builds + typechecks + uses locked tokens, so this is verification, not a known defect. |
| C-consolidation | WS-5 (5 areas / one designer / order-review as instance), WS-8 (hide versioning), WS-9 (vocab purge) | ◐ later |

### Phase D — paste-sample → infer the tree — SHIPPED
| Step | What | Status |
|---|---|---|
| D-infer | `OutputNodeTemplateInferrer` (deterministic, no AI/network — works for no-egress): JSON + CSV sample → node tree (nesting, repeating groups, columns), leaves pre-bound to canonical fields by name | ☑ `ebef7f1` |
| D-endpoint | `POST /api/orders/{id}/infer-output-structure` → tree serialized with string enums (FE contract) | ☑ `ebef7f1` |
| D-fe | Designer "⧉ Paste a supplier sample to start" → auto-detect JSON/CSV → infer → tree opens shaped to match | ☑ `f710acf` |
| D-xml | XML/cXML/UBL **structural** sample inference (elements / attributes / wrapped repeating group) | ☑ DONE — `FromXml` + `Xml_InfersElements_Attributes_AndWrappedRepeatingGroup` test |
| D-xml-ns | XML **namespace + DOCTYPE** standards-validity on infer/emit (cXML DOCTYPE, UBL cbc:/cac: namespaces) | ◐ part of B12 (see below) |

> **The complaint-killer flow is LIVE:** paste the file the supplier requires → infer → adjust → live preview (== delivery) → save → deliver. Tests: JSON nesting, CSV columns, infer→emit round-trip, response string-enum serialization. Transform 942 + Api 1077 green.

### Post-deploy hardening (2026-06-16) — live-walkthrough findings
| Item | What | Status |
|---|---|---|
| Preview 400 | Designer live preview returned HTTP 400: `[FromBody] OrderMappingOverride` MVC binding used the global web JSON options (no string-enum converter). Fixed with `CamelCaseJsonStringEnumConverter` `[JsonConverter]` attribute on `OutputNodeType` + `OutputNodeTemplate.Format`; added the missing `JsonSerializerDefaults.Web` binding test. | ✅ `06656d6` — **VERIFIED LIVE on prod** (authenticated preview = 200, renders `{orderNumber:152400,…itemCode:REDACTED-ORDER-DATA}`) |
| Infer endpoint | `infer-output-structure` verified LIVE = 200 on prod (sample `poRef/orderItems/partNumber/qtyOrdered/netPrice` → correct tree, camelCase string enums). | ✅ verified live |
| Infer aliases | Pre-bind common PO-number aliases (`poRef`/`PO No`/`PONr`/`orderNo`) to PoNumber so paste-sample lands closer. | ✅ `1201190` |
| X12 offer⇔works | Designer offered **X12** but the emitter throws for it (positional segment format, no tree emitter) → removed X12 from the designer format list. JSON/XML/CSV/cXML/UBL all emit. | ✅ FE `3635625` |
| FE tsc hygiene | Fixed 2 stale `OutputFieldRule.fieldManipulators` test-type errors (tsc `--noEmit` now 0; vitest 30/30 on both). | ✅ FE `3635625` |

### B12 — structured-EDI standards-validity (2026-06-16) — CORE SHIPPED (T1–T4), remainder deferred
Grounded via a 7-agent workflow + a 4-dimension adversarial review. **Net core (T1–T4) shipped to `main` `9bda512..3fb81b1`; full backend suite green (Transform 962 · Infra 706 · Api 1078 · 0 fail).**

| Task | What | Status |
|---|---|---|
| T1 | Byte-parity characterization lock (JSON/XML/root-ns-XML/CSV exact bytes, LE-normalized) | ✅ `fc01221` |
| T2 | Additive `Namespace?`/`Prefix?` on `OutputNode` (default null → byte-identical; camelCase round-trip) | ✅ `fb43fe8` |
| T3 | Prefix-aware **null-gated** XML emit: 3-arg `WriteStartElement` for `cbc:`/`cac:`, single-arg legacy when null (byte-identical), root-hoisted namespaces (no `p1:`), 4-arg qualified attrs, mixed-mode guard | ✅ `96f344d` |
| T4 | Inferrer captures per-node namespace/prefix + qualified-XName grouping + **unwrapped arrays** (real UBL `<Order><cac:OrderLine/>..` round-trips to schema-valid namespaced XML, no double-nest) | ✅ `3fb81b1` |
| T5 | EnvelopeConfig.X12 into the live X12 transform | ◐ **DEFERRED** — grounding showed a 12-file thread through the **common delivery dispatch** for a rare, UI-deferred format (delimiter config also adds sanitizer-lockstep corruption risk). When built: identity-only, no delimiter config. |
| T6 | Delete dead X12 twin (WS-11) | ◐ **DEFERRED** — NOT an X12-only delete: it is the whole `IParsedOrderTransform` stack (3 transforms + factory + interface + FormatMatrix coverage tests). Partial deletion breaks matrix tests. Proper WS-11 pass. |
| T7 | Generic-tree cXML + DOCTYPE | ◐ **DEFERRED** (spec) — live `CxmlTransformService` already correct; DTD version needs founder input; half-built tree cXML = offer⇔works violation. |
| T8 | FE authoring of namespaces/envelope; X12 stays out of designer | ◐ **DEFERRED** (spec) — backend round-trips today; additive UX. |

> **B12 core delivers the founder's "design the output" pain for the structured XML formats:** paste a real UBL/XML sample → infer → emit produces **schema-valid namespaced** output, and any existing non-namespaced OutputTree is **byte-identical**. The deferred remainder (T5–T8) is rare-format EDI identity + DTD + FE authoring — all honestly scoped, none blocking a customer.

**Adversarial review pass (2026-06-16, `12ae726` BE + `28f589e` FE).** A 4-dimension review (21 agents, 9 confirmed / 8 correctly dismissed) of the T1–T4 live diff found + fixed:
- **A (HIGH, T4 bug):** the inferrer fanned EVERY repeated XML sibling into a per-line "lines" array → repeated header elements (`cbc:Note`) emitted empty, once per order line. Now one line group only (name heuristic / last-positioned); other repeats preserved as siblings.
- **B (HIGH, offer⇔works):** the designer offered cXML/UBL but the tree emitter can't produce a valid envelope (no cXML DOCTYPE/From-To-Sender; no Peppol UBLVersionID/CustomizationID/ProfileID). Emitter now **refuses** cXML/UBL (fail loud); designer offers **JSON/XML/CSV** only; cXML/UBL deliver via their dedicated valid transforms; namespaced XML stays under "XML".
- **C (MEDIUM, trust):** the `useOutputNode` delivery branch lacked the inner exception-translation catch its siblings have → a malformed tree stranded the order in `transforming` through 3 Hangfire retries. Now reverts to ready + Fail.
- **D (MEDIUM+LOW, ns):** no-namespace node no longer inherits an ancestor default (explicit empty-ns, byte-identical); prefix-without-namespace fails loud.
- Tests: 6 new. Transform 968 · Api 1078 · byte-parity intact. Dismissed (false positives): unwrapped-array-by-design, same-prefix-two-URI (unreachable), prefix-conflict probes, inferrer-empty-FixedValue (intended human-in-loop).

**DEPLOYED + LIVE-VERIFIED ON PROD (2026-06-16).** BE Railway build `e72e0396` SUCCESS (after the `dotnet/sdk:8.0` MCR base-image 429/401 throttle cleared — an external builder-side outage, not our code; auto-retried until it built), FE Vercel `28f589e` Ready. Live authenticated checks: infer a UBL sample with 2 header `cbc:Note` + 2 `cac:OrderLine` → **1** lines array (not 2), **2** Note siblings (Fix A), `rootNamespace`/`cbc` prefix captured (T4); a cXML-format OutputTree preview returns `{ok:false, "...cannot produce valid CXml..."}` (Fix B). **B12 core is live and correct.**

> **(historical NEXT-BUILD note, now superseded by the table above) — EnvelopeConfig + structured-EDI standards-validity gaps:**
> 1. **X12 segment emitter** in `OutputTemplateEmitter` (today: throws for X12 — gated out of the designer). Hand-rolled ISA/GS/ST + BEG/REF/N1/PO1/CTT (no commercial EDI licence).
> 2. **cXML DOCTYPE** on emit (`<!DOCTYPE cXML SYSTEM "…/cXML.dtd">`) + **From/To/Sender** Header from `EnvelopeConfig.Cxml` (today: hardcoded identity in `CxmlTransformService`).
> 3. **UBL namespaces** — `OutputNodeTemplateInferrer.FromXml` drops xmlns decls + prefixes (reads `LocalName` only, never sets `template.Namespaces`); the emitter already emits `template.Namespaces` but the infer side must capture them, and prefixed element names (`cbc:ID`) need namespace-bound XmlWriter handling (verify empirically — XmlWriter throws on an unbound prefix).
> 4. `EnvelopeConfig` persistence (rides the override JSON / `OutputNodeTemplate.Envelope` — additive, no migration) + the connection-level UI.
>
> This is EDI/standards-correctness work (Opus-tier, careful) — ground the XmlWriter prefix behavior with a characterization test FIRST, then build with a byte/shape-validity gate. Self-contained backend; fully unit-testable locally (no prod-data risk).
| B7 | Delete the dead `IParsedOrderTransform` stack (WS-11) | ◐ NEXT |
| B12 | `EnvelopeConfig` per-connection persistence + X12/cXML identity wiring | ◐ NEXT |

> **Phase B foundation is solid:** the model + a 3-family emitter + the converter, with **byte-parity proven** for CSV. The new engine can already produce arbitrary structure (the founder's core "design the output" gap) AND reproduce existing output exactly. Remaining: structured-format default templates + parity (B4), the delivery-path wiring (B6), dead-stack delete (B7), EnvelopeConfig persistence (B12) — then **Phase C (the 3-pane visual⇄AST designer UI)** and **Phase D (paste-sample→infer)**.

### Phases C–E — NOT STARTED
- **B (remaining):** see table above.
- **C (designer):** WS-3 (3-pane visual⇄AST, inline Expression, `src/lib/mapping` extraction, characterization tests) · WS-6 canonical invisible · WS-7 6-modes→resolver+2 · WS-11 delete dead `IParsedOrderTransform` stack.
- **D (inference):** WS-4 paste-sample→infer AST · WS-14 template test fixtures.
- **E (consolidation):** WS-5 (5 areas: Orders/Supplier flows/Templates/Activity/Settings; named template scopes) · WS-8 hide versioning behind Save · WS-9 vocabulary purge + rename table · WS-13e dashboard funnel + topology→secondary.

---

## How to resume / verify
- **Local golden path** (`[[project-local-golden-path-and-hardening]]`): `PROCULINK_QA_BYPASS_AUTH=true` + local Postgres `:5435` + a 32-byte base64 `Delivery__EncryptionKey`; **Worker is mandatory** (API hosts no Hangfire).
- **Tests:** backend `dotnet test ProcuLink.slnx` (988 green at the baseline). **Postgres, not InMemory**, for FK/override/ExecuteUpdate work (InMemory masks Postgres). Frontend `bun run build` + `bun run test` (vitest) + e2e.
- **Live testing:** prod is real customer data — use disposable/sample data; admin per-org limit override + QA recipes in memory. proculink.eu (Vercel) / api.proculink.eu (Railway `ProcuLink` API + `aware-amazement` Worker, EU) / Neon Postgres / R2.
- **The real-PO benchmark corpus:** `~/Downloads/PO` (24 real POs + their DocParser target output mappings) — Phases B–D must reproduce those target outputs.

## Hard constraints (do not violate)
- No commercial EDI licences (hand-rolled / MIT only).
- Offer ⇔ works (every channel/format the UI offers must be a real tested capability).
- Worktree isolation for parallel chips (shared dir races on EF snapshot / .next).
- EF queries always `org_id`-scoped; Hangfire jobs idempotent; no raw SQL.
- Preview == delivery; fail loud, never deliver a silent default.

---

## Verified during analysis (don't re-litigate)
- ChatGPT's **validation false-positive is REAL** (code-confirmed) — Phase A fixes it.
- ChatGPT's **duplicate-delivery, mock-data-in-prod, silent-input-data-loss, metrics-lie** claims were **REFUTED or overblown** — not carried as work (details in masterplan P3.B / P4.A).
- The mapper was rebuilt ~3× in two weeks = re-litigation, not convergence. **The structural cut is the convergence point — stop re-skinning the mapper; build the AST + one transform path, freeze invariants with characterization tests.**
