# Group V1 — Versioned Supplier Connection: Implementation Plan

**Status:** PLAN OF RECORD (drafted 2026-06-09). Implements Group **V1** of
`docs/strategy/2026-06-09-supplier-connection-north-star.md`.
**Scope:** the `SupplierConnection` + `SupplierConnectionRevision` aggregate, the
draft→test→publish→archive lifecycle, `ConnectionRevisionId` pinning on every order,
and a zero-behaviour-change backfill of existing per-supplier config into
"revision 1 (published)".

**Prime directive:** the app stays working at every step. Existing orders keep
flowing end-to-end after every phase. No phase requires a big-bang cutover. A
backfilled order must transform **byte-identical** before and after V1.

---

## 0. Grounding — what exists today (the things V1 subsumes)

A supplier's "configuration" is today **five scattered, mostly-mutable, unversioned
surfaces** plus one already-versioned precedent:

| Concept | Entity / storage | Scope | Versioned today? |
|---|---|---|---|
| Input/CSV parse mapping | `SupplierPoMapping` (`supplier_po_mappings.config_json` jsonb = `PoMappingConfig`) | UNIQUE(org_id, supplier_id) | No (upsert) |
| Output template | `OutputTemplate` (`output_templates.config_json` jsonb) + per-order override in `purchase_orders.canonical_json` `mappingOverride` | org-level / per-order | No |
| Product-code mapping | `ItemMapping` (`item_mappings`) | (org_id, supplier_id) rows | No |
| Supplier catalog | `SupplierProduct` (`supplier_products`, `is_active`) | (org_id, supplier_id) rows | No |
| Delivery channel + creds | `SupplierDeliveryConfig` (`supplier_delivery_configs`: protocol, config_json, encrypted_credentials, output_format, auto_deliver) | (org_id, supplier_id) | No |
| Acceptance/validation | `SupplierAcceptanceProfile` + `SupplierAcceptanceRule` | UNIQUE(org_id, supplier_id, version_no), status draft/active/archived | **Yes** ← our precedent |

**The precedent to copy** (`ProcuLink.Api/Services/SupplierAcceptanceService.cs`):
- `VersionNo` int, `Status` in {draft, active, archived}, `EffectiveFrom`/`EffectiveTo`.
- DB unique constraint `HasIndex(OrgId, SupplierId, VersionNo).IsUnique()`
  (`ProcuLinkDbContext.cs:718`).
- Activation flips exactly one active: archive prior active (set `EffectiveTo=now`),
  set target `active` + `EffectiveFrom=now` (`ActivateVersionAsync`, lines 63-85).
- `GetActiveAsync` returns status=active; `GetLatestAsync` falls back to latest draft.

V1 generalises this precedent from "just acceptance" to **the whole connection**, and
adds the missing piece the acceptance precedent never had: **orders pin the exact
revision id they used** (acceptance only stamps `OrderValidationResult.ProfileId` at
validation time; it does not pin a single connection-level id to the order row).

**Anti-pattern reminder (North Star):** give connection concepts **first-class
tables/columns**. Do NOT pile the new revision bundle into `canonical_json`. The
per-order `mappingOverride` lives in `canonical_json` today as a migration-dodge; V1
must not extend that pattern.

---

## 1. Data model — `SupplierConnection` + `SupplierConnectionRevision`

Two new top-level entities plus child component tables. The aggregate root is the
**connection** (one stable identity per org+supplier integration); the **revision** is
the immutable, versioned bundle.

### 1.1 `SupplierConnection` (the stable aggregate root)

Table `supplier_connections`. One per (org, supplier) integration — a stable handle the
active-revision pointer hangs off, so the active revision can change without changing the
connection identity.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | stable connection identity |
| `org_id` | uuid FK→organisations | tenant scope, non-null |
| `supplier_id` | uuid FK→suppliers | non-null |
| `name` | text | display name (defaults to supplier name) |
| `active_revision_id` | uuid? FK→supplier_connection_revisions | **the live pointer**; null until first publish |
| `created_at` / `updated_at` | timestamptz | |
| `created_by` | text? | mirrors acceptance `CreatedBy` |

