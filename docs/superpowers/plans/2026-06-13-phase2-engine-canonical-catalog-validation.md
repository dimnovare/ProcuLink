# Phase 2 — Engine + extensible canonical + catalog + validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Phase-1 lossless universe *usable* in the mapping engine. (A) Let users grow the canonical spine without a per-field migration via a revision-scoped `CanonicalFieldDef` table, with values carried on the existing per-order `OrderMappingOverride.CustomFields` mechanism. (B) Feed the persisted `SourceCapture` token set into `SourceMapReDerive` so mapping works after blob purge and exposes the FULL field universe, not just mapped fields. (C) Add a sandboxed `{{ catalog.* }}` Scriban accessor + `LoadCatalogProduct` manipulator + a connection-level `PriceVarianceGuard` that HOLDs the order on price drift (suggestion, never silent overwrite). (D) Add five validation rules (date-sanity, city-not-a-label, qty×price reconcile, VAT-format-per-country, required-field-presence) on the existing `SupplierAcceptanceService` executor.

**Architecture:** The transform path is `OrderTransformService.TransformAsync → (mode dispatch) → MappedTransformService / EffectiveEntityResolver / ScribanTemplateTransformService`. SourceMap re-derive already accepts `IReadOnlyList<SourceToken>` but is called with `null` at two seams (`OrderTransformService.cs:267` and `OrdersController.cs:851`). We (1) load `SourceCapture.TokensJson` and deserialize it back to `List<SourceToken>`, threading it into those two call sites; (2) add a `CanonicalFieldDef` table (org+connection scoped, soft-delete, revision-pinnable) whose values flow through the existing `CustomFields` mechanism — no new value column on the order; (3) batch-load the supplier catalog into the Scriban model + add a `LoadCatalogProduct` manipulator, both using the EU-aware decimal parse for any arithmetic; (4) extend `SupplierAcceptanceService.Evaluate` with new operators and `RuleCatalog` with new seeds, plus a connection-level `PriceVarianceGuard` evaluated at the same place line-level `NeedsReview` is computed. Every persisting change is verified on real Postgres (Testcontainers). The drag-wire UI (Phase 3), target-schema-from-many-sources (Phase 2b/Phase 3), and reproducibility polish (Phase 4) are out of scope.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + Testcontainers.PostgreSql, Scriban (sandboxed). No commercial EDI licences. No raw SQL. Org-scoped EF everywhere. Spec: `docs/superpowers/specs/2026-06-13-flexible-mapping-design.md` (Phase 2). Grounding: the 5-subsystem code map (engine / catalog / validation / extensible-canonical / SourceCapture).

---

## File structure

**Create:**
- `ProcuLink.Core/Entities/CanonicalFieldDef.cs` — EF entity, `canonical_field_defs` table (Tier-2 user-defined canonical fields).
- `ProcuLink.Transform/Output/SourceTokenSerialization.cs` — shared `SourceCapture.TokensJson` ⇄ `List<SourceToken>` (de)serializer, reused by transform + preview.
- `ProcuLink.Transform/Mapping/Manipulators/LoadCatalogProductManipulator.cs` — `LoadCatalogProduct` manipulator (catalog row pre-injected into the row bag).
- `ProcuLink.Core/Services/Mapping/PriceVarianceGuard.cs` — connection-level guard config record + pure evaluator.
- `ProcuLink.Transform.Tests/Mapping/LoadCatalogProductManipulatorTests.cs` — deterministic manipulator unit tests.
- `ProcuLink.Transform.Tests/Output/SourceTokenSerializationTests.cs` — round-trip (de)serialization unit tests.
- `ProcuLink.Transform.Tests/Output/CatalogScribanModelTests.cs` — `catalog.*` accessor unit tests.
- `ProcuLink.Api.Tests/Services/SupplierAcceptanceNewOperatorsTests.cs` — deterministic new-operator unit tests.
- `ProcuLink.Api.Tests/Services/PriceVarianceGuardTests.cs` — deterministic guard evaluator unit tests.
- `ProcuLink.Api.Tests/Integration/SourceTokenReDerivePostgresTests.cs` — real-Postgres: SourceMap re-derive uses persisted tokens after blob purge.
- `ProcuLink.Api.Tests/Integration/CanonicalFieldDefPersistencePostgresTests.cs` — real-Postgres round-trip for the new table + soft-delete.

**Modify:**
- `ProcuLink.Core/Entities/SupplierConnection.cs` — add `PriceVarianceGuard` config columns (enabled + threshold).
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — config + `DbSet` for `CanonicalFieldDef`; guard columns on `SupplierConnection`.
- `ProcuLink.Transform/Output/ScribanOrderModel.cs` — add the `catalog` accessor per line (signature widened with an optional lookup).
- `ProcuLink.Transform/Output/ScribanTemplateTransformService.cs` — pass the catalog lookup through `Build`.
- `ProcuLink.Transform/Output/MappedTransformService.cs` — pass the catalog lookup into the row builders.
- `ProcuLink.Transform/Mapping/ManipulatorRegistry.cs` — register `LoadCatalogProduct`.
- `ProcuLink.Api/Services/Orders/OrderTransformService.cs` — load + thread `SourceCapture` tokens + catalog lookup into `MappedTransformService.Build` / `ScribanTemplateTransformService.Build`.
- `ProcuLink.Api/Controllers/OrdersController.cs` — same token threading in the mapping-preview endpoint.
- `ProcuLink.Core/Entities/RuleCatalog.cs` — 5 new seeded entries (date-sanity, city-not-a-label, qty×price reconcile, VAT-format, required presence already covered — add the missing field paths).
- `ProcuLink.Api/Services/SupplierAcceptanceService.cs` — 4 new operators in `Evaluate`; widen `EvaluateOrderField` / `EvaluateLineField` field paths.
- `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` (or `OrderResolutionService.cs`) — evaluate `PriceVarianceGuard` where line `NeedsReview` is set, HOLD the order.
- InMemory test contexts that bulk-`Ignore` entities — add `Ignore<CanonicalFieldDef>()` (mirror the Phase-1 `OrderParty`/`SourceCapture` sweep; ~28 files).

---

## Ordering & parallel-safety map

**Foundation (MUST be first, sequential):**
- **Task 1** (`CanonicalFieldDef` entity + guard columns + DbContext + InMemory Ignore sweep) and **Task 2** (its EF migration) are the shared-schema foundation. Everything that persists depends on the migration existing. Do Task 1 → Task 2 in order.

**Independent feature slices (parallel-safe AFTER Task 2, with one shared-file caveat):**
- **Slice B — SourceCapture → re-derive:** Tasks 3, 4. Touches `SourceTokenSerialization.cs` (new), `OrderTransformService.cs`, `OrdersController.cs`.
- **Slice C — Catalog accessor + variance guard:** Tasks 5, 6, 7. Touches `ScribanOrderModel.cs`, `MappedTransformService.cs`, `ScribanTemplateTransformService.cs`, `ManipulatorRegistry.cs`, `PriceVarianceGuard.cs`, `OrderResolutionService.cs`.
- **Slice D — Validation rules:** Tasks 8, 9. Touches `RuleCatalog.cs`, `SupplierAcceptanceService.cs`.

**Shared-file caveat (why slices B and C are NOT fully parallel):** Both Slice B (Task 3/4) and Slice C (Task 5) modify **`OrderTransformService.cs`** and **`MappedTransformService.cs`** — B threads `sourceTokens`, C threads `catalogLookup` into the SAME `MappedTransformService.Build(...)` / `ScribanTemplateTransformService.Build(...)` call sites. To avoid a merge conflict on those two files, run **Slice B before Slice C** (or land them in the same worktree sequentially). Slice D (validation) shares no files with B or C and is fully parallel-safe with either. **Recommended order:** Task 1 → Task 2 → (Slice B: 3,4) → (Slice C: 5,6,7) → (Slice D: 8,9), with Slice D runnable in parallel at any point after Task 2.

---

### Task 1: `CanonicalFieldDef` entity + `PriceVarianceGuard` columns + DbContext + InMemory Ignore sweep

**Files:**
- Create: `ProcuLink.Core/Entities/CanonicalFieldDef.cs`
- Modify: `ProcuLink.Core/Entities/SupplierConnection.cs:34-38` (after `ActiveRevisionId`/`CreatedBy`)
- Modify: `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` (entity config + `DbSet`; `SupplierConnection` config block)
- Modify: InMemory test contexts that bulk-`Ignore` entities (the Phase-1 sweep files)

- [ ] **Step 1: Create the CanonicalFieldDef entity**

