using ClosedXML.Excel;
using FluentAssertions;
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

/// <summary>
/// Verifies that <c>OrderService</c> routes <c>.xlsx</c> uploads through the SAME LLM-backed
/// <see cref="IStructuredOrderExtractor"/> the PDF path uses — rendering the workbook to text
/// (<see cref="XlsxTextExtractor"/>) and calling <c>ExtractFromTextAsync</c> — falls back to the
/// deterministic <see cref="XlsxOrderParser"/> when the extractor is unavailable, and never sends
/// a no-egress org's workbook data to OpenAI.
///
/// These target the internal <c>ParseXlsxAsync</c> seam directly (the full
/// <c>ParseStoredFileAsync</c> persistence path uses <c>ExecuteUpdateAsync</c>, which the EF
/// InMemory provider can't translate), mirroring <see cref="OrderServicePdfRoutingTests"/>.
/// </summary>
public class OrderServiceXlsxRoutingTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static OrderService BuildService(IStructuredOrderExtractor? extractor) =>
        BuildService(NewDb(), extractor);

    private static OrderService BuildService(ProcuLinkDbContext db, IStructuredOrderExtractor? extractor)
    {
        var parserFactory = new OrderParserFactory(new IPurchaseOrderParser[]
        {
            new CsvOrderParser(),
            new XlsxOrderParser(),
            new PdfOrderParser(),
        });

        return new OrderService(
            db,
            new Mock<IFileStorageService>().Object,
            parserFactory,
            new Mock<IItemMappingService>().Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            extractor);
    }

    [Fact]
    public async Task ParseXlsxAsync_ExtractorAvailableAndSucceeds_UsesStructuredExtraction()
    {
        var orgId = Guid.NewGuid();

        string? capturedText = null;
        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(true);
        extractor
            .Setup(e => e.ExtractFromTextAsync(It.IsAny<string>(), orgId, It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((text, _, _) => capturedText = text)
            .ReturnsAsync(new StructuredExtractionResult(
                Success: true, Confidence: 0.9,
                Order: new ExtractedOrder("226131000790", new DateTime(2026, 5, 20), "Fabrikam", "PLN", new[]
                {
                    new ExtractedOrderLine(1, "ABC-123", "Widget A", 10, "PCS", 4.5m),
                    new ExtractedOrderLine(2, "XYZ-999", "Widget B", 6, "PCS", 8.25m),
                }),
                FailureReason: null, ReviewLineNumbers: new[] { 2 }));

        var svc = BuildService(extractor.Object);

        var (parsed, review, _, failureReason) = await svc.ParseXlsxAsync(
            BuildLabelledWorkbook(), orgId, Guid.NewGuid(), CancellationToken.None);

        failureReason.Should().BeNull("extraction succeeded");
        parsed.PoNumber.Should().Be("226131000790");
        parsed.Currency.Should().Be("PLN");
        parsed.Lines.Should().HaveCount(2);
        parsed.Lines[0].BuyerItemCode.Should().Be("ABC-123");
        parsed.Lines[0].Quantity.Should().Be(10m);
        review.Should().BeEquivalentTo(new[] { 2 });

        // Proves the workbook was rendered to text and handed to the LLM — the labelled
        // header cell that the deterministic row1=headers parser would never read.
        capturedText.Should().NotBeNull();
        capturedText.Should().Contain("PurchaseOrderNr | 226131000790");

        extractor.Verify(
            e => e.ExtractFromTextAsync(It.IsAny<string>(), orgId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ParseXlsxAsync_ExtractorUnavailable_FallsBackToDeterministicParser()
    {
        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(false);

        var svc = BuildService(extractor.Object);

        // A SIMPLE row1=headers table — exactly what the deterministic parser handles.
        var (parsed, review, _, failureReason) = await svc.ParseXlsxAsync(
            BuildSimpleTableWorkbook(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        failureReason.Should().BeNull("the extractor was never invoked");
        parsed.PoNumber.Should().Be("DEMO-1");
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].BuyerItemCode.Should().Be("DET-CODE");
        parsed.Lines[0].Quantity.Should().Be(4m);
        review.Should().BeEmpty();

        extractor.Verify(
            e => e.ExtractFromTextAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ParseXlsxAsync_ExtractionReturnsFailure_FallsBackToDeterministicParser()
    {
        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(true);
        extractor
            .Setup(e => e.ExtractFromTextAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StructuredExtractionResult.Fail("low confidence"));

        var svc = BuildService(extractor.Object);

        var (parsed, review, _, failureReason) = await svc.ParseXlsxAsync(
            BuildSimpleTableWorkbook(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        parsed.PoNumber.Should().Be("DEMO-1");
        parsed.Lines.Should().ContainSingle();
        review.Should().BeEmpty();
        failureReason.Should().Be("low confidence",
            "the extractor's failure reason must be threaded out so the caller can explain an empty fallback honestly");
    }

    [Fact]
    public async Task ParseXlsxAsync_SelfHostedOcrOrg_RoutesToDeterministicParser_BypassesExtractor()
    {
        var orgId = Guid.NewGuid();
        var db = NewDb();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "clerk", Name = "No-Egress Co", Slug = "no-egress",
            SelfHostedOcr = true, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Extractor is available — but a no-egress org must NEVER reach it.
        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(true);

        var svc = BuildService(db, extractor.Object);

        var (parsed, review, _, failureReason) = await svc.ParseXlsxAsync(
            BuildSimpleTableWorkbook(), orgId, Guid.NewGuid(), CancellationToken.None);

        failureReason.Should().BeNull("the extractor was never invoked");
        parsed.PoNumber.Should().Be("DEMO-1");
        parsed.Lines.Should().ContainSingle();
        review.Should().BeEmpty();
        extractor.Verify(
            e => e.ExtractFromTextAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "a no-egress org's XLSX data must never be sent to OpenAI");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>Fabrikam-style labelled/sectioned workbook (header label/value cells + a Lines section).</summary>
    private static byte[] BuildLabelledWorkbook()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Order");
        ws.Cell("A1").Value = "PurchaseOrderNr"; ws.Cell("B1").Value = "226131000790";
        ws.Cell("A2").Value = "Currency";        ws.Cell("B2").Value = "PLN";
        ws.Cell("A4").Value = "Lines LineNr"; ws.Cell("B4").Value = "Lines ManufPN";
        ws.Cell("C4").Value = "Lines ProductName"; ws.Cell("D4").Value = "Lines Quantity";
        ws.Cell("E4").Value = "Lines PriceWoVAT";
        ws.Cell("A5").Value = 1; ws.Cell("B5").Value = "ABC-123"; ws.Cell("C5").Value = "Widget A";
        ws.Cell("D5").Value = 10; ws.Cell("E5").Value = 4.5;
        ws.Cell("A6").Value = 2; ws.Cell("B6").Value = "XYZ-999"; ws.Cell("C6").Value = "Widget B";
        ws.Cell("D6").Value = 6;  ws.Cell("E6").Value = 8.25;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>A simple row1=headers table the deterministic <see cref="XlsxOrderParser"/> handles.</summary>
    private static byte[] BuildSimpleTableWorkbook()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");
        var headers = new[] { "PoNumber", "Currency", "LineNumber", "BuyerItemCode", "Description", "Quantity", "UnitPrice" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var row = new[] { "DEMO-1", "EUR", "1", "DET-CODE", "Widget", "4", "12.50" };
        for (var c = 0; c < row.Length; c++) ws.Cell(2, c + 1).Value = row[c];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