Indexes: `UNIQUE(org_id, supplier_id)` (one connection per supplier — matches the
existing 1:1 `SupplierPoMapping`/`SupplierDeliveryConfig` cardinality). The
`active_revision_id` FK is **deferred / nullable** to avoid a circular-FK creation
ordering problem (connection references revision, revision references connection) — see
§3.

> Cardinality decision: **one connection per supplier** in V1. This matches every
> existing surface's `UNIQUE(org_id, supplier_id)`. Multi-connection-per-supplier (e.g.
> two ERPs for one vendor) is explicitly deferred to a later group; the schema does not
> preclude it because the connection has its own id.

### 1.2 `SupplierConnectionRevision` (the immutable versioned bundle)

Table `supplier_connection_revisions`. This is the unit the North Star describes.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | **this is the `ConnectionRevisionId` pinned to orders** |
| `connection_id` | uuid FK→supplier_connections | non-null |
| `org_id` | uuid | denormalised for tenant-scoped queries + index (mirrors how `OrderException`/`PurchaseOrderEntity` carry `org_id`) |
| `supplier_id` | uuid | denormalised for ingest-time lookup without a join |
| `version_no` | int | monotonic per connection |
| `status` | text | `draft` \| `test` \| `published` \| `archived` |
| `effective_from` | timestamptz? | set on publish |
| `effective_to` | timestamptz? | set on archive (superseded) |
| `published_at` | timestamptz? | |
| `created_at` | timestamptz | |
| `created_by` / `published_by` | text? | |

Indexes: `UNIQUE(connection_id, version_no)` (copy of the acceptance precedent),
`INDEX(org_id, supplier_id, status)` for the ingest-time "resolve active revision" path.

**What a revision bundles** (each is a first-class column or a child row referencing the
revision — NOT a JSON dump). A revision is a *snapshot* taken at draft-creation /
publish, so a published revision is fully reproducible even if the supplier's "current"
loose config is later edited (during the transition period) or deleted.

The bundle uses a **hybrid columns + scoped-JSON** strategy: structural identity and
lifecycle live in columns; the already-JSON config blobs (which the engine already
deserializes) are carried as **dedicated jsonb columns on the revision**, not folded
into one mega-blob and not in `canonical_json`.

