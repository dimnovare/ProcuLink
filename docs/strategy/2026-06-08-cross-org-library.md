# Cross-Org Mapping Library — design (FROZEN / future, design-only)

**Date:** 2026-06-08
**Status:** 🧊 **FROZEN — design only. DO NOT IMPLEMENT.**
CLAUDE.md lists "cross-org mapping library" under *FREEZE until there are paying
customers*, and the four-lens analysis is explicit that this network-effect moat is
*"worthless until there is order volume to learn from — no data, no moat"*
(`docs/strategy/2026-05-30-four-lens-product-analysis.md:268-270`). `SchemaFingerprint`
already reserves this as **"Horizon 3 / Group Q, explicitly out of scope"**
(`ProcuLink.Core/Entities/SchemaFingerprint.cs:10-11`,
`ProcuLink.Core/Services/Detection/ISchemaFingerprintService.cs:18-19`).
This document is the *blueprint to pick up later* — no code, no migration, no config.
**Track:** cross-org mapping library (DESIGN ONLY).

---

## 0. TL;DR

Today every mapping artifact ProcuLink holds is **strictly single-tenant**: a row
carries `OrgId` and every query filters by it. The only thing shared across all
tenants is the **static, hand-authored, read-only** `StarterTemplate` set (Erply,
Directo, generic CSV, …) shipped as embedded JSON fixtures with no DB and no org
scope (`ProcuLink.Api/Services/StarterTemplates/StarterTemplateService.cs`,
`PoMappingTemplatesController.cs:8-9` "*No org scoping — templates are global,
not per-tenant*").

The cross-org library generalises that one static idea into a **curated, global,
read-only catalog of community mapping templates** that any tenant can discover and
apply — without ever exposing one tenant's data to another. The hard part is **not**
the data model (it is a near-clone of `StarterTemplateDto`); the hard part is
**privacy/PII, contribution → anonymization → curation, and the tenancy guardrails**
that keep a shared global table from becoming a cross-tenant leak.

**The one non-negotiable invariant:** a shared-catalog template contains **only a
column-name → canonical-field mapping plus literal manipulator recipes**. It must
**never** carry order data, item codes, supplier item codes, prices, buyer/supplier
identities, credentials, or anything derived from a specific order. A template is
*structure*, not *content*.

---

## 1. What exists today (grounding)

### 1.1 The single-org mapping building blocks

| Concern | Type | File:line | Tenancy |
|---|---|---|---|
| Mapping config shape | `PoMappingConfig` / `FieldMappingEntry` / `ManipulatorEntry` | `ProcuLink.Core/Services/Mapping/PoMappingConfig.cs:3-30` | none (pure value record) |
| Per-supplier stored mapping | `SupplierPoMapping` (jsonb `ConfigJson`) | `ProcuLink.Core/Entities/SupplierPoMapping.cs:3-16` | `OrgId` + `SupplierId` |
| Stored-mapping CRUD | `PoMappingService` | `ProcuLink.Infrastructure/Services/PoMappingService.cs:23-77` | every query `Where(OrgId == … && SupplierId == …)` |
| Apply mapping at parse time | `PoMappingEngine.Apply` (static, pure) | `ProcuLink.Transform/Mapping/PoMappingEngine.cs:7-48` | none (pure function over a `PoMappingConfig`) |
| Static global templates | `StarterTemplateDto` + `StarterTemplateService` | `ProcuLink.Api/Services/StarterTemplates/StarterTemplateDto.cs:9-21`, `StarterTemplateService.cs:11-64` | **global, read-only, no DB** |
| List templates | `GET /api/po-mapping-templates` (`PoMappingTemplatesController`) | `ProcuLink.Api/Controllers/PoMappingTemplatesController.cs:11-30` | `[Authorize]`, no org scope (static data) |
| Apply a template to a supplier | `POST /api/suppliers/{id}/po-mapping/apply-template` | `ProcuLink.Api/Controllers/SuppliersController.cs:502-534` | org-scoped supplier lookup → `PoMappingService.UpsertAsync(orgId, …)` |

**Key observation:** the apply path is already the right seam. The controller
(`SuppliersController.cs:521-532`) (a) resolves `orgId` from `ICurrentTenantService`,
(b) loads the org's supplier, (c) takes a `PoMappingConfig` from *somewhere* (today
the static `StarterTemplateService.GetAll()`), and (d) calls
`PoMappingService.UpsertAsync(orgId, id, config, ct)`. A cross-org library is, at the
application layer, **just a richer source for step (c)** — the template config is
copied into the tenant's own `SupplierPoMapping` and from that moment is 100%
single-tenant. The shared catalog is never wired into the parse/transform hot path.

