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
        r.Setup(x => x.RenderPagesPng(It.IsAny<byte[]>(), It.IsAny<int>()))
            .Returns(Enumerable.Range(0, pages).Select(_ => TinyPng).ToList());
        return r.Object;
    }

    private static (ReadDocumentJob Job, ProcuLinkDbContext Db, Mock<IDocumentReader> Reader, Guid DocId) Arrange(
        byte[] pdf, bool scans = false, string fileName = "invoice.pdf", IPdfRasterizer? rasterizer = null,
        IReadOnlyList<PageWord>? ocrWords = null, ReadField[]? readFields = null)
    {
        var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"read-job-{Guid.NewGuid()}").Options);
        var fields = new FlowFieldList(
        [
            new FlowField("document_number", "Invoice number", FieldTypes.Text, null, true),
            new FlowField("sender_name", "Sender", FieldTypes.Text, null, true),
        ]);
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = Org, Name = "Invoices", FieldsJson = DocumentJson.Serialize(fields), FieldsConfirmed = true };
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
        ocr.Setup(o => o.ReadWordsAsync(It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>())).ReturnsAsync(ocrWords ?? []);
        var gate = new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions { EnabledForAll = true, ScansForAll = scans }));
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db), Mock.Of<Hangfire.IBackgroundJobClient>(),
            rasterizer ?? Mock.Of<IPdfRasterizer>(), ocr.Object, gate, new ProcuLink.Transform.Tokenizing.SourceTokenizer(), NullLogger<ReadDocumentJob>.Instance);
        return (job, db, reader, doc.Id);
    }

    [Fact]
    public async Task Hints_for_removed_fields_are_not_passed()
    {
        var (job, db, reader, id) = Arrange(TextPdf());
        var flow = await db.Flows.SingleAsync();
        FieldHint Hint(string key) => new(key, "", "", null, "x", null, null, null, DateTime.UtcNow);
        flow.HintsJson = DocumentJson.Serialize(new[] { Hint("sender_name"), Hint("gone") });
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
    public async Task Scanned_pdf_is_read_from_page_images_when_scans_are_on()
    {
        var box = new Box(0.1, 0.2, 0.3, 0.05);
        var (job, db, reader, id) = Arrange(ImageOnlyPdf(11), scans: true, rasterizer: Rasterizer(10),
            ocrWords: [new PageWord("PO-77", 2, box)], readFields: [Field("po", "PO-77"), Field("ghost", "ZZZ")]);
        (await db.FlowDocuments.SingleAsync(d => d.Id == id)).BillingUnits = 2; // what intake bills for 11 pages
        await db.SaveChangesAsync();

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        reader.Verify(r => r.ReadAsync(It.Is<DocumentReadInput>(i => i.Pages!.Count == 10), Org, It.IsAny<CancellationToken>()), Times.Once);
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.BillingUnits.Should().Be(1);
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        read.Truncated.Should().BeTrue();
        var located = read.Fields.Single(f => f.Key == "po").Location!;
        located.Page.Should().Be(2);
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
}
