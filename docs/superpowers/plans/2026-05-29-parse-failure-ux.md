# Parse-Failure UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface human-readable parse-error messages in the frontend so users landing on a failed-parse order see an actionable `ParseFailedPanel` with a re-upload CTA, and clearly distinguish parse / transform / delivery failures.

**Architecture:** Backend adds a `ParseFailureExplain` helper that generates operator-friendly messages, closes two audit-event gaps in `OrderService.ParseStoredFileAsync`, and exposes `ErrorMessage` on `OrderDto`. Frontend gets a `ParseFailedPanel` + `FailedPanel` component pair wired into both `OrderDetailPage` and `SpineReview`, with detect-format result caching via `sessionStorage` and supplier preselect on the `/upload` re-upload link.

**Tech Stack:** .NET 8 / ASP.NET Core, EF Core 8 InMemory (tests), xUnit, Moq 4, FluentAssertions; Next.js 15 App Router, React 18, TanStack Query v5, TypeScript, Tailwind.

---

## File Map

### Backend — create
- `ProcuLink.Api/Services/ParseFailureExplain.cs` — pure static helper, friendly message factory
- `ProcuLink.Api.Tests/Services/ParseFailureExplainTests.cs` — unit tests for all three methods
- `ProcuLink.Api.Tests/Services/OrderServiceParseAuditTests.cs` — integration-style tests: ParseStoredFileAsync writes ParseFailed audit events
- `ProcuLink.Api.Tests/Controllers/OrdersControllerErrorMessageTests.cs` — controller test: GET /api/orders/{id} returns errorMessage

### Backend — modify
- `ProcuLink.Api/Contracts/OrderDto.cs` — add `ErrorMessage` optional parameter
- `ProcuLink.Api/Controllers/OrdersController.cs` — Get action: query newest failed audit event, pass errorMessage to MapToDto; MapToDto signature update
- `ProcuLink.Api/Services/OrderService.cs` — close two ParseFailed audit gaps; upgrade exception-catch message to ParseFailureExplain

### Frontend — create
- `project-proculink/src/components/bridge/FailedPanels.tsx` — `ParseFailedPanel` + `FailedPanel` components

### Frontend — modify
- `project-proculink/src/types/procurement.ts` — add `errorMessage?: string | null` to `Order`
- `project-proculink/src/lib/api-client.ts` — add `redeliverOrder` mock + real function and export
- `project-proculink/src/views/OrderDetailPage.tsx` — add three-branch failed gate before main render
- `project-proculink/src/components/bridge/SpineReview.tsx` — add `status === "failed"` branch before existing error gate
- `project-proculink/src/components/bridge/UploadWorkbench.tsx` — cache detect-format result after upload; preselect supplier from URL param

---

## Task 1: `ParseFailureExplain` helper (TDD)

**Files:**
- Create: `ProcuLink.Api/Services/ParseFailureExplain.cs`
- Create: `ProcuLink.Api.Tests/Services/ParseFailureExplainTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ProcuLink.Api.Tests/Services/ParseFailureExplainTests.cs`:

```csharp
using ProcuLink.Api.Services;

namespace ProcuLink.Api.Tests.Services;

public class ParseFailureExplainTests
{
    // ── ForEmptyLines ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".pdf",  "scanned or image-only")]
    [InlineData(".PDF",  "scanned or image-only")]   // case-insensitive
    [InlineData(".csv",  "No line-table columns")]
    [InlineData(".CSV",  "No line-table columns")]
    [InlineData(".xlsx", "No line-table columns")]
    [InlineData(".xls",  "No line-table columns")]
    [InlineData(".xml",  "zero line items")]
    [InlineData(".edi",  "zero line items")]
    public void ForEmptyLines_ReturnsFormatSpecificMessage(string ext, string expectedFragment)
    {
        var msg = ParseFailureExplain.ForEmptyLines(ext);
        Assert.Contains(expectedFragment, msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── ForUnsupportedFormat ──────────────────────────────────────────────────

    [Theory]
    [InlineData(".rar")]
    [InlineData(".docx")]
    [InlineData(".zip")]
    public void ForUnsupportedFormat_IncludesExtensionAndSupportedList(string ext)
    {
        var msg = ParseFailureExplain.ForUnsupportedFormat(ext);
        Assert.Contains(ext, msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Supported:", msg);
    }

    // ── ForException ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".edi",  "EDI file")]
    [InlineData(".txt",  "EDI file")]
    [InlineData(".x12",  "EDI file")]
    [InlineData(".xml",  "XML file")]
    [InlineData(".cxml", "XML file")]
    [InlineData(".csv",  "Could not parse file")]
    [InlineData(".xlsx", "Could not parse file")]
    [InlineData(".pdf",  "Could not parse file")]
    public void ForException_ReturnsContextualCopy(string ext, string expectedFragment)
    {
        var ex = new Exception("test detail message");
        var msg = ParseFailureExplain.ForException(ext, ex);
        Assert.Contains(expectedFragment, msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test detail message", msg);
    }
}
```

