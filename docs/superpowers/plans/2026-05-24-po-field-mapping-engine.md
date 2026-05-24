# PO Field Mapping Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hardcoded CSV column aliases in `CsvOrderParser` with per-supplier configurable mapping templates stored as JSONB, enabling non-developer configuration of any client''s PO CSV layout.

**Architecture:** New `SupplierPoMapping` entity (JSONB config) + `IPoMappingService` CRUD + `PoMappingEngine` in `ProcuLink.Transform` that applies the template to raw CSV rows. `OrderService.ParseStoredFileAsync` gains a template-aware code path; existing `CsvOrderParser` path remains as fallback.

**Tech Stack:** .NET 8 / ASP.NET Core / EF Core + Npgsql JSONB; xUnit + FluentAssertions for tests; Next.js 15 App Router + TanStack Query v5 + bun

---

## File Map

### Backend — new files
| File | Responsibility |
|---|---|
| `ProcuLink.Core/Services/Mapping/PoMappingConfig.cs` | POCOs: `PoMappingConfig`, `FieldMappingEntry`, `ManipulatorEntry` |
| `ProcuLink.Core/Services/Mapping/IPoMappingService.cs` | Service interface |
| `ProcuLink.Core/Entities/SupplierPoMapping.cs` | EF entity |
| `ProcuLink.Transform/Mapping/IFieldManipulator.cs` | Manipulator interface |
| `ProcuLink.Transform/Mapping/ManipulatorRegistry.cs` | Resolves manipulator by name |
| `ProcuLink.Transform/Mapping/Manipulators/ReplaceManipulator.cs` | Replace(find, with) |
| `ProcuLink.Transform/Mapping/Manipulators/TrimManipulator.cs` | Trim whitespace |
| `ProcuLink.Transform/Mapping/Manipulators/DateFormatManipulator.cs` | Parse/reformat date |
| `ProcuLink.Transform/Mapping/Manipulators/ConcatManipulator.cs` | Join multiple columns |
| `ProcuLink.Transform/Mapping/Manipulators/FallbackManipulator.cs` | First non-empty value |
| `ProcuLink.Transform/Mapping/Manipulators/SplitManipulator.cs` | Split on delimiter, take index |
| `ProcuLink.Transform/Mapping/Manipulators/MultiplyManipulator.cs` | Numeric multiply |
| `ProcuLink.Transform/Mapping/Manipulators/DivideManipulator.cs` | Numeric divide |
| `ProcuLink.Transform/Mapping/PoMappingEngine.cs` | `Apply(headerRow, lineRows, config) -> MappedOrder` |
| `ProcuLink.Transform/Mapping/MappedOrder.cs` | `MappedOrder` + `MappedOrderLine` records |
| `ProcuLink.Infrastructure/Services/PoMappingService.cs` | EF Core upsert implementation |
| `ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj` | xUnit test project |
| `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs` | Tests for all 8 manipulators |
| `ProcuLink.Transform.Tests/Mapping/PoMappingEngineTests.cs` | Integration tests for engine |

### Backend — modified files
| File | Change |
|---|---|
| `ProcuLink.Core/Entities/Supplier.cs` | Add `List<SupplierPoMapping> PoMappings` nav |
| `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` | Add `DbSet<SupplierPoMapping>`, config |
| `ProcuLink.Api/Program.cs` | Register `IPoMappingService` -> `PoMappingService` |
| `ProcuLink.Api/Controllers/SuppliersController.cs` | 4 new endpoints |
| `ProcuLink.Api/Services/OrderService.cs` | Inject `IPoMappingService`, template-aware branch |

### Frontend — new files
| File | Responsibility |
|---|---|
| `src/lib/api/mapping.ts` | 4 typed fetch functions |
| `src/components/bridge/PoMappingEditor.tsx` | Visual mapping editor component |

### Frontend — modified files
| File | Change |
|---|---|
| `src/lib/api/types.ts` | Add `PoMappingConfig`, `FieldMappingEntry`, `ManipulatorEntry` types |
| `src/components/bridge/SupplierDockProfile.tsx` | Add "PO Mapping" tab + content |

---

## Task 1: Core POCOs + Service Interface + Test Project

**Files:**
- Create: `ProcuLink.Core/Services/Mapping/PoMappingConfig.cs`
- Create: `ProcuLink.Core/Services/Mapping/IPoMappingService.cs`
- Create: `ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj`
- Create: `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs` (placeholder)

- [ ] **Step 1: Create the config POCOs**

Create `ProcuLink.Core/Services/Mapping/PoMappingConfig.cs`:

```csharp
namespace ProcuLink.Core.Services.Mapping;

public record PoMappingConfig
{
    public bool HasHeaderRecord { get; init; } = true;
    public string Separator { get; init; } = ",";
    /// <summary>Maps canonical header field names to column mapping entry.</summary>
    public Dictionary<string, FieldMappingEntry> Header { get; init; } = new();
    /// <summary>Maps canonical line field names to column mapping entry.</summary>
    public Dictionary<string, FieldMappingEntry> Lines { get; init; } = new();
}

public record FieldMappingEntry
{
    /// <summary>Source column name in the supplier CSV. Null if using FixedValue.</summary>
    public string? ExternalField { get; init; }
    /// <summary>Constant value to use when no external column exists.</summary>
    public string? FixedValue { get; init; }
    public List<ManipulatorEntry> FieldManipulators { get; init; } = new();
}

public record ManipulatorEntry
{
    /// <summary>Manipulator type name, e.g. "Replace", "Trim", "DateFormat".</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>Ordered parameters for the manipulator.</summary>
    public List<string> Params { get; init; } = new();
}
```

- [ ] **Step 2: Create the service interface**

Create `ProcuLink.Core/Services/Mapping/IPoMappingService.cs`:

```csharp
namespace ProcuLink.Core.Services.Mapping;

public interface IPoMappingService
{
    Task<PoMappingConfig?> GetAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default);
    Task<PoMappingConfig> UpsertAsync(Guid organisationId, Guid supplierId, PoMappingConfig config, CancellationToken ct = default);
    Task DeleteAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Create the test project**

Run from `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`:

```
dotnet new xunit -n ProcuLink.Transform.Tests -o ProcuLink.Transform.Tests
dotnet add ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj reference ProcuLink.Transform/ProcuLink.Transform.csproj
dotnet add ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj reference ProcuLink.Core/ProcuLink.Core.csproj
dotnet add ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj package FluentAssertions --version 6.12.0
dotnet sln add ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj
```

- [ ] **Step 4: Delete default test file and create placeholder**

Delete `ProcuLink.Transform.Tests/UnitTest1.cs`.

Create `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs`:

```csharp
namespace ProcuLink.Transform.Tests.Mapping;

