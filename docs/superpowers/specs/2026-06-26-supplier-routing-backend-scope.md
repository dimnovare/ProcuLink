# Supplier Routing — Backend Build Scope

*Scoped 2026-06-26. Grounded in the live code (4 parallel investigations). This is the engineering scope for the routing track that the redesign brief carves out. Routing UI = `docs/design-system/2026-06-26-routing-triage-design-brief.md`.*

---

## Goal

When orders arrive on a **shared channel** (one SFTP folder / S3 prefix / inbox / API key) carrying **many suppliers' POs**, ProcuLink must decide **which supplier each order is for**, hold the unsure ones for a human instead of silently dropping them, and **learn** the decision so the next identical order self-routes.

## Non-goals (explicit)

- No change to the **existing happy path**: upload-with-supplier and channel-bound-to-one-supplier stay **byte-for-byte identical**. Routing is **opt-in per channel**.
- Never auto-send a guessed route. Auto-assignment only on a single high-confidence, non-colliding match; everything else holds for a human. The existing Send gate (NeedsReview, preview==delivery, HTTP 200≠acceptance) is untouched.

---

## The core architectural decision: reorder the pipeline by *re-entry*, not a parallel path

**The problem.** Today the supplier is fixed **before** parse (`CreateStubAsync` pins it; `ParseStoredFileAsync` resolves lines against it). Content routing needs the **parsed content** (supplier VAT/EDI/name, layout) to pick the supplier — i.e. routing must happen *after* extraction but *before* line resolution.

**What needs the supplier vs what doesn't** (verified):