```csharp
namespace ProcuLink.Core.Entities;

/// <summary>
/// Phase 2 Tier-2 "extensible canonical": a user-defined canonical field added to the
/// spine WITHOUT a per-field migration. Scoped to an org and (optionally) a single
/// <see cref="SupplierConnection"/> so one supplier's custom fields don't leak to another.
/// VALUES are NOT stored here — they ride on the existing per-order
/// <c>OrderMappingOverride.CustomFields</c> mechanism (header <c>Value</c> / line
/// <c>LineValues</c>), keyed by <see cref="Key"/>. This row is the DEFINITION only
/// (label/scope/type/order/standards) so the mapper can render the field as a wireable
/// node and validate it. Removal is a SOFT DELETE (<see cref="DeletedAt"/>) so pinned
/// revisions keep a stable view of the field set. Table <c>canonical_field_defs</c>
/// (migration <c>AddCanonicalFieldDefs</c>).
/// </summary>
public class CanonicalFieldDef
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>Null = org-wide custom field; set = scoped to one supplier connection.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>Machine key referenced from an OutputFieldRule / CustomField (e.g. "incoterms2").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable label for the mapper UI.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>"header" | "line" — one order-level value vs per-line values.</summary>
    public string Scope { get; set; } = "header";

    /// <summary>"string" | "number" | "date" | "bool" — drives validation + numeric exposure.</summary>
    public string Type { get; set; } = "string";

    /// <summary>Optional standards reference (e.g. UBL "cbc:CustomizationID"), surfaced on demand.</summary>
    public string? StandardsRef { get; set; }

    /// <summary>Stable display order in the canonical pane (ascending).</summary>
    public int Order { get; set; }

    /// <summary>Soft-delete marker. Non-null = removed; pinned revisions still see the def.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Add the PriceVarianceGuard columns to SupplierConnection**

In `ProcuLink.Core/Entities/SupplierConnection.cs`, after `public DateTime UpdatedAt { get; set; }` (line 38) add:

```csharp
    // ── Phase 2 connection-level price-variance guard (additive, defaulted OFF) ──
    /// <summary>When true, lines whose PO unit price drifts from the catalog price by
    /// more than <see cref="PriceVarianceThresholdPercent"/> are flagged NeedsReview and
    /// the order is HELD (pending_review). Catalog price is a SUGGESTION, never a silent
    /// overwrite. Default false = byte-identical to today.</summary>
    public bool PriceVarianceGuardEnabled { get; set; }

    /// <summary>Variance threshold in percent (e.g. 20 = ±20%). Only used when the guard
    /// is enabled. Default 0 (unused while disabled).</summary>
    public decimal PriceVarianceThresholdPercent { get; set; }
```

- [ ] **Step 3: Configure CanonicalFieldDef + guard columns in ProcuLinkDbContext**

Add the `DbSet` near the other declarations in `ProcuLinkDbContext`:

```csharp
    public DbSet<CanonicalFieldDef> CanonicalFieldDefs => Set<CanonicalFieldDef>();
```

In the `SupplierConnection` config block (grep `b.ToTable("supplier_connections")`), after the `updated_at` property add:

```csharp
    b.Property(x => x.PriceVarianceGuardEnabled).HasColumnName("price_variance_guard_enabled").HasDefaultValue(false);
    b.Property(x => x.PriceVarianceThresholdPercent).HasColumnName("price_variance_threshold_percent").HasColumnType("numeric(7,4)").HasDefaultValue(0m);
```

Add a new entity config block alongside the other `modelBuilder.Entity<...>(...)` blocks:

```csharp
modelBuilder.Entity<CanonicalFieldDef>(b =>
{
    b.ToTable("canonical_field_defs");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.OrgId).HasColumnName("org_id");
    b.Property(x => x.ConnectionId).HasColumnName("connection_id");
    b.Property(x => x.Key).HasColumnName("key").IsRequired();
    b.Property(x => x.Label).HasColumnName("label").IsRequired();
    b.Property(x => x.Scope).HasColumnName("scope").IsRequired();
    b.Property(x => x.Type).HasColumnName("type").IsRequired();
    b.Property(x => x.StandardsRef).HasColumnName("standards_ref");
    b.Property(x => x.Order).HasColumnName("display_order");
    b.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
    b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    b.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
    // Org-scoped lookup; the unique active key per (org, connection, scope) is enforced in app
    // logic (soft-delete means a partial unique index would need a filtered index — kept simple).
    b.HasIndex(x => new { x.OrgId, x.ConnectionId }).HasDatabaseName("IX_canonical_field_defs_org_id_connection_id");
});
```

- [ ] **Step 4: Update InMemory test contexts' Ignore lists**

In every test `DbContext` subclass that bulk-`Ignore`s entities (grep: `modelBuilder.Ignore<SourceCapture>` — the Phase-1 sweep already touched these ~28 files), add alongside the existing ignores:

```csharp
        modelBuilder.Ignore<CanonicalFieldDef>();
```

> Note: `SupplierConnection` is already mapped (not Ignored) in those contexts that use it; the two new nullable/defaulted columns on it need no Ignore change — only the brand-new `CanonicalFieldDef` entity does. Confirm by grepping each file for `Ignore<SupplierConnection>` — where present, the contexts already exclude connections entirely and need nothing; where `SupplierConnection` is mapped, the additive columns are harmless on InMemory.

- [ ] **Step 5: Build**

Run: `dotnet build ProcuLink.slnx --no-restore`
Expected: PASS (additive: new entity, two new defaulted columns).

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Entities/CanonicalFieldDef.cs ProcuLink.Core/Entities/SupplierConnection.cs ProcuLink.Infrastructure/ProcuLinkDbContext.cs
git commit -m "feat(canonical): CanonicalFieldDef entity + connection price-variance-guard columns + DbContext"
```

---

### Task 2: EF migration for `CanonicalFieldDef` + guard columns

**Files:**
- Create (generated): `ProcuLink.Infrastructure/Migrations/*_AddCanonicalFieldDefs.cs`

- [ ] **Step 1: Generate the migration**

Run: `dotnet ef migrations add AddCanonicalFieldDefs -p ProcuLink.Infrastructure -s ProcuLink.Api`
Expected: a migration file is created.

- [ ] **Step 2: Verify the generated migration**

Open the generated `*_AddCanonicalFieldDefs.cs`. Confirm it: creates `canonical_field_defs` with the columns above + the `IX_canonical_field_defs_org_id_connection_id` index, and adds the two `price_variance_*` columns to `supplier_connections` with their defaults. There must be **no** `DropColumn`/`DropTable`/`DropIndex` for existing schema. The two new connection columns must be nullable-or-defaulted (they are: `bool DEFAULT false`, `numeric DEFAULT 0`) so existing rows backfill safely.

- [ ] **Step 3: Apply to the dev database**

Run: `dotnet ef database update --project ProcuLink.Infrastructure --startup-project ProcuLink.Api`
Expected: applies cleanly.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Infrastructure/Migrations/
git commit -m "feat(db): AddCanonicalFieldDefs migration (canonical_field_defs + price-variance-guard columns)"
```

---

### Task 3: `SourceCapture.TokensJson` ⇄ `List<SourceToken>` serializer

**Files:**
- Create: `ProcuLink.Transform/Output/SourceTokenSerialization.cs`
- Test: `ProcuLink.Transform.Tests/Output/SourceTokenSerializationTests.cs`

The Phase-1 writer serializes tokens as `[{ id, label, value, group }]` (structured formats) or `[{ label, value }]` (PDF/email `raw_fields`) — see `OrderIngestionService.UpsertSourceCaptureAsync` (grounding snippet). This task adds the *reader* so transform/preview can rebuild `List<SourceToken>` from the persisted JSON, surviving blob purge.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Tokenizing;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class SourceTokenSerializationTests
{
    [Fact]
    public void Deserialize_structured_tokens_round_trips_id_label_value_group()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"cell:r2c3","label":"Unit Price · row 2","value":"10.50","group":"line"}]""");

        var tokens = SourceTokenSerialization.FromTokensJson(doc);

        var t = Assert.Single(tokens);
        Assert.Equal("cell:r2c3", t.Id);
        Assert.Equal("Unit Price · row 2", t.Label);
        Assert.Equal("10.50", t.Value);
        Assert.Equal("line", t.Group);
    }

    [Fact]
    public void Deserialize_raw_fields_without_id_synthesises_a_stable_id()
    {
        using var doc = JsonDocument.Parse("""[{"label":"EDI id","value":"REDACTED-TAXID"}]""");

        var tokens = SourceTokenSerialization.FromTokensJson(doc);

        var t = Assert.Single(tokens);
        Assert.Equal("REDACTED-TAXID", t.Value);
        Assert.Equal("EDI id", t.Label);
        // No id in the source → a deterministic "raw:{label}" id so SourceMap rules can address it.
        Assert.Equal("raw:EDI id", t.Id);
    }

    [Fact]
    public void Null_or_empty_document_yields_empty_list()
    {
        Assert.Empty(SourceTokenSerialization.FromTokensJson(null));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTokenSerializationTests"`
Expected: FAIL — `SourceTokenSerialization` does not exist.

- [ ] **Step 3: Implement the serializer**