- [ ] **Step 2: Run tests — expect compile error (class doesn't exist yet)**

```
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore
```

Expected: build error `CS0234: The type or namespace name 'ParseFailureExplain' does not exist`.

- [ ] **Step 3: Create the implementation**

Create `ProcuLink.Api/Services/ParseFailureExplain.cs`:

```csharp
namespace ProcuLink.Api.Services;

/// <summary>
/// Produces operator-friendly error messages for parse failures.
/// Pure static helper — no dependencies.
/// </summary>
public static class ParseFailureExplain
{
    public static string ForEmptyLines(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".pdf"                    => "This PDF looks scanned or image-only — we couldn't extract any text. OCR isn't enabled; export a text-based PDF or upload a CSV/XLSX instead.",
            ".csv" or ".xlsx" or ".xls" => "No line-table columns detected. We couldn't find recognisable item columns (item code, quantity, unit price). Check the header row or map columns using a PO template.",
            _                         => "The document was read but contained zero line items.",
        };

    public static string ForUnsupportedFormat(string extension) =>
        $"Unsupported file format '{extension}'. Supported: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT).";

    public static string ForException(string extension, Exception ex)
    {
        var ext = extension.ToLowerInvariant();
        if (ext is ".edi" or ".txt" or ".x12")
            return $"We couldn't read this EDI file: {ex.Message}";
        if (ext is ".xml" or ".cxml")
            return $"We couldn't read this XML file: {ex.Message}";
        return $"Could not parse file: {ex.Message}";
    }
}
```

- [ ] **Step 4: Run tests — expect all pass**

```
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "ParseFailureExplainTests"
```

Expected: `Test Run Successful. Total: 17`.

- [ ] **Step 5: Commit**

```
git add ProcuLink.Api/Services/ParseFailureExplain.cs ProcuLink.Api.Tests/Services/ParseFailureExplainTests.cs
git commit -m "feat(parse-failure): add ParseFailureExplain friendly-message helper"
```

---

## Task 2: Close audit gaps in `OrderService.ParseStoredFileAsync` (TDD)

**Files:**
- Create: `ProcuLink.Api.Tests/Services/OrderServiceParseAuditTests.cs`
- Modify: `ProcuLink.Api/Services/OrderService.cs`

- [ ] **Step 1: Write the failing tests**

Create `ProcuLink.Api.Tests/Services/OrderServiceParseAuditTests.cs`:

```csharp
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

public class OrderServiceParseAuditTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(
        ProcuLinkDbContext db,
        IFileStorageService fileStorage)
    {
        var parserFactory = new OrderParserFactory(new IPurchaseOrderParser[]
        {
            new CsvOrderParser(),
            new XlsxOrderParser(),
            new PdfOrderParser(),
        });

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<AiMappingLineContext>(), It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiMappingSuggestion?)null);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage,
            parserFactory,
            itemMappings.Object,
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object);
    }

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId)> SeedParsingOrderAsync(
        string fileKeyExtension)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-TEST",
            OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency      = "EUR",
            Status        = "parsing",
            SourceFileKey = $"{orgId}/{orderId}/file{fileKeyExtension}",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, orgId, orderId);
    }

    [Fact]
    public async Task ParseStoredFileAsync_EmptyLinesCsv_WritesParseFailed_WithFriendlyMessage()
    {
        var (db, orgId, orderId) = await SeedParsingOrderAsync(".csv");

        // CSV with a header row but no data rows → Lines.Count == 0
        var csvBytes = Encoding.UTF8.GetBytes("foo,bar,baz\n");
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var svc = BuildService(db, fileStorage.Object);
        var result = await svc.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);

        // db is the same context instance passed to BuildService, so audit events written
        // by OrderService are visible here without needing a new context.
        var auditEvent = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == orderId && e.Action == "ParseFailed")
            .FirstOrDefaultAsync();

        Assert.NotNull(auditEvent);
        Assert.NotNull(auditEvent.Payload);
        var errorProp = auditEvent.Payload!.RootElement.GetProperty("error").GetString();
        Assert.NotNull(errorProp);
        Assert.Contains("No line-table columns", errorProp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseStoredFileAsync_UnsupportedFormat_WritesParseFailed_WithFriendlyMessage()
    {
        var (db, orgId, orderId) = await SeedParsingOrderAsync(".rar");

        // Any bytes — the parser factory throws before reading the file
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x52, 0x61, 0x72 }));

        var svc = BuildService(db, fileStorage.Object);
        var result = await svc.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);

        var auditEvent = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == orderId && e.Action == "ParseFailed")
            .FirstOrDefaultAsync();

        Assert.NotNull(auditEvent);
        var errorProp = auditEvent!.Payload!.RootElement.GetProperty("error").GetString();
        Assert.NotNull(errorProp);
        Assert.Contains("Unsupported file format", errorProp, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests — expect failures**

```
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "OrderServiceParseAuditTests"
```

Expected: both tests FAIL. The `SeedParsingOrderAsync` and DB helpers compile but the assertions on `AuditEvent` rows fail because the events are not yet written.

- [ ] **Step 3: Fix the three paths in `OrderService.ParseStoredFileAsync`**

Open `ProcuLink.Api/Services/OrderService.cs`. Add `using ProcuLink.Api.Services;` if not already in the `using` block (it's in the same project so the namespace is available without an explicit using).

**Path 1 — unsupported format (~line 434–441):**

Find:
```csharp
            try { _parserFactory.GetParser(extension); }
            catch (UnsupportedFileFormatException ex)
            {
                await SetOrderFailedAsync(orderId, organisationId, ct);
                return Result<PurchaseOrderEntity>.Fail(ex.Message);
            }
```

Replace with:
```csharp
            try { _parserFactory.GetParser(extension); }
            catch (UnsupportedFileFormatException ex)
            {
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForUnsupportedFormat(extension), stage = "parse", detail = ex.Message }));
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail(ex.Message);
            }
