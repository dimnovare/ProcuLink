# Supplier auto-detect from document content — design

**Date:** 2026-07-24
**Source:** the 2026-07-24 open-queue handover, item BE-5 (that prompt has since been
deleted — it carried live production identifiers)
**Status:** **DECIDED 2026-07-25, BACKEND IMPLEMENTED 2026-07-26.** P0 shipped separately
(BE PR #54). P1 + P2 + P3 built together on the rulings below. The frontend half — supplier
profile fields for the new identity columns, and rendering the suggestions in
`AssignSupplierBanner` — is a separate FE chip; the API contract it consumes is live.
**Every file:line below was read against the tree at commit `59e2721`.**

## Founder rulings — 2026-07-25 ("take your defaults")

The six decision points at the bottom of this document were ruled on as follows. The ruling
text is authoritative; where the body of the spec above still reads as undecided
("Phase 2, founder decision #1", "Deferred"), these rulings supersede it.

| # | Ruling | Consequence for the build |
|---|---|---|
| **D1** | **YES** — add identity columns to `Supplier`: VAT, registration number, GLN/EDI code, primary domain. | Columns + migration ship now, and they are exposed in the supplier API contract (list, detail, create, update) immediately so the FE chip has something to bind to. The FE profile fields themselves are that separate chip. Unblocks the VAT / reg-nr / EDI signal, which is therefore **in v1**, not Phase 2. |
| **D2** | **Sender DOMAIN only, 12-month retention.** | The full address keeps its existing SHA-256-only treatment — the GDPR posture at `InboundEmailRouter.cs:469-479` is unchanged. Only the domain part is persisted, as a separate column with its own capture timestamp, and the 12-month scrub is wired into the existing data-retention sweep. Phase 3 is therefore **in v1**. |
| **D3** | **Suggest-only. NEVER auto-assign in v1**, at any score. | No threshold, no auto-apply path, no configuration flag that could become one. The routing write stays exclusively in `assign-supplier`. P4 stays unscoped until the accumulated decision rows make a threshold defensible. |
| **D4** | **Shared layouts suggest ALL bound suppliers, ranked.** | The fingerprint contribution goes to every bound supplier equally, so it can never break a tie between them — which is what `SchemaFingerprint.cs:33-38` demands. Suppressing the signal entirely was the alternative and was rejected: on a shared layout the bound set is still the best evidence available, it just cannot discriminate *within* itself. |
| **D5** | **Upload keeps its required `supplierId`.** | `OrdersController` upload is untouched — no behaviour change, no FE consequence. Suggestions exist for orders that arrive without a supplier through the pull/push channels, which is where the unrouted park actually happens. |
| **D6** | Ship P0 independently — **already done**, BE PR #54, merged. | This build depends on it and does not redo it: an operator's correction is what teaches `SupplierIdsCsv` the supplier a layout belongs to, which is the input the strongest signal reads. |

## Problem

An inbound document has to be bound to a supplier before it can resolve item codes,
transform, or deliver. Today that binding is always supplied from outside the document:

- **Upload** — `supplierId` is mandatory; blank returns 400
  (`ProcuLink.Api/Controllers/OrdersController.cs:169-170`). The operator must name the
  supplier before they have seen what arrived.
- **Pull channels** — email/SFTP/S3 use a configured default supplier
  (`ProcuLink.Worker/Jobs/EmailPollOrgJob.cs:126`,
  `ProcuLink.Infrastructure/Services/Ingress/SftpIngressService.cs:77`,
  `.../S3IngressService.cs:84`); with no default they create a supplier-less stub
  (`CreateUnroutedStubAsync`, `ProcuLink.Api/Services/OrderService.cs:86`).
- A supplier-less order is parked `unrouted` after extraction
  (`ProcuLink.Api/Services/Orders/OrderIngestionService.cs:856-857`;
  `OrderStatusConstants.Unrouted`, `ProcuLink.Core/Constants/OrderStatusConstants.cs:74`)
  and is resolvable only by `POST /orders/{id}/assign-supplier`
  (`OrdersController.cs:583`), from a flat list, with no hint.

The document itself carries evidence of who sent it. Nothing reads that evidence.

**Goal:** rank the org's suppliers against the parsed content, show the top candidates with
a reason, and let the operator confirm. **Suggest, never auto-assign** — the same contract
AI mapping already ships under.

## Ground truth — which signals actually have a backing today

| Signal (as briefed) | State | Evidence |
|---|---|---|
| PO header parties | **EXISTS** | `ParsedOrder.SupplierName` (`ProcuLink.Transform/Parsing/ParsedOrder.cs:14`), `BuyerTaxId` (`:17`), `Parties` (`:28`) → `ParsedParty(Role, Name, …, Vat, RegNr, EdiCode, Email, …)` (`ParsedParty.cs:9-22`). Persisted as `order_parties` rows (`ProcuLink.Infrastructure/ProcuLinkDbContext.cs:569-587`) plus typed columns (`OrderIngestionService.cs:863`, written at `:937`). |
| Supplier **name** match | **HALF** | Left side exists (above). Right side is thin: the only identifying fields on `Supplier` are `Name` and a sample-path-only `Code` (`ProcuLink.Core/Entities/Supplier.cs:7`, `:14`). Exact-normalised compare is possible; no alias table. |
| Supplier **VAT** match | **BLOCKED** | `Supplier` has **no VAT, no reg-nr, no EDI code, no email, no domain** (`Supplier.cs:3-29`). The document side has `ParsedParty.Vat`/`RegNr`/`EdiCode`, but there is nothing to compare them to. Needs new columns + migration. |
| Catalog code overlap | **POSSIBLE, new query** | `SupplierProduct.Code` is unique per `(OrgId, SupplierId, Code)` (`ProcuLinkDbContext.cs:660`). Every existing read is `(org, supplier)`-scoped by contract — `ICatalogRetrievalService` ("All queries are strictly scoped to (orgId, supplierId)", `ProcuLink.Core/Services/ICatalogRetrievalService.cs:21`) and `OrderServiceShared.BuildCatalogLookupAsync` (`ProcuLink.Api/Services/Orders/OrderServiceShared.cs:95`). "Which suppliers sell these codes?" is a **new** access pattern. |
| Sender address / domain history | **NOT PERSISTED** | `InboundEmailPayload.FromEmail` exists (`ProcuLink.Core/Services/Email/IInboundEmailRouter.cs:54`) but is written only as a one-way SHA-256 into the audit payload (`ProcuLink.Infrastructure/Services/Email/InboundEmailRouter.cs:477-479`). That is a deliberate GDPR choice, documented at `InboundEmailRouter.cs:469-476`. No sender column on the order. |
| **Layout fingerprint → supplier** (not briefed) | **ALREADY EXISTS** | `SchemaFingerprint.SupplierIdsCsv` — "the binding the future auto-apply needs: it answers *whose recipe?*" (`ProcuLink.Core/Entities/SchemaFingerprint.cs:32-40`); surfaced as `SchemaFingerprintMatch.SupplierIds` + `IsSharedLayout` (`ProcuLink.Core/Services/Detection/ISchemaFingerprintService.cs:10-23`), queried by `LookupAsync` (`:56`). |

The strongest signal is the one that was not on the list. It is live, org-scoped, and
already collision-aware.

Two limits on it, both real:

1. It only covers **header-bearing formats**. Null/empty headers → the recorder returns
   without writing (`ProcuLink.Infrastructure/Services/Detection/SchemaFingerprintService.cs:53-59`,
   comment: "Header-less format (XML / EDIFACT / PDF) — nothing to fingerprint in v1").
   A PDF arriving by email — the common unrouted case — gets nothing from it.
2. It is not learning. See below.

## Blocking prerequisite — the fingerprint never learns from a correction

Found while verifying, not previously tracked. Chain:

1. An unrouted order parses with `SupplierId == null`. `RecordParseSuccessAsync` runs from
   `ParseOrderJob.cs:143`, reaches `BindSupplier(fpRow, order.SupplierId)`
   (`SchemaFingerprintService.cs:100`), which returns `false` on a null supplier
   (`:164-166`) — correct, there is nothing to bind. It then persists the per-order guard
   hash: `order.SchemaFingerprintHash = hash` (`:103`).
2. The operator assigns a supplier. `assign-supplier` sets `SupplierId`,
   `ConnectionRevisionId`, `Status`, `UpdatedAt` (`OrdersController.cs:609-615`) and
   re-enqueues the parse (`:627`). It does **not** clear `SchemaFingerprintHash`.
3. The re-parse calls `RecordParseSuccessAsync` again, which short-circuits on
   `if (order.SchemaFingerprintHash is not null) return;` (`SchemaFingerprintService.cs:47-51`).

**The supplier is never bound to the layout.** Every human correction — precisely the
training signal an auto-detect feature would learn from — is discarded. `SupplierIdsCsv`
can only ever accumulate suppliers that were already known at ingest, which are the orders
that never needed detecting.

**Fix (preferred):** make the guard supplier-aware rather than clearing the hash — on
re-entry with a non-null `SupplierId` that is absent from the row's set, bind it and return
without touching `ParseSuccessCount`. Clearing the hash in `assign-supplier` instead would
also re-arm the **count** increment and double-count the layout, so it is the worse option.

Size **S**. Ship it independently of the rest of this spec: it is what makes the
layout→supplier moat actually accumulate, and it is the input the scorer depends on.

## Design

### Hook point

`ParseStoredFileAsync` (`OrderIngestionService.cs:599`), immediately **before** the status
decision at `:850-857` and **before** the atomic persist block that starts at `:905`.

At that point everything the scorer needs is already in scope and nothing is written yet:

- `parsedOrder` — header + `Parties` (assigned on each format branch, `:685-738`)
- `lineEntities` — the parsed line codes (`:800`)
- `detected` — format + `ColumnHeaders`, captured while the buffer is in memory (`:639-649`)
- `entity.SupplierId` — the null test that already gates the unrouted park (`:856`)

Run the scorer **only when `entity.SupplierId is null`**. Routed orders pay nothing.

**The status decision does not change.** The order still lands `unrouted` at `:857`. A
suggestion is a sibling row, never a status, and never a supplier write.

Failure is non-fatal — a scorer exception must not fail a successful parse. Precedent: the
fingerprint call is wrapped exactly this way (`ParseOrderJob.cs:141-153`).

### New persistence

One new table, `order_supplier_suggestions`: `Id, OrgId, OrderId, SupplierId, Rank, Score,
SignalsJson, ModelVersion, Decision, DecidedBy, DecidedAt, CreatedAt`. Rows are additive
evidence; a re-parse supersedes rather than deletes.

`Decision` reuses the vocabulary already in the codebase —
`accepted` / `rejected` / `superseded` / `manual` (`ProcuLink.Core/Entities/AiSuggestionDecision.cs`,
kinds at `:71-84`) — and takes the same style of unique idempotency index.

**Rejected alternative:** storing these in `AiSuggestionDecision` itself. That entity is
line-scoped (`LineNumber`, `SuggestedSupplierItemCode`); overloading it to mean
order-scoped supplier routing is exactly the `CanonicalJson`-overloading mistake CLAUDE.md
forbids. New concept → new first-class table.

### Scoring — deterministic, no LLM in v1

| Signal | Rule | Notes |
|---|---|---|
| Layout fingerprint | `LookupAsync(orgId, detected.ColumnHeaders)` → `SupplierIds`. Sole bound supplier = the strongest single contribution. | When `IsSharedLayout` is true the contribution goes to **every** bound supplier equally and can never break a tie — the entity comment already mandates this ("a layout COLLISION … auto-apply must NOT silently pick one", `SchemaFingerprint.cs:33-38`). |
| Catalog code overlap | Fraction of the order's line codes that are real `SupplierProduct.Code` values for supplier S. | Needs the new cross-supplier query **and** a supporting index — see below. |
| Supplier name | Normalised (trim/case/punctuation/legal-suffix) equality of `parsedOrder.SupplierName` or the `"supplier"`-role party name against `Supplier.Name`. | Exact-normalised only in v1. No fuzzy: trigram ranking exists for **products** only (`ICatalogRetrievalService.cs:17-19`); there is no supplier-side fuzzy matcher anywhere in the solution. |
| VAT / reg-nr / EDI code | Deferred — no right-hand side (`Supplier.cs:3-29`). | Phase 2, founder decision #1. |
| Sender domain | Deferred — not persisted (`InboundEmailRouter.cs:469-479`). | Phase 3, founder decision #2. |

**Index note (effort-relevant):** the only code index is
`(OrgId, SupplierId, Code)` (`ProcuLinkDbContext.cs:660`). A cross-supplier probe filters on
`OrgId` + `Code` with `SupplierId` unconstrained in the middle, so Postgres scans the org's
whole index slice and filters. On a 250k-row org (see BE-2's cap raise) that is the wrong
shape. Add `(OrgId, Code)`, and cap the probe to the first N distinct line codes.

**Output shape** mirrors `AiMappingSuggestion` — `Confidence` (0–1), `Reason` (one human
sentence), `Provenance` (which signals fired) —
(`ProcuLink.Core/Services/Ai/IAiMappingService.cs:87`). Confidence never reaches 1.0;
the existing precedent for that ceiling is `FingerprintBoost.ConfidenceCeiling = 0.99`,
"a heuristic should never claim certainty" (`ISchemaFingerprintService.cs:73-74`).

This does not change the standing rule that the extracted `supplier_name` column is display
only and **not** routing (`ProcuLink.Api/Services/Orders/OrderResolutionService.cs:159`).
The suggestion is a ranked hint next to the order; the routing write stays exclusively in
`assign-supplier`.

### Operator flow

1. Order DTO for an `unrouted` order carries up to 3 ranked suggestions, each with score,
   reason, and provenance.
2. The operator confirms through the **existing** `POST /orders/{id}/assign-supplier`
   (`OrdersController.cs:583`) — no second accept endpoint. Add an optional
   `suggestionId` to the request so acceptance is attributable; absent = manual pick.
3. The endpoint records the decision (`accepted` for the chosen row, `rejected` for the
   other shown rows, `manual` when the operator picked an unsuggested supplier) and writes
   an audit event. Names follow the PascalCase order-event convention used by
   `OrderServiceShared.BuildAuditEvent` (`OrderServiceShared.cs:74-84`) — e.g.
   `SupplierSuggested`, `SupplierSuggestionAccepted`.
4. **No auto-assign in v1, at any score.** The accumulated decision rows are what make a
   future threshold defensible; without them a threshold is a guess.

### Cost and safety

- Deterministic v1 spends nothing on AI, so no `IAiUsageTracker` gate is required. If a
  later phase adds an LLM tiebreak, gate it with
  `IAiUsageTracker.IsAtOrOverLimitAsync` and soft-fail to no-suggestion, the way
  `OpenAiMappingService` does — never hard-block.
- Every query org-scoped, no exceptions.
- Suggestions are advisory data; nothing in the delivery path reads them.

## Effort

| Phase | Work | Size |
|---|---|---|
| P0 | Fingerprint learns from corrections (supplier-aware guard) + Postgres test | **S** |
| P1 | Suggestion service, table + migration, fingerprint + catalog-overlap + name signals, `(OrgId, Code)` index, DTO, decision recording on `assign-supplier` | **M** |
| P2 | `Supplier` identity columns (VAT / reg-nr / EDI code / domain) + migration + FE fields + VAT signal | **M**, gated on decision #1 |
| P3 | Sender-domain provenance + history signal | **S** code, gated on decision #2 |
| P4 | Auto-assign above a threshold | not scoped — needs P1 decision data first |

P0 + P1 is one focused chip. P2 onward are separate and each need a founder answer first.

## Testing — TDD, RED first

| Item | Test | Project |
|---|---|---|
| P0 | Unrouted order parses → assign supplier → re-parse → assert `SupplierIdsCsv` now contains the supplier **and** `ParseSuccessCount` did not double-count. RED today (empty set). | `ProcuLink.Api.Tests/Integration` (real Postgres) |
| P1 | Scorer unit tests per signal, plus: shared layout (2 bound suppliers) never breaks a tie; routed order (`SupplierId` non-null) produces zero suggestions and zero queries. | `ProcuLink.Api.Tests` |
| P1 | Cross-supplier catalog-overlap query on **real Postgres** — the index path is provider-sensitive and InMemory would pass against a broken query (same hazard `ICatalogRetrievalService.cs:23-26` documents). | `ProcuLink.Api.Tests/Integration` |
| P1 | Scorer throws → parse still succeeds and the order still lands `unrouted`. | `ProcuLink.Api.Tests` |
| P1 | `assign-supplier` with `suggestionId` writes `accepted` + audit; without it writes `manual`; unshown suppliers write nothing. | `ProcuLink.Api.Tests` |

## Founder decision points — ALL RULED 2026-07-25

The questions as originally posed are kept below for the record. Every one of them is answered in
the rulings table at the top of this document; where the two disagree, the rulings win.

1. **Add identity columns to `Supplier`** (VAT, reg-nr, EDI/GLN code, primary domain)?
   Without them the VAT-match signal has no right-hand side and cannot ship. Also implies
   FE fields on the supplier profile.
2. **Persist inbound sender provenance?** Today the address is hashed on purpose for GDPR
   (`InboundEmailRouter.cs:469-476`). Storing the **domain only** is a smaller footprint
   than the full address — but it is still a change to a deliberate privacy posture, and it
   needs a retention answer.
3. **Will auto-assign ever be allowed?** v1 says never. If yes: which score, and may any
   single signal stand alone (fingerprint-only auto-assign is the tempting one, and is
   exactly what the collision comment warns against).
4. **Shared layouts** — suggest all bound suppliers, or suppress the signal entirely when
   the layout is shared?
5. **Does the upload path get suggestions too?** It currently requires `supplierId`
   (`OrdersController.cs:169-170`). Making it optional so an uploaded file can land
   unrouted-and-suggested is a real behaviour change with FE consequences.
6. **Ship P0 now, independently?** It is small, it is a live defect in the fingerprint
   moat, and it costs nothing to fix ahead of the rest.

## Out of scope

- Any product code — this item is spec-only by directive.
- Cross-org fingerprint/supplier catalog: explicitly out of scope by the entity's own
  contract (`SchemaFingerprint.cs:9-10`).
- Invoice routing (`ParseStoredFileAsync` forces invoices to review, `:842-850`).
- The assign-supplier UI itself — that is FE-1.

## Coordination

- **FE-1** builds the assign-supplier resolver UI. Suggestions must arrive in the DTO that
  screen consumes; either agree the DTO shape up front or let FE-1 ship first and add the
  suggestion block behind it.
- **BE-1** (park inbound email unrouted instead of rejecting) increases unrouted volume,
  which raises this feature's value — and its P0 dependency, since those orders are
  header-less PDFs as often as not.
- **BE-2** (catalog row cap raise) makes the `(OrgId, Code)` index non-optional rather than
  nice-to-have.
