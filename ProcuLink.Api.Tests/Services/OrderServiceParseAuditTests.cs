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
            .Setup(s => s.ResolveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodeAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<AiMappingLineContext>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiMappingSuggestion?)null);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
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
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            new ProcuLink.Infrastructure.Services.Detection.SupplierSchemaMappingService(
                db, NullLogger<ProcuLink.Infrastructure.Services.Detection.SupplierSchemaMappingService>.Instance));
    }

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid orderId)> SeedParsingOrderAsync(
        string fileKeyExtension)
    {
        var db    = NewDb();
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Seed a Supplier so EF Include(x => x.Supplier) doesn't return null for the entity
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Test Supplier",
            CreatedAt = DateTime.UtcNow,
        });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
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

        // CSV with a header row only (no data rows) → CsvOrderParser produces Lines.Count == 0
        var csvBytes = Encoding.UTF8.GetBytes("foo,bar,baz\n");
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var svc = BuildService(db, fileStorage.Object);
        var result = await svc.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);

        // db is the same context instance passed to BuildService so audit events are visible
        var auditEvent = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.EntityId == orderId && e.Action == "ParseFailed")
            .FirstOrDefaultAsync();

        Assert.NotNull(auditEvent);
        Assert.NotNull(auditEvent!.Payload);
        var errorProp = auditEvent.Payload!.RootElement.GetProperty("error").GetString();
        Assert.NotNull(errorProp);
        Assert.Contains("No line-table columns", errorProp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseStoredFileAsync_UnsupportedFormat_WritesParseFailed_WithFriendlyMessage()
    {
        var (db, orgId, orderId) = await SeedParsingOrderAsync(".rar");

        // Any bytes — the parser factory throws UnsupportedFileFormatException before reading content
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