public class ManipulatorTests
{
    // Tests added in Tasks 3-5
}
```

- [ ] **Step 5: Build to confirm**

```
dotnet build ProcuLink.sln
```

Expected: Build succeeded, 0 error(s).

- [ ] **Step 6: Commit**

```
git add ProcuLink.Core/Services/Mapping/ ProcuLink.Transform.Tests/ ProcuLink.sln
git commit -m "feat: add PoMappingConfig POCOs, IPoMappingService, and test project"
```

---

## Task 2: SupplierPoMapping Entity + EF Config + Migration

**Files:**
- Create: `ProcuLink.Core/Entities/SupplierPoMapping.cs`
- Modify: `ProcuLink.Core/Entities/Supplier.cs`
- Modify: `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`

- [ ] **Step 1: Create the entity**

Create `ProcuLink.Core/Entities/SupplierPoMapping.cs`:

```csharp
namespace ProcuLink.Core.Entities;

public class SupplierPoMapping
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }
    /// <summary>JSONB column -- serialized PoMappingConfig.</summary>
    public string ConfigJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
```

- [ ] **Step 2: Add navigation to Supplier**

In `ProcuLink.Core/Entities/Supplier.cs`, add to the navigation properties section:

```csharp
public List<SupplierPoMapping> PoMappings { get; set; } = new();
```

- [ ] **Step 3: Register in DbContext**

In `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`:

1. Add `DbSet` with the other sets:

```csharp
public DbSet<SupplierPoMapping> SupplierPoMappings { get; set; }
```

2. Inside `OnModelCreating`, add after the existing supplier config block:

```csharp
modelBuilder.Entity<SupplierPoMapping>(b =>
{
    b.ToTable("supplier_po_mappings");
    b.HasKey(x => x.Id);
    b.Property(x => x.Id).HasColumnName("id");
    b.Property(x => x.OrgId).HasColumnName("org_id");
    b.Property(x => x.SupplierId).HasColumnName("supplier_id");
    b.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
    b.Property(x => x.CreatedAt).HasColumnName("created_at");
    b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

    b.HasIndex(x => new { x.OrgId, x.SupplierId }).IsUnique();

    b.HasOne(x => x.Organisation)
        .WithMany()
        .HasForeignKey(x => x.OrgId);
    b.HasOne(x => x.Supplier)
        .WithMany(s => s.PoMappings)
        .HasForeignKey(x => x.SupplierId);
});
```

- [ ] **Step 4: Add EF migration**

Run from `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`:

```
dotnet ef migrations add AddSupplierPoMappings --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

Expected: a new migration file under `ProcuLink.Infrastructure/Migrations/`.

- [ ] **Step 5: Apply migration locally**

```
dotnet ef database update --project ProcuLink.Infrastructure --startup-project ProcuLink.Api
```

Expected: Done.

- [ ] **Step 6: Build**

```
dotnet build ProcuLink.sln
```

Expected: 0 errors.

- [ ] **Step 7: Commit**

```
git add ProcuLink.Core/Entities/SupplierPoMapping.cs ProcuLink.Core/Entities/Supplier.cs ProcuLink.Infrastructure/ProcuLinkDbContext.cs ProcuLink.Infrastructure/Migrations/
git commit -m "feat: add SupplierPoMapping entity and EF migration"
```

---

## Task 3: IFieldManipulator + ManipulatorRegistry + Replace + Trim

**Files:**
- Create: `ProcuLink.Transform/Mapping/IFieldManipulator.cs`
- Create: `ProcuLink.Transform/Mapping/ManipulatorRegistry.cs`
- Create: `ProcuLink.Transform/Mapping/Manipulators/ReplaceManipulator.cs`
- Create: `ProcuLink.Transform/Mapping/Manipulators/TrimManipulator.cs`
- Modify: `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs`

- [ ] **Step 1: Write failing tests**

Replace `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs`:

```csharp
using FluentAssertions;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Mapping.Manipulators;

namespace ProcuLink.Transform.Tests.Mapping;

public class ManipulatorTests
{
    // Replace
    [Fact]
    public void Replace_SubstitutesAllOccurrences()
    {
        var m = new ReplaceManipulator(new[] { "/", "-" });
        m.Apply("01/02/2024", row: null!).Should().Be("01-02-2024");
    }

    [Fact]
    public void Replace_WhenFindNotPresent_ReturnsOriginal()
    {
        var m = new ReplaceManipulator(new[] { "X", "Y" });
        m.Apply("hello", row: null!).Should().Be("hello");
    }

    // Trim
    [Fact]
    public void Trim_RemovesLeadingAndTrailingWhitespace()
    {
        var m = new TrimManipulator(Array.Empty<string>());
        m.Apply("  hello  ", row: null!).Should().Be("hello");
    }

    [Fact]
    public void Trim_NullInput_ReturnsEmpty()
    {
        var m = new TrimManipulator(Array.Empty<string>());
        m.Apply(null, row: null!).Should().Be(string.Empty);
    }

    // Registry
    [Fact]
    public void Registry_Resolve_KnownType_ReturnsInstance()
    {
        var m = ManipulatorRegistry.Resolve("Replace", new[] { "a", "b" });
        m.Should().BeOfType<ReplaceManipulator>();
    }

    [Fact]
    public void Registry_Resolve_UnknownType_ThrowsInvalidOperationException()
    {
        var act = () => ManipulatorRegistry.Resolve("NonExistent", Array.Empty<string>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*NonExistent*");
    }
}
```

- [ ] **Step 2: Run tests -- expect FAIL**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~ManipulatorTests"
```

Expected: FAIL -- types not found.

- [ ] **Step 3: Create IFieldManipulator**

Create `ProcuLink.Transform/Mapping/IFieldManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping;

public interface IFieldManipulator
{
    string? Apply(string? value, IReadOnlyDictionary<string, string> row);
}
```

- [ ] **Step 4: Create ReplaceManipulator**

Create `ProcuLink.Transform/Mapping/Manipulators/ReplaceManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [find, replacement]</summary>
public class ReplaceManipulator : IFieldManipulator
{
    private readonly string _find;
    private readonly string _with;

    public ReplaceManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 2)
            throw new ArgumentException("Replace requires 2 params: [find, with]", nameof(@params));
        _find = @params[0];
        _with = @params[1];
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
        => value?.Replace(_find, _with);
}
```

- [ ] **Step 5: Create TrimManipulator**

Create `ProcuLink.Transform/Mapping/Manipulators/TrimManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>No params required.</summary>
public class TrimManipulator : IFieldManipulator
{
    public TrimManipulator(IReadOnlyList<string> _) { }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
        => value?.Trim() ?? string.Empty;
}
```

- [ ] **Step 6: Create ManipulatorRegistry + stub remaining 6 manipulators**

Create `ProcuLink.Transform/Mapping/ManipulatorRegistry.cs`:

```csharp
using ProcuLink.Transform.Mapping.Manipulators;