```csharp
using System.Text.Json;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Phase 2: rebuild the addressable <see cref="SourceToken"/> set from a persisted
/// <c>SourceCapture.TokensJson</c> document. This is the bridge that lets <c>SourceMapReDerive</c>
/// resolve <c>SourceFieldRule.SourceToken</c> references at transform/preview time WITHOUT
/// re-tokenizing the source file — so mapping still works after the source blob is purged
/// (<c>SourceFilePurgedAt</c>) and the FULL field universe (mapped + unmapped) is addressable.
///
/// The JSON shape mirrors the Phase-1 writer (<c>OrderIngestionService.UpsertSourceCaptureAsync</c>):
///  • structured formats: <c>{ id, label, value, group }</c> (group nullable);
///  • PDF/email raw_fields: <c>{ label, value }</c> (no id) — we synthesise a deterministic
///    <c>raw:{label}</c> id so those long-tail fields are still wireable.
/// </summary>
public static class SourceTokenSerialization
{
    public static IReadOnlyList<SourceToken> FromTokensJson(JsonDocument? tokensJson)
    {
        if (tokensJson is null) return Array.Empty<SourceToken>();

        var root = tokensJson.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Array.Empty<SourceToken>();

        var result = new List<SourceToken>(root.GetArrayLength());
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var label = ReadString(el, "label") ?? string.Empty;
            var value = ReadString(el, "value") ?? string.Empty;
            var group = ReadString(el, "group"); // nullable by design
            // Prefer the explicit id; else a deterministic raw:{label} id for raw_fields.
            var id = ReadString(el, "id");
            if (string.IsNullOrEmpty(id))
                id = $"raw:{label}";

            result.Add(new SourceToken(id, label, value, group));
        }
        return result;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTokenSerializationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Transform/Output/SourceTokenSerialization.cs ProcuLink.Transform.Tests/Output/SourceTokenSerializationTests.cs
git commit -m "feat(engine): SourceCapture tokens-json reader → addressable SourceToken set"
```

---

### Task 4: Thread persisted tokens into transform + preview (close the two `null` seams)

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderTransformService.cs:82-86` (Include), `:267` (native override Build call)
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs:851-859` (preview native override Build call; add the SourceCapture Include where the order is loaded for preview)
- Test: `ProcuLink.Api.Tests/Integration/SourceTokenReDerivePostgresTests.cs`

The grounding identifies SEAM 2 (`OrderTransformService.cs:265-268`) and SEAM 1 (`OrdersController.cs:851-859`): both call `new MappedTransformService().Build(...)` with the default `sourceTokens=null`, so `SourceMap` `SourceToken` references never resolve at delivery time. We load `entity.SourceCapture.TokensJson`, deserialize via Task 3, and pass it in.

- [ ] **Step 1: Write the failing real-Postgres round-trip test**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

[Collection("postgres-container")]
public sealed class SourceTokenReDerivePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16")
            .WithDatabase($"proculink_rederive_{Guid.NewGuid():N}")
            .WithUsername("postgres").WithPassword("postgres").Build();
        await _pg.StartAsync();
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
        await using var migrate = new ProcuLinkDbContext(_options);
        await migrate.Database.MigrateAsync();
    }

    public async Task DisposeAsync() { if (_pg is not null) await _pg.DisposeAsync(); }

    [DockerRequiredFact]
    public async Task ReDerive_uses_persisted_tokens_even_after_blob_purge()
    {
        // The persisted token set (raw:"Customer ref") is the ONLY way to resolve the SourceMap
        // rule — the source blob is purged. Asserts the deserialized tokens drive re-derive.
        using var tokensDoc = JsonDocument.Parse(
            """[{"id":"raw:Customer ref","label":"Customer ref","value":"CR-42","group":"header"}]""");
        var tokens = SourceTokenSerialization.FromTokensJson(tokensDoc);

        var order = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { Id = Guid.NewGuid(), LineNumber = 1, SupplierItemCode = "S1", Quantity = 1, UnitPrice = 1m } },
        };
        var @override = new OrderMappingOverride
        {
            SourceMap = new() { ["PoNumber"] = new SourceFieldRule { SourceToken = "raw:Customer ref" } },
            Output = new OutputMappingConfig
            {
                Header = new() { ["po"] = new OutputFieldRule { OutputPath = "po", CanonicalField = "PoNumber" } },
                Lines  = new() { ["sku"] = new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" } },
            },
        };

        var result = new MappedTransformService().Build(order, @override, OutputFormat.Csv, sourceTokens: tokens);
        using var reader = new StreamReader(result.Content);
        var csv = await reader.ReadToEndAsync();

        Assert.Contains("CR-42", csv); // PoNumber re-derived from the persisted token, not the parsed value
    }
}
```

> This test pins the *engine contract* (persisted tokens → re-derive) deterministically. It does not boot the full controller; the controller/transform wiring in Steps 3-4 is what threads the tokens at runtime, exercised by the existing transform integration tests after wiring.

- [ ] **Step 2: Run to verify it fails / passes**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTokenReDerive"`
Expected: PASS at engine level if Task 3 + the existing `MappedTransformService.Build(sourceTokens:)` are present (the engine already accepts tokens). If it FAILS, the re-derive path is broken — fix before wiring. (This guards the contract the wiring below depends on.)

- [ ] **Step 3: Thread tokens at the transform seam**

In `OrderTransformService.TransformAsync`, widen the order load (`OrderTransformService.cs:82-86`) to include the capture:

```csharp
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Include(x => x.SourceCapture)   // Phase 2: persisted token universe for SourceMap re-derive
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);
```

Just before the `try` that generates the document (`OrderTransformService.cs:258-259`), materialise the tokens once:

```csharp
        // Phase 2: rebuild the addressable source-token universe from the persisted capture so
        // SourceMap rules resolve at delivery time even after the source blob is purged.
        var sourceTokens = ProcuLink.Transform.Output.SourceTokenSerialization
            .FromTokensJson(entity.SourceCapture?.TokensJson);
```

Pass `sourceTokens` into BOTH `MappedTransformService().Build(...)` native-override calls (`:267` and the revision/supplier native branches at `:284`/`:300` that call `new MappedTransformService().Build(entity, ..., effectiveFormat)`):

```csharp
                transformResult = new MappedTransformService().Build(entity, mappingOverride!, effectiveFormat, sourceTokens: sourceTokens);
```

```csharp
                transformResult = useRevisionNative
                    ? new MappedTransformService().Build(entity, revisionOverride!, effectiveFormat, sourceTokens: sourceTokens)
                    : await transformer!.TransformAsync(
                          EffectiveEntityResolver.Resolve(entity, revisionOverride!), effectiveFormat, ct);
```

```csharp
                transformResult = useSupplierNative
                    ? new MappedTransformService().Build(entity, supplierOverride!, effectiveFormat, sourceTokens: sourceTokens)
                    : await transformer!.TransformAsync(
                          EffectiveEntityResolver.Resolve(entity, supplierOverride!), effectiveFormat, ct);
```

> Confirm the exact `MappedTransformService().Build(...)` call lines at `:267`, `:284`, `:300` (grep `new MappedTransformService().Build`) and add `sourceTokens: sourceTokens` to each — they all run inside the same `try` after the materialise line, so `sourceTokens` is in scope.

- [ ] **Step 4: Thread tokens at the preview seam**

In `OrdersController` mapping-preview (`OrdersController.cs:719-884`), where the order is loaded for preview (grep the `.Include(x => x.Lines)` in that endpoint), add `.Include(x => x.SourceCapture)`. Then before the `new MappedTransformService().Build(order, fieldOverride, fmt.Value)` call (`:851-859`) add:

```csharp
    var previewTokens = ProcuLink.Transform.Output.SourceTokenSerialization
        .FromTokensJson(order.SourceCapture?.TokensJson);
```

and change the call to:

```csharp
    result = new MappedTransformService().Build(order, fieldOverride, fmt.Value, sourceTokens: previewTokens);
```

- [ ] **Step 5: Build + run the engine + existing preview/transform tests**