### 1.2 Adjacent learned data (the "moat" candidates) — and why they are NOT the library

| Entity | What it learns | Why it is single-tenant-only | File |
|---|---|---|---|
| `ItemMapping` | learned `BuyerItemCode → SupplierItemCode` resolutions | **PII / commercial secret** — reveals what a buyer buys from whom | `ProcuLink.Core/Entities/ItemMapping.cs:3-27` |
| `SupplierProduct` | a supplier's real catalog of codes | **supplier's commercial data** | `ProcuLink.Core/Entities/SupplierProduct.cs:14-50` |
| `SchemaFingerprint` | column-layout hashes this org has parsed | hash is structural, but `SampleSupplierName` is identifying | `ProcuLink.Core/Entities/SchemaFingerprint.cs:12-40` |

The library shares **mapping structure** (column-name → canonical-field). It must
**not** share `ItemMapping`, `SupplierProduct`, prices, or supplier identities. Those
are the very things a competitor would pay to see. (See §4.)

### 1.3 Canonical PO model fields a template maps onto

`PoMappingEngine.Apply` resolves a fixed, public set of canonical fields — header:
`PoNumber`, `OrderDate`, `BuyerName`, `Currency`; line: `LineNumber`, `BuyerItemCode`,
`Description`, `Quantity`, `Unit`, `UnitPrice` (`PoMappingEngine.cs:14-26`). These
names are **public schema**, not tenant data — they are exactly what a shared template
keys on. This bounds the blast radius: a template can only ever say *"source column X
feeds canonical field Y, with these manipulators"* over this closed vocabulary.

---

## 2. Goals / non-goals

**Goals (future):**
- A **global, curated, read-only** catalog of mapping templates, discoverable by all
  tenants, that one-click-applies into a tenant's own `SupplierPoMapping`.
- A **safe contribution path**: a tenant can offer one of its own mappings as a
  candidate template; it is anonymized and curated before it can ever be discovered.
- **Discovery that's better than a flat list**: surface the most relevant templates
  for the file the user is staring at (by ERP brand, by detected column layout).
- **Provable tenant isolation**: it must be *impossible* for tenant A to read tenant
  B's data through the library, and *impossible* for a contributed template to carry
  B's data to A.

**Non-goals (explicitly out of scope, even for the future build):**
- Sharing `ItemMapping` / `SupplierProduct` / prices / supplier identities. **Never.**
- A live "federated lookup" where one tenant's parse queries another tenant's data.
  The library is *copy-on-apply*, never *read-through*.
- Replacing the static `StarterTemplate` fixtures — the library is **additive**;
  the curated static set remains the trusted baseline.
- A public/anonymous endpoint. The library is authenticated-tenant-only.

---

## 3. Data model (future)

A single new **system-owned (org-less)** table. This is the first table in the schema
with no `OrgId` — by design — so it gets extra scrutiny (§5). All tenant-scoped tables
keep their `OrgId`; nothing about existing tenancy changes.

### 3.1 `MappingLibraryTemplate` (global, read-only to tenants)