namespace ProcuLink.Transform.Mapping;

public static class ManipulatorRegistry
{
    public static IFieldManipulator Resolve(string type, IReadOnlyList<string> @params)
        => type switch
        {
            "Replace"    => new ReplaceManipulator(@params),
            "Trim"       => new TrimManipulator(@params),
            "DateFormat" => new DateFormatManipulator(@params),
            "Concat"     => new ConcatManipulator(@params),
            "Fallback"   => new FallbackManipulator(@params),
            "Split"      => new SplitManipulator(@params),
            "Multiply"   => new MultiplyManipulator(@params),
            "Divide"     => new DivideManipulator(@params),
            _            => throw new InvalidOperationException($"Unknown manipulator type: {type}")
        };
}
```

Create stub files so the registry compiles (these are replaced in Tasks 4-5):

`ProcuLink.Transform/Mapping/Manipulators/DateFormatManipulator.cs`:
```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;
public class DateFormatManipulator : IFieldManipulator {
    public DateFormatManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
```

`ProcuLink.Transform/Mapping/Manipulators/ConcatManipulator.cs`:
```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;
public class ConcatManipulator : IFieldManipulator {
    public ConcatManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
```

`ProcuLink.Transform/Mapping/Manipulators/FallbackManipulator.cs`:
```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;
public class FallbackManipulator : IFieldManipulator {
    public FallbackManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
```

`ProcuLink.Transform/Mapping/Manipulators/SplitManipulator.cs`:
```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;
public class SplitManipulator : IFieldManipulator {
    public SplitManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
```

`ProcuLink.Transform/Mapping/Manipulators/MultiplyManipulator.cs`:
```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;
public class MultiplyManipulator : IFieldManipulator {
    public MultiplyManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
```

`ProcuLink.Transform/Mapping/Manipulators/DivideManipulator.cs`:
```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;
public class DivideManipulator : IFieldManipulator {
    public DivideManipulator(IReadOnlyList<string> _) { }
    public string? Apply(string? value, IReadOnlyDictionary<string, string> row) => value;
}
```

- [ ] **Step 7: Run tests -- expect PASS**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~ManipulatorTests"
```

Expected: 6/6 PASS.

- [ ] **Step 8: Commit**

```
git add ProcuLink.Transform/Mapping/ ProcuLink.Transform.Tests/
git commit -m "feat: add IFieldManipulator, ManipulatorRegistry, Replace and Trim manipulators"
```

---

## Task 4: DateFormat, Concat, Fallback Manipulators

**Files:**
- Modify: `ProcuLink.Transform/Mapping/Manipulators/DateFormatManipulator.cs`
- Modify: `ProcuLink.Transform/Mapping/Manipulators/ConcatManipulator.cs`
- Modify: `ProcuLink.Transform/Mapping/Manipulators/FallbackManipulator.cs`
- Modify: `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs`

- [ ] **Step 1: Add failing tests**

Append inside `ManipulatorTests` class:

```csharp
// DateFormat
[Fact]
public void DateFormat_ConvertsFromInputFormatToOutput()
{
    var m = new DateFormatManipulator(new[] { "dd/MM/yyyy", "yyyy-MM-dd" });
    m.Apply("24/05/2026", row: null!).Should().Be("2026-05-24");
}

[Fact]
public void DateFormat_InvalidDate_ReturnsOriginal()
{
    var m = new DateFormatManipulator(new[] { "dd/MM/yyyy", "yyyy-MM-dd" });
    m.Apply("not-a-date", row: null!).Should().Be("not-a-date");
}

[Fact]
public void DateFormat_NullInput_ReturnsNull()
{
    var m = new DateFormatManipulator(new[] { "dd/MM/yyyy", "yyyy-MM-dd" });
    m.Apply(null, row: null!).Should().BeNull();
}

// Concat
[Fact]
public void Concat_JoinsColumnsWithSeparator()
{
    var row = new Dictionary<string, string> { ["first"] = "Hello", ["second"] = "World" };
    var m = new ConcatManipulator(new[] { " ", "first", "second" });
    m.Apply(null, row).Should().Be("Hello World");
}

[Fact]
public void Concat_MissingColumn_TreatedAsEmpty()
{
    var row = new Dictionary<string, string> { ["first"] = "A" };
    var m = new ConcatManipulator(new[] { "-", "first", "missing" });
    m.Apply(null, row).Should().Be("A-");
}

// Fallback
[Fact]
public void Fallback_ReturnsFirstNonEmptyColumnValue()
{
    var row = new Dictionary<string, string> { ["a"] = "", ["b"] = "found", ["c"] = "other" };
    var m = new FallbackManipulator(new[] { "a", "b", "c" });
    m.Apply(null, row).Should().Be("found");
}

[Fact]
public void Fallback_AllEmpty_ReturnsNull()
{
    var row = new Dictionary<string, string> { ["a"] = "", ["b"] = "" };
    var m = new FallbackManipulator(new[] { "a", "b" });
    m.Apply(null, row).Should().BeNull();
}
```

- [ ] **Step 2: Run tests -- expect partial FAIL**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~ManipulatorTests"
```

Expected: 6 PASS, 7 FAIL (stubs return value unchanged).

- [ ] **Step 3: Implement DateFormatManipulator**

Replace `ProcuLink.Transform/Mapping/Manipulators/DateFormatManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [inputFormat, outputFormat]</summary>
public class DateFormatManipulator : IFieldManipulator
{
    private readonly string _inputFormat;
    private readonly string _outputFormat;

    public DateFormatManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 2)
            throw new ArgumentException("DateFormat requires 2 params: [inputFormat, outputFormat]", nameof(@params));
        _inputFormat = @params[0];
        _outputFormat = @params[1];
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (value is null) return null;
        return DateTime.TryParseExact(value, _inputFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToString(_outputFormat, System.Globalization.CultureInfo.InvariantCulture)
            : value;
    }
}
```

- [ ] **Step 4: Implement ConcatManipulator**

Replace `ProcuLink.Transform/Mapping/Manipulators/ConcatManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [separator, col1, col2, ...] -- reads named columns from the row and joins them.</summary>
public class ConcatManipulator : IFieldManipulator
{
    private readonly string _separator;
    private readonly IReadOnlyList<string> _columns;

    public ConcatManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 2)
            throw new ArgumentException("Concat requires at least 2 params: [separator, col1, ...]", nameof(@params));
        _separator = @params[0];
        _columns = @params.Skip(1).ToList();
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        var parts = _columns.Select(c => row.TryGetValue(c, out var v) ? v : string.Empty);
        return string.Join(_separator, parts);
    }
}
```

- [ ] **Step 5: Implement FallbackManipulator**

Replace `ProcuLink.Transform/Mapping/Manipulators/FallbackManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [col1, col2, ...] -- returns first non-empty value from the named columns.</summary>
public class FallbackManipulator : IFieldManipulator
{
    private readonly IReadOnlyList<string> _columns;