| Bundle component | Storage on / under the revision | Source it snapshots |
|---|---|---|
| Input detection & parse mapping | `input_mapping_json` jsonb (= `PoMappingConfig`) | `SupplierPoMapping.config_json` |
| Canonical mapping | part of `input_mapping_json` (`PoMappingConfig.Header/Lines`) | same |
| Output template / field-map | `output_mapping_json` jsonb (= `OutputMappingConfig`/template ref) + `output_format` text | `OutputTemplate.config_json` + `SupplierDeliveryConfig.OutputFormat` + the `PoMappingConfig.Output` promotion |
| Product-code mapping | child table `connection_revision_item_mappings` (snapshot of `ItemMapping` rows: buyer_code, supplier_code, confidence, source) | `ItemMapping` |
| Catalog binding | `catalog_binding` — V1 uses a **live reference** (catalog stays mutable; revision records `catalog_mode`='live'), NOT a full snapshot | `SupplierProduct` (see §1.4 decision) |
| Validation bindings | `acceptance_profile_id` uuid? FK→supplier_acceptance_profiles + `acceptance_version_no` int? | `SupplierAcceptanceProfile` (bind, don't copy — North Star V4 anti-pattern) |
| Delivery channel | `delivery_protocol` text, `delivery_config_json` jsonb, `delivery_auto_deliver` bool | `SupplierDeliveryConfig` |
| Credentials ref | `credentials_ref` text (encrypted payload **or** a key pointer) | `SupplierDeliveryConfig.EncryptedCredentials` |
| Test pack | child table `connection_revision_test_cases` (sample source file key + expected output artifact key) | new in V1 (empty for backfilled rev 1) |

`SupplierConnectionRevision` C# entity navigation: `Connection`, `Organisation`,
`List<ConnectionRevisionItemMapping> ItemMappings`, `List<ConnectionRevisionTestCase>
TestCases`. EF config in `ProcuLinkDbContext.cs` mirrors the acceptance block
(`:700-718`): `ToTable`, snake_case `HasColumnName`, `jsonb` columns via the existing
`jsonDocConverter`, `HasMany(...).WithOne(...).HasForeignKey(...)`, the two indexes.

### 1.3 Why columns-per-component, not one `config_json`

- The engine already reads each blob through a typed contract
  (`PoMappingConfig`, `OutputMappingConfig`, delivery `ConfigJson`). Keeping them as
  separate jsonb columns lets the **existing readers be re-pointed with near-zero
  reshaping** (§4) — we hand them the same shape they parse today.
- Validation and catalog are **bound by id**, not copied, so V1 doesn't fork the
  executable acceptance engine (`SupplierAcceptanceService`) — the North Star V4
  "bind, don't re-implement" rule applies already.
- Lifecycle/identity columns (`status`, `version_no`, `effective_*`) must be SQL-
  queryable and indexable — they cannot live inside jsonb.

### 1.4 Catalog decision (recorded)

Supplier catalogs (`SupplierProduct`) can be large (≤2,000 loaded today;
`OrderIngestionService.cs:929`) and change frequently via re-sync. **V1 binds the
catalog live** (`catalog_mode='live'`): the revision does not snapshot products; ingest
continues reading `SupplierProducts WHERE IsActive`. This keeps V1 small and preserves
byte-identical backfill. True catalog snapshotting (for full replay reproducibility) is
deferred to V2/V10 where indexed retrieval lands. This is a conscious reproducibility
trade-off, documented so V2 can close it.

---

## 2. Lifecycle — draft → test → publish → archive

States and transitions (generalising `SupplierAcceptanceService` semantics):

```
        create/edit                publish                supersede
draft ───────────────► test ──────────────► published ───────────────► archived
  ▲          (optional run of test pack)        │  (immutable)             ▲
  └───────────────── new draft from published ──┘──────────────────────────┘
```

- **draft** — mutable. All edits (mapping, output, delivery, rules binding, test pack)
  mutate the *draft* revision's columns/children. Multiple drafts may exist; only the
  highest `version_no` draft is "the working draft".
- **test** — a draft that has been validated against its test pack (V2 makes this rich;
  V1 allows the transition and records it, optionally running the deterministic transform
  on test-pack samples). Still mutable; `test` is a readiness marker, not a freeze.
- **published** — **immutable**. On publish: assign next `version_no` if not already set,
  set `status='published'`, `published_at`/`effective_from=now`; **archive the prior
  published revision** (`status='archived'`, `effective_to=now`) exactly as
  `ActivateVersionAsync` archives the prior active; set the connection's
  `active_revision_id` to this revision in the **same transaction**.
- **archived** — historical, immutable, retained forever (orders pin to it).

**Immutability enforcement (defence in depth):**
1. Service layer: `UpdateDraftAsync` / all mutators reject any revision whose
   `status != 'draft'` (and `test` allows edits that demote back to draft, or we keep
   `test` editable — pick: **`test` is editable**, publish is the freeze line).
2. EF: an interceptor (`SaveChangesInterceptor`) that throws if a modified
   `SupplierConnectionRevision` or its children has `status in (published, archived)` and
   any non-lifecycle property changed. Cheap, central, catches stray code paths.
3. DB (belt): a Postgres trigger/`CHECK` is optional in V1; the interceptor is the
   primary guard to avoid migration complexity. Record as a hardening follow-up.

**One published per connection** is enforced in `PublishAsync` (archive-prior-on-
transition), identical to the acceptance one-active invariant.

---

## 3. Migration + backfill (zero behaviour change)

Three EF migrations, additive only. **No existing table is altered destructively; no
existing column is dropped in V1.** The loose config tables remain the live source of
truth until §4 flips reads — backfill *copies* them, it does not move them.

### Migration A — `AddSupplierConnections` (schema only, no data)

`dotnet ef migrations add AddSupplierConnections -p ProcuLink.Infrastructure -s ProcuLink.Api`

Creates: `supplier_connections`, `supplier_connection_revisions`,
`connection_revision_item_mappings`, `connection_revision_test_cases`. Add the
`UNIQUE`/`INDEX`es from §1. **Make `supplier_connections.active_revision_id` nullable with
the FK created in a second `migrationBuilder.AddForeignKey` call after both tables exist**
(resolves the circular FK; same technique used when two tables reference each other).
Register DbSets in `ProcuLinkDbContext` (mirror lines 19-52) and the `OnModelCreating`
blocks (mirror the acceptance block `:700-718`).

App still behaves identically — nothing reads these tables yet.

### Migration B — `AddConnectionRevisionPinToOrders` (additive column)

Adds `purchase_orders.connection_revision_id uuid NULL` (FK→
`supplier_connection_revisions`, `ON DELETE SET NULL`/`RESTRICT` — choose RESTRICT so a
pinned revision can never be deleted). New nullable column on
`PurchaseOrderEntity` (mirror the additive-column pattern of `RequeueCount` /
`SchemaFingerprintHash`, documented at `PurchaseOrderEntity.cs:44-59`). Existing rows get
NULL → readers must treat NULL as "legacy / fall back to live config" (§4). Pure additive;
zero behaviour change.

### Migration C / data backfill — `BackfillConnectionRevision1`

The behaviour-preserving step. Implemented as an **idempotent data migration**
(`migrationBuilder.Sql` with `INSERT … SELECT … WHERE NOT EXISTS`, or a guarded
one-shot `IHostedService`/admin command run once — prefer SQL-in-migration so it's
versioned and runs in deploy order). For **every (org_id, supplier_id) that has any
existing config** (a `SupplierPoMapping` OR `SupplierDeliveryConfig` OR `ItemMapping` OR
active `SupplierAcceptanceProfile` OR `SupplierProduct`):

1. Insert one `supplier_connections` row (id = new uuid, name = supplier name).
2. Insert one `supplier_connection_revisions` row, `version_no=1`,
   `status='published'`, `effective_from=now`, `published_at=now`, copying:
   - `input_mapping_json` ← `supplier_po_mappings.config_json` (or `'{}'` if none).
   - `output_format` ← `supplier_delivery_configs.output_format`;
     `output_mapping_json` ← the org `OutputTemplate.config_json` if assigned, else null
     (today output is per-order override or fixed transformer — null reproduces "fixed
     transformer" exactly).
   - `delivery_protocol` / `delivery_config_json` / `delivery_auto_deliver` /
     `credentials_ref` ← the matching `supplier_delivery_configs` row.
   - `acceptance_profile_id` / `acceptance_version_no` ← the supplier's **active**
     `SupplierAcceptanceProfile` (null if none).
   - `catalog_mode='live'` (no product copy — §1.4).
3. Insert `connection_revision_item_mappings` snapshotting current `ItemMapping` rows.
4. Set `supplier_connections.active_revision_id` = the rev-1 id.
5. Leave **all source tables untouched.**

**The orders backfill** (separate, can run lazily/online): for existing
`purchase_orders` with NULL `connection_revision_id`, set it to the rev-1 id of their
(org, supplier) connection. Run as a batched `UPDATE … FROM` after Migration C, or leave
NULL and let readers fall back (safer; do the UPDATE as a low-priority sweep). Either way
behaviour is unchanged because rev-1 == current config.

**Idempotency:** every insert is `WHERE NOT EXISTS`; re-running the migration is a no-op.
This matters because the deploy may be retried (Hangfire/restart culture in this codebase).

---

## 4. `ConnectionRevisionId` resolution + pinning, and which reads switch

### 4.1 Resolve + pin at ingest (write side)

Add `IConnectionResolver.ResolveActiveRevisionAsync(orgId, supplierId, ct)` returning the
connection's `active_revision_id` (the `SupplierConnection.ActiveRevisionId`). Pin it on
**order creation**, both paths:
- `OrderIngestionService.CreateFromFileAsync` — set `entity.ConnectionRevisionId` before
  `_db.PurchaseOrders.Add(entity)` (around `:159`, alongside the existing field
  assignments `:140-156`).
- `OrderIngestionService.CreateStubAsync` — set it on the stub entity (around `:229-242`),
  so the async parse job inherits the pin.

Resolution rule: if no active revision exists yet (supplier with zero config, or pre-
backfill race), pin NULL and behave exactly as today (live config). Pin once, at create;
**never re-pin** — that's the whole reproducibility invariant. Record the pin in the
existing audit event (`:160`) payload (`connectionRevisionId`).

### 4.2 Reads that switch to the pinned revision (read side, phased)

Each of the 13 surveyed read points is wired to **prefer the pinned revision, fall back
to live config when the pin is NULL**. This fallback is what guarantees old orders and
zero-config suppliers keep working.

| Pipeline read point (survey) | File | Switch to |
|---|---|---|
| #3/#6 CSV parse mapping (`PoMappingService.GetAsync`) | `OrderIngestionService.cs:481,513` | revision `input_mapping_json`; fallback `SupplierPoMapping` |
| #1/#4 Item-mapping resolve + AI candidates | `OrderIngestionService.cs:784,896` | revision `connection_revision_item_mappings`; fallback `ItemMapping` |
| #2/#7 Catalog grounding | `OrderIngestionService.cs:829,945` | **unchanged** (live, `catalog_mode='live'`) |
| #8-#11 Transform (output template / overrides) | `OrderTransformService.cs:70-109` | revision `output_mapping_json`/`output_format`; per-order `mappingOverride` still wins (it's order-specific); fallback fixed transformer (unchanged) |
| #12 Acceptance validation | `SupplierAcceptanceService.ValidateOrderAsync:95` | revision-bound `acceptance_profile_id` instead of `GetActiveAsync`; fallback active profile |
| #13 Delivery config + creds | `DeliveryService.cs:94-109` | revision `delivery_*` + `credentials_ref`; fallback `SupplierDeliveryConfig` |

Implementation shape: a thin `EffectiveConnectionConfig` accessor that, given an order,
returns either the pinned revision's components or the live values. **The downstream
services keep consuming the same typed shapes** (`PoMappingConfig`, delivery
`ConfigJson`, etc.) — only the *source* changes. This is why §1 stored components as
component-shaped jsonb.

---

## 5. API + minimal UI surface

### 5.1 API — new `ConnectionsController`

Mirror `SupplierAcceptanceController` conventions (org-scoped, same auth filter).

CRUD + lifecycle:
- `GET    /api/connections` — list connections for org (supplier, active version, status).
- `GET    /api/connections/{id}` — connection + revision list.
- `GET    /api/connections/{id}/revisions/{revId}` — full revision bundle.
- `POST   /api/connections/{id}/revisions` — create a **new draft** (clone from active
  published revision; this is what "edit a published connection" does).
- `PUT    /api/connections/{id}/revisions/{revId}` — update draft bundle (rejected unless
  status=draft/test).
- `POST   /api/connections/{id}/revisions/{revId}/test` — mark test / run test pack
  (deterministic transform of samples in V1; rich diff in V2).
- `POST   /api/connections/{id}/revisions/{revId}/publish` — freeze + flip
  `active_revision_id` (the `PublishAsync` transition).
- `POST   /api/connections/{id}/revisions/{revId}/archive` /
  `POST …/rollback` — archive, or republish a prior revision (rollback = publish an old
  immutable revision as the new active).

Service: `IConnectionService` / `ConnectionService` (in `ProcuLink.Api/Services`), modeled
directly on `SupplierAcceptanceService` (Create/Get/List/Publish/Archive).

### 5.2 Minimal UI

- **One Connection page** per supplier: tabs for Input mapping · Output · Product codes ·
  Validation · Delivery · Test pack — each tab reads/writes the **current draft revision**.
- The existing per-supplier editors (PO-mapping editor, delivery config form, acceptance
  rules editor, output mapping/wiring) are **rehosted** to edit the draft revision instead
  of the loose row. Minimal change: point their save calls at
  `PUT …/revisions/{draftId}` instead of the legacy upsert endpoints.
- A **status/version header**: current published version, draft-in-progress badge,
  "Publish" / "Discard draft" / "Rollback to v{n}" actions.
- Order detail shows **"processed with connection v{n}"** (the pin) — the visible payoff.

Keep legacy editor endpoints alive during transition (they still write loose rows); a
later cleanup deprecates them once the draft path is the only writer.

---

## 6. Test strategy

**The headline test: byte-identical backfill.**
1. Snapshot: for a representative set of real orders across all 8×7 in/out combos,
   transform under current `main` and store the output artifacts (golden files).
2. Apply Migration A+B+C + backfill on the same DB.
3. Re-transform the same orders **through the pinned rev-1 path**.
4. Assert output bytes are identical to the golden files. This is the definition-of-done
   gate; wire it into the existing FormatMatrix deterministic suite (tasks #18/#19/#23).

Other coverage:
- **Lifecycle unit tests** (mirror acceptance tests): create draft → publish flips
  active + archives prior; second publish archives first; rollback republishes; only one
  published per connection.
- **Immutability tests**: editing a published/archived revision throws (service +
  interceptor). EF `Modified` on frozen revision is blocked.
- **Pinning tests**: order created → `connection_revision_id` set to active; publishing a
  *new* revision does NOT change the pin on already-created orders (reproducibility).
- **Fallback tests**: order with NULL pin (legacy / zero-config supplier) transforms
  exactly as today through every switched read point.
- **Tenant isolation**: all connection queries scoped to org_id (the codebase's standing
  invariant — cf. cross-tenant guard at `OrderIngestionService.cs:222`). Add a test that
  org A cannot resolve org B's revision.
- **Idempotent backfill**: run Migration C twice → identical row counts.
- **InMemory context handling** for the new entities (per task #14 convention).

Run `dotnet test ProcuLink.slnx` GREEN at the end of every phase.

---

## 7. Phased breakdown (each independently shippable)

Every phase leaves a working end-to-end order path; phases ship in order.

- **V1a — Schema + entities (dark).** Migration A, entities, DbContext config, DbSets,
  immutability interceptor, services with unit tests. Nothing reads/writes the tables in
  the live path. *Ship: dead code behind no caller; zero runtime change.*
- **V1b — Backfill + pin column.** Migration B (`connection_revision_id` on orders) +
  Migration C (rev-1 published per supplier, idempotent) + orders backfill sweep. Still
  no read switch. *Ship: every supplier now has a published rev-1; orders gain a (still-
  unused) pin. Verify byte-identical golden snapshot — the §6 headline test runs here.*
- **V1c — Pin at ingest.** `IConnectionResolver` + pin in `CreateFromFileAsync` /
  `CreateStubAsync` + audit payload. Reads still use live config (pin written, not yet
  read). *Ship: new orders record their revision; behaviour unchanged.*
- **V1d — Switch reads to the pin (with live fallback).** Re-point the 13 read points
  (delivery → transform → validation → mapping → parse) one cluster at a time, each behind
  the NULL-fallback. Catalog stays live. *Ship: the connection now actually drives
  processing; old/NULL orders unaffected.*
- **V1e — Lifecycle API + Connection UI.** `ConnectionsController`, draft-clone-on-edit,
  publish/rollback, the Connection page, rehost existing editors onto the draft revision.
  *Ship: changing a mapping creates a draft; publishing flips active — the North Star
  definition-of-done.*
- **V1f — Hardening + deprecation.** Optional DB-level immutability trigger, deprecate the
  legacy loose-config write endpoints, docs. (Loose tables kept readable for one more
  group as the safety net; do not drop in V1.)

**Definition of done (North Star):** changing a supplier's mapping creates a new draft
revision; publishing flips the active revision; orders record which revision they used —
delivered in V1e, proven byte-identical in V1b.

### Key risks
1. **Backfill not byte-identical** (e.g. a per-order override or fixed-transformer path is
   accidentally diverted). *Mitigation:* golden-file gate in V1b; rev-1 stores null
   output mapping to reproduce the fixed transformer exactly; per-order `mappingOverride`
   continues to win.
2. **Circular FK** (connection↔revision) breaks migration ordering. *Mitigation:*
   nullable `active_revision_id`, FK added in a second step (§3).
3. **Catalog reproducibility gap** (live binding ≠ true snapshot). *Mitigation:* recorded
   as a conscious V1 trade-off; closed in V2/V10.
4. **Read-switch regressions across 13 points.** *Mitigation:* NULL-fallback on every
   point + per-cluster phased rollout (V1d) + fallback tests.
5. **Dual write window** (legacy editors + draft editors both writing) causing drift.
   *Mitigation:* draft is the single writer once V1e lands; legacy endpoints deprecated in
   V1f, not before, so rollback is always possible.
6. **Idempotency under deploy retries.** *Mitigation:* all backfill inserts are
   `WHERE NOT EXISTS`.

---

## Appendix — primary code touch-points
- `ProcuLink.Core/Entities/` — new `SupplierConnection.cs`,
  `SupplierConnectionRevision.cs`, `ConnectionRevisionItemMapping.cs`,
  `ConnectionRevisionTestCase.cs`; add `ConnectionRevisionId` to
  `PurchaseOrderEntity.cs`.
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — DbSets (`:19-52`) + config blocks
  (mirror acceptance `:700-718`) + new migrations under `Migrations/`.
- `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` — pin at `:159` / `:229-242`.
- `ProcuLink.Api/Services/Orders/OrderTransformService.cs` — revision output source `:70-109`.
- `ProcuLink.Infrastructure/Services/DeliveryService.cs` — revision delivery source `:94-109`.
- `ProcuLink.Api/Services/SupplierAcceptanceService.cs` — validation binding `:95`;
  the lifecycle template for the new `ConnectionService`.
- `ProcuLink.Api/Controllers/` — new `ConnectionsController.cs`.
````

---

### Phased summary (tight)

- **V1a** Schema + entities + lifecycle service + immutability interceptor, all dark (no live caller).
- **V1b** Migration: `connection_revision_id` on `purchase_orders` + idempotent backfill of each supplier's current mapping/output/delivery/acceptance into **revision 1 (published)**; catalog stays live-bound; golden byte-identical gate runs here.
- **V1c** Resolve + pin the active `ConnectionRevisionId` at ingest (`CreateFromFileAsync` / `CreateStubAsync`); reads still use live config.
- **V1d** Switch the 13 pipeline read points to the pinned revision, each with NULL→live fallback (delivery, transform, validation, item-mapping, parse).
- **V1e** Lifecycle API (`ConnectionsController`: CRUD + test/publish/archive/rollback) + one Connection page; existing per-supplier editors rehosted to edit the **draft revision** — delivers the North Star DoD (edit→draft, publish→flip active, orders record their revision).
- **V1f** Hardening (optional DB immutability trigger) + deprecate legacy loose-config write endpoints.

Key risks: backfill not byte-identical (golden-file gate), circular connection↔revision FK (nullable pointer + 2-step FK), catalog reproducibility gap (conscious live-binding trade-off, closed in V2/V10), 13-point read-switch regressions (per-cluster rollout + fallback), dual-write drift (legacy endpoints deprecated only in V1f), deploy-retry idempotency (`WHERE NOT EXISTS`).

### Critical Files for Implementation
- C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Infrastructure\ProcuLinkDbContext.cs
- C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Api\Services\SupplierAcceptanceService.cs
- C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Api\Services\Orders\OrderIngestionService.cs
- C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Api\Services\Orders\OrderTransformService.cs
- C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\ProcuLink.Core\Entities\PurchaseOrderEntity.cs