```
MappingLibraryTemplate
  Id                Guid       PK
  Slug              string     unique, kebab-case, stable public id (e.g. "erply-csv-v2")
  Erp              string?    ERP/system brand this targets (e.g. "Erply", "Directo"), nullable
  Name              string     display name
  Description       string     one-sentence description
  Category          string     "erp" | "generic" | "industry" | "community"
  ConfigJson        string     jsonb — a PoMappingConfig (STRUCTURE ONLY, validated, see §4)
  ColumnNameHash    string?    SHA-256 of canonical column set (for layout-based discovery, see §6)
  Status            string     "draft" | "in_review" | "published" | "deprecated"
  Visibility        string     "public" (all tenants) | "curated" (staff-published only)
  Source            string     "builtin" | "contributed" | "staff"
  Version           int        monotonic; a new revision is a new row sharing FamilyId
  FamilyId          Guid       groups versions of the same logical template
  AppliedCount      int        how many times applied across all tenants (aggregate, non-identifying)
  PublishedAt       DateTime?
  CreatedAt         DateTime
  UpdatedAt         DateTime
  -- explicitly ABSENT: OrgId, SupplierId, any buyer/supplier name, any item code,
  --                    any price, any contributor identity (see ContributionAudit below)
```

`ConfigJson` deserialises to the **existing** `PoMappingConfig`
(`PoMappingConfig.cs:3-11`). No new mapping shape is invented — this is the whole point:
a library template is byte-compatible with what `PoMappingService.UpsertAsync` already
stores and what `PoMappingEngine.Apply` already consumes. Applying it is literally the
existing `apply-template` flow with a different config source.

### 3.2 `MappingLibraryContribution` (the staging/curation queue — org-scoped)

```
MappingLibraryContribution
  Id                Guid       PK
  ContributingOrgId Guid       FK → Organisations  (org-scoped! who offered it)
  SourceSupplierId  Guid?      the supplier whose mapping was offered (for the contributor's own audit only)
  ProposedConfig    string     jsonb — the anonymized PoMappingConfig candidate
  AnonymizationLog  string     jsonb — what was stripped/redacted (for review + the contributor's transparency)
  Status            string     "submitted" | "auto_rejected" | "in_review" | "approved" | "rejected" | "withdrawn"
  RejectionReason   string?
  ReviewedByUserId  string?    staff reviewer (Clerk user id)
  PublishedTemplateId Guid?    FK → MappingLibraryTemplate once approved+published
  CreatedAt         DateTime
  UpdatedAt         DateTime
```

This table **is** org-scoped (`ContributingOrgId`) — it is a tenant's own outbox.
A tenant can see/withdraw only its own contributions (every query
`Where(ContributingOrgId == orgId)`). The crucial transition is
`MappingLibraryContribution` (org-scoped, may contain residue) →
**human + automated curation + anonymization** → `MappingLibraryTemplate`
(global, scrubbed). **Nothing is ever copied straight through.**

### 3.3 Migration / rollout note

Introducing the **first org-less table** is the single riskiest schema decision here.
Guardrails (§5): it ships behind a feature flag, read-only to tenants via a dedicated
read-only service, and — if/when Postgres RLS lands (currently FINAL-DEFERRED per
`STATUS.md:14`) — `MappingLibraryTemplate` is the one table whose RLS policy is
`USING (true)` for SELECT and `USING (false)` for tenant-role writes (writes only via
the curation service running as a privileged role). Until RLS exists, app-level
enforcement is identical to every other table today.

---

## 4. Privacy / PII — the heart of the design

> **Invariant (restate):** a published `MappingLibraryTemplate.ConfigJson` contains
> *only* `{ source-column-name → canonical-field, manipulator recipe }` over the
> closed canonical vocabulary in §1.3. It carries **no order content, no item codes,
> no supplier item codes, no prices, no party names, no credentials, no free-text
> values from any order.**

### 4.1 What a `PoMappingConfig` *can* legitimately leak (the threat surface)

Reading `PoMappingConfig.cs:13-30`, the fields that could carry a tenant's data are:

1. **`FieldMappingEntry.ExternalField`** — a *source column name* (e.g. `"Tellija
   kood"`, `"Artikkelnummer"`). Generally structural and safe (it's the supplier's
   file-format vocabulary, not order content), **but** a column name can occasionally
   be identifying (e.g. a custom export header containing a company name). → Must pass
   an anonymization scan (§4.3).