```

**Path 2 — exception catch (~line 457–465):**

Find:
```csharp
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse file for order {OrderId}", orderId);
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ex.Message }));
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail($"Could not parse file: {ex.Message}");
            }
```

Replace with:
```csharp
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse file for order {OrderId}", orderId);
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForException(extension, ex), stage = "parse", detail = ex.Message }));
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail($"Could not parse file: {ex.Message}");
            }
```

**Path 3 — no line items (~line 467–470):**

Find:
```csharp
            if (parsedOrder.Lines.Count == 0)
            {
                await SetOrderFailedAsync(orderId, organisationId, ct);
                return Result<PurchaseOrderEntity>.Fail("File contains no line items.");
            }
```

Replace with:
```csharp
            if (parsedOrder.Lines.Count == 0)
            {
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForEmptyLines(extension), stage = "parse", detail = "0 lines parsed" }));
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail("File contains no line items.");
            }
```

Note: the existing exception-catch path (Path 2) already had `_db.AuditEvents.Add(...)` + `_db.SaveChangesAsync(ct)` — just update the `error` field to use `ParseFailureExplain.ForException`. If that `SaveChangesAsync` call was missing in your version, add it as shown.

- [ ] **Step 4: Run tests — expect all pass**

```
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "OrderServiceParseAuditTests"
```

Expected: `Test Run Successful. Total: 2`.

- [ ] **Step 5: Run full test suite to confirm no regressions**

```
dotnet test ProcuLink.slnx --no-restore
```

Expected: all tests pass (272 + 2 new = 274 minimum).

- [ ] **Step 6: Commit**

```
git add ProcuLink.Api/Services/OrderService.cs ProcuLink.Api.Tests/Services/OrderServiceParseAuditTests.cs
git commit -m "feat(parse-failure): close ParseFailed audit gaps in OrderService; friendly messages"
```

---

## Task 3: `ErrorMessage` on `OrderDto` + controller wire-up (TDD)

**Files:**
- Create: `ProcuLink.Api.Tests/Controllers/OrdersControllerErrorMessageTests.cs`
- Modify: `ProcuLink.Api/Contracts/OrderDto.cs`
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs`

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Api.Tests/Controllers/OrdersControllerErrorMessageTests.cs`:

```csharp
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Controllers;

public class OrdersControllerErrorMessageTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Get_FailedOrderWithParsedFailedAuditEvent_ReturnsErrorMessage()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        const string friendlyMessage = "No line-table columns detected. We couldn't find recognisable item columns.";

        await using var db = NewDb();
        db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = orderId,
            Action     = "ParseFailed",
            Payload    = JsonDocument.Parse(
                $$$"""{"error":"{{{friendlyMessage}}}","stage":"parse","detail":"0 lines parsed"}"""),
            CreatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var failedEntity = new PurchaseOrderEntity
        {
            Id               = orderId,
            OrgId            = orgId,
            SupplierId       = Guid.NewGuid(),
            PoNumber         = "PO-FAIL",
            OrderDate        = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency         = "EUR",
            Status           = "failed",
            SourceFileKey    = $"{orgId}/{orderId}/file.csv",
            CreatedAt        = DateTime.UtcNow,
            UpdatedAt        = DateTime.UtcNow,
            Lines            = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(failedEntity));

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new OrdersController(
            ordersSvc.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object);

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Equal(friendlyMessage, dto.ErrorMessage);
    }

    [Fact]
    public async Task Get_ReadyOrder_ReturnsNullErrorMessage()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var db = NewDb();

        var readyEntity = new PurchaseOrderEntity
        {
            Id               = orderId,
            OrgId            = orgId,
            SupplierId       = Guid.NewGuid(),
            PoNumber         = "PO-READY",
            OrderDate        = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency         = "EUR",
            Status           = "ready",
            CreatedAt        = DateTime.UtcNow,
            UpdatedAt        = DateTime.UtcNow,
            Lines            = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(readyEntity));

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new OrdersController(
            ordersSvc.Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object);

        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);
        Assert.Null(dto.ErrorMessage);
    }
}
```

- [ ] **Step 2: Run tests — expect compile errors**

```
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "OrdersControllerErrorMessageTests"
```

Expected: compile error — `OrderDto` has no `ErrorMessage` property.

- [ ] **Step 3: Add `ErrorMessage` to `OrderDto`**

Open `ProcuLink.Api/Contracts/OrderDto.cs`. Change:

```csharp
public record OrderDto(
    Guid       Id,
    string     PoNumber,
    Guid       SupplierId,
    string     SupplierName,
    string     OrderDate,
    string     Currency,
    string     Status,
    string?    SourceFileKey,
    DateTime   CreatedAt,
    DateTime   UpdatedAt,
    IReadOnlyList<OrderLineDto>    Lines,
    IReadOnlyList<ArtifactDto>     Artifacts,
    /// <summary>Buyer name extracted from CanonicalJson; null until parsing completes.</summary>
    string?    BuyerName = null
);
```

To:

```csharp
public record OrderDto(
    Guid       Id,
    string     PoNumber,
    Guid       SupplierId,
    string     SupplierName,
    string     OrderDate,
    string     Currency,
    string     Status,
    string?    SourceFileKey,
    DateTime   CreatedAt,
    DateTime   UpdatedAt,
    IReadOnlyList<OrderLineDto>    Lines,
    IReadOnlyList<ArtifactDto>     Artifacts,
    /// <summary>Buyer name extracted from CanonicalJson; null until parsing completes.</summary>
    string?    BuyerName = null,
    /// <summary>Human-readable error message from the newest *Failed audit event; null for non-failed orders.</summary>
    string?    ErrorMessage = null
);
```

- [ ] **Step 4: Update `MapToDto` and `Get` in `OrdersController`**

Open `ProcuLink.Api/Controllers/OrdersController.cs`.

**4a. Update `MapToDto` signature** — find:
```csharp
    private static OrderDto MapToDto(PurchaseOrderEntity e) => new(
```
Replace with:
```csharp
    private static OrderDto MapToDto(PurchaseOrderEntity e, string? errorMessage = null) => new(
```

Add `ErrorMessage: errorMessage` as the last constructor argument — find the closing of the `new(` call:
```csharp
        BuyerName: ExtractBuyerName(e)
    );
```
Replace with:
```csharp
        BuyerName:    ExtractBuyerName(e),
        ErrorMessage: errorMessage
    );
```

**4b. Update the `Get` action** — find:
```csharp
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetByIdAsync(_tenant.OrganisationId, id, ct);

        if (!result.IsSuccess)
            return NotFound();

        return Ok(MapToDto(result.Value!));
    }
```

Replace with:
```csharp
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await _orders.GetByIdAsync(_tenant.OrganisationId, id, ct);

        if (!result.IsSuccess)
            return NotFound();

        var entity = result.Value!;
        string? errorMessage = null;

        if (entity.Status is "failed" or "transform_failed" or "delivery_failed")
        {
            var payload = await _db.AuditEvents
                .AsNoTracking()
                .Where(e => e.EntityId == id
                         && e.OrgId == _tenant.OrganisationId
                         && e.EntityType == "Order"
                         && (e.Action == "ParseFailed"
                          || e.Action == "TransformFailed"
                          || e.Action == "DeliveryFailed"))
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => e.Payload)
                .FirstOrDefaultAsync(ct);

            if (payload != null)
            {
                try
                {
                    if (payload.RootElement.TryGetProperty("error", out var el))
                        errorMessage = el.GetString();
                }
                catch { /* malformed payload — ignore */ }
            }
        }

        return Ok(MapToDto(entity, errorMessage));
    }
