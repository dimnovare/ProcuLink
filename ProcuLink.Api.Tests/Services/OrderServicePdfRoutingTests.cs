using System.Globalization;
using System.Text;
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
/// Verifies that <c>OrderService</c> routes <c>.pdf</c> uploads to the LLM-backed
/// <see cref="IStructuredOrderExtractor"/> when one is available, falls back to the
/// deterministic regex parser when it is not (or when extraction fails), and
/// overlays the extractor's per-line review flags onto the persisted lines.
///
/// These target the internal <c>ParsePdfAsync</c> / <c>ApplyExtractionReviewFlags</c>
/// seams directly — the full <c>ParseStoredFileAsync</c> persistence path uses
/// <c>ExecuteUpdateAsync</c>, which the EF InMemory provider can't translate.
/// </summary>
public class OrderServicePdfRoutingTests
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
    public async Task ParsePdfAsync_ExtractorAvailableAndSucceeds_UsesStructuredExtraction()
    {
        var orgId = Guid.NewGuid();

        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(true);
        extractor
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), "application/pdf", orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructuredExtractionResult(
                Success: true, Confidence: 0.9,
                Order: new ExtractedOrder("PO-PDF-1", new DateTime(2026, 5, 20), "Buyer X", "EUR", new[]
                {
                    new ExtractedOrderLine(1, "PDF-CODE-1", "First", 3, "PCS", 10m),
                    new ExtractedOrderLine(2, "PDF-CODE-2", "Second", 5, "PCS", 20m),
                }),
                FailureReason: null, ReviewLineNumbers: new[] { 2 }));

        var svc = BuildService(extractor.Object);

        // Deliberately NOT a real PDF — if the deterministic parser ran it would
        // throw, so a clean structured result proves the LLM path was taken.
        var (parsed, review, _, failureReason) = await svc.ParsePdfAsync(
            Encoding.UTF8.GetBytes("not a real pdf"), orgId, Guid.NewGuid(), CancellationToken.None);

        failureReason.Should().BeNull("extraction succeeded");
        parsed.PoNumber.Should().Be("PO-PDF-1");
        parsed.Currency.Should().Be("EUR");
        parsed.Lines.Should().HaveCount(2);
        parsed.Lines[0].BuyerItemCode.Should().Be("PDF-CODE-1");
        parsed.Lines[0].Quantity.Should().Be(3m);
        parsed.Lines[1].BuyerItemCode.Should().Be("PDF-CODE-2");
        review.Should().BeEquivalentTo(new[] { 2 });

        extractor.Verify(
            e => e.ExtractAsync(It.IsAny<Stream>(), "application/pdf", orgId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ParsePdfAsync_ExtractorUnavailable_FallsBackToDeterministicParser()
    {
        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(false);

        var svc = BuildService(extractor.Object);

        var (parsed, review, _, failureReason) = await svc.ParsePdfAsync(
            CreatePdf("PO Number: PO-DET-1", "1 DET-CODE Widget 4 PCS 12.50"),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        failureReason.Should().BeNull("the extractor was never invoked");
        parsed.PoNumber.Should().Be("PO-DET-1");
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].BuyerItemCode.Should().Be("DET-CODE");
        review.Should().BeEmpty();

        extractor.Verify(
            e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ParsePdfAsync_ExtractionReturnsFailure_FallsBackToDeterministicParser()
    {
        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(true);
        extractor
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StructuredExtractionResult.Fail("low confidence"));

        var svc = BuildService(extractor.Object);

        var (parsed, review, _, failureReason) = await svc.ParsePdfAsync(
            CreatePdf("PO Number: PO-DET-2", "1 DET-CODE Widget 4 PCS 12.50"),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-DET-2");
        parsed.Lines.Should().ContainSingle();
        review.Should().BeEmpty();
        failureReason.Should().Be("low confidence",
            "the extractor's failure reason must be threaded out so the caller can explain an empty fallback honestly");
    }

    [Fact]
    public async Task ParsePdfAsync_SelfHostedOcrOrg_RoutesToDeterministicParser_BypassesExtractor()
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

        var (parsed, review, _, failureReason) = await svc.ParsePdfAsync(
            CreatePdf("PO Number: PO-NOEGRESS", "1 DET-CODE Widget 4 PCS 12.50"),
            orgId, Guid.NewGuid(), CancellationToken.None);

        failureReason.Should().BeNull("the extractor was never invoked");
        parsed.PoNumber.Should().Be("PO-NOEGRESS");
        parsed.Lines.Should().ContainSingle();
        parsed.Lines[0].BuyerItemCode.Should().Be("DET-CODE");
        review.Should().BeEmpty();
        extractor.Verify(
            e => e.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never, "a no-egress org's PDF data must never be sent to OpenAI");
    }

    [Fact]
    public async Task ParsePdfAsync_CarriesEnrichmentAndDocType_FromExtractor()
    {
        var orgId = Guid.NewGuid();

        var extractor = new Mock<IStructuredOrderExtractor>();
        extractor.SetupGet(e => e.IsAvailable).Returns(true);
        extractor
            .Setup(e => e.ExtractAsync(It.IsAny<Stream>(), "application/pdf", orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructuredExtractionResult(
                Success: true, Confidence: 0.9,
                Order: new ExtractedOrder(
                    "INV-9", new DateTime(2026, 5, 20), "Acme", "EUR",
                    new[] { new ExtractedOrderLine(1, "ABC", "Widget", 4, "PCS", 10m, LineAmount: 40m, TaxRate: 0.2m, DeliveryDate: new DateOnly(2026, 6, 30)) },
                    SupplierName: "Supplier Co", SubTotal: 40m, TaxTotal: 8m, GrandTotal: 48m,
                    PaymentTerms: "Net 30", DocumentType: "invoice"),
                FailureReason: null, ReviewLineNumbers: Array.Empty<int>()));

        var svc = BuildService(extractor.Object);

        var (parsed, _, _, _) = await svc.ParsePdfAsync(
            Encoding.UTF8.GetBytes("not a real pdf"), orgId, Guid.NewGuid(), CancellationToken.None);

        parsed.DocumentType.Should().Be("invoice");
        parsed.SupplierName.Should().Be("Supplier Co");
        parsed.GrandTotal.Should().Be(48m);
        parsed.TaxTotal.Should().Be(8m);
        parsed.PaymentTerms.Should().Be("Net 30");
        parsed.Lines[0].LineAmount.Should().Be(40m);
        parsed.Lines[0].TaxRate.Should().Be(0.2m);
        parsed.Lines[0].DeliveryDate.Should().Be(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void ApplyExtractionReviewFlags_ForcesNeedsReview_OnFlaggedLinesOnly()
    {
        var lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, SupplierItemCode = "SUP-1", NeedsReview = false, Confidence = 1.0f },
            new() { LineNumber = 2, SupplierItemCode = "SUP-2", NeedsReview = false, Confidence = 1.0f },
        };

        OrderService.ApplyExtractionReviewFlags(lines, new[] { 1 });

        lines[0].NeedsReview.Should().BeTrue("line 1 was flagged by the extractor");
        lines[0].Confidence.Should().BeLessThanOrEqualTo(0.5f);
        lines[1].NeedsReview.Should().BeFalse("line 2 was not flagged");
        lines[1].Confidence.Should().Be(1.0f);
    }

    [Fact]
    public void ApplyExtractionReviewFlags_NoFlags_LeavesLinesUntouched()
    {
        var lines = new List<PurchaseOrderLineEntity>
        {
            new() { LineNumber = 1, NeedsReview = false, Confidence = 1.0f },
        };

        OrderService.ApplyExtractionReviewFlags(lines, Array.Empty<int>());

        lines[0].NeedsReview.Should().BeFalse();
        lines[0].Confidence.Should().Be(1.0f);
    }

    // Minimal valid text PDF (mirrors ProcuLink.Transform.Tests.PdfOrderParserTests).
    // Internal so OrderServiceParseAuditTests can build a parseable-but-empty PDF too.
    internal static byte[] CreatePdf(params string[] lines)
    {
        var content = new StringBuilder();
        content.AppendLine("BT");
        content.AppendLine("/F1 12 Tf");
        content.AppendLine("72 720 Td");
        foreach (var line in lines)
        {
            content.Append('(').Append(EscapePdfText(line)).AppendLine(") Tj");
            content.AppendLine("0 -18 Td");
        }
        content.AppendLine("ET");
        var contentText = content.ToString();

        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(contentText)} >>\nstream\n{contentText}endstream\nendobj\n"),
        };

        var pdf = new StringBuilder();
        pdf.AppendLine("%PDF-1.4");
        var offsets = new List<int> { 0 };
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(obj);
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.AppendLine("xref");
        pdf.AppendLine("0 6");
        pdf.AppendLine("0000000000 65535 f ");
        for (var i = 1; i <= 5; i++)
            pdf.AppendLine(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n ");
        pdf.AppendLine("trailer");
        pdf.AppendLine("<< /Size 6 /Root 1 0 R >>");
        pdf.AppendLine("startxref");
        pdf.AppendLine(xrefOffset.ToString(CultureInfo.InvariantCulture));
        pdf.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string EscapePdfText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("(", "\\(", StringComparison.Ordinal)
             .Replace(")", "\\)", StringComparison.Ordinal);
}