2. **`FieldMappingEntry.FixedValue`** — a **constant injected into every order**. This
   is the highest-risk field: a tenant might hardcode `BuyerName = "REDACTED-PARTY"` or a
   currency or a buyer code here. → **`FixedValue` must be stripped or generalised on
   contribution** (a template should describe *that* a fixed value is used and for
   which field, e.g. a placeholder `"<your-buyer-name>"`, never the literal).
3. **`ManipulatorEntry.Params`** — manipulator arguments (e.g. `Replace("Markit",
   "MK")`, a `DateFormat` pattern, a regex). Some params are pure format recipes
   (safe: date formats, trims, separators); some embed tenant literals (the `Replace`
   "from"/"to" strings). → Params are classified per-manipulator-type:
   *format-only* (allow) vs *value-bearing* (redact to a placeholder or drop the
   manipulator with a note in `AnonymizationLog`).

`PoMappingConfig.Separator` / `HasHeaderRecord` (`PoMappingConfig.cs:5-6`) are pure
format flags — always safe.

### 4.2 Privacy classification table (the rule the anonymizer encodes)

| Field | Default disposition on contribution | Rationale |
|---|---|---|
| `HasHeaderRecord`, `Separator` | keep | pure format flags |
| `Header`/`Lines` keys (canonical field names) | keep | public schema (§1.3) |
| `ExternalField` (column name) | keep **iff** passes the deny-scan; else redact | structural but occasionally identifying |
| `FixedValue` | **strip → placeholder token** | most likely to carry literal tenant data |
| Manipulator `Type` | keep | type name is public |
| Manipulator `Params` (format-only types: Trim/DateFormat/Pad/Case/Separator) | keep | format recipe |
| Manipulator `Params` (value-bearing types: Replace/Lookup/Default/Const) | **redact to placeholder** | embeds tenant literals |

The "value-bearing" type list is derived by auditing `ManipulatorRegistry`
(`ProcuLink.Transform/Mapping/` — referenced from `PoMappingEngine.cs:42`) at build
time; any manipulator type not on an explicit *format-only allowlist* is treated as
value-bearing and redacted by default (fail-closed).

### 4.3 The anonymization pipeline (contribution time)

```
tenant's SupplierPoMapping.ConfigJson
  → deserialize to PoMappingConfig
  → STRIP/PLACEHOLDER value-bearing fields per §4.2 (fail-closed)
  → deny-scan every remaining string against:
       • the contributing org's own buyer/supplier names (from Suppliers/Buyers/Organisation)
       • a generic PII regex set (emails, IBANs, VAT ids, phone, long digit runs)
  → if any deny-scan hit remains → auto_reject with a clear reason (no silent publish)
  → produce ProposedConfig + AnonymizationLog (what changed) for human review
  → store as MappingLibraryContribution (org-scoped, NOT yet in the global table)
```

Two independent gates (automated strip + deny-scan, then human curation) before
anything reaches the global table. The `AnonymizationLog` is shown back to the
contributing tenant so they can confirm *exactly* what would be shared — consent is
informed, not blanket.

### 4.4 Consent & data-protection posture

- Contribution is **opt-in per template**, never automatic and never org-wide-default.
- The contribution screen shows the fully-anonymized preview + `AnonymizationLog`
  before submit ("this is exactly what other ProcuLink customers would see").
- A published template's `Source`/`AppliedCount` are aggregate and non-identifying;
  the **contributor's identity is never published** (it lives only in the org-scoped
  `MappingLibraryContribution`).
- DPA/ToS note (legal, not code): the customer agreement must grant ProcuLink a
  licence to publish *anonymized structural mappings* a customer explicitly
  contributes. This is a contract clause to add *before* build, not a code task.

---

## 5. Tenancy / security guardrails

These are the rules that keep a global table from becoming a cross-tenant hole. They
mirror the existing house style: *"Every service method takes `Guid organisationId`.
All EF queries: `.Where(x => x.OrganisationId == organisationId)`"* (CLAUDE.md).

1. **Two services, two postures.**
   - `IMappingLibraryReadService` — serves the **global, read-only** catalog. Returns
     only `published` + `public`/`curated` templates. **No `OrgId` parameter** (the
     data is genuinely global and contains no tenant data, exactly like
     `StarterTemplateService` today). It is read-only: no method mutates the global
     table.
   - `IMappingLibraryContributionService` — **org-scoped**, takes `Guid organisationId`
     on every method; every query `Where(ContributingOrgId == organisationId)`. Tenants
     touch only their own contributions.