```

- [ ] **Step 5: Run the controller tests — expect pass**

```
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "OrdersControllerErrorMessageTests"
```

Expected: `Test Run Successful. Total: 2`.

- [ ] **Step 6: Run full suite**

```
dotnet test ProcuLink.slnx --no-restore
```

Expected: all pass.

- [ ] **Step 7: Commit**

```
git add ProcuLink.Api/Contracts/OrderDto.cs ProcuLink.Api/Controllers/OrdersController.cs ProcuLink.Api.Tests/Controllers/OrdersControllerErrorMessageTests.cs
git commit -m "feat(parse-failure): add ErrorMessage to OrderDto; wire into GET /api/orders/{id}"
```

---

## Task 4: Frontend — extend `Order` type + add `redeliverOrder` to api-client

**Files:**
- Modify: `project-proculink/src/types/procurement.ts`
- Modify: `project-proculink/src/lib/api-client.ts`

- [ ] **Step 1: Add `errorMessage` to the `Order` interface**

Open `project-proculink/src/types/procurement.ts`. In the `Order` interface, add the new field after `isSample`:

```ts
export interface Order {
  id: string;
  poNumber: string;
  supplierId: string;
  supplierName: string;
  /** Buyer name extracted from canonical JSON after parsing; null while parsing. */
  buyerName?: string | null;
  orderDate: string; // "yyyy-MM-dd"
  currency: string;
  status: OrderStatus;
  sourceFileKey?: string | null;
  createdAt: string;
  updatedAt: string;
  lines: OrderLine[];
  artifacts: Artifact[];
  /** True when this order was created by the onboarding sample-order endpoint. */
  isSample?: boolean;
  /** Human-readable error message from the newest *Failed audit event; null for non-failed orders. */
  errorMessage?: string | null;
}
```

- [ ] **Step 2: Add `redeliverOrder` to api-client**

Open `project-proculink/src/lib/api-client.ts`.

Add the mock function — place it near other mock order functions (around the mock section):

```ts
async function mockRedeliverOrder(_orderId: string): Promise<void> {
  await delay(800);
  // Mock always succeeds — live wiring verified by manual QA
}
```

Add the real function — place it near other real order functions (near `realGetOrderAudit`):

```ts
async function realRedeliverOrder(orderId: string): Promise<void> {
  const res = await fetchWithTimeout(
    `${API_BASE_URL}/api/orders/${orderId}/redeliver`,
    { method: "POST", headers: await authHeader() },
  );
  if (!res.ok) {
    const body = await res.json().catch(() => ({}) as Record<string, unknown>);
    throw new Error(
      (body as { error?: string }).error ?? `Redeliver failed: ${res.statusText}`,
    );
  }
}
```

Add to the exported `apiClient` object (after `getOrderAudit`):

```ts
  redeliverOrder:         USE_MOCK ? mockRedeliverOrder        : realRedeliverOrder,
```

- [ ] **Step 3: Verify build**

```
cd project-proculink && bun run build
```

Expected: build succeeds (existing warnings remain; no new type errors).

- [ ] **Step 4: Commit**

```
git add project-proculink/src/types/procurement.ts project-proculink/src/lib/api-client.ts
git commit -m "feat(parse-failure): add errorMessage to Order type; add redeliverOrder to api-client"
```

---

## Task 5: Create `ParseFailedPanel` + `FailedPanel` components

**Files:**
- Create: `project-proculink/src/components/bridge/FailedPanels.tsx`

- [ ] **Step 1: Create the component file**

Create `project-proculink/src/components/bridge/FailedPanels.tsx`:

```tsx
"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { apiClient } from "@/lib/api-client";
import type { DetectFormatResult } from "@/lib/api-client";
import type { AuditEvent, Order } from "@/types/procurement";

// ─── Design tokens (Bridge Layer) ────────────────────────────────────────────

const T = {
  danger:      "#C53A3A",
  dangerSoft:  "#FBE3E3",
  amber:       "#C97A14",
  amberSoft:   "#FAEFD6",
  navy:        "#0B1A2F",
  ink:         "#0B1A2F",
  inkMuted:    "#56627A",
  inkFaint:    "#8A93A5",
  surface:     "#FFFFFF",
  surface2:    "#F1F3F7",
  border:      "#E2E6EE",
  bg:          "#F6F7FA",
  ui:          '"Inter", system-ui, sans-serif',
  mono:        '"JetBrains Mono", ui-monospace, monospace',
};

const SRC_META: Record<string, { bg: string; color: string; label: string }> = {
  pdf:   { bg: "#FEE2E2", color: "#B91C1C", label: "PDF"   },
  csv:   { bg: "#DBEAFE", color: "#1D4ED8", label: "CSV"   },
  xlsx:  { bg: "#DCFCE7", color: "#15803D", label: "XLSX"  },
  cxml:  { bg: "#CCFBF1", color: "#0F766E", label: "cXML"  },
  edi:   { bg: "#FEF3C7", color: "#B45309", label: "EDI"   },
  ubl:   { bg: "#CCFBF1", color: "#0F766E", label: "UBL"   },
  x12:   { bg: "#FEF3C7", color: "#B45309", label: "X12"   },
};

function deriveSourceFormat(fileKey: string | null | undefined): string | null {
  if (!fileKey) return null;
  const ext = fileKey.split(".").pop()?.toLowerCase() ?? "";
  if (ext === "pdf") return "pdf";
  if (ext === "csv") return "csv";
  if (ext === "xlsx" || ext === "xls") return "xlsx";
  if (ext === "xml" || ext === "cxml") return "cxml";
  if (ext === "edi" || ext === "x12") return "edi";
  return null;
}

// ─── ParseFailedPanel ─────────────────────────────────────────────────────────

