using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services.Ai;

namespace ProcuLink.Infrastructure.Tests.Services.Ai;

/// <summary>
/// Tests for the LLM-backed PDF structured extractor.
///
/// The OpenAI call itself is never exercised live — the validation/mapping logic
/// (<c>ValidateAndMap</c>) is a pure function tested directly, and the plumbing
/// paths (no key, over cap) are tested via the no-op / cap short-circuits.
/// </summary>
public class OpenAiPdfOrderExtractorTests
{
    // ── ValidateAndMap: happy path ───────────────────────────────────────────

    [Fact]
    public void ValidateAndMap_HappyPath_MapsCanonicalFields_AndFlagsNothing()
    {
        const string source =
            "PO Number: PO-2026-008412\n" +
            "Buyer: Heinrich Industries\n" +
            "Currency: EUR\n" +
            "1 HEI-PLT-09 Mounting plate 4 PCS 12.50 50.00\n" +
            "2 HEI-BRK-40 Steel bracket 8 PCS 7.25 58.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.95,
            PoNumber: "PO-2026-008412",
            OrderDate: "2026-05-20",
            Currency: "EUR",
            BuyerName: "Heinrich Industries",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "HEI-PLT-09", "Mounting plate", 4, "PCS", 12.50, 50.00),
                new OpenAiPdfOrderExtractor.ExtractionLineDto(2, "HEI-BRK-40", "Steel bracket", 8, "PCS", 7.25, 58.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.FailureReason.Should().BeNull();
        result.Confidence.Should().BeApproximately(0.95, 0.0001);
        result.ReviewLineNumbers.Should().BeEmpty("every number reconciles and appears in the source");

        result.Order.Should().NotBeNull();
        result.Order!.PoNumber.Should().Be("PO-2026-008412");
        result.Order.OrderDate.Should().Be(new DateTime(2026, 5, 20));
        result.Order.BuyerName.Should().Be("Heinrich Industries");
        result.Order.Currency.Should().Be("EUR");

        result.Order.Lines.Should().HaveCount(2);
        result.Order.Lines[0].LineNumber.Should().Be(1);
        result.Order.Lines[0].BuyerItemCode.Should().Be("HEI-PLT-09");
        result.Order.Lines[0].Description.Should().Be("Mounting plate");
        result.Order.Lines[0].Quantity.Should().Be(4m);
        result.Order.Lines[0].Unit.Should().Be("PCS");
        result.Order.Lines[0].UnitPrice.Should().Be(12.50m);
        result.Order.Lines[1].BuyerItemCode.Should().Be("HEI-BRK-40");
    }

    // ── ValidateAndMap: anti-hallucination (number not in source) ────────────

    [Fact]
    public void ValidateAndMap_LineWithNumberNotInSource_FlagsLineForReview()
    {
        // unit_price 13.50 never appears in the source text; quantity (4) and
        // line_amount (54.00) do, and 4 × 13.50 = 54.00 reconciles — so the ONLY
        // trigger is the hallucinated unit price.
        const string source = "1 ABC Widget 4 PCS 54.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 13.50, 54.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue("a partially-suspect order is still returned, but flagged");
        result.ReviewLineNumbers.Should().Contain(1);
    }

    // ── ValidateAndMap: arithmetic mismatch (all numbers present) ────────────

    [Fact]
    public void ValidateAndMap_QuantityTimesPriceDoesNotMatchAmount_FlagsLineForReview()
    {
        // 4, 12.50 and 99.99 all appear in the source (no hallucination), but
        // 4 × 12.50 = 50.00 ≠ 99.99 → the line must be flagged.
        const string source = "1 ABC Widget 4 PCS 12.50 99.99";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.9, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 12.50, 99.99),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeTrue();
        result.ReviewLineNumbers.Should().Contain(1);
    }

    [Fact]
    public void ValidateAndMap_BelowConfidenceThreshold_ReturnsFailureSoCallerFallsBack()
    {
        const string source = "1 ABC Widget 4 PCS 12.50 50.00";

        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.3, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: new[]
            {
                new OpenAiPdfOrderExtractor.ExtractionLineDto(1, "ABC", "Widget", 4, "PCS", 12.50, 50.00),
            });

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, source);

        result.Success.Should().BeFalse();
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ValidateAndMap_NoLines_ReturnsFailure()
    {
        var dto = new OpenAiPdfOrderExtractor.ExtractionDto(
            Confidence: 0.95, PoNumber: "PO-1", OrderDate: "", Currency: "EUR", BuyerName: "Acme",
            Lines: Array.Empty<OpenAiPdfOrderExtractor.ExtractionLineDto>());

        var result = OpenAiPdfOrderExtractor.ValidateAndMap(dto, "PO-1 nothing here");

        result.Success.Should().BeFalse();
        result.Order.Should().BeNull();
    }

    // ── Plumbing: no-op when no key ──────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_NoApiKey_ReturnsFailure_AndIsNotAvailable()
    {
        var extractor = CreateExtractor(
            new Dictionary<string, string?> { ["Ai:Provider"] = "openai" }, // no ApiKey
            tracker: null);

        extractor.IsAvailable.Should().BeFalse();

        await using var pdf = new MemoryStream(CreatePdf("PO Number: PO-1", "1 ABC Widget 4 PCS 12.50"));
        var result = await extractor.ExtractAsync(pdf, "application/pdf", Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Order.Should().BeNull();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IsAvailable_WithApiKeyAndOpenAiProvider_IsTrue()
    {
        var extractor = CreateExtractor(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: null);

        extractor.IsAvailable.Should().BeTrue();
    }

    // ── Plumbing: per-org token cap short-circuits before any OpenAI call ─────

    [Fact]
    public async Task ExtractAsync_AtOrOverCap_DoesNotCallOpenAi_AndReturnsFailure()
    {
        var orgId = Guid.NewGuid();
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);
        tracker.SetupGet(t => t.MonthlyLimit).Returns(1000);
        tracker.Setup(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var extractor = CreateExtractor(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object);

        await using var pdf = new MemoryStream(CreatePdf("PO Number: PO-1", "1 ABC Widget 4 PCS 12.50"));
        var result = await extractor.ExtractAsync(pdf, "application/pdf", orgId, CancellationToken.None);

        result.Success.Should().BeFalse("the per-org cap blocks the extraction call");
        tracker.Verify(t => t.IsAtOrOverLimitAsync(orgId, It.IsAny<CancellationToken>()), Times.Once);
        tracker.Verify(
            t => t.IncrementAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static OpenAiPdfOrderExtractor CreateExtractor(
        Dictionary<string, string?> config,
        IAiUsageTracker? tracker)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        return new OpenAiPdfOrderExtractor(
            cfg, NullLogger<OpenAiPdfOrderExtractor>.Instance, tracker);
    }

    // Minimal valid text PDF (mirrors ProcuLink.Transform.Tests.PdfOrderParserTests).
    private static byte[] CreatePdf(params string[] lines)
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