2. **Curation/publish is staff-only.** The transition contribution → published
   template runs behind an `[AdminOnly]` gate (the existing fail-closed env-allowlist
   pattern, `STATUS.md` "owner/admin backend … adversarial security review CLEAN") and
   runs as a privileged role, never as a tenant.
3. **Apply is copy-on-write into the tenant's own row.** `apply-from-library` resolves
   `orgId` from `ICurrentTenantService`, loads the org's supplier (org-scoped, exactly
   as `SuppliersController.cs:521-525` does today), then
   `PoMappingService.UpsertAsync(orgId, supplierId, libraryConfig, ct)`. From that
   moment the config is the tenant's own single-tenant data; the global row is
   untouched. **The shared catalog is never on the parse/transform hot path** — only
   the tenant's own copied `SupplierPoMapping` is.
4. **No read-through.** There is no code path where parsing tenant A's order reads
   tenant B's (or the library's) data at runtime. The library influences a tenant only
   via an explicit, audited apply action.
5. **Global table is read-only to tenant code.** Only the curation service writes to
   `MappingLibraryTemplate`. If RLS lands, enforce at the DB; until then, enforce by
   not exposing any tenant-reachable write path (no controller, no service method).
6. **Validation on apply (defense-in-depth).** Before upserting a library config into
   a tenant, re-validate it is a well-formed `PoMappingConfig` over the closed
   canonical vocabulary and that no value-bearing field slipped through (re-run the
   §4.2 scan on read, not just on write — fail-closed). This protects a tenant even if
   a bad template were ever published.
7. **Rate-limit + abuse controls** on contribution (reuse existing rate-limit policies,
   `STATUS.md` Wave 2) so the queue can't be flooded.

---

## 6. Discovery + apply (UX seam)

Discovery should be *better than a flat list* but must not require any cross-org read
of tenant data:

- **By ERP brand:** filter `MappingLibraryTemplate.Erp == "Erply"` etc. — pure global
  query, no tenant data.
- **By detected layout (uses the existing fingerprint idea, safely):** when a tenant
  uploads a file, the detector already computes a canonical column hash
  (`SchemaFingerprint.ColumnNameHash`, `SchemaFingerprint.cs:19-24` — SHA-256 of
  trimmed+lowercased+sorted headers). A library template can carry the *same kind of
  hash* in `MappingLibraryTemplate.ColumnNameHash`. Matching is a **hash equality on
  non-identifying structural data** — the tenant's actual column *values/order content*
  never leave the tenant; only the structural hash is compared, and it is compared
  against global templates, not against other tenants. This gives "a community template
  matches your file's layout" without any cross-tenant read.
- **Apply:** new `POST /api/suppliers/{id}/po-mapping/apply-library-template` with
  `{ "templateSlug": "erply-csv-v2" }` — a near-clone of the existing
  `ApplyPoMappingTemplate` (`SuppliersController.cs:510-534`), differing only in the
  config source (`IMappingLibraryReadService.GetBySlug` instead of
  `StarterTemplateService.GetAll`) and an `AppliedCount` increment on the global row
  (the only tenant-triggered write to the global table, and it touches no tenant data).
- **Review-before-use stays mandatory.** Same as the static templates today: applying
  loads the config into `PoMappingEditor` for the user to verify column names against
  their real export (`docs/superpowers/specs/2026-06-03-erply-directo-mapping-templates.md:101-102`).
  A community template is a *starting point*, never auto-trusted.

---

## 7. Governance

- **Curation queue + staff review** (§3.2, §5.2). Default-reject anything the
  automated anonymizer flags; a human approves publication.
- **Versioning:** templates are immutable once published; an edit is a new `Version`
  in the same `FamilyId`. Tenants who applied v1 keep their copied config (copy-on-
  write) — they are never silently re-mapped.