export function ParseFailedPanel({
  order,
  auditEvents,
}: {
  order: Order;
  auditEvents?: AuditEvent[];
}) {
  const [detectResult, setDetectResult] = useState<DetectFormatResult | null>(null);

  useEffect(() => {
    try {
      const raw = sessionStorage.getItem(`detectResult:${order.id}`);
      if (raw) setDetectResult(JSON.parse(raw) as DetectFormatResult);
    } catch {
      // sessionStorage unavailable or JSON invalid — ignore
    }
  }, [order.id]);

  const errorMessage =
    order.errorMessage ??
    (auditEvents
      ?.find((e) => e.action === "ParseFailed")
      ?.payload?.["error"] as string | undefined) ??
    "The file could not be parsed. Try a different format or check the file contents.";

  const sourceFmt = deriveSourceFormat(order.sourceFileKey);
  const srcMeta = sourceFmt ? (SRC_META[sourceFmt] ?? null) : null;

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        minHeight: "60vh",
        padding: "32px 24px",
        background: T.bg,
        fontFamily: T.ui,
      }}
    >
      <div
        style={{
          width: "100%",
          maxWidth: 520,
          background: T.surface,
          border: `1px solid ${T.border}`,
          borderLeft: `3px solid ${T.danger}`,
          borderRadius: 10,
          overflow: "hidden",
        }}
      >
        {/* Header */}
        <div
          style={{
            padding: "14px 20px",
            display: "flex",
            alignItems: "center",
            justifyContent: "space-between",
            borderBottom: `1px solid ${T.border}`,
            background: T.dangerSoft,
          }}
        >
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={T.danger} strokeWidth="2">
              <circle cx="12" cy="12" r="10" />
              <line x1="12" y1="8" x2="12" y2="12" />
              <line x1="12" y1="16" x2="12.01" y2="16" />
            </svg>
            <span style={{ fontSize: 13, fontWeight: 700, color: T.danger }}>
              Parsing failed
            </span>
          </div>
          <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
            {srcMeta && (
              <span
                style={{
                  display: "inline-flex",
                  alignItems: "center",
                  height: 18,
                  padding: "0 6px",
                  borderRadius: 4,
                  fontSize: 10,
                  fontWeight: 700,
                  letterSpacing: "0.04em",
                  background: srcMeta.bg,
                  color: srcMeta.color,
                }}
              >
                {srcMeta.label}
              </span>
            )}
            {detectResult && (
              <span
                style={{
                  fontSize: 10.5,
                  color: T.inkFaint,
                  fontFamily: T.mono,
                }}
              >
                {Math.round((detectResult.confidence ?? 0) * 100)}% confidence
              </span>
            )}
          </div>
        </div>

        {/* Body */}
        <div style={{ padding: "16px 20px" }}>
          <p style={{ fontSize: 13.5, color: T.ink, lineHeight: 1.6, margin: "0 0 12px" }}>
            {errorMessage}
          </p>
          <p style={{ fontSize: 12, color: T.inkFaint, margin: "0 0 20px", lineHeight: 1.5 }}>
            Your source file is still stored and visible to support if you need help.
          </p>

          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Link
              href={`/upload?supplierId=${order.supplierId}`}
              style={{
                display: "inline-flex",
                alignItems: "center",
                justifyContent: "center",
                gap: 6,
                padding: "9px 18px",
                borderRadius: 7,
                background: T.navy,
                color: "#FFFFFF",
                fontSize: 13,
                fontWeight: 600,
                textDecoration: "none",
              }}
            >
              Re-upload — try a different format
              <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
                <path d="M6 3l5 5-5 5" stroke="#FFFFFF" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </Link>
            <Link
              href="/orders"
              style={{
                display: "inline-flex",
                alignItems: "center",
                justifyContent: "center",
                padding: "7px 14px",
                borderRadius: 7,
                background: "transparent",
                border: `1px solid ${T.border}`,
                color: T.inkMuted,
                fontSize: 12.5,
                fontWeight: 500,
                textDecoration: "none",
              }}
            >
              ← Back to orders
            </Link>
          </div>
        </div>
      </div>
    </div>
  );
}

// ─── FailedPanel ──────────────────────────────────────────────────────────────