    public FallbackManipulator(IReadOnlyList<string> @params)
    {
        _columns = @params;
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        foreach (var col in _columns)
            if (row.TryGetValue(col, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return null;
    }
}
```

- [ ] **Step 6: Run tests -- expect PASS**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~ManipulatorTests"
```

Expected: 13/13 PASS.

- [ ] **Step 7: Commit**

```
git add ProcuLink.Transform/Mapping/Manipulators/ ProcuLink.Transform.Tests/
git commit -m "feat: implement DateFormat, Concat, Fallback manipulators"
```

---

## Task 5: Split, Multiply, Divide Manipulators

**Files:**
- Modify: `ProcuLink.Transform/Mapping/Manipulators/SplitManipulator.cs`
- Modify: `ProcuLink.Transform/Mapping/Manipulators/MultiplyManipulator.cs`
- Modify: `ProcuLink.Transform/Mapping/Manipulators/DivideManipulator.cs`
- Modify: `ProcuLink.Transform.Tests/Mapping/ManipulatorTests.cs`

- [ ] **Step 1: Add failing tests**

Append inside `ManipulatorTests` class:

```csharp
// Split
[Fact]
public void Split_ReturnsTokenAtIndex()
{
    var m = new SplitManipulator(new[] { "/", "2" });
    m.Apply("01/02/2024", row: null!).Should().Be("2024");
}

[Fact]
public void Split_IndexOutOfRange_ReturnsOriginal()
{
    var m = new SplitManipulator(new[] { "/", "9" });
    m.Apply("a/b", row: null!).Should().Be("a/b");
}

// Multiply
[Fact]
public void Multiply_ScalesNumericValue()
{
    var m = new MultiplyManipulator(new[] { "1.21" });
    m.Apply("100", row: null!).Should().Be("121");
}

[Fact]
public void Multiply_NonNumericInput_ReturnsOriginal()
{
    var m = new MultiplyManipulator(new[] { "2" });
    m.Apply("abc", row: null!).Should().Be("abc");
}

// Divide
[Fact]
public void Divide_ScalesNumericValueDown()
{
    var m = new DivideManipulator(new[] { "100" });
    m.Apply("1000", row: null!).Should().Be("10");
}

[Fact]
public void Divide_DivideByZero_ReturnsOriginal()
{
    var m = new DivideManipulator(new[] { "0" });
    m.Apply("100", row: null!).Should().Be("100");
}
```

- [ ] **Step 2: Run tests -- expect partial FAIL**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~ManipulatorTests"
```

Expected: 13 PASS, 6 FAIL.

- [ ] **Step 3: Implement SplitManipulator**

Replace `ProcuLink.Transform/Mapping/Manipulators/SplitManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [delimiter, zeroBasedIndex] -- splits on delimiter and returns the token at index.</summary>
public class SplitManipulator : IFieldManipulator
{
    private readonly string _delimiter;
    private readonly int _index;

    public SplitManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 2)
            throw new ArgumentException("Split requires 2 params: [delimiter, index]", nameof(@params));
        _delimiter = @params[0];
        _index = int.Parse(@params[1]);
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (value is null) return null;
        var parts = value.Split(_delimiter);
        return _index >= 0 && _index < parts.Length ? parts[_index] : value;
    }
}
```

- [ ] **Step 4: Implement MultiplyManipulator**

Replace `ProcuLink.Transform/Mapping/Manipulators/MultiplyManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [factor] -- multiplies the numeric value by factor. Returns integer string when result is whole.</summary>
public class MultiplyManipulator : IFieldManipulator
{
    private readonly decimal _factor;