- **Deprecation:** `Status = "deprecated"` hides a template from discovery but does not
  touch any tenant that already copied it.
- **Quality signal:** `AppliedCount` (aggregate, non-identifying) ranks discovery;
  optionally a later "this template parsed N orders successfully" aggregate, computed
  staff-side, never per-tenant-visible.
- **Takedown:** a contributor can request removal of a template they contributed; the
  org-scoped `MappingLibraryContribution` link makes provenance auditable for that.
- **Builtin baseline stays authoritative:** the curated static `StarterTemplate`
  fixtures (`StarterTemplateService.cs:17-24`) remain `Source = "builtin"` and are the
  trusted defaults; community templates are clearly badged as community-contributed.

---

## 8. Phased rollout (when unfrozen, gated on paying customers + order volume)

**Precondition to start any phase:** there are paying customers AND enough order
volume that shared templates would actually help (the four-lens "no data, no moat"
gate, `2026-05-30-four-lens-product-analysis.md:268-270`). Until then: **do nothing.**

- **Phase 0 — promote the static set (no DB).** Reframe the existing static
  `StarterTemplate` fixtures as the "library v0" in the UI (badge as builtin). Zero new
  tables, zero risk. Proves the discovery/apply UX with already-safe data.
- **Phase 1 — global read-only catalog (staff-authored only).** Add
  `MappingLibraryTemplate` (global table) + `IMappingLibraryReadService` +
  `GET /api/mapping-library` + `apply-library-template`. **Only staff** populate it
  (`Source = "staff"`). No contribution path yet → no PII surface yet. This is the
  smallest reversible slice: a global read-only lookup feeding the existing apply seam.
- **Phase 2 — layout-based discovery.** Add `ColumnNameHash` matching (§6) reusing the
  fingerprint hashing already in `SchemaFingerprint`. Still no contribution.
- **Phase 3 — contribution + anonymization + curation queue.** Add
  `MappingLibraryContribution` (org-scoped) + the anonymization pipeline (§4.3) +
  staff curation (§5.2). **This is the phase that introduces PII risk** — it ships only
  after a security + DPA review, behind a feature flag, contribution opt-in per
  template.
- **Phase 4 — quality signals + governance polish.** `AppliedCount` ranking,
  versioning/deprecation/takedown workflows, per-industry curated sets.

Each phase is independently shippable and reversible (drop the flag → behaves like the
prior phase). No phase changes existing single-tenant mapping behaviour; the library is
purely additive.

---

## 9. Risks & "can this land green?" notes

- **🟥 First org-less table.** `MappingLibraryTemplate` breaks the "every table has
  `OrgId`" invariant. This is intentional and safe *because the table holds no tenant
  data*, but it warrants the §5 guardrails and (eventually) the RLS exception in §3.3.
  This is the single thing a reviewer must scrutinise.
- **🟥 Anonymization is the whole ballgame.** A leak here ships one tenant's data to
  every other tenant. Mitigations: fail-closed classification (§4.2), two gates
  (automated + human, §4.3), re-scan on apply (§5.6), opt-in + informed consent (§4.4).
  Phase 3 must not ship without a dedicated security review.
- **🟨 Manipulator param classification** depends on an accurate format-only allowlist
  derived from `ManipulatorRegistry`. If a new value-bearing manipulator type is added
  later without updating the allowlist, fail-closed (redact unknown types) prevents a
  leak but may over-redact. Acceptable trade.
- **🟩 Apply path is low-risk.** It reuses the existing, tested
  `PoMappingService.UpsertAsync` + `apply-template` controller pattern verbatim; the
  only new tenant-side write is an `AppliedCount` increment on a tenant-data-free row.
- **🟩 Reversibility.** Everything is feature-flagged and additive; copied configs are
  the tenant's own data, unaffected by deprecating/removing a library template.

**Bottom line:** Phases 0–2 are safe, reversible, and could land green when unfrozen.
**Phase 3 (contribution/anonymization) cannot be considered "ready to land green"** —
it must clear a security + data-protection review first, because a single
classification miss is a cross-tenant data leak. Hence: **design now, build never until
there are paying customers and a security sign-off.**