export function FailedPanel({
  order,
  stage,
}: {
  order: Order;
  stage: "transform" | "delivery";
}) {
  const [isRetrying, setIsRetrying] = useState(false);
  const [retryError, setRetryError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  const isTransform  = stage === "transform";
  const accentColor  = isTransform ? T.amber : T.danger;
  const bgColor      = isTransform ? T.amberSoft : T.dangerSoft;
  const title        = isTransform ? "Output generation failed" : "Delivery to supplier failed";
  const errorMessage =
    order.errorMessage ??
    (isTransform
      ? "The transform step could not complete. Review the order and try again."
      : "The delivery attempt failed. Check the delivery config and try again.");

  async function handleRedeliver() {
    if (isRetrying) return;
    setIsRetrying(true);
    setRetryError(null);
    try {
      await apiClient.redeliverOrder(order.id);
      void queryClient.invalidateQueries({ queryKey: ["order", order.id] });
    } catch (err) {
      setRetryError(err instanceof Error ? err.message : "Retry failed. Check the delivery config.");
    } finally {
      setIsRetrying(false);
    }
  }

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        minHeight: "60vh",
        padding: "32px 24px",
        background: T.bg,
        fontFamily: T.ui,
      }}
    >
      <div
        style={{
          width: "100%",
          maxWidth: 520,
          background: T.surface,
          border: `1px solid ${T.border}`,
          borderLeft: `3px solid ${accentColor}`,
          borderRadius: 10,
          overflow: "hidden",
        }}
      >
        {/* Header */}
        <div
          style={{
            padding: "14px 20px",
            display: "flex",
            alignItems: "center",
            gap: 8,
            borderBottom: `1px solid ${T.border}`,
            background: bgColor,
          }}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={accentColor} strokeWidth="2">
            <path d="M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
            <line x1="12" y1="9" x2="12" y2="13" />
            <line x1="12" y1="17" x2="12.01" y2="17" />
          </svg>
          <span style={{ fontSize: 13, fontWeight: 700, color: accentColor }}>
            {title}
          </span>
        </div>

        {/* Body */}
        <div style={{ padding: "16px 20px" }}>
          <p style={{ fontSize: 13.5, color: T.ink, lineHeight: 1.6, margin: "0 0 12px" }}>
            {errorMessage}
          </p>
          {retryError && (
            <p
              style={{
                fontSize: 12,
                color: T.danger,
                margin: "0 0 12px",
                padding: "8px 12px",
                background: T.dangerSoft,
                borderRadius: 6,
                border: `1px solid ${T.danger}30`,
              }}
            >
              {retryError}
            </p>
          )}

          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {isTransform ? (
              <Link
                href={`/orders/${order.id}`}
                style={{
                  display: "inline-flex",
                  alignItems: "center",
                  justifyContent: "center",
                  padding: "9px 18px",
                  borderRadius: 7,
                  background: T.navy,
                  color: "#FFFFFF",
                  fontSize: 13,
                  fontWeight: 600,
                  textDecoration: "none",
                }}
              >
                Back to review
              </Link>
            ) : (
              <button
                onClick={() => void handleRedeliver()}
                disabled={isRetrying}
                style={{
                  display: "inline-flex",
                  alignItems: "center",
                  justifyContent: "center",
                  gap: 6,
                  padding: "9px 18px",
                  borderRadius: 7,
                  background: isRetrying ? T.inkFaint : T.navy,
                  color: "#FFFFFF",
                  fontSize: 13,
                  fontWeight: 600,
                  border: "none",
                  cursor: isRetrying ? "not-allowed" : "pointer",
                  fontFamily: T.ui,
                }}
              >
                {isRetrying ? "Retrying…" : "Retry delivery"}
              </button>
            )}
            <Link
              href="/orders"
              style={{
                display: "inline-flex",
                alignItems: "center",
                justifyContent: "center",
                padding: "7px 14px",
                borderRadius: 7,
                background: "transparent",
                border: `1px solid ${T.border}`,
                color: T.inkMuted,
                fontSize: 12.5,
                fontWeight: 500,
                textDecoration: "none",
              }}
            >
              ← Back to orders
            </Link>
          </div>
        </div>
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Verify TypeScript compiles**

```
cd project-proculink && bun run build
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```
git add project-proculink/src/components/bridge/FailedPanels.tsx
git commit -m "feat(parse-failure): add ParseFailedPanel + FailedPanel components"
```

---

## Task 6: Wire `FailedPanels` into `OrderDetailPage`

**Files:**
- Modify: `project-proculink/src/views/OrderDetailPage.tsx`

- [ ] **Step 1: Add import at the top of `OrderDetailPage.tsx`**

Find the existing imports block. Add after the last import line:

```ts
import { FailedPanel, ParseFailedPanel } from "@/components/bridge/FailedPanels";
```

- [ ] **Step 2: Add the three-branch gate before the main render**

In `OrderDetailPage`, after the `if (!order) return <SpineReviewSkeleton />;` guard (around line 765) and before the `const isProcessing = ...` line, insert:

```tsx
  // ── Failure gates — render before the full page so we don't need all fields ──
  if (order.status === "failed") {
    return <ParseFailedPanel order={order} auditEvents={auditEvents} />;
  }
  if (order.status === "transform_failed") {
    return <FailedPanel order={order} stage="transform" />;
  }
  if (order.status === "delivery_failed") {
    return <FailedPanel order={order} stage="delivery" />;
  }
```

These go **after** the existing `isError`, `isNotFound`, and `!order` guards, so by the time we hit them `order` is guaranteed to be non-null.

- [ ] **Step 3: Build**

```
cd project-proculink && bun run build
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```
git add project-proculink/src/views/OrderDetailPage.tsx
git commit -m "feat(parse-failure): wire ParseFailedPanel + FailedPanel into OrderDetailPage"
```

---

## Task 7: Wire `ParseFailedPanel` into `SpineReview`

**Files:**
- Modify: `project-proculink/src/components/bridge/SpineReview.tsx`

- [ ] **Step 1: Add import**

In `SpineReview.tsx`, add to the existing imports:

```ts
import { ParseFailedPanel } from "@/components/bridge/FailedPanels";
```

- [ ] **Step 2: Add the `failed` branch**

In the `SpineReview` component, find the existing error/null gate (around line 894):

```tsx
  if (isLoading) return <SpineReviewSkeleton />;
  if (isError || order === null) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-4" style={{ background: "#F6F7FA" }}>
        ...
      </div>
    );
  }
```