    public MultiplyManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 1)
            throw new ArgumentException("Multiply requires 1 param: [factor]", nameof(@params));
        _factor = decimal.Parse(@params[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
            return value;
        var result = n * _factor;
        return result == Math.Truncate(result)
            ? ((long)result).ToString()
            : result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 5: Implement DivideManipulator**

Replace `ProcuLink.Transform/Mapping/Manipulators/DivideManipulator.cs`:

```csharp
namespace ProcuLink.Transform.Mapping.Manipulators;

/// <summary>Params: [divisor] -- divides the numeric value by divisor. Returns integer string when result is whole.</summary>
public class DivideManipulator : IFieldManipulator
{
    private readonly decimal _divisor;

    public DivideManipulator(IReadOnlyList<string> @params)
    {
        if (@params.Count < 1)
            throw new ArgumentException("Divide requires 1 param: [divisor]", nameof(@params));
        _divisor = decimal.Parse(@params[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    public string? Apply(string? value, IReadOnlyDictionary<string, string> row)
    {
        if (_divisor == 0) return value;
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var n))
            return value;
        var result = n / _divisor;
        return result == Math.Truncate(result)
            ? ((long)result).ToString()
            : result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 6: Run tests -- expect PASS**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~ManipulatorTests"
```

Expected: 19/19 PASS.

- [ ] **Step 7: Commit**

```
git add ProcuLink.Transform/Mapping/Manipulators/ ProcuLink.Transform.Tests/
git commit -m "feat: implement Split, Multiply, Divide manipulators -- all 8 manipulators complete"
```

---

## Task 6: MappedOrder Records + PoMappingEngine

**Files:**
- Create: `ProcuLink.Transform/Mapping/MappedOrder.cs`
- Create: `ProcuLink.Transform/Mapping/PoMappingEngine.cs`
- Create: `ProcuLink.Transform.Tests/Mapping/PoMappingEngineTests.cs`

- [ ] **Step 1: Create MappedOrder records**

Create `ProcuLink.Transform/Mapping/MappedOrder.cs`:

```csharp
namespace ProcuLink.Transform.Mapping;

public record MappedOrder
{
    public string? PoNumber { get; init; }
    public string? OrderDate { get; init; }
    public string? BuyerName { get; init; }
    public string? Currency { get; init; }
    public List<MappedOrderLine> Lines { get; init; } = new();
}

public record MappedOrderLine
{
    public string? LineNumber { get; init; }
    public string? BuyerItemCode { get; init; }
    public string? Description { get; init; }
    public string? Quantity { get; init; }
    public string? Unit { get; init; }
    public string? UnitPrice { get; init; }
}
```

- [ ] **Step 2: Write failing engine tests**

Create `ProcuLink.Transform.Tests/Mapping/PoMappingEngineTests.cs`:

```csharp
using FluentAssertions;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Transform.Tests.Mapping;

public class PoMappingEngineTests
{
    private static PoMappingConfig SimpleConfig() => new()
    {
        HasHeaderRecord = true,
        Separator = ",",
        Header = new Dictionary<string, FieldMappingEntry>
        {
            ["PoNumber"]  = new() { ExternalField = "PO_NUMBER" },
            ["OrderDate"] = new() { ExternalField = "ORDER_DATE", FieldManipulators = new()
            {
                new() { Type = "DateFormat", Params = new() { "dd/MM/yyyy", "yyyy-MM-dd" } }
            }},
            ["BuyerName"] = new() { FixedValue = "Nordic Distribution" },
            ["Currency"]  = new() { ExternalField = "CURR" },
        },
        Lines = new Dictionary<string, FieldMappingEntry>
        {
            ["LineNumber"]    = new() { ExternalField = "LINE" },
            ["BuyerItemCode"] = new() { ExternalField = "ITEM" },
            ["Description"]   = new() { ExternalField = "DESC" },
            ["Quantity"]      = new() { ExternalField = "QTY" },
            ["Unit"]          = new() { ExternalField = "UNIT" },
            ["UnitPrice"]     = new() { ExternalField = "PRICE" },
        }
    };

    [Fact]
    public void Apply_MapsHeaderFieldsFromFirstRow()
    {
        var headerRow = new Dictionary<string, string>
        {
            ["PO_NUMBER"]  = "PO-001",
            ["ORDER_DATE"] = "24/05/2026",
            ["CURR"]       = "EUR",
        };
        var lineRows = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string>
            {
                ["LINE"] = "1", ["ITEM"] = "SKU123", ["DESC"] = "Widget",
                ["QTY"]  = "10", ["UNIT"] = "EA", ["PRICE"] = "9.99",
            }
        };

        var result = PoMappingEngine.Apply(headerRow, lineRows, SimpleConfig());

        result.PoNumber.Should().Be("PO-001");
        result.OrderDate.Should().Be("2026-05-24");
        result.BuyerName.Should().Be("Nordic Distribution");
        result.Currency.Should().Be("EUR");
        result.Lines.Should().HaveCount(1);
        result.Lines[0].BuyerItemCode.Should().Be("SKU123");
        result.Lines[0].Quantity.Should().Be("10");
    }

    [Fact]
    public void Apply_MissingColumn_YieldsNull()
    {
        var headerRow = new Dictionary<string, string>();
        var result = PoMappingEngine.Apply(headerRow, new List<IReadOnlyDictionary<string, string>>(), SimpleConfig());
        result.PoNumber.Should().BeNull();
    }

    [Fact]
    public void Apply_EmptyLines_ReturnsEmptyLinesList()
    {
        var headerRow = new Dictionary<string, string> { ["PO_NUMBER"] = "X" };
        var result = PoMappingEngine.Apply(headerRow, new List<IReadOnlyDictionary<string, string>>(), SimpleConfig());
        result.Lines.Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run tests -- expect FAIL**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --filter "FullyQualifiedName~PoMappingEngineTests"
```

Expected: FAIL -- `PoMappingEngine` not found.

- [ ] **Step 4: Implement PoMappingEngine**

Create `ProcuLink.Transform/Mapping/PoMappingEngine.cs`:

```csharp
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Mapping;

public static class PoMappingEngine
{
    public static MappedOrder Apply(
        IReadOnlyDictionary<string, string> headerRow,
        IReadOnlyList<IReadOnlyDictionary<string, string>> lineRows,
        PoMappingConfig config)
    {
        return new MappedOrder
        {
            PoNumber  = ResolveField("PoNumber",  config.Header, headerRow),
            OrderDate = ResolveField("OrderDate", config.Header, headerRow),
            BuyerName = ResolveField("BuyerName", config.Header, headerRow),
            Currency  = ResolveField("Currency",  config.Header, headerRow),
            Lines = lineRows.Select(row => new MappedOrderLine
            {
                LineNumber    = ResolveField("LineNumber",    config.Lines, row),
                BuyerItemCode = ResolveField("BuyerItemCode", config.Lines, row),
                Description   = ResolveField("Description",  config.Lines, row),
                Quantity      = ResolveField("Quantity",      config.Lines, row),
                Unit          = ResolveField("Unit",          config.Lines, row),
                UnitPrice     = ResolveField("UnitPrice",     config.Lines, row),
            }).ToList()
        };
    }

    private static string? ResolveField(
        string canonicalField,
        Dictionary<string, FieldMappingEntry> mapping,
        IReadOnlyDictionary<string, string> row)
    {
        if (!mapping.TryGetValue(canonicalField, out var entry)) return null;

        string? value = entry.FixedValue
            ?? (entry.ExternalField is not null && row.TryGetValue(entry.ExternalField, out var v) ? v : null);

        foreach (var m in entry.FieldManipulators)
        {
            var manipulator = ManipulatorRegistry.Resolve(m.Type, m.Params);
            value = manipulator.Apply(value, row);
        }

        return value;
    }
}
```

- [ ] **Step 5: Run all tests -- expect PASS**

```
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj
```

Expected: All 22 tests PASS (19 manipulator + 3 engine).

- [ ] **Step 6: Commit**

```
git add ProcuLink.Transform/Mapping/ ProcuLink.Transform.Tests/
git commit -m "feat: add MappedOrder records and PoMappingEngine"
```

---

## Task 7: PoMappingService (Infrastructure)

**Files:**
- Create: `ProcuLink.Infrastructure/Services/PoMappingService.cs`

- [ ] **Step 1: Implement PoMappingService**

Create `ProcuLink.Infrastructure/Services/PoMappingService.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Infrastructure.Services;

public class PoMappingService : IPoMappingService
{
    private readonly ProcuLinkDbContext _db;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public PoMappingService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    public async Task<PoMappingConfig?> GetAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default)
    {
        var entity = await _db.SupplierPoMappings
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return null;
        return JsonSerializer.Deserialize<PoMappingConfig>(entity.ConfigJson, _jsonOptions);
    }

    public async Task<PoMappingConfig> UpsertAsync(Guid organisationId, Guid supplierId, PoMappingConfig config, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        var now = DateTimeOffset.UtcNow;

        var entity = await _db.SupplierPoMappings
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
        {
            entity = new SupplierPoMapping
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                SupplierId = supplierId,
                ConfigJson = json,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SupplierPoMappings.Add(entity);
        }
        else
        {
            entity.ConfigJson = json;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return config;
    }

    public async Task DeleteAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default)
    {
        var entity = await _db.SupplierPoMappings
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (entity is not null)
        {
            _db.SupplierPoMappings.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build ProcuLink.sln
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```
git add ProcuLink.Infrastructure/Services/PoMappingService.cs
git commit -m "feat: implement PoMappingService with JSONB upsert"
```

---

## Task 8: DI Registration + API Endpoints

**Files:**
- Modify: `ProcuLink.Api/Program.cs`
- Modify: `ProcuLink.Api/Controllers/SuppliersController.cs`

- [ ] **Step 1: Register service in DI**

In `ProcuLink.Api/Program.cs`, add after the billing service registration:

```csharp
builder.Services.AddScoped<IPoMappingService, PoMappingService>();
```

Add usings:
```csharp
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure.Services;
```

- [ ] **Step 2: Add IPoMappingService to SuppliersController**

In `ProcuLink.Api/Controllers/SuppliersController.cs`:

Add field:
```csharp
private readonly IPoMappingService _poMappingService;
```

Add to constructor parameter list and assign:
```csharp
IPoMappingService poMappingService
// ...
_poMappingService = poMappingService;
```

Add usings:
```csharp
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;
```

- [ ] **Step 3: Add 4 endpoint methods to the controller**

```csharp
[HttpGet("{id:guid}/po-mapping")]
public async Task<IActionResult> GetPoMapping(Guid id, CancellationToken ct)
{
    var orgId = _tenant.OrganisationId;
    var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id).FirstOrDefaultAsync(ct);
    if (supplier is null) return NotFound();
    var config = await _poMappingService.GetAsync(orgId, id, ct);
    if (config is null) return NoContent();
    return Ok(config);
}

[HttpPut("{id:guid}/po-mapping")]
public async Task<IActionResult> UpsertPoMapping(Guid id, [FromBody] PoMappingConfig config, CancellationToken ct)
{
    var orgId = _tenant.OrganisationId;
    var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id).FirstOrDefaultAsync(ct);
    if (supplier is null) return NotFound();
    var saved = await _poMappingService.UpsertAsync(orgId, id, config, ct);
    return Ok(saved);
}

[HttpDelete("{id:guid}/po-mapping")]
public async Task<IActionResult> DeletePoMapping(Guid id, CancellationToken ct)
{
    var orgId = _tenant.OrganisationId;
    var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id).FirstOrDefaultAsync(ct);
    if (supplier is null) return NotFound();
    await _poMappingService.DeleteAsync(orgId, id, ct);
    return NoContent();
}

[HttpPost("{id:guid}/po-mapping/test")]
public async Task<IActionResult> TestPoMapping(Guid id, [FromBody] TestPoMappingRequest request, CancellationToken ct)
{
    var orgId = _tenant.OrganisationId;
    var supplier = await _db.Suppliers.Where(s => s.OrgId == orgId && s.Id == id).FirstOrDefaultAsync(ct);
    if (supplier is null) return NotFound();
    var result = PoMappingEngine.Apply(request.HeaderRow, request.LineRows, request.Config);
    return Ok(result);
}
```

Add the request record in the same file (outside the controller class, inside the namespace):

```csharp
public record TestPoMappingRequest(
    IReadOnlyDictionary<string, string> HeaderRow,
    IReadOnlyList<IReadOnlyDictionary<string, string>> LineRows,
    PoMappingConfig Config);
```

- [ ] **Step 4: Build**

```
dotnet build ProcuLink.sln
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```
git add ProcuLink.Api/Program.cs ProcuLink.Api/Controllers/SuppliersController.cs
git commit -m "feat: wire PoMappingService into DI and add 4 API endpoints"
```

---

## Task 9: OrderService Integration

**Files:**
- Modify: `ProcuLink.Api/Services/OrderService.cs`

- [ ] **Step 1: Add IPoMappingService injection**

In `ProcuLink.Api/Services/OrderService.cs`, add field and constructor assignment:

```csharp
private readonly IPoMappingService _poMappingService;
// constructor param: IPoMappingService poMappingService
// constructor body:  _poMappingService = poMappingService;
```

Add usings:
```csharp
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
```

- [ ] **Step 2: Replace the parser call in ParseStoredFileAsync**

Find the line `var extension = Path.GetExtension(entity.SourceFileKey);` and replace from that line through the parsedOrder assignment:

```csharp
var extension = Path.GetExtension(entity.SourceFileKey).ToLowerInvariant();

var poMapping = await _poMappingService.GetAsync(organisationId, entity.SupplierId, ct);

ParsedOrder parsedOrder;

if (poMapping is not null && extension == ".csv")
{
    parsedOrder = await ParseWithMappingTemplateAsync(buffer, poMapping, ct);
}
else
{
    var parser = _parserFactory.GetParser(extension);
    parsedOrder = await parser.ParseAsync(buffer, ct);
}
```

- [ ] **Step 3: Add ParseWithMappingTemplateAsync helper method**

Add as a private static method in the `OrderService` class:

```csharp
private static Task<ParsedOrder> ParseWithMappingTemplateAsync(
    byte[] buffer, PoMappingConfig config, CancellationToken ct)
{
    using var stream = new MemoryStream(buffer);
    using var reader = new StreamReader(stream);

    var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = config.HasHeaderRecord,
        Delimiter = config.Separator,
        PrepareHeaderForMatch = args => args.Header?.ToLowerInvariant().Trim() ?? string.Empty,
        MissingFieldFound = null,
        BadDataFound = null,
    };

    using var csv = new CsvReader(reader, csvConfig);
    csv.Read();
    csv.ReadHeader();
    var headers = csv.HeaderRecord ?? Array.Empty<string>();

    var allRows = new List<Dictionary<string, string>>();
    while (csv.Read())
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
            row[h] = csv.GetField(h) ?? string.Empty;
        allRows.Add(row);
    }

    // For flat PO CSVs: first row provides header-section values, all rows provide lines
    var headerRow = allRows.Count > 0
        ? (IReadOnlyDictionary<string, string>)allRows[0]
        : new Dictionary<string, string>();
    var lineRows = allRows.Cast<IReadOnlyDictionary<string, string>>().ToList();

    var mapped = PoMappingEngine.Apply(headerRow, lineRows, config);

    DateOnly? orderDate = null;
    if (mapped.OrderDate is not null && DateOnly.TryParse(mapped.OrderDate, out var d))
        orderDate = d;

    var lines = mapped.Lines.Select((l, i) => new ParsedOrderLine(
        LineNumber:    l.LineNumber ?? (i + 1).ToString(),
        BuyerItemCode: l.BuyerItemCode ?? string.Empty,
        Description:   l.Description,
        Quantity:      decimal.TryParse(l.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) ? qty : 0,
        Unit:          l.Unit,
        UnitPrice:     decimal.TryParse(l.UnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var up) ? up : null
    )).ToList();

    return Task.FromResult(new ParsedOrder(
        PoNumber:  mapped.PoNumber ?? string.Empty,
        OrderDate: orderDate,
        BuyerName: mapped.BuyerName,
        Currency:  mapped.Currency,
        Lines:     lines
    ));
}
```

- [ ] **Step 4: Build**

```
dotnet build ProcuLink.sln
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```
git add ProcuLink.Api/Services/OrderService.cs
git commit -m "feat: integrate PoMappingEngine into OrderService.ParseStoredFileAsync"
```

---

## Task 10: Frontend TypeScript Types + API Client

**Files:**
- Modify: `src/lib/api/types.ts` (create if absent)
- Create: `src/lib/api/mapping.ts`

> All commands from `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`.

- [ ] **Step 1: Add TypeScript types**

In `src/lib/api/types.ts`, append:

```typescript
// PO Mapping Engine

export interface ManipulatorEntry {
  type: string;
  params: string[];
}

export interface FieldMappingEntry {
  externalField?: string;
  fixedValue?: string;
  fieldManipulators?: ManipulatorEntry[];
}

export interface PoMappingConfig {
  hasHeaderRecord: boolean;
  separator: string;
  header: Record<string, FieldMappingEntry>;
  lines: Record<string, FieldMappingEntry>;
}

export interface MappedOrderLine {
  lineNumber?: string;
  buyerItemCode?: string;
  description?: string;
  quantity?: string;
  unit?: string;
  unitPrice?: string;
}

export interface MappedOrder {
  poNumber?: string;
  orderDate?: string;
  buyerName?: string;
  currency?: string;
  lines: MappedOrderLine[];
}

export interface TestPoMappingRequest {
  headerRow: Record<string, string>;
  lineRows: Record<string, string>[];
  config: PoMappingConfig;
}
```

- [ ] **Step 2: Create API client**

Create `src/lib/api/mapping.ts`:

```typescript
import type { PoMappingConfig, MappedOrder, TestPoMappingRequest } from "./types";

const BASE = "/api";

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { "Content-Type": "application/json", ...init?.headers },
    ...init,
  });
  if (res.status === 204) return undefined as T;
  if (!res.ok) throw new Error(`API error ${res.status}: ${await res.text()}`);
  return res.json() as Promise<T>;
}

