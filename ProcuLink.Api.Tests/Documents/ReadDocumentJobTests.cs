using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class ReadDocumentJobTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static byte[] TextPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(40);
            p.Content().Column(col =>
            {
                col.Item().Text("Invoice No. 4471 issued by the supplier for the goods delivered last month");
                col.Item().Text("Total 2,380.00");
            });
        })).GeneratePdf();
    }

    private static byte[] TextPdf(int pages, Func<int, bool>? blank = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c =>
        {
            for (var n = 1; n <= pages; n++)
            {
                var page = n;
                c.Page(p =>
                {
                    p.Size(PageSizes.A4);
                    p.Margin(40);
                    if (blank?.Invoke(page) == true) p.Content().Width(200).Height(200).Background(Colors.Grey.Medium);
                    else p.Content().Text($"Statement page {page} carries line ITEM-{page:000} for the synthetic customer");
                });
            }
        }).GeneratePdf();
    }

    private static ReadTable Lines(params string[] skus) =>
        new("lines", "Lines", [new ReadColumn("sku", "SKU", FieldTypes.Text, null)],
            skus.Select(s => new ReadRow(new Dictionary<string, ReadCell> { ["sku"] = new(s, 0.9, null, false, false) })).ToList(), false);

    private static byte[] ImageOnlyPdf(int pages = 1)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c =>
        {
            for (var i = 0; i < pages; i++)
                c.Page(p =>
                {
                    p.Size(PageSizes.A4);
                    p.Content().Width(200).Height(200).Background(Colors.Grey.Medium);
                });
        }).GeneratePdf();
    }

    private static byte[] Jpeg()
    {
        using var bmp = new SKBitmap(40, 40);
        bmp.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bmp);
        return image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
    }

    private static ReadField Field(string key, string value) =>
        new(key, key, value, FieldTypes.Text, null, 0.95, null, false, false, false);

    // A real 1x1 PNG: rendered pages now pass through ScanImages.Upright, which drops bytes it cannot decode.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    private static IPdfRasterizer Rasterizer(int pages)
    {
        var r = new Mock<IPdfRasterizer>();
        r.Setup(x => x.RenderPagesPng(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            // `bytes`, not `_`: a lone `_` is a named parameter, and the inner `_ =>` would then not compile.
            .Returns((byte[] bytes, int max, int skip) => Enumerable.Range(0, Math.Clamp(pages - skip, 0, max)).Select(_ => TinyPng).ToList());
        return r.Object;
    }

    private static (ReadDocumentJob Job, ProcuLinkDbContext Db, Mock<IDocumentReader> Reader, Guid DocId) Arrange(
        byte[] pdf, bool scans = false, string fileName = "invoice.pdf", IPdfRasterizer? rasterizer = null,
        IReadOnlyList<PageWord>? ocrWords = null, ReadField[]? readFields = null, bool fieldsConfirmed = true,
        Func<IReadOnlyList<byte[]>, IReadOnlyList<PageWord>>? ocrPages = null)
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"read-job-{Guid.NewGuid()}").Options);
        var fields = new FlowFieldList(
        [
            new FlowField("document_number", "Invoice number", FieldTypes.Text, null, true),
            new FlowField("sender_name", "Sender", FieldTypes.Text, null, true),
        ]);
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = Org, Name = "Invoices", FieldsJson = DocumentJson.Serialize(fields), FieldsConfirmed = fieldsConfirmed };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = fileName, ContentType = "application/pdf",
            SourceFileKey = "k", PageCount = 1, BillingUnits = 1, Status = DocumentStatus.Received,
        };
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        db.SaveChanges();

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync("k", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(pdf));

        var reader = new Mock<IDocumentReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(true, new ReadResult("Invoice", readFields ??
            [
                new ReadField("document_number", "Invoice number", "4471", FieldTypes.Text, null, 0.95, null, false, false, false),
                new ReadField("total_gross", "Total", "2380.00", FieldTypes.Money, null, 0.95, null, false, false, false),
                new ReadField("sender_name", "Sender", "Imaginary Supplier Oy", FieldTypes.Text, null, 0.95, null, false, false, false),
            ]), null));

        var ocr = new Mock<IDocumentOcrService>();
        ocr.Setup(o => o.ReadWordsAsync(It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<byte[]> images, CancellationToken _) => ocrPages?.Invoke(images) ?? ocrWords ?? []);
        var gate = new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions { EnabledForAll = true, ScansForAll = scans }));
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), Moq.Mock.Of<ProcuLink.Core.Documents.IStandardValidator>()), Mock.Of<Hangfire.IBackgroundJobClient>(),
            rasterizer ?? Mock.Of<IPdfRasterizer>(), ocr.Object, gate, new ProcuLink.Transform.Tokenizing.SourceTokenizer(), NullLogger<ReadDocumentJob>.Instance);
        return (job, db, reader, doc.Id);
    }

    [Fact]
    public async Task Hints_for_removed_fields_are_not_passed()
    {
        var (job, db, reader, id) = Arrange(TextPdf());
        var flow = await db.Flows.SingleAsync();
        FieldHint Hint(string key, string sender = "") => new(key, sender, "", null, "x", null, null, null, DateTime.UtcNow);
        flow.HintsJson = DocumentJson.Serialize(new[] { Hint("sender_name"), Hint("document_number", "vat:XX999"), Hint("gone") });
        await db.SaveChangesAsync();
        DocumentReadInput? seen = null;
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .Callback<DocumentReadInput, Guid, CancellationToken>((i, _, _) => seen = i)
            .ReturnsAsync(new DocumentReadOutcome(true, new ReadResult("Invoice", [Field("sender_name", "A")]), null));

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        seen!.Hints!.Select(h => h.FieldKey).Should().Equal("sender_name");
    }

    [Fact]
    public async Task A_json_document_is_read_as_text_and_located_by_pointer()
    {
        var (job, db, reader, id) = Arrange(System.Text.Encoding.UTF8.GetBytes("{\"po\":\"PO-9\"}"), fileName: "order.json",
            readFields: [Field("po", "PO-9")]);

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        reader.Verify(r => r.ReadAsync(It.Is<DocumentReadInput>(i => i.Text.Contains("PO-9") && i.Pages == null), Org, It.IsAny<CancellationToken>()), Times.Once);
        var po = DocumentJson.Deserialize<ReadResult>((await db.FlowDocuments.SingleAsync(d => d.Id == id)).ReadJson).Fields.Single();
        po.Location!.Address.Should().Be("json:/po");
        po.NotOnPage.Should().BeFalse();
    }

    [Fact]
    public async Task A_damaged_word_file_fails_unreadable()
    {
        var (job, db, reader, id) = Arrange(System.Text.Encoding.UTF8.GetBytes("not a zip"), fileName: "po.docx");

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.Status.Should().Be(DocumentStatus.ReadFailed);
        doc.StatusReason.Should().Be("unreadable_file");
        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reads_pdf_locates_values_and_flags_value_not_on_page()
    {
        var (job, db, _, id) = Arrange(TextPdf());

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        var number = read.Fields.Single(f => f.Key == "document_number");
        number.Location!.Page.Should().Be(1);
        number.Location.Box.Should().NotBeNull();
        read.Fields.Single(f => f.Key == "sender_name").NotOnPage.Should().BeTrue();
        doc.Status.Should().Be(DocumentStatus.NeedsReview);
        doc.ChecksJson.Should().Contain(DocumentChecks.NotOnPage);
        doc.ReadAt.Should().NotBeNull();
        doc.BillingUnits.Should().Be(1);
    }

    [Fact]
    public async Task Scanned_pdf_fails_without_ai_call()
    {
        var (job, db, reader, id) = Arrange(ImageOnlyPdf());

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.Status.Should().Be(DocumentStatus.ReadFailed);
        doc.StatusReason.Should().Be("scanned_not_supported");
        doc.ReadAt.Should().BeNull();
        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_scan_is_read_ten_pages_at_a_time_and_a_value_on_page_11_is_shown_on_page_11()
    {
        var box = new Box(0.1, 0.2, 0.3, 0.05);
        // 25 pages in the file, 11 that render: two parts are read, the third window yields nothing.
        // The word is reported on the first page of the one-page part, which is page 11 of the file.
        var (job, db, reader, id) = Arrange(ImageOnlyPdf(25), scans: true, rasterizer: Rasterizer(11),
            ocrPages: pages => pages.Count == 1 ? new[] { new PageWord("PO-77", 1, box) } : Array.Empty<PageWord>());
        var seen = new List<DocumentReadInput>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) =>
            {
                seen.Add(i);
                return new DocumentReadOutcome(true, new ReadResult("Order", [Field("po", "PO-77"), Field("ghost", "ZZZ")]), null);
            });

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        seen.Select(i => (i.Pages!.Count, i.Part, i.Parts)).Should().Equal((10, 1, 3), (1, 2, 3));
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.BillingUnits.Should().Be(2);
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        read.Truncated.Should().BeTrue("pages 12 to 25 did not render");
        var located = read.Fields.Single(f => f.Key == "po").Location!;
        located.Page.Should().Be(11);
        located.Box.Should().BeEquivalentTo(box, o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-9)).WhenTypeIs<double>());
        read.Fields.Single(f => f.Key == "ghost").NotOnPage.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Image_is_read_only_while_scans_are_on(bool scans)
    {
        var (job, db, reader, id) = Arrange(Jpeg(), scans: scans, fileName: "IMG_0001.JPG");

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        reader.Verify(r => r.ReadAsync(It.Is<DocumentReadInput>(i => i.Pages!.Count == 1), Org, It.IsAny<CancellationToken>()),
            scans ? Times.Once() : Times.Never());
        (doc.Status == DocumentStatus.ReadFailed).Should().Be(!scans);
        doc.StatusReason.Should().Be(scans ? null : "scanned_not_supported");
    }

    [Fact]
    public async Task Scan_without_ocr_words_flags_nothing()
    {
        var (job, db, _, id) = Arrange(ImageOnlyPdf(), scans: true, rasterizer: Rasterizer(1));

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var read = DocumentJson.Deserialize<ReadResult>((await db.FlowDocuments.SingleAsync(d => d.Id == id)).ReadJson);
        read.Fields.Should().NotBeEmpty().And.OnlyContain(f => f.Location == null && !f.NotOnPage);
    }

    [Fact]
    public async Task Rerun_after_read_is_noop()
    {
        var (job, _, reader, id) = Arrange(TextPdf());

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);
        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_long_pdf_is_read_ten_pages_at_a_time_up_to_page_100_and_billed_for_the_parts_read()
    {
        var (job, db, reader, id) = Arrange(TextPdf(105));
        var seen = new List<DocumentReadInput>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) =>
            {
                seen.Add(i);
                return new DocumentReadOutcome(true,
                    new ReadResult("Statement", [Field("document_number", "ST-1")], [Lines($"ITEM-{i.Part}")]),
                    null, new ReadUsage("test-model", 100 * i.Part, 10));
            });

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        seen.Select(i => (i.Part, i.Parts)).Should().Equal(Enumerable.Range(1, 10).Select(n => (n, 10)));
        seen[1].Text.Should().Contain("=== Page 11 ===").And.Contain("=== Page 20 ===")
            .And.NotContain("=== Page 10 ===").And.NotContain("=== Page 21 ===");
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.BillingUnits.Should().Be(10);
        doc.ReadAt.Should().NotBeNull();
        doc.ChecksJson.Should().Contain(DocumentChecks.TextTruncated, "pages 101 to 105 were not read");
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        read.Truncated.Should().BeTrue();
        read.Tables.Single().Rows.Select(r => r.Cells["sku"].Value).Should().Equal(Enumerable.Range(1, 10).Select(n => $"ITEM-{n}"));
        var calls = await db.Set<FlowReadCall>().OrderBy(c => c.Part).ToListAsync();
        calls.Select(c => c.Part).Should().Equal(Enumerable.Range(1, 10));
        calls.Should().OnlyContain(c => c.OrgId == Org && c.DocumentId == id && c.Model == "test-model");
        calls.Sum(c => c.TokensIn).Should().Be(5500);
    }

    [Fact]
    public async Task Ten_pages_with_no_text_are_not_sent_and_not_billed()
    {
        var (job, db, reader, id) = Arrange(TextPdf(25, blank: n => n is >= 11 and <= 20));
        var seen = new List<DocumentReadInput>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) =>
            {
                seen.Add(i);
                return new DocumentReadOutcome(true, new ReadResult("Statement", [Field("document_number", "ST-1")]), null);
            });

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        seen.Select(i => (i.Part, i.Parts)).Should().Equal((1, 2), (2, 2));
        seen[1].Text.Should().Contain("=== Page 21 ===").And.NotContain("=== Page 11 ===");
        (await db.FlowDocuments.SingleAsync(d => d.Id == id)).BillingUnits.Should().Be(2);
    }

    [Fact]
    public async Task A_flow_with_no_confirmed_fields_gives_later_parts_the_keys_found_so_far()
    {
        var (job, db, reader, id) = Arrange(TextPdf(11), fieldsConfirmed: false);
        var seen = new List<DocumentReadInput>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) =>
            {
                seen.Add(i);
                return new DocumentReadOutcome(true, new ReadResult("Statement", [Field("invoice_no", "7")], [Lines("A")]), null);
            });

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        seen[0].Expected.Should().BeNull();
        seen[1].Expected!.Fields.Select(f => f.Key).Should().Equal("invoice_no");
        seen[1].Expected!.Tables.Single().Columns.Select(c => c.Key).Should().Equal("sku");
        DocumentJson.Deserialize<ReadResult>((await db.FlowDocuments.SingleAsync(d => d.Id == id)).ReadJson)
            .Fields.Should().OnlyContain(f => f.Listed);
    }
}