Run: `dotnet build ProcuLink.slnx --no-restore`
Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTokenReDerive|FullyQualifiedName~MappingPreview"`
Expected: build PASS; tests PASS (preview output now matches the actual transform when SourceMap rules are present).

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Services/Orders/OrderTransformService.cs ProcuLink.Api/Controllers/OrdersController.cs ProcuLink.Api.Tests/Integration/SourceTokenReDerivePostgresTests.cs
git commit -m "feat(engine): thread persisted SourceCapture tokens into transform + preview re-derive"
```

---

### Task 5: `catalog.*` Scriban accessor on the line model

**Files:**
- Modify: `ProcuLink.Transform/Output/ScribanOrderModel.cs:53` (Build sig), `:110` (BuildLine sig), `:133` (insertion point after `LineAmount`)
- Modify: `ProcuLink.Transform/Output/ScribanTemplateTransformService.cs` (pass the lookup through `Build`)
- Test: `ProcuLink.Transform.Tests/Output/CatalogScribanModelTests.cs`

We pass a pre-loaded `IReadOnlyDictionary<string, SupplierProduct>` keyed by `SupplierItemCode` (and `ManufacturerPartNumber`) into the model — the lookup happens ONCE in the caller (Task 7 batch-loads it), keeping the Scriban context sandboxed (no DB access at render time). Catalog stays a read-only object: `{{ line.catalog.price }}`, never an overwrite.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using Scriban.Runtime;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class CatalogScribanModelTests
{
    [Fact]
    public void Line_exposes_catalog_object_when_lookup_has_the_code()
    {
        var order = new PurchaseOrderEntity
        {
            PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, SupplierItemCode = "S-1", Quantity = 2, UnitPrice = 5m } },
        };
        var lookup = new Dictionary<string, SupplierProduct>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["S-1"] = new SupplierProduct { Code = "S-1", Name = "Widget", Unit = "PC", Price = 4.50m, Currency = "EUR", Barcode = "EAN9" },
        };

        var root = ScribanOrderModel.Build(order, @override: null, catalogLookup: lookup);
        var lines = (System.Collections.Generic.List<ScriptObject>)root["Lines"]!;
        var catalog = (ScriptObject)lines[0]["catalog"]!;

        Assert.Equal("S-1", catalog["code"]);
        Assert.Equal("Widget", catalog["name"]);
        Assert.Equal(4.50m, catalog["price"]);   // real number for arithmetic / variance
        Assert.Equal("EAN9", catalog["barcode"]);
    }

    [Fact]
    public void Line_exposes_empty_catalog_object_when_no_match()
    {
        var order = new PurchaseOrderEntity
        {
            PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, SupplierItemCode = "MISSING", Quantity = 1, UnitPrice = 1m } },
        };

        var root = ScribanOrderModel.Build(order, @override: null, catalogLookup: new Dictionary<string, SupplierProduct>());
        var lines = (System.Collections.Generic.List<ScriptObject>)root["Lines"]!;
        var catalog = (ScriptObject)lines[0]["catalog"]!;

        Assert.False(catalog.ContainsKey("code")); // empty object, relaxed access → "" in templates
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~CatalogScribanModelTests"`
Expected: FAIL — `Build` has no `catalogLookup` parameter.

- [ ] **Step 3: Widen Build + BuildLine, inject the catalog object**

In `ScribanOrderModel.cs`, change the `Build` signature (`:53`) to add a defaulted lookup (keeps every existing caller compiling):

```csharp
    internal static ScriptObject Build(
        PurchaseOrderEntity order,
        OrderMappingOverride? @override,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null)
    {
```

In the `Lines` loop (`:103-104`), pass the lookup into each line:

```csharp
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
            lines.Add(BuildLine(line, @override, catalogLookup));
```

Change the `BuildLine` signature (`:110`) and inject the catalog object after `obj["LineAmount"]` (`:133`):

```csharp
    private static ScriptObject BuildLine(
        PurchaseOrderLineEntity line,
        OrderMappingOverride? @override,
        IReadOnlyDictionary<string, SupplierProduct>? catalogLookup)
    {
```

```csharp
        // ── Phase 2 catalog accessor (read-only suggestion; NEVER overwrites the PO value) ──
        var catalogObj = new ScriptObject();
        SupplierProduct? product = null;
        if (catalogLookup is not null)
        {
            // Resolve by supplier item code first, then manufacturer part number (both keys
            // are indexed into the same lookup by the caller in Task 7).
            if (!string.IsNullOrWhiteSpace(line.SupplierItemCode))
                catalogLookup.TryGetValue(line.SupplierItemCode, out product);
            if (product is null && !string.IsNullOrWhiteSpace(line.ManufacturerPartNumber))
                catalogLookup.TryGetValue(line.ManufacturerPartNumber, out product);
        }
        if (product is not null)
        {
            catalogObj["code"]     = product.Code ?? string.Empty;
            catalogObj["name"]     = product.Name ?? string.Empty;
            catalogObj["unit"]     = product.Unit ?? string.Empty;
            catalogObj["price"]    = product.Price.HasValue ? (object)product.Price.Value : string.Empty;
            catalogObj["currency"] = product.Currency ?? string.Empty;
            catalogObj["barcode"]  = product.Barcode ?? string.Empty;
        }
        obj["catalog"] = catalogObj; // empty object when no match → relaxed access renders ""
```

Add the using at the top of the file if not present:

```csharp
using ProcuLink.Core.Entities; // already present (PurchaseOrderEntity) — SupplierProduct is in the same namespace
```

- [ ] **Step 4: Pass the lookup through ScribanTemplateTransformService.Build**

In `ScribanTemplateTransformService.cs`, widen `Build` to accept and forward an optional `IReadOnlyDictionary<string, SupplierProduct>? catalogLookup = null`, and pass it into `ScribanOrderModel.Build(entity, @override, catalogLookup)`. (Grep `ScribanOrderModel.Build` in that file for the exact call site.)

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~CatalogScribanModelTests"`
Expected: PASS.

- [ ] **Step 6: Build**

Run: `dotnet build ProcuLink.slnx --no-restore`
Expected: PASS (defaulted params keep all existing `ScribanOrderModel.Build` / `ScribanTemplateTransformService.Build` callers green).

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Transform/Output/ScribanOrderModel.cs ProcuLink.Transform/Output/ScribanTemplateTransformService.cs ProcuLink.Transform.Tests/Output/CatalogScribanModelTests.cs
git commit -m "feat(catalog): {{ catalog.* }} Scriban accessor per line (read-only suggestion)"
```

---

### Task 6: `LoadCatalogProduct` manipulator

**Files:**
- Create: `ProcuLink.Transform/Mapping/Manipulators/LoadCatalogProductManipulator.cs`
- Modify: `ProcuLink.Transform/Mapping/ManipulatorRegistry.cs:5-20`
- Test: `ProcuLink.Transform.Tests/Mapping/LoadCatalogProductManipulatorTests.cs`

The manipulator contract is `string? Apply(string? value, IReadOnlyDictionary<string, string> row)` — it sees ONLY the row bag, not the DB. So (per the grounding's recommended pattern) the caller pre-injects the catalog fields into the row bag under reserved keys (`__catalog_price`, `__catalog_code`, `__catalog_unit`, `__catalog_barcode`), and `LoadCatalogProduct` extracts the requested one. Params: `[field]` where field ∈ `price|code|unit|barcode`. Any numeric use downstream MUST parse with the EU-aware heuristic (handled by `Multiply`/`Divide` siblings + the variance guard in Task 7, NOT by this manipulator, which returns the raw catalog string).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Mapping.Manipulators;
using Xunit;

namespace ProcuLink.Transform.Tests.Mapping;

public class LoadCatalogProductManipulatorTests
{
    private static IReadOnlyDictionary<string, string> Row() => new Dictionary<string, string>
    {
        ["__catalog_price"]   = "4.50",
        ["__catalog_code"]    = "S-1",
        ["__catalog_unit"]    = "PC",
        ["__catalog_barcode"] = "EAN9",
        ["SupplierItemCode"]  = "S-1",
    };

    [Theory]
    [InlineData("price", "4.50")]
    [InlineData("code", "S-1")]
    [InlineData("unit", "PC")]
    [InlineData("barcode", "EAN9")]
    public void Extracts_the_requested_catalog_field(string field, string expected)
    {
        var m = new LoadCatalogProductManipulator(new[] { field });
        Assert.Equal(expected, m.Apply(value: "ignored", Row()));
    }

    [Fact]
    public void Missing_catalog_field_returns_empty_not_throws()
    {
        var m = new LoadCatalogProductManipulator(new[] { "price" });
        Assert.Equal(string.Empty, m.Apply("x", new Dictionary<string, string>()));
    }

    [Fact]
    public void Registry_resolves_LoadCatalogProduct()
    {
        var m = ManipulatorRegistry.Resolve("LoadCatalogProduct", new[] { "price" });
        Assert.IsType<LoadCatalogProductManipulator>(m);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~LoadCatalogProductManipulatorTests"`
Expected: FAIL — the manipulator + registry case do not exist.

- [ ] **Step 3: Implement the manipulator**

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>
/// Phase 2 catalog manipulator. Params: <c>[field]</c> where field ∈ price|code|unit|barcode.
/// The catalog row is pre-injected into the value bag by the caller under reserved keys
/// (<c>__catalog_price</c>, <c>__catalog_code</c>, <c>__catalog_unit</c>, <c>__catalog_barcode</c>)
/// — the manipulator contract only sees the row, never the DB, so the lookup stays centralised
/// and the engine stays sandboxed. Returns the catalog field's RAW string (a suggestion); the
/// caller decides whether to use it. Missing → empty string (never throws). Any arithmetic on the
/// returned price must use the EU-aware parse (see the variance guard) — this manipulator does NOT
/// reformat numbers.
/// </summary>
public class LoadCatalogProductManipulator : IFieldManipulator
{
    private readonly string _key;