export async function getPoMapping(supplierId: string): Promise<PoMappingConfig | null> {
  return apiFetch<PoMappingConfig | null>(`/suppliers/${supplierId}/po-mapping`);
}

export async function upsertPoMapping(
  supplierId: string,
  config: PoMappingConfig
): Promise<PoMappingConfig> {
  return apiFetch<PoMappingConfig>(`/suppliers/${supplierId}/po-mapping`, {
    method: "PUT",
    body: JSON.stringify(config),
  });
}

export async function deletePoMapping(supplierId: string): Promise<void> {
  return apiFetch<void>(`/suppliers/${supplierId}/po-mapping`, { method: "DELETE" });
}

export async function testPoMapping(
  supplierId: string,
  request: TestPoMappingRequest
): Promise<MappedOrder> {
  return apiFetch<MappedOrder>(`/suppliers/${supplierId}/po-mapping/test`, {
    method: "POST",
    body: JSON.stringify(request),
  });
}
```

- [ ] **Step 3: TypeScript check**

```
node_modules\.bin\tsc.exe --noEmit
```

Expected: exit 0.

- [ ] **Step 4: Commit**

```
git add src/lib/api/types.ts src/lib/api/mapping.ts
git commit -m "feat: add PoMappingConfig types and API client functions"
```

---

## Task 11: PoMappingEditor Component + Supplier Tab

**Files:**
- Create: `src/components/bridge/PoMappingEditor.tsx`
- Modify: `src/components/bridge/SupplierDockProfile.tsx`

- [ ] **Step 1: Create PoMappingEditor**

Create `src/components/bridge/PoMappingEditor.tsx`:

```tsx
"use client";