Insert a new branch **after** the `isError || order === null` block and **before** the main `return (...)`:

```tsx
  if (order?.status === "failed") {
    return <ParseFailedPanel order={order} />;
  }
```

The `auditEvents` prop is omitted here because `SpineReview` doesn't fetch audit events — `ParseFailedPanel` falls back to `order.errorMessage` which comes from the DTO.

- [ ] **Step 3: Build**

```
cd project-proculink && bun run build
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```
git add project-proculink/src/components/bridge/SpineReview.tsx
git commit -m "feat(parse-failure): wire ParseFailedPanel into SpineReview for status=failed"
```

---

## Task 8: Detect-format caching + supplier preselect in `UploadWorkbench`

**Files:**
- Modify: `project-proculink/src/components/bridge/UploadWorkbench.tsx`

- [ ] **Step 1: Add `useSearchParams` import**

Find the navigation import line:
```ts
import { useRouter } from "next/navigation";
```
Replace with:
```ts
import { useRouter, useSearchParams } from "next/navigation";
```

- [ ] **Step 2: Add `useSearchParams()` call inside the component**

In `UploadWorkbench`, after `const router = useRouter();`, add:
```ts
  const searchParams = useSearchParams();
```

- [ ] **Step 3: Update the supplier-validation effect to honour the URL param**

Find the existing supplier effect:
```tsx
  useEffect(() => {
    if (suppliers.length === 0) {
      if (supplierId) setSupplierId("");
      return;
    }
    const stillValid = suppliers.some((s) => s.id === supplierId);
    if (!supplierId || !stillValid) {
      setSupplierId(suppliers[0].id);
    }
  }, [suppliers, supplierId]);
```

Replace with:
```tsx
  useEffect(() => {
    if (suppliers.length === 0) {
      if (supplierId) setSupplierId("");
      return;
    }
    const stillValid = suppliers.some((s) => s.id === supplierId);
    if (!supplierId || !stillValid) {
      const paramId = searchParams?.get("supplierId");
      if (paramId && suppliers.some((s) => s.id === paramId)) {
        setSupplierId(paramId);
      } else {
        setSupplierId(suppliers[0].id);
      }
    }
  }, [suppliers, supplierId, searchParams]);
```

- [ ] **Step 4: Cache the detect-format result after a successful upload**

In `handleUpload`, find where `uploadedOrderId` is assigned:
```ts
      uploadedOrderId = result.order.id;
```
Add directly after that line:
```ts
      // Cache the format-detection result so ParseFailedPanel can show it if parsing fails.
      if (detection) {
        try {
          sessionStorage.setItem(`detectResult:${uploadedOrderId}`, JSON.stringify(detection));
        } catch {
          // sessionStorage unavailable — silently skip
        }
      }
```

- [ ] **Step 5: Build**

```
cd project-proculink && bun run build
```

Expected: build succeeds.

- [ ] **Step 6: Commit**

```
git add project-proculink/src/components/bridge/UploadWorkbench.tsx
git commit -m "feat(parse-failure): cache detect-format result; preselect supplier from ?supplierId param"
```

---

## Task 9: Verify build + run full test suite

- [ ] **Step 1: Run backend test suite**

```
dotnet test ProcuLink.slnx --no-restore
```

Expected: all tests pass. Count ≥ 276 (272 baseline + ParseFailureExplainTests ×17 + OrderServiceParseAuditTests ×2 + OrdersControllerErrorMessageTests ×2 = minimum 276; exact count depends on whether any existing tests overlap).

- [ ] **Step 2: Verify frontend build one final time**

```
cd project-proculink && bun run build
```

Expected: build succeeds. No new type errors. Existing Sentry/Browserslist/ESLint warnings remain.

- [ ] **Step 3: Manual verification — parse failure**

1. Start the API: `dotnet run --project ProcuLink.Api`
2. Start the frontend: `bun dev` in `project-proculink`
3. Upload a CSV file with only unrecognized column names (e.g. `foo,bar,baz\nval1,val2,val3`)
4. Navigate to `/orders/{id}` — should show `ParseFailedPanel` with "No line-table columns detected"
5. Click "Re-upload — try a different format" — should navigate to `/upload?supplierId={id}` with the original supplier pre-selected
6. Verify `GET /api/orders/{id}` returns `errorMessage` in the JSON (check Scalar at `http://localhost:5223/scalar` or browser devtools)

- [ ] **Step 4: Manual verification — SpineReview**

Navigate to `/inbox/{id}` for the same failed order — should also show `ParseFailedPanel` instead of the generic error gate.

- [ ] **Step 5: Update STATUS.md**

Update the "Latest committed implementation state" section to reflect:
- `ParseFailedPanel` + `FailedPanel` implemented
- `errorMessage` on `OrderDto`
- Backend test count updated (new count from step 1)
- P0 parse-error UX resolved

```
git add ProcuLink.Api/... project-proculink/... STATUS.md
git commit -m "docs(status): P0 parse-failure UX complete — ParseFailedPanel, errorMessage DTO, audit gaps closed"
```