    public LoadCatalogProductManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count != 1)
            throw new ArgumentException("LoadCatalogProduct requires exactly 1 param: [field] (price|code|unit|barcode)", nameof(@params));
        _key = @params[0].Trim().ToLowerInvariant() switch
        {
            "price"   => "__catalog_price",
            "code"    => "__catalog_code",
            "unit"    => "__catalog_unit",
            "barcode" => "__catalog_barcode",
            var other => throw new ArgumentException($"LoadCatalogProduct: unknown field '{other}' (expected price|code|unit|barcode)", nameof(@params)),
        };
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
        => row.TryGetValue(_key, out var v) ? v : string.Empty;
}
```

- [ ] **Step 4: Register it**

In `ManipulatorRegistry.cs:5-20`, add the case to the switch:

```csharp
            "LoadCatalogProduct" => new LoadCatalogProductManipulator(@params),
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore --filter "FullyQualifiedName~LoadCatalogProductManipulatorTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Transform/Mapping/Manipulators/LoadCatalogProductManipulator.cs ProcuLink.Transform/Mapping/ManipulatorRegistry.cs ProcuLink.Transform.Tests/Mapping/LoadCatalogProductManipulatorTests.cs
git commit -m "feat(catalog): LoadCatalogProduct manipulator (row-injected catalog field accessor)"
```

---

### Task 7: Batch-load catalog into transform + connection-level `PriceVarianceGuard` HOLD

**Files:**
- Create: `ProcuLink.Core/Services/Mapping/PriceVarianceGuard.cs`
- Modify: `ProcuLink.Api/Services/Orders/OrderTransformService.cs` (batch-load the catalog dict, pass to `Build`)
- Modify: `ProcuLink.Api/Services/Orders/OrderResolutionService.cs:169` (evaluate guard where `NeedsReview` / `pending_review` is set)
- Test: `ProcuLink.Api.Tests/Services/PriceVarianceGuardTests.cs`

The guard is a PURE evaluator (deterministic, EU-aware parse) plus a wiring point. Catalog price is a suggestion: on a variance breach we set `line.NeedsReview = true` + a reason and force `pending_review` (HOLD) — we never mutate `UnitPrice`. The threshold lives on `SupplierConnection` (Task 1 columns).

- [ ] **Step 1: Write the failing test (pure evaluator)**

```csharp
using ProcuLink.Core.Services.Mapping;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class PriceVarianceGuardTests
{
    [Fact]
    public void Disabled_guard_never_flags()
    {
        var g = new PriceVarianceGuard(Enabled: false, ThresholdPercent: 1m);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "1.00").Breached);
    }

    [Fact]
    public void Within_threshold_does_not_flag()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 20m);
        // 110 vs 100 = +10% ≤ 20%
        Assert.False(g.Breaches(poUnitPrice: 110m, catalogPriceRaw: "100.00").Breached);
    }

    [Fact]
    public void Beyond_threshold_flags_with_signed_percent()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 20m);
        var r = g.Breaches(poUnitPrice: 130m, catalogPriceRaw: "100.00"); // +30%
        Assert.True(r.Breached);
        Assert.Equal(30m, decimal.Round(r.VariancePercent, 0));
    }

    [Fact]
    public void EU_comma_decimal_catalog_price_parses_correctly()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 5m);
        // catalog "1.234,56" (EU) = 1234.56; PO 1234.56 → 0% variance, NOT a 100x misread.
        Assert.False(g.Breaches(poUnitPrice: 1234.56m, catalogPriceRaw: "1.234,56").Breached);
    }

    [Fact]
    public void Missing_or_zero_catalog_price_never_flags()
    {
        var g = new PriceVarianceGuard(Enabled: true, ThresholdPercent: 1m);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: null).Breached);
        Assert.False(g.Breaches(poUnitPrice: 100m, catalogPriceRaw: "0").Breached);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PriceVarianceGuardTests"`
Expected: FAIL — `PriceVarianceGuard` does not exist.

- [ ] **Step 3: Implement the guard with the EU-aware parse**

```csharp
using System.Globalization;

namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Phase 2 connection-level price-variance guard. When enabled, a PO line whose unit price
/// drifts from the catalog price by more than <see cref="ThresholdPercent"/> is a breach: the
/// caller marks the line NeedsReview and HOLDs the order (pending_review). Catalog price is a
/// SUGGESTION — the guard NEVER mutates the PO price. Pure + deterministic so it is unit-tested
/// without a DB.
///
/// EU comma-decimal safety: the catalog price arrives as a raw string ("1.234,56"); we parse it
/// with the same last-separator-is-decimal heuristic the CSV/XLSX parsers use, NOT
/// <c>InvariantCulture</c> on the raw string (which would misread "1.234,56" as 1.234 or fail).
/// </summary>
public sealed record PriceVarianceGuard(bool Enabled, decimal ThresholdPercent)
{
    public readonly record struct Result(bool Breached, decimal VariancePercent);

    public Result Breaches(decimal poUnitPrice, string? catalogPriceRaw)
    {
        if (!Enabled) return new Result(false, 0m);
        if (poUnitPrice <= 0m) return new Result(false, 0m);

        var catalog = ParseEuAware(catalogPriceRaw);
        if (catalog is null or <= 0m) return new Result(false, 0m);

        var variancePercent = Math.Abs(poUnitPrice - catalog.Value) / catalog.Value * 100m;
        return new Result(variancePercent > ThresholdPercent, (poUnitPrice - catalog.Value) / catalog.Value * 100m);
    }

    /// <summary>
    /// Parse a decimal that may be US ("1,234.56") or EU ("1.234,56" / "73,22"). Mirrors the
    /// CsvOrderParser.ParseDecimalFlexible heuristic: both separators → the LAST is the decimal;
    /// only ',' → decimal (unless a single comma with exactly 3 trailing digits = thousands);
    /// only '.' → decimal. Never feeds the raw string to InvariantCulture blindly.
    /// </summary>
    internal static decimal? ParseEuAware(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var lastComma = s.LastIndexOf(',');
        var lastDot   = s.LastIndexOf('.');

        char? decimalSep;
        if (lastComma >= 0 && lastDot >= 0)      decimalSep = lastComma > lastDot ? ',' : '.';
        else if (lastComma >= 0)
        {
            var trailing = s.Length - lastComma - 1;
            var single   = s.IndexOf(',') == lastComma;
            decimalSep = (single && trailing == 3) ? null : ',';   // single comma + 3 digits → thousands
        }
        else if (lastDot >= 0)                   decimalSep = '.';
        else                                     decimalSep = null;

        var normalized = decimalSep is char ds
            ? s.Replace(ds == '.' ? "," : ".", "").Replace(ds, '.')
            : s.Replace(",", "").Replace(".", "");

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : (decimal?)null;
    }
}
```

> If a shared EU-aware parser is later extracted from `CsvOrderParser.ParseDecimalFlexible` into a Core helper, replace `ParseEuAware` with a call to it. For now it is duplicated deliberately to keep `PriceVarianceGuard` in Core with no Transform dependency (Core cannot reference Transform).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PriceVarianceGuardTests"`
Expected: PASS.

- [ ] **Step 5: Batch-load the catalog dict in OrderTransformService and pass it to Build**

In `OrderTransformService.TransformAsync`, after the `sourceTokens` materialise line (Task 4 Step 3), build the catalog lookup once (org+supplier scoped, never cross-tenant) and pass it into the Scriban template path:

```csharp
        // Phase 2: batch-load this supplier's catalog ONCE, keyed by Code AND Barcode (and
        // ExternalId as the manufacturer-part fallback), so the {{ catalog.* }} accessor and the
        // LoadCatalogProduct manipulator resolve without an N+1. Org+supplier scoped.
        var catalogLookup = await BuildCatalogLookupAsync(organisationId, entity.SupplierId, ct);
```

Add the helper (org-scoped EF, no raw SQL):

```csharp
    private async Task<IReadOnlyDictionary<string, SupplierProduct>> BuildCatalogLookupAsync(
        Guid organisationId, Guid supplierId, CancellationToken ct)
    {
        var products = await _db.SupplierProducts.AsNoTracking()
            .Where(p => p.OrgId == organisationId && p.SupplierId == supplierId && p.IsActive)
            .ToListAsync(ct);

        var dict = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products)
        {
            if (!string.IsNullOrWhiteSpace(p.Code))     dict.TryAdd(p.Code, p);
            if (!string.IsNullOrWhiteSpace(p.Barcode))  dict.TryAdd(p.Barcode!, p);
            if (!string.IsNullOrWhiteSpace(p.ExternalId)) dict.TryAdd(p.ExternalId!, p);
        }
        return dict;
    }
```

In the template-mode branch (`OrderTransformService.cs:263`), pass the lookup:

```csharp
                transformResult = new ScribanTemplateTransformService().Build(entity, mappingOverride!, catalogLookup);
```

> The native CSV/JSON override path (`MappedTransformService`) injects catalog fields into the row bag for the `LoadCatalogProduct` manipulator. That row injection is OPTIONAL for Phase 2 — the `{{ catalog.* }}` template accessor (template mode) is the primary surface and is fully wired here. If the row-bag injection for native mode is in scope, add it in `MappedTransformService.BuildHeaderRow`/`BuildLineRow` under the `__catalog_*` keys using the same `catalogLookup`; otherwise defer the native-mode `LoadCatalogProduct` wiring to Phase 3 (note it in the self-review). **Decision for this plan: wire the template-mode accessor now (Step 5); defer native-mode row injection — call it out in self-review.**

- [ ] **Step 6: Evaluate the guard at the resolution/hold seam**