import { useState } from "react";
import type { PoMappingConfig, FieldMappingEntry } from "@/lib/api/types";

const CANONICAL_HEADER_FIELDS = ["PoNumber", "OrderDate", "BuyerName", "Currency"] as const;
const CANONICAL_LINE_FIELDS = [
  "LineNumber", "BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice"
] as const;

const EMPTY_CONFIG: PoMappingConfig = {
  hasHeaderRecord: true,
  separator: ",",
  header: {},
  lines: {},
};

interface PoMappingEditorProps {
  supplierId: string;
  initialConfig: PoMappingConfig | null;
  onSave: (config: PoMappingConfig) => Promise<void>;
  onDelete?: () => Promise<void>;
  saving?: boolean;
}

export function PoMappingEditor({
  supplierId: _supplierId,
  initialConfig,
  onSave,
  onDelete,
  saving = false,
}: PoMappingEditorProps) {
  const [config, setConfig] = useState<PoMappingConfig>(initialConfig ?? EMPTY_CONFIG);
  const [activeSection, setActiveSection] = useState<"header" | "lines">("header");

  function updateEntry(
    section: "header" | "lines",
    field: string,
    patch: Partial<FieldMappingEntry>
  ) {
    setConfig((prev) => ({
      ...prev,
      [section]: {
        ...prev[section],
        [field]: { ...(prev[section][field] ?? {}), ...patch },
      },
    }));
  }

  const sectionFields =
    activeSection === "header" ? CANONICAL_HEADER_FIELDS : CANONICAL_LINE_FIELDS;

  return (
    <div className="rounded-[8px] overflow-hidden" style={{ border: "1px solid #E2E6EE" }}>
      {/* Toolbar */}
      <div
        className="flex items-center gap-3 px-4 py-3"
        style={{ borderBottom: "1px solid #E2E6EE", background: "#F6F7FA" }}
      >
        <div className="flex items-center gap-2">
          <span className="text-[12px] font-medium" style={{ color: "#56627A" }}>Separator</span>
          <select
            value={config.separator}
            onChange={(e) => setConfig((p) => ({ ...p, separator: e.target.value }))}
            className="text-[12px] rounded-[5px] px-2 py-1"
            style={{ border: "1px solid #D5DAEA", background: "#FFF", color: "#0B1A2F" }}
          >
            <option value=",">, (comma)</option>
            <option value=";">; (semicolon)</option>
            <option value={"\t"}>tab</option>
            <option value="|">| (pipe)</option>
          </select>
        </div>

        <label className="flex items-center gap-1.5 text-[12px]" style={{ color: "#56627A" }}>
          <input
            type="checkbox"
            checked={config.hasHeaderRecord}
            onChange={(e) => setConfig((p) => ({ ...p, hasHeaderRecord: e.target.checked }))}
          />
          Has header row
        </label>

        <div className="flex-1" />

        <div className="flex rounded-[6px] overflow-hidden" style={{ border: "1px solid #D5DAEA" }}>
          {(["header", "lines"] as const).map((s) => (
            <button
              key={s}
              onClick={() => setActiveSection(s)}
              className="px-3 py-1 text-[12px] font-medium transition-colors"
              style={{
                background: activeSection === s ? "#0B1A2F" : "#FFF",
                color: activeSection === s ? "#FFF" : "#56627A",
              }}
            >
              {s === "header" ? "Order Header" : "Order Lines"}
            </button>
          ))}
        </div>
      </div>

      {/* Column headers */}
      <div
        className="grid px-4 py-2 text-[11px] font-semibold uppercase tracking-wide"
        style={{ gridTemplateColumns: "160px 1fr 1fr", color: "#8A93A5" }}
      >
        <span>Canonical field</span>
        <span>Source column</span>
        <span>Fixed value</span>
      </div>

      {/* Mapping rows */}
      <div className="divide-y" style={{ borderColor: "#F0F2F7" }}>
        {sectionFields.map((field) => {
          const entry: FieldMappingEntry = config[activeSection][field] ?? {};
          return (
            <div
              key={field}
              className="grid items-center px-4 py-2.5"
              style={{ gridTemplateColumns: "160px 1fr 1fr" }}
            >
              <span
                className="text-[12.5px] font-medium"
                style={{ color: "#0B1A2F", fontFamily: "JetBrains Mono, monospace" }}
              >
                {field}
              </span>
              <input
                type="text"
                placeholder="CSV column name"
                value={entry.externalField ?? ""}
                onChange={(e) =>
                  updateEntry(activeSection, field, { externalField: e.target.value || undefined })
                }
                className="mr-4 rounded-[5px] px-2.5 py-1 text-[12px]"
                style={{ border: "1px solid #D5DAEA", color: "#0B1A2F" }}
              />
              <input
                type="text"
                placeholder="Fixed value (optional)"
                value={entry.fixedValue ?? ""}
                onChange={(e) =>
                  updateEntry(activeSection, field, { fixedValue: e.target.value || undefined })
                }
                className="rounded-[5px] px-2.5 py-1 text-[12px]"
                style={{ border: "1px solid #D5DAEA", color: "#0B1A2F" }}
              />
            </div>
          );
        })}
      </div>

      {/* Footer */}
      <div
        className="flex items-center gap-3 px-4 py-3"
        style={{ borderTop: "1px solid #E2E6EE", background: "#F6F7FA" }}
      >
        {onDelete && (
          <button
            onClick={onDelete}
            className="text-[12px] font-medium"
            style={{ color: "#C53A3A" }}
          >
            Delete mapping
          </button>
        )}
        <div className="flex-1" />
        <button
          onClick={() => onSave(config)}
          disabled={saving}
          className="flex items-center rounded-[6px] px-4 text-[13px] font-semibold"
          style={{ height: 32, background: saving ? "#8A93A5" : "#0B1A2F", color: "#FFF", border: "none" }}
        >
          {saving ? "Saving..." : "Save mapping"}
        </button>
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Add "PO Mapping" tab to SupplierDockProfile**

In `src/components/bridge/SupplierDockProfile.tsx`:

1. Find `type Tab` and add `"po-mapping"`:
```typescript
type Tab = "overview" | "mappings" | "po-mapping" | "rules" | "templates" | "connectors" | "history";
```

2. In the `TABS` array, add between "mappings" and "rules":
```typescript
{ id: "po-mapping" as Tab, label: "PO Mapping" },
```

3. Add imports at the top:
```typescript
import { useState } from "react";
import { PoMappingEditor } from "./PoMappingEditor";
import { upsertPoMapping, deletePoMapping } from "@/lib/api/mapping";
import type { PoMappingConfig } from "@/lib/api/types";
```

4. Add state inside the component:
```typescript
const [poMappingConfig, setPoMappingConfig] = useState<PoMappingConfig | null>(null);
const [savingMapping, setSavingMapping] = useState(false);
```

5. Add the tab panel alongside the other tab content blocks:
```tsx
{activeTab === "po-mapping" && (
  <PoMappingEditor
    supplierId={supplier.id}
    initialConfig={poMappingConfig}
    saving={savingMapping}
    onSave={async (config) => {
      setSavingMapping(true);
      try {
        const saved = await upsertPoMapping(supplier.id, config);
        setPoMappingConfig(saved);
      } finally {
        setSavingMapping(false);
      }
    }}
    onDelete={
      poMappingConfig
        ? async () => {
            await deletePoMapping(supplier.id);
            setPoMappingConfig(null);
          }
        : undefined
    }
  />
)}
```

- [ ] **Step 3: TypeScript check**

```
node_modules\.bin\tsc.exe --noEmit
```

Expected: exit 0.

- [ ] **Step 4: Commit**

```
git add src/components/bridge/PoMappingEditor.tsx src/components/bridge/SupplierDockProfile.tsx src/lib/api/
git commit -m "feat: add PoMappingEditor component and PO Mapping tab to SupplierDockProfile"
```

---

## Task 12: Push Both Repos + Update STATUS.md

- [ ] **Step 1: Run full test suite**

From `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`:

```
dotnet test ProcuLink.sln
```

Expected: All tests PASS.

- [ ] **Step 2: Push backend**

```
git push origin main
```

- [ ] **Step 3: Push frontend**

From `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`:

```
git pull --rebase origin main
git push origin main
```

- [ ] **Step 4: Update STATUS.md**

In `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\STATUS.md`:

1. Change `## Where we are` to: `## Where we are: **Phase 4 Group D2 — Supplier Delivery Config**`
2. Add to the completed phases table: `| **Group D** OK | PO Field Mapping Engine -- 12 tasks done |`
3. Remove the "Group D -- PO Field Mapping Engine" section.
4. Add a brief "Group D2" note: `Group D2 (supplier delivery config -- HTTP/SFTP/FTP) is the next group. Design spec not yet created.`

- [ ] **Step 5: Commit and push**

```
git add STATUS.md
git commit -m "docs: mark Group D complete, set Group D2 as next"
git push origin main
```

---

## Self-Review

**Spec coverage:**
- OK `PoMappingConfig` + `FieldMappingEntry` + `ManipulatorEntry` POCOs -- Task 1
- OK `SupplierPoMapping` entity + JSONB migration -- Task 2
- OK All 8 manipulators (Replace, Trim, DateFormat, Concat, Fallback, Split, Multiply, Divide) -- Tasks 3-5
- OK `PoMappingEngine.Apply` -- Task 6
- OK `IPoMappingService` + `PoMappingService` -- Tasks 1 + 7
- OK 4 API endpoints (GET, PUT, DELETE, POST /test) -- Task 8
- OK `OrderService` template-aware branch -- Task 9
- OK Frontend TS types -- Task 10
- OK `PoMappingEditor` component + supplier tab -- Task 11

**No placeholders.** All code blocks complete.

**Type consistency:** `PoMappingConfig` identical across Core POCOs, Infrastructure service, API controller, and TypeScript client.