| Needs supplierId | Does NOT need supplierId |
|---|---|
| Item-mapping lookup `(orgId, supplierId)` | File download + format detection |
| AI suggestions + catalog grounding | Parse → `ParsedOrder` (header, **parties**, lines) |
| Connection-revision pinning (`SupplierConnections(orgId,supplierId)`) | Source tokenization + `SourceCapture` |
| PO mapping-template retrieval | `OrderParty` persistence (ship-to/bill-to/**supplier identity**) |
| `BuildLineEntitiesAsync` (resolves codes) | Schema-fingerprint *recording* of the layout |

So the seam sits **after extraction, before `BuildLineEntitiesAsync`** — inside `ParseStoredFileAsync` (`OrderIngestionService.cs`).

**The decision — reuse the whole resolve tail via re-entry.** Do **not** write a second "resolve after assignment" method (duplication = drift). Instead:

1. `ParseStoredFileAsync` learns to run with `SupplierId == null`: it extracts (parse, persist parties + source capture + lines carried **unresolved**), records the layout fingerprint **without** a supplier bind, then **branches**:
   - run the **router** over the parsed content;
   - **single high-confidence, non-colliding** match → set `SupplierId`, pin the revision, and **fall through to the existing resolve tail** (zero new resolve code);
   - otherwise → set status `unrouted`, persist the candidate evidence, and **return** before the resolve tail.
2. `POST /api/orders/{id}/assign-supplier` sets `SupplierId` (atomic claim `unrouted → parsing`) and **re-enqueues `ParseOrderJob`**. The job re-runs `ParseStoredFileAsync`, which now sees a supplier and resolves **through the identical, already-tested path**.

Parse is already idempotent and re-runnable; re-download + re-extract on assignment costs a few seconds and buys us **one** resolve path instead of two. This is the lowest-risk shape.

---

## Data-model changes

1. **`PurchaseOrderEntity.SupplierId` → nullable (`Guid?`).** Today it's a non-nullable `Guid` with a cascade FK (`ProcuLinkDbContext.cs:508`). Migration: make column nullable, FK `OnDelete: SetNull`. **This is the highest-blast-radius change** — see Risks.

2. **New status `unrouted`** in `OrderStatusConstants` (held-after-extract-awaiting-supplier). Not in `FailureBucket` (it's a backlog, not a fault).

3. **New `SupplierIdentifier` table** — supplier-side match keys (the Supplier entity has **none** today; only Id/OrgId/Name/Code-sample-marker). Columns: `Id, OrgId, SupplierId, Type (vat|edi|gln|duns|alias), Value (normalized), CreatedAt`. Multi-value per supplier; unique `(OrgId, Type, Value)`. Preferred over columns-on-Supplier (a supplier can have several VAT/EDI ids + name aliases). Do **not** repurpose `CxmlConfigJson` (delivery-format-specific, documented cleartext-credential invariant).

4. **New `OrderRoutingCandidate`** (or a `RoutingEvidenceJson` column on the order) — `orderId, supplierId, score, signals[]` so the UI can show the *why* ("matched on VAT REDACTED-TAXID + layout seen 7×").

5. **New `RoutingDecision`** audit row (mirror `AiSuggestionDecision`) — who/what chose, accepted candidate, for the learn-loop + calibration. (Phase 4.)

6. **Channel configs gain `RouteByContent bool` (default false).** On `SftpIngressConfig`, `S3IngressConfig`, `EmailConfig`. When false → today's behavior, byte-identical. When true → `DefaultSupplierId` optional; orders land supplier-less → routing.

7. **Exception + ops surfacing:** add code `unrouted_order` (stage `Route`, severity `warning`) to `OrderExceptionService.ProblemFor()`; add `PendingRouting` count to `OpsHealthService` (indexed `(OrgId, Status)`). Reconcile auto-clears when supplier assigned.

---

## The matcher — `ISupplierRoutingService`

`Task<RoutingResult> RouteAsync(orgId, ParsedOrder parsed, IngestChannel channel, ct)` → ranked candidates + a tier. **Every query org-scoped.**

**Signals (deterministic first):**
1. **Channel binding** — channel bound to one supplier & `RouteByContent` off → that supplier, conf 1.0 (today's path).
2. **Exact identity** — `SupplierIdentifier(vat|edi|gln)` == `OrderParty(role=supplier).Vat/EdiCode/RegNr`. Unique hit → ~0.97.
3. **Exact normalized name** — `OrderParty(supplier).Name` == `Supplier.Name` / alias → high-medium.
4. **Fingerprint** (tabular CSV/XLSX only — PDF/XML/EDI have no headers) — `SchemaFingerprintService.LookupAsync` → bound `SupplierIds` + `SeenCount`; single bound supplier, `SeenCount ≥ MIN_SEEN` → medium-high; `IsSharedLayout` → **collision**.
5. **Fuzzy name / alias** — medium.

**Combine & tier:** signals agreeing on **one** supplier boost each other; signals pointing at **different** suppliers ⇒ **collision → never auto**.
- `≥ 0.90` single, no collision → **Matched** → auto-assign + fall through to resolve (lines still obey existing NeedsReview/Send gate).
- `0.60–0.90` → **Needs confirmation** → `unrouted`, candidate pre-selected.
- `< 0.60` or collision → **Unrouted** → `unrouted`, full candidate list.

Thresholds/`MIN_SEEN` configurable; reuse the fingerprint auto-apply gate values (≥0.75 / ≥5 sightings / collision-blocks) already specced in `docs/superpowers/plans/2026-06-16-schema-fingerprint-autoapply.md`.

---

## Ingest changes — convert 5 "reject/skip" points to "hold"

When the channel has `RouteByContent` (or simply no resolvable default supplier *and* opt-in), create an `unrouted` order instead of dropping:

| Point | File:line today | Change |
|---|---|---|
| Inbound email 422 | `InboundEmailRouter.cs:149-157` | create `unrouted` stub, ingest attachment |
| API ingress 400 | `IngressController.cs:115-137` | make `SupplierId` optional → `unrouted` when absent |
| SFTP skip | `SftpIngressService.cs:76-82` | import as `unrouted` |
| S3 skip | `S3IngressService.cs:82-88` | import as `unrouted` |
| IMAP skip | `EmailPollOrgJob.cs:99-105` | import as `unrouted` |

All gated on the per-channel opt-in so existing single-supplier channels are unchanged.

---

## API surface

- `POST /api/orders/{id}/assign-supplier` `{ supplierId, rememberRule?: bool }` — atomic claim `unrouted→parsing`, set supplier, re-enqueue parse. `rememberRule` → Phase 4 learn-loop.
- `GET /api/orders/{id}/routing` — candidates + evidence for the decision card.
- `GET /api/orders?status=unrouted` — the triage queue (existing list endpoint, new filter).
- `SupplierIdentifier` CRUD under `/api/suppliers/{id}/identifiers` (Phase 2).
- `RouteByContent` + optional default on the existing channel-config endpoints.

---

## Phasing (each phase independently shippable)

**Phase 0 — Foundations (no behavior change).** Nullable `SupplierId` + EF/migration + FK `SetNull`; audit **every** `.SupplierId` reader for null-safety; add `Unrouted` status, `unrouted_order` exception, `PendingRouting` count (wired, not yet emitted). *Riskiest phase — see Risks.* ~1–2 days.

**Phase 1 — Hold + manual assign (no matcher).** 5 reject→hold conversions; `ParseStoredFileAsync` supplier-null branch (extract→hold); `assign-supplier` endpoint + re-enqueue; queue filter + exception/ops now reachable. **Kills the silent-drop problem and gives manual routing today.** ~2–3 days.

**Phase 2 — Deterministic identity matcher.** `SupplierIdentifier` table + CRUD; `ISupplierRoutingService` with channel + exact-identity + exact-name; single high-conf → auto-assign; persist evidence. **Common case (VAT/EDI on the doc) auto-routes.** ~3–4 days.

**Phase 3 — Fingerprint + fuzzy + collision.** Add fingerprint signal (tabular) + fuzzy/alias; cross-signal collision → never auto; ranked evidence. ~3 days.

**Phase 4 — Learn-loop + calibration.** `rememberRule` → upsert `SupplierIdentifier` and/or bind fingerprint; `RoutingDecision` audit + acceptance-rate calibration (mirror `AiSuggestionDecision`). **The queue shrinks itself.** ~2–3 days.

Phase 0+1 (~1 week) alone removes silent data loss and enables manual triage. Full track ~2–3 weeks.

---

## Risks & safety

- **★ Nullable `SupplierId` blast radius (P0).** Many services assume non-null. Mitigation: grep **every** consumer; the vast majority run downstream of `ready` where supplier is guaranteed set — guard only the pre-resolve readers. Add Postgres round-trip + "unrouted order persists with null supplier" tests. Do Phase 0 in isolation, full suite green, before anything emits the null.
- **Byte-parity of the existing path.** `RouteByContent` defaults off; `DefaultSupplierId` and upload-with-supplier paths unchanged; existing orders untouched. Lock with a characterization test.
- **Never auto-send a guess.** Auto-assign only `≥` high threshold + single + no collision; all else holds. Auto-assigned orders still pass the normal NeedsReview/Send gate. Routing cannot bypass delivery trust.
- **Org-scope** every matcher/identifier query (tenancy invariant).
- **Concurrency.** `assign-supplier` must atomically claim (`ExecuteUpdate` `unrouted→parsing`) to avoid double-resolve on a double-click/retry (reuse the delivery atomic-claim pattern). Re-enqueue idempotent.
- **Fingerprint is tabular-only.** PDF/cXML/EDI route on identity/name signals, not layout — don't imply layout-routing for them.

---

## Test plan

Unit: matcher scoring + tiering + collision; identifier normalization. Postgres (Testcontainers): nullable-supplier round-trip, `SupplierIdentifier` uniqueness, unrouted persistence. Pipeline: extract→hold→assign→resolve end-to-end; re-entry idempotency. Ingest: each of the 5 channels holds (opt-in) vs rejects (opt-out). Byte-parity: existing single-supplier path unchanged. e2e on a prod-like org with a multi-supplier shared folder.

---

## Files that change (anchor list)

`PurchaseOrderEntity.cs` (nullable) · `ProcuLinkDbContext.cs:508` (FK) · `OrderStatusConstants.cs` · `OrderIngestionService.cs` (`ParseStoredFileAsync` supplier-null branch; assign path) · `OrdersController.cs` (assign + routing endpoints) · `InboundEmailRouter.cs:149` · `IngressController.cs:115` · `SftpIngressService.cs:76` · `S3IngressService.cs:82` · `EmailPollOrgJob.cs:99` · `OrderExceptionService.cs` (`ProblemFor`) · `OpsHealthService.cs` · new `SupplierIdentifier` + `OrderRoutingCandidate` (+`RoutingDecision` P4) entities + `ISupplierRoutingService`/impl + migrations.