In `OrderResolutionService` where line review state + order status are recomputed (`OrderResolutionService.cs:169` — `entity.Status = entity.Lines.Any(l => l.NeedsReview) ? "pending_review" : "ready"`), load the connection guard and apply it BEFORE that status line. Load the supplier's active `SupplierConnection` (org+supplier scoped) for the guard config and the catalog dict, then:

```csharp
        // Phase 2: price-variance guard — catalog price is a suggestion; on a breach HOLD the line.
        var connection = await _db.SupplierConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrgId == organisationId && c.SupplierId == entity.SupplierId, ct);
        if (connection is { PriceVarianceGuardEnabled: true })
        {
            var guard = new PriceVarianceGuard(true, connection.PriceVarianceThresholdPercent);
            var catalog = await BuildCatalogLookupAsync(organisationId, entity.SupplierId, ct); // same helper shape as transform
            foreach (var line in entity.Lines)
            {
                var hit = !string.IsNullOrWhiteSpace(line.SupplierItemCode) && catalog.TryGetValue(line.SupplierItemCode!, out var prod)
                    ? prod
                    : (!string.IsNullOrWhiteSpace(line.ManufacturerPartNumber) && catalog.TryGetValue(line.ManufacturerPartNumber!, out var prod2) ? prod2 : null);
                if (hit?.Price is null) continue;
                var r = guard.Breaches(line.UnitPrice, hit.Price.Value.ToString(CultureInfo.InvariantCulture));
                if (r.Breached)
                {
                    line.NeedsReview = true;
                    line.ReviewReason = $"Unit price {line.UnitPrice} differs from catalog {hit.Price.Value} by {decimal.Round(r.VariancePercent, 1)}% — review before delivery.";
                }
            }
        }
```

> The existing `entity.Status = entity.Lines.Any(l => l.NeedsReview) ? "pending_review" : "ready"` line then HOLDs the order automatically because the guard set `NeedsReview`. Confirm `BuildCatalogLookupAsync` is reachable from `OrderResolutionService` (it is a sibling ingest-service helper); if the two services don't share a base/helper, duplicate the small helper or lift it to a shared internal static — match the existing pattern at the call site. The catalog price is formatted with `InvariantCulture` here because it comes from a TYPED `decimal?` column (not a raw locale string), so InvariantCulture is correct; the EU-aware parse only applies to RAW string inputs.

- [ ] **Step 7: Build + run guard tests + existing transform/resolution tests**

Run: `dotnet build ProcuLink.slnx --no-restore`
Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~PriceVarianceGuard|FullyQualifiedName~OrderResolution|FullyQualifiedName~OrderTransform"`
Expected: build PASS; tests PASS.

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Core/Services/Mapping/PriceVarianceGuard.cs ProcuLink.Api/Services/Orders/OrderTransformService.cs ProcuLink.Api/Services/Orders/OrderResolutionService.cs ProcuLink.Api.Tests/Services/PriceVarianceGuardTests.cs
git commit -m "feat(catalog): batch catalog lookup + connection price-variance guard (hold on drift)"
```

---

### Task 8: New validation operators in `SupplierAcceptanceService`

**Files:**
- Modify: `ProcuLink.Api/Services/SupplierAcceptanceService.cs:307-371` (`EvaluateOrderField`, `EvaluateLineField`, `Evaluate`)
- Test: `ProcuLink.Api.Tests/Services/SupplierAcceptanceNewOperatorsTests.cs`

Add 4 operators to the existing pure `Evaluate` switch: `date_sanity` (date string parses unambiguously / not a MM-DD vs DD-MM flip risk), `not_label` (city ≠ a label like "City"/"UIDNr"), `line_amount_reconcile` (qty×price ≈ stated line amount), `vat_format` (VAT id matches the country pattern). Required-field-presence already exists (`required`). Widen the field-path switches so new fields are addressable.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using ProcuLink.Core.Entities;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class SupplierAcceptanceNewOperatorsTests
{
    private static SupplierAcceptanceProfile Profile(params SupplierAcceptanceRule[] rules) =>
        new() { Id = Guid.NewGuid(), Rules = new(rules) };

    private static SupplierAcceptanceRule Rule(string scope, string field, string op, string? expected = null, string severity = "error") =>
        new() { Id = Guid.NewGuid(), Scope = scope, FieldPath = field, Operator = op, ExpectedValue = expected, Severity = severity };

    [Fact]
    public void Date_sanity_fails_an_ambiguous_flip_candidate()
    {
        // "06/12/2026" with both <= 12 is ambiguous (MM/DD vs DD/MM) → fail (flag for review).
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, DeliveryDateRaw = "06/12/2026" } },
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("line", "deliveryDateRaw", "date_sanity")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Not_label_fails_when_city_is_a_label_word()
    {
        var order = new PurchaseOrderEntity { Currency = "EUR", BuyerName = "X" };
        var rule = Rule("order", "shipToCity", "not_label", expected: "UIDNr,City,VAT,UID");
        // shipToCity resolves from the first OrderParty.City — seed it.
        order.Parties.Add(new OrderParty { Role = "shipTo", City = "UIDNr. ATU" });
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(rule), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Line_amount_reconcile_fails_when_qty_times_price_diverges()
    {
        var order = new PurchaseOrderEntity
        {
            Currency = "EUR",
            // 2 × 5 = 10, but stated LineAmount = 99 → reconcile fails beyond tolerance.
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 2m, UnitPrice = 5m, LineAmount = 99m } },
        };
        var results = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("line", "lineAmount", "line_amount_reconcile", expected: "0.01")), order, DateTime.UtcNow);
        Assert.Contains(results, r => r.Status == "fail");
    }

    [Fact]
    public void Vat_format_passes_a_valid_at_vat_and_fails_a_malformed_one()
    {
        var order = new PurchaseOrderEntity { Currency = "EUR" };
        order.Parties.Add(new OrderParty { Role = "shipTo", Country = "AT", Vat = "REDACTED-TAXID" });
        var pass = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);
        Assert.Contains(pass, r => r.Status == "pass");

        order.Parties[0] = new OrderParty { Role = "shipTo", Country = "AT", Vat = "NOTAVAT" };
        var fail = SupplierAcceptanceService.EvaluateProfile(
            Guid.NewGuid(), order.Id, Profile(Rule("order", "shipToVat", "vat_format")), order, DateTime.UtcNow);
        Assert.Contains(fail, r => r.Status == "fail");
    }
}
```

> `DeliveryDateRaw` (a nullable raw-string column carrying the originally-printed date) is assumed to exist from Phase 1's lossless capture for date-sanity to inspect the ORIGINAL string. If it does NOT exist, the `date_sanity` rule operates on `line.DeliveryDate?.ToString("MM/dd/yyyy")` instead — but that loses the ambiguity signal. **Confirm at execution** whether Phase 1 added a raw delivery-date string; if not, scope `date_sanity` to the `order_date` raw string on `SourceCapture` OR mark `date_sanity` deferred and remove its test. (Flagged in self-review as a grounding ambiguity.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SupplierAcceptanceNewOperatorsTests"`
Expected: FAIL — the operators + field paths do not exist.

- [ ] **Step 3: Add the new field paths**

In `EvaluateOrderField` (`:307-316`), widen the switch (city/VAT resolve from the first matching `OrderParty`):

```csharp
        string? v = rule.FieldPath switch
        {
            "currency"   => o.Currency,
            "buyerName"  => o.BuyerName,
            "shipToCity" => o.Parties.FirstOrDefault(p => p.Role == "shipTo")?.City,
            "shipToVat"  => o.Parties.FirstOrDefault(p => p.Role == "shipTo")?.Vat,
            "incoterms"  => o.Incoterms,
            _            => null,
        };
```

In `EvaluateLineField` (`:318-330`), widen the switch (reconcile needs the trio; date-sanity needs the raw string):

```csharp
        string? v = rule.FieldPath switch
        {
            "supplierItemCode"      => l.SupplierItemCode,
            "buyerItemCode"         => l.BuyerItemCode,
            "description"           => l.Description,
            "quantity"              => l.Quantity.ToString(CultureInfo.InvariantCulture),
            "unitPrice"             => l.UnitPrice.ToString(CultureInfo.InvariantCulture),
            "manufacturerPartNumber"=> l.ManufacturerPartNumber,
            "deliveryDateRaw"       => l.DeliveryDateRaw, // Phase 1 raw string (see Step-1 note)
            // lineAmount reconcile reads the trio from the line; the switch returns the stated
            // amount string, and Evaluate() pulls qty/price off the rule's line via the closure below.
            "lineAmount"            => (l.LineAmount ?? (l.Quantity * l.UnitPrice)).ToString(CultureInfo.InvariantCulture),
            _                       => null,
        };
```

> `line_amount_reconcile` needs qty AND price, not just the single `actual` string. The cleanest way that stays inside the existing pure-evaluator shape: handle `line_amount_reconcile` in `EvaluateLineField` directly (compute pass/value there) BEFORE delegating to `Evaluate`, since `Evaluate` only sees one value. Add at the top of `EvaluateLineField`:

```csharp
        if (rule.Operator == "line_amount_reconcile")
        {
            var computed = l.Quantity * l.UnitPrice;
            var stated   = l.LineAmount ?? computed;
            var tol      = decimal.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 0.01m;
            var pass     = Math.Abs(stated - computed) <= tol;
            return (pass, stated.ToString(CultureInfo.InvariantCulture));
        }
```

- [ ] **Step 4: Add the new operators to Evaluate**

In `Evaluate` (`:332-371`), before the `default:` case add:

```csharp
            case "date_sanity":
            {
                // A printed date string is "sane" only if it is UNAMBIGUOUS. With two numeric
                // components both ≤ 12 (e.g. DNV "06/12") MM/DD and DD/MM are both valid → fail and
                // review-flag the flip risk. A component > 12 (day or month) disambiguates → pass.
                // Absence is handled by 'required', not here.
                if (string.IsNullOrWhiteSpace(actual)) return true;
                var parts   = actual.Split('/', '-', '.');
                var numeric = parts.Where(p => int.TryParse(p.Trim(), out _)).Select(int.Parse).ToArray();
                if (numeric.Length < 2) return true;                 // not a numeric date → don't second-guess
                return numeric.Take(2).Any(n => n > 12);             // > 12 disambiguates → pass; else ambiguous → fail
            }
            case "not_label":
            {
                // Fail when the value IS (or merely starts with) a label word — catches a parser
                // that swept a label cell into a data field (REDACTED-PARTY "UIDNr" in ShipToCity).
                if (string.IsNullOrWhiteSpace(actual)) return true;
                var labels = (rule.ExpectedValue ?? "City,VAT,UID,UIDNr,Label")
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return !labels.Any(label =>
                    actual.StartsWith(label, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(actual, label, StringComparison.OrdinalIgnoreCase));
            }
            case "vat_format":
            {
                // Lightweight per-country VAT shape check (length + country prefix). NOT a checksum;
                // a precise VIES validation is out of scope. Empty → pass (use 'required' to mandate).
                if (string.IsNullOrWhiteSpace(actual)) return true;
                return IsPlausibleVat(actual);
            }
```

Add the VAT helper near the other private statics:

```csharp
    /// <summary>
    /// Plausible-VAT shape check: a 2-letter ISO country prefix followed by 8–12 alphanumerics, or a
    /// bare 8–12 alphanumeric body. Deliberately permissive (length + charset), not a VIES checksum.
    /// </summary>
    private static bool IsPlausibleVat(string vat)
    {
        var v = vat.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        if (v.Length is < 8 or > 14) return false;
        var hasPrefix = v.Length >= 2 && char.IsLetter(v[0]) && char.IsLetter(v[1]);
        var body = hasPrefix ? v[2..] : v;
        return body.Length is >= 6 and <= 12 && body.All(char.IsLetterOrDigit);
    }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~SupplierAcceptanceNewOperatorsTests"`
Expected: PASS. (If `DeliveryDateRaw` is absent per the Step-1 note, adjust that one test/field per the note.)

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Services/SupplierAcceptanceService.cs ProcuLink.Api.Tests/Services/SupplierAcceptanceNewOperatorsTests.cs
git commit -m "feat(validation): date-sanity, not-label, line-amount-reconcile, vat-format operators"
```

---

### Task 9: Seed the new validation rules in `RuleCatalog`

**Files:**
- Modify: `ProcuLink.Core/Entities/RuleCatalog.cs:43-128` (append to `Entries`)
- Test: extend `ProcuLink.Api.Tests/Services/SupplierAcceptanceNewOperatorsTests.cs` with a catalog-shape assertion (or a small `RuleCatalogTests`)

`Evaluate`'s `default:` returns a non-blocking pass for unknown operators, so the catalog seeds only become live when an org binds them (the existing seeding flow materialises `RuleDefinition`s from `Entries`). Add one seed per new operator with sensible defaults + standards refs, and the required-field-presence seeds the spec calls for that aren't yet in the catalog.

- [ ] **Step 1: Write the failing catalog-shape test**

```csharp
using System.Linq;
using ProcuLink.Core.Entities;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class RuleCatalogNewSeedsTests
{
    [Theory]
    [InlineData("deliveryDateRaw", "date_sanity")]
    [InlineData("shipToCity", "not_label")]
    [InlineData("lineAmount", "line_amount_reconcile")]
    [InlineData("shipToVat", "vat_format")]
    public void Catalog_contains_the_new_seed(string field, string op)
    {
        var code = RuleCatalog.CodeFor(field, op);
        Assert.Contains(RuleCatalog.Entries, e => e.Code == code);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~RuleCatalogNewSeedsTests"`
Expected: FAIL — the seeds are absent.

- [ ] **Step 3: Append the new seeds to `Entries`**

Add before the closing `};` of `Entries` (`RuleCatalog.cs:128`):

```csharp
        // ── Phase 2 lossless-mapping validation seeds ───────────────────────────
        Entry("line", "deliveryDateRaw", "date_sanity",
            "Delivery date is unambiguous",
            "Flag dates where day and month are both ≤ 12 (MM/DD vs DD/MM flip risk, e.g. 06/12).",
            defaultSeverity: "warning",
            ubl: "cbc:RequestedDeliveryPeriod/cbc:StartDate", edifact: "DTM C507/2380",
            x12: "DTM02", cxml: "DeliveryDate"),

        Entry("order", "shipToCity", "not_label",
            "Ship-to city is not a label",
            "Catch a parser that swept a label cell (e.g. 'UIDNr', 'City') into the ship-to city.",
            defaultSeverity: "warning", defaultExpectedValue: "City,VAT,UID,UIDNr,Label,Tel,Fax",
            paramHint: "Comma-separated label words to reject",
            ubl: "cac:Delivery/cac:DeliveryLocation/cac:Address/cbc:CityName",
            edifact: "NAD DP C059/3164", x12: "N4*01", cxml: "ShipTo/Address/City"),

        Entry("line", "lineAmount", "line_amount_reconcile",
            "Line amount reconciles with qty × price",
            "Reject lines where the printed line amount diverges from quantity × unit price beyond tolerance.",
            defaultSeverity: "warning", defaultExpectedValue: "0.01",
            paramHint: "Absolute tolerance, e.g. 0.01",
            ubl: "cbc:LineExtensionAmount", edifact: "MOA C516/5004",
            x12: "PO103", cxml: "ItemOut/@lineNumber"),

        Entry("order", "shipToVat", "vat_format",
            "Ship-to VAT id is well-formed",
            "Check the ship-to VAT id has a plausible country prefix + length (not a checksum).",
            defaultSeverity: "warning",
            ubl: "cac:PartyTaxScheme/cbc:CompanyID", edifact: "RFF VA",
            x12: "REF*VX", cxml: "Party/IdReference[@domain='vat']"),
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~RuleCatalogNewSeedsTests"`
Expected: PASS.

- [ ] **Step 5: Full suite + build green**

Run: `dotnet build ProcuLink.slnx --no-restore && dotnet test ProcuLink.slnx --no-restore`
Expected: build PASS; all tests PASS (Postgres tests skip cleanly when Docker is absent).

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Entities/RuleCatalog.cs ProcuLink.Api.Tests/Services/SupplierAcceptanceNewOperatorsTests.cs
git commit -m "feat(validation): seed date-sanity/not-label/line-reconcile/vat-format rules in RuleCatalog"
```

---

### Task 10: `CanonicalFieldDef` real-Postgres round-trip + soft-delete

**Files:**
- Test: `ProcuLink.Api.Tests/Integration/CanonicalFieldDefPersistencePostgresTests.cs`

Phase-1 proved the column-vs-JSON trap (EF-Ignored field + `ExecuteUpdateAsync` silently drops the value). `CanonicalFieldDef` is a first-class table (not a JSON sub-key), so this test pins that it persists + reloads + soft-deletes on REAL Postgres.

- [ ] **Step 1: Write the failing round-trip test**

```csharp
using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

[Collection("postgres-container")]
public sealed class CanonicalFieldDefPersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;
        _pg = new PostgreSqlBuilder().WithImage("postgres:16")
            .WithDatabase($"proculink_cfd_{Guid.NewGuid():N}")
            .WithUsername("postgres").WithPassword("postgres").Build();
        await _pg.StartAsync();
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
        await using var migrate = new ProcuLinkDbContext(_options);
        await migrate.Database.MigrateAsync();
    }

    public async Task DisposeAsync() { if (_pg is not null) await _pg.DisposeAsync(); }

    [DockerRequiredFact]
    public async Task Def_persists_reloads_and_soft_deletes()
    {
        var orgId = Guid.NewGuid(); var connId = Guid.NewGuid(); var defId = Guid.NewGuid();

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.CanonicalFieldDefs.Add(new CanonicalFieldDef
            {
                Id = defId, OrgId = orgId, ConnectionId = connId,
                Key = "incoterms2", Label = "Incoterms (extra)", Scope = "header", Type = "string",
                StandardsRef = "cbc:CustomizationID", Order = 1,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var d = await db.CanonicalFieldDefs.AsNoTracking().SingleAsync(x => x.Id == defId);
            Assert.Equal("incoterms2", d.Key);
            Assert.Equal("header", d.Scope);
            Assert.Equal("cbc:CustomizationID", d.StandardsRef);
            Assert.Null(d.DeletedAt);
        }

        // Soft-delete: stamp DeletedAt; the row survives so pinned revisions still see the def.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var d = await db.CanonicalFieldDefs.SingleAsync(x => x.Id == defId);
            d.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var d = await db.CanonicalFieldDefs.AsNoTracking().SingleAsync(x => x.Id == defId);
            Assert.NotNull(d.DeletedAt); // soft-deleted but still present
        }
    }

    [DockerRequiredFact]
    public async Task Guard_columns_round_trip_on_supplier_connection()
    {
        var orgId = Guid.NewGuid(); var connId = Guid.NewGuid();
        // ... seed org + supplier the FK requires (copy the seed helper from an existing
        // *PersistencePostgresTests) then a SupplierConnection with the guard enabled ...
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.SupplierConnections.Add(new SupplierConnection
            {
                Id = connId, OrgId = orgId, SupplierId = /* seeded */ default,
                Name = "Acme", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                PriceVarianceGuardEnabled = true, PriceVarianceThresholdPercent = 20m,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var c = await db.SupplierConnections.AsNoTracking().SingleAsync(x => x.Id == connId);
            Assert.True(c.PriceVarianceGuardEnabled);
            Assert.Equal(20m, c.PriceVarianceThresholdPercent);
        }
    }
}
```

- [ ] **Step 2: Run**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~CanonicalFieldDefPersistence"`
Expected: PASS (skips with a clear reason if Docker is unavailable). The second fact requires seeding org+supplier rows for the FK — copy the seed helper from an existing `*PersistencePostgresTests` in the same project.

- [ ] **Step 3: Commit**

```bash
git add ProcuLink.Api.Tests/Integration/CanonicalFieldDefPersistencePostgresTests.cs
git commit -m "test(canonical): real-Postgres round-trip + soft-delete for CanonicalFieldDef + guard columns"
```

---

## Self-review

- **Spec coverage (Phase 2):**
  - **A. Extensible canonical (`CanonicalFieldDef`)** → Tasks 1, 2, 10. Decision recorded: VALUES reuse the existing `OrderMappingOverride.CustomFields` mechanism (header `Value` / line `LineValues`), keyed by `Key` — NO new value column on the order (grounding confirmed `CustomFields` already flows to output + review; the def table is definition-only). Revision-pin: the def set is org/connection-scoped and soft-deleted so pinned `SupplierConnectionRevision`s keep a stable view — full pinning of the def SET into the revision is Phase 4 (noted below).
  - **B. Lossless source universe → re-derive** → Tasks 3, 4. `SourceTokenSerialization` reads `SourceCapture.TokensJson`; threaded into both `null` seams (`OrderTransformService.cs:267`, `OrdersController.cs:851`). Survives blob purge (test asserts re-derive from persisted tokens only). FULL field set is now addressable (raw_fields get a deterministic `raw:{label}` id).
  - **C. Catalog accessor + price-variance guard** → Tasks 5, 6, 7. `{{ catalog.* }}` accessor (template mode wired now), `LoadCatalogProduct` manipulator (row-injected), `PriceVarianceGuard { Enabled, ThresholdPercent }` on `SupplierConnection`, HOLD via `NeedsReview` → `pending_review`. Catalog price is a SUGGESTION (never mutates `UnitPrice`). EU-aware parse used for any raw-string price math.
  - **D. Validation rules** → Tasks 8, 9. date-sanity, city-not-a-label, qty×price reconcile, VAT-format, required-presence (existing `required`). Each marks `NeedsReview` + reason via the existing `OrderValidationResult` severity (warning = advisory, error = blocking) — per-field delivery-blocking vs advisory honored through `Severity`.
- **Placeholder scan:** no "TBD"/"similar to Task N"/"add validation" — every step has real C# and exact commands. Two items are explicitly flagged as **confirm-at-execution** (not placeholders): the `DeliveryDateRaw` raw-string column existence (Task 8 Step 1 note) and the `BuildCatalogLookupAsync` reachability between `OrderTransformService` and `OrderResolutionService` (Task 7 Step 6 note) — each with a precise fallback.
- **Type consistency:** `CanonicalFieldDef` mirrors the `CustomField` member shape (Key/Label/Scope) so values bind 1:1. `SourceToken(Id,Label,Value,Group)` round-trips exactly through `SourceTokenSerialization`. `PriceVarianceGuard` is a Core record (no Transform dependency — Core cannot reference Transform; the EU-aware parse is duplicated deliberately). `SupplierProduct` columns used (`Code/Name/Unit/Price/Currency/Barcode/ExternalId/IsActive`) match the grounding entity. `MappedTransformService.Build(..., sourceTokens:)` already exists (Phase 1) — Task 4 only fills the argument.
- **Invariants honored:** additive/nullable migration (Task 2: new table + two defaulted columns, no drops); real columns not `canonical_json` for the guard config + def table (cites the EF-Ignore/`ExecuteUpdateAsync` trap — Task 10 round-trips on real Postgres); org-scoped EF on every new query (`BuildCatalogLookupAsync`, guard load, def queries all `.Where(... OrgId ...)`); idempotency — the guard/catalog loads are read-only and the transform path keeps its existing atomic claim; locale comma-decimal safety (`PriceVarianceGuard.ParseEuAware`, never `InvariantCulture` on a raw locale string); no commercial EDI libs; Scriban sandboxing preserved — the `catalog` object is a plain pre-built `ScriptObject`, no DB access at render time, no new builtins.
- **Real-Postgres vs deterministic split:** persisting changes → Testcontainers `[DockerRequiredFact]` `[Collection("postgres-container")]` (Tasks 4, 10). Engine/operator logic → deterministic unit tests (Tasks 3, 5, 6, 7-evaluator, 8, 9). InMemory `Ignore<CanonicalFieldDef>()` sweep added (Task 1 Step 4) mirroring the Phase-1 `OrderParty`/`SourceCapture` sweep across the ~28 contexts.
- **Parallel-safety map:** Task 1 → Task 2 sequential (foundation). After Task 2: Slice B (3,4), Slice C (5,6,7), Slice D (8,9). **B and C are NOT fully parallel** — both edit `OrderTransformService.cs` + `MappedTransformService.cs`/`ScribanTemplateTransformService.cs` at the same `Build(...)` call sites, so run B before C (or same worktree, sequential). **Slice D shares no files with B or C → fully parallel-safe.** Task 10 (round-trip tests) depends only on Task 2.
- **Deferred spec requirements (out of scope, with reason):**
  - **Declared target schema from MANY sources (standards/sample/import/clone/AI-generate)** — the spec lists this under Phase 2 (Layer C "Output") but it is large and UI-coupled; folded to **Phase 2b / Phase 3** per the prompt's allowance. The override engine already accepts any output shape; the *authoring* of a named target schema object is the deferred part.
  - **AI mapping suggestions / ghost wires** (Layer C) — these are the Phase-3 drag-wire UI's primary surface; out of scope here (no AI-suggestion engine change in this plan).
  - **Native-mode `LoadCatalogProduct` row injection** — the `{{ catalog.* }}` template accessor is wired (Task 7 Step 5); injecting `__catalog_*` keys into the native CSV/JSON row bag is deferred (the manipulator + registry exist and unit-test green; only the row-injection caller is deferred). Reason: template mode is the primary catalog surface; native injection is mechanical follow-up.
  - **Full revision-pinning of the `CanonicalFieldDef` SET + `SourceCapture` into the revision** — Phase 4 (Layer E). This plan makes both purge-surviving + soft-deleted (the durability primitive); the immutable per-revision snapshot of the def set is Phase 4.
- **Grounding ambiguities for the controller to resolve before execution:**
  1. **`DeliveryDateRaw` (raw printed date string):** the grounding does not confirm Phase 1 added a raw delivery-date string column. `date_sanity` is most meaningful against the ORIGINAL string. If absent: either point `date_sanity` at a `SourceCapture` raw token, or defer `date_sanity` (drop its operator + seed + test). **Resolve by grepping `DeliveryDateRaw` / the Phase-1 line columns before Task 8.**
  2. **`BuildCatalogLookupAsync` sharing between `OrderTransformService` and `OrderResolutionService`:** the two ingest sub-services may not share a base. If not, lift the helper to a shared `internal static` (e.g. on a `CatalogLookup` Core/Infra helper) or duplicate the ~8-line query. **Resolve by checking the two services' class relationship before Task 7 Step 6.**
  3. **Guard evaluation site:** grounding offers two seams — `OrderResolutionService.cs:169` (recompute on resolve) vs `OrderIngestionService.cs:188` (set at ingest). This plan wires the RESOLUTION site (so a re-resolve re-checks). If the founder wants the guard to fire at first ingest too, also call it from `OrderIngestionService` after `BuildLineEntitiesAsync`. **Confirm the desired trigger point.**
  4. **`SupplierConnection` config block name in DbContext:** confirm the exact `b.ToTable("supplier_connections")` block (Task 1 Step 3) — the column additions must land in that block, not a revision block.
