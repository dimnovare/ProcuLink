using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
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

    private static void VerifyTimeLimitLogged(Mock<ILogger<ReadDocumentJob>> logger, Times times) =>
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("time limit")),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

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

    private static DocumentReadOutcome Read(ReadUsage? usage = null, string sku = "A") =>
        new(true, new ReadResult("Statement", [Field("document_number", "ST-1")], [Lines(sku)]), null, usage);

    // A real 1x1 PNG: rendered pages now pass through ScanImages.Upright, which drops bytes it cannot decode.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    private static IPdfRasterizer Rasterizer(int pages, int undecodablePage = 0)
    {
        var r = new Mock<IPdfRasterizer>();
        r.Setup(x => x.RenderPagesPng(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            // `bytes`, not `_`: a lone `_` is a named parameter, and the inner `_ =>` would then not compile.
            .Returns((byte[] bytes, int max, int skip) => Enumerable.Range(0, Math.Clamp(pages - skip, 0, max))
                .Select(n => skip + n + 1 == undecodablePage ? new byte[] { 1 } : TinyPng).ToList());
        return r.Object;
    }

    private static (ReadDocumentJob Job, ProcuLinkDbContext Db, Mock<IDocumentReader> Reader, Guid DocId) Arrange(
        byte[] pdf, bool scans = false, string fileName = "invoice.pdf", IPdfRasterizer? rasterizer = null,
        IReadOnlyList<PageWord>? ocrWords = null, ReadField[]? readFields = null, bool fieldsConfirmed = true,
        Func<IReadOnlyList<byte[]>, IReadOnlyList<PageWord>>? ocrPages = null, Func<DateTime>? utcNow = null,
        Hangfire.IBackgroundJobClient? jobs = null, TimeSpan? readBudget = null, ILogger<ReadDocumentJob>? logger = null, bool pageImagesWithText = true,
        bool split = false, IReadOnlyList<DocumentSpan>? documents = null, BillingStatus? billingStatus = null)
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
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(pdf));

        var reader = new Mock<IDocumentReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(true, new ReadResult("Invoice", readFields ??
            [
                new ReadField("document_number", "Invoice number", "4471", FieldTypes.Text, null, 0.95, null, false, false, false),
                new ReadField("total_gross", "Total", "2380.00", FieldTypes.Money, null, 0.95, null, false, false, false),
                new ReadField("sender_name", "Sender", "Imaginary Supplier Oy", FieldTypes.Text, null, 0.95, null, false, false, false),
            ]), null, Documents: documents));

        var ocr = new Mock<IDocumentOcrService>();
        ocr.Setup(o => o.ReadWordsAsync(It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<byte[]> images, CancellationToken _) => ocrPages?.Invoke(images) ?? ocrWords ?? []);
        var gate = new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions { EnabledForAll = true, ScansForAll = scans, PageImagesWithText = pageImagesWithText }));
        jobs ??= Mock.Of<Hangfire.IBackgroundJobClient>();
        var billing = TestDoubles.PermissiveBilling.Mock();
        billing.Setup(b => b.GetStatusAsync(Org, It.IsAny<CancellationToken>())).ReturnsAsync(billingStatus ?? Status(ProcuLink.Core.Constants.PlanConstants.Operations, 0, 500));
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), Moq.Mock.Of<ProcuLink.Core.Documents.IStandardValidator>()), jobs,
            rasterizer ?? Mock.Of<IPdfRasterizer>(), ocr.Object, gate, new ProcuLink.Transform.Tokenizing.SourceTokenizer(), logger ?? NullLogger<ReadDocumentJob>.Instance,
            split ? new DocumentIntakeService(db, storage.Object, jobs, gate, billing.Object, NullLogger<DocumentIntakeService>.Instance) : null)
        {
            UtcNow = utcNow ?? (() => DateTime.UtcNow),
            ReadBudget = readBudget ?? TimeSpan.FromMinutes(20),
            RetryDelay = TimeSpan.Zero,
        };
        return (job, db, reader, doc.Id);
    }

    private static BillingStatus Status(string plan, int used, int limit) =>
        new(plan, "active", used, limit, 0, 1, null, null, false, used >= limit, false, true, true, null, null, 0, 0, false, false);

    [Fact]
    public async Task A_trial_at_its_limit_keeps_a_file_of_several_documents_whole()
    {
        var (job, db, _, id) = Arrange(TextPdf(4), split: true, billingStatus: Status(ProcuLink.Core.Constants.PlanConstants.Pilot, 17, 20),
            documents: Enumerable.Range(1, 4).Select(n => new DocumentSpan(n, $"INV-100{n}", false)).ToList());

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.AsNoTracking().SingleAsync();
        doc.ReadAt.Should().NotBeNull();
        (await db.AuditEvents.CountAsync(a => a.Action == "document.split")).Should().Be(0);
    }

    [Fact]
    public async Task A_start_outside_its_part_is_ignored()
    {
        var (job, db, reader, id) = Arrange(TextPdf(11), split: true);
        // Part 2 holds page 11 only, yet reports a start on page 5.
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) => Read() with
            {
                Documents = i.Part == 1 ? [new DocumentSpan(1, "INV-1001", false)] : [new DocumentSpan(5, "INV-1002", false)],
            });

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        (await db.FlowDocuments.CountAsync()).Should().Be(1);
        (await db.AuditEvents.CountAsync(a => a.Action == "document.split")).Should().Be(0);
    }

    [Fact]
    public async Task A_file_of_four_invoices_becomes_four_documents_each_on_its_own_pages()
    {
        var jobs = new Mock<Hangfire.IBackgroundJobClient>();
        var (job, db, _, id) = Arrange(TextPdf(4), jobs: jobs.Object, split: true,
            documents: Enumerable.Range(1, 4).Select(n => new DocumentSpan(n, $"INV-100{n}", false)).ToList());

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var docs = await db.FlowDocuments.AsNoTracking().ToListAsync();
        docs.Should().HaveCount(4).And.OnlyContain(d => d.PartOfId == id && d.PageCount == 1 && d.BillingUnits == 1 && d.ReadAt == null);
        var parts = docs.Where(d => d.Id != id).ToList();
        parts.Select(d => d.Sha256).Should().OnlyHaveUniqueItems();
        parts.Select(d => d.SourceFileKey).Should().OnlyHaveUniqueItems();
        parts.Should().OnlyContain(d => d.SplitFileKey == "k");
        var original = docs.Single(d => d.Id == id);
        original.Status.Should().Be(DocumentStatus.Received);
        original.FileName.Should().Be("invoice (page 1).pdf");
        (await db.AuditEvents.CountAsync(a => a.EntityId == id && a.Action == "document.split")).Should().Be(1);
        jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Exactly(4));
    }

    [Fact]
    public async Task A_file_printed_three_times_is_one_document_read_on_its_first_copy()
    {
        var (job, db, _, id) = Arrange(TextPdf(3), split: true,
            documents: [new DocumentSpan(1, "PO-77", false), new DocumentSpan(2, "PO-77", true), new DocumentSpan(3, "PO-77", true)]);

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.AsNoTracking().SingleAsync();
        doc.PageCount.Should().Be(1);
        doc.Status.Should().Be(DocumentStatus.Received);
    }

    [Fact]
    public async Task A_part_of_a_split_file_is_never_split_again()
    {
        var (job, db, _, id) = Arrange(TextPdf(4), split: true,
            documents: Enumerable.Range(1, 4).Select(n => new DocumentSpan(n, $"INV-100{n}", false)).ToList());
        (await db.FlowDocuments.SingleAsync()).PartOfId = Guid.NewGuid();
        await db.SaveChangesAsync();

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.AsNoTracking().SingleAsync();
        doc.ReadAt.Should().NotBeNull();
        doc.Status.Should().BeOneOf(DocumentStatus.Ready, DocumentStatus.NeedsReview);
        (await db.AuditEvents.CountAsync(a => a.Action == "document.split")).Should().Be(0);
    }

    [Fact]
    public async Task A_document_read_before_is_not_split_on_a_later_read()
    {
        var (job, db, _, id) = Arrange(TextPdf(4), split: true,
            documents: Enumerable.Range(1, 4).Select(n => new DocumentSpan(n, $"INV-100{n}", false)).ToList());
        (await db.FlowDocuments.SingleAsync()).ReadAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        (await db.FlowDocuments.CountAsync()).Should().Be(1);
        (await db.AuditEvents.CountAsync(a => a.Action == "document.split")).Should().Be(0);
    }

    [Fact]
    public async Task A_long_file_name_still_splits_into_pdf_parts()
    {
        var (job, db, _, id) = Arrange(TextPdf(2), split: true, fileName: new string('a', 195) + ".pdf",
            documents: [new DocumentSpan(1, "INV-1001", false), new DocumentSpan(2, "INV-1002", false)]);

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var names = await db.FlowDocuments.Select(d => d.FileName).ToListAsync();
        names.Should().HaveCount(2).And.OnlyContain(n => n.EndsWith(".pdf"));
    }

    [Fact]
    public async Task A_file_named_like_its_first_part_keeps_the_whole_file_apart()
    {
        var fileName = new string('a', 150) + " (page 1).pdf";
        var (job, db, _, id) = Arrange(TextPdf(2), split: true, fileName: fileName,
            documents: [new DocumentSpan(1, "INV-1001", false), new DocumentSpan(2, "INV-1002", false)]);
        var whole = $"{Org}/documents/{id}/{fileName}";
        (await db.FlowDocuments.SingleAsync()).SourceFileKey = whole;
        await db.SaveChangesAsync();

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var docs = await db.FlowDocuments.AsNoTracking().ToListAsync();
        docs.Single(d => d.Id == id).SourceFileKey.Should().NotBe(whole);
        docs.Single(d => d.Id != id).SplitFileKey.Should().Be(whole);
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

        // Page 13 cannot be decoded: part 2 ends at page 12, so no later image takes its page number, and the rest is flagged.
        var (job2, db2, reader2, id2) = Arrange(ImageOnlyPdf(25), scans: true, rasterizer: Rasterizer(25, undecodablePage: 13));
        var counts = new List<int>();
        reader2.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) => { counts.Add(i.Pages!.Count); return Read(); });

        await job2.ExecuteAsync(id2, Org, null, CancellationToken.None);

        counts.Should().Equal(10, 2, 5);
        DocumentJson.Deserialize<ReadResult>((await db2.FlowDocuments.SingleAsync(d => d.Id == id2)).ReadJson).Truncated.Should().BeTrue();
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
    public async Task A_document_sent_by_another_run_while_this_one_reads_is_left_as_it_is()
    {
        var jobs = new Mock<Hangfire.IBackgroundJobClient>();
        var (job, db, reader, id) = Arrange(TextPdf(), jobs: jobs.Object);
        (await db.Flows.SingleAsync()).Automation = FlowAutomation.Auto;
        await db.SaveChangesAsync();
        var options = (DbContextOptions<ProcuLinkDbContext>)db.GetService<IDbContextOptions>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // The other run finishes first and sends the document.
                using var other = new ProcuLinkDbContext(options);
                other.FlowDocuments.Single(d => d.Id == id).Status = DocumentStatus.Sent;
                other.SaveChanges();
                return Read();
            });

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        using var check = new ProcuLinkDbContext(options);
        (await check.FlowDocuments.SingleAsync(d => d.Id == id)).Status.Should().Be(DocumentStatus.Sent);
        (await check.AuditEvents.CountAsync(a => a.EntityId == id)).Should().Be(0);
        jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Text_pdf_part_is_read_with_its_page_images(bool pageImagesWithText)
    {
        var (job, _, reader, id) = Arrange(TextPdf(2), rasterizer: Rasterizer(2), pageImagesWithText: pageImagesWithText);
        DocumentReadInput? seen = null;
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .Callback<DocumentReadInput, Guid, CancellationToken>((i, _, _) => seen = i)
            .ReturnsAsync(Read());

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        seen!.Text.Should().NotBeNullOrWhiteSpace();
        if (pageImagesWithText) seen.Pages.Should().HaveCount(2);
        else seen.Pages.Should().BeNull();
    }

    [Fact]
    public async Task A_long_pdf_is_read_ten_pages_at_a_time_up_to_page_100_and_billed_for_the_parts_read()
    {
        var rasterizer = Rasterizer(105);
        var (job, db, reader, id) = Arrange(TextPdf(105), rasterizer: rasterizer);
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
        seen.Should().OnlyContain(i => i.Pages!.Count == 10 && !i.Scanned);
        Mock.Get(rasterizer).Verify(r => r.RenderPagesPng(It.IsAny<byte[]>(), 10, 90), Times.Once);
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
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.BillingUnits.Should().Be(2);
        doc.ChecksJson.Should().NotContain(DocumentChecks.TextTruncated, "every page was read");
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

    [Fact]
    public async Task When_one_part_cannot_be_read_the_document_is_not_read_and_not_billed()
    {
        var (job, db, reader, id) = Arrange(TextPdf(25));
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) => i.Part == 2
                ? new DocumentReadOutcome(false, null, "document_too_large", new ReadUsage("test-model", 500, 16000))
                : Read(new ReadUsage("test-model", 500, 50)));

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.Status.Should().Be(DocumentStatus.ReadFailed);
        doc.StatusReason.Should().Be("document_too_large");
        doc.ReadAt.Should().BeNull("an unread document is not billed");
        doc.ReadJson.Should().Be("{}");
        reader.Verify(r => r.ReadAsync(It.Is<DocumentReadInput>(i => i.Part == 3), Org, It.IsAny<CancellationToken>()), Times.Never);
        (await db.Set<FlowReadCall>().CountAsync()).Should().Be(2, "what the two calls cost is kept");
    }

    [Fact]
    public async Task A_part_that_fails_once_is_read_again_and_a_second_failure_ends_the_read_without_billing()
    {
        var usage = new ReadUsage("test-model", 100, 10);

        // Part 2 fails once: it is read again, every answered call has its own row, and the retried part is merged once.
        var (job, db, reader, id) = Arrange(TextPdf(11));
        var failures = 1;
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) =>
                i.Part == 2 && failures-- > 0 ? new DocumentReadOutcome(false, null, "ai_error", usage) : Read(usage, $"ITEM-{i.Part}"));

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()), Times.Exactly(3));
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.BillingUnits.Should().Be(2);
        DocumentJson.Deserialize<ReadResult>(doc.ReadJson).Tables.Single().Rows.Select(r => r.Cells["sku"].Value)
            .Should().Equal("ITEM-1", "ITEM-2");
        (await db.Set<FlowReadCall>().OrderBy(c => c.Part).Select(c => c.Part).ToListAsync()).Should().Equal(1, 2, 2);

        // Part 2 fails twice: final, nothing billed, no Hangfire retry of parts already paid for.
        var (job2, db2, reader2, id2) = Arrange(TextPdf(11));
        reader2.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken _) =>
                i.Part == 2 ? new DocumentReadOutcome(false, null, "ai_error") : Read());

        await job2.ExecuteAsync(id2, Org, null, CancellationToken.None);

        reader2.Verify(r => r.ReadAsync(It.Is<DocumentReadInput>(i => i.Part == 2), Org, It.IsAny<CancellationToken>()), Times.Exactly(2));
        var doc2 = await db2.FlowDocuments.SingleAsync(d => d.Id == id2);
        (doc2.Status, doc2.StatusReason, doc2.ReadAt).Should().Be((DocumentStatus.ReadFailed, "ai_error", (DateTime?)null));

        // Part 1 fails twice: thrown, so Hangfire retries from part 1, as today.
        var (job3, _, reader3, id3) = Arrange(TextPdf(11));
        reader3.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(false, null, "ai_error"));

        await FluentActions.Awaiting(() => job3.ExecuteAsync(id3, Org, null, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        reader3.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_read_that_runs_past_twenty_minutes_stops_and_is_not_billed()
    {
        var t0 = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
        var asked = 0;
        var (job, db, reader, id) = Arrange(TextPdf(25), utcNow: () => asked++ == 0 ? t0 : t0.AddMinutes(21));

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()), Times.Once);
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == id);
        doc.Status.Should().Be(DocumentStatus.ReadFailed);
        doc.StatusReason.Should().Be("document_too_large");
        doc.ReadAt.Should().BeNull();

        // The time limit ends during part 2's call: final, no throw, not billed, part 1's cost kept.
        var usage = new ReadUsage("test-model", 100, 10);
        var log2 = new Mock<ILogger<ReadDocumentJob>>();
        var (job2, db2, reader2, id2) = Arrange(TextPdf(11), utcNow: () => t0, readBudget: TimeSpan.FromMilliseconds(200), logger: log2.Object);
        reader2.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .Returns(async (DocumentReadInput i, Guid _, CancellationToken t) =>
            {
                if (i.Part == 2) await Task.Delay(Timeout.Infinite, t);
                return Read(usage);
            });

        await job2.ExecuteAsync(id2, Org, null, CancellationToken.None);

        var doc2 = await db2.FlowDocuments.SingleAsync(d => d.Id == id2);
        (doc2.Status, doc2.StatusReason, doc2.ReadAt).Should().Be((DocumentStatus.ReadFailed, "document_too_large", (DateTime?)null));
        (await db2.Set<FlowReadCall>().CountAsync()).Should().Be(1);
        VerifyTimeLimitLogged(log2, Times.Once());

        // The worker shuts down during part 2's call: the stop propagates, the document is left to be read again,
        // and it is not reported as a time-limit stop (the only trace that tells the two stops apart).
        using var shutdown = new CancellationTokenSource();
        var log3 = new Mock<ILogger<ReadDocumentJob>>();
        var (job3, db3, reader3, id3) = Arrange(TextPdf(11), logger: log3.Object);
        reader3.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentReadInput i, Guid _, CancellationToken t) =>
            {
                if (i.Part == 2) { shutdown.Cancel(); t.ThrowIfCancellationRequested(); }
                return Read(usage);
            });

        await FluentActions.Awaiting(() => job3.ExecuteAsync(id3, Org, null, shutdown.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        db3.ChangeTracker.Clear();
        (await db3.FlowDocuments.SingleAsync(d => d.Id == id3)).Status.Should().Be(DocumentStatus.Reading);
        (await db3.AuditEvents.CountAsync(a => a.EntityId == id3)).Should().Be(0);
        (await db3.Set<FlowReadCall>().CountAsync()).Should().Be(1);
        VerifyTimeLimitLogged(log3, Times.Never());
    }

    private const string PrintedIban = "DE00 1111 2222 3333 4444";

    // Made-up EN 16931 invoice: two lines, one VAT rate; the seller's tax number comes before its VAT id, as German invoices often have it.
    private static string FacturXml(string iban, string sellerEndpoint = "") => $"""
        <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100" xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
        <rsm:ExchangedDocumentContext><ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>urn:cen.eu:en16931:2017</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter></rsm:ExchangedDocumentContext>
        <rsm:ExchangedDocument><ram:ID>FX-2026-0042</ram:ID><ram:TypeCode>380</ram:TypeCode><ram:IssueDateTime><udt:DateTimeString format="102">20261101</udt:DateTimeString></ram:IssueDateTime></rsm:ExchangedDocument>
        <rsm:SupplyChainTradeTransaction>
        <ram:IncludedSupplyChainTradeLineItem><ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct><ram:SellerAssignedID>WID-7</ram:SellerAssignedID><ram:Name>Widget</ram:Name></ram:SpecifiedTradeProduct><ram:SpecifiedLineTradeAgreement><ram:NetPriceProductTradePrice><ram:ChargeAmount>50.00</ram:ChargeAmount></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement><ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="C62">2.0000</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery><ram:SpecifiedLineTradeSettlement><ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19.00</ram:RateApplicablePercent></ram:ApplicableTradeTax><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>100.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement></ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem><ram:AssociatedDocumentLineDocument><ram:LineID>2</ram:LineID></ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct><ram:SellerAssignedID>BLT-3</ram:SellerAssignedID><ram:Name>Bolt</ram:Name></ram:SpecifiedTradeProduct><ram:SpecifiedLineTradeAgreement><ram:NetPriceProductTradePrice><ram:ChargeAmount>0.25</ram:ChargeAmount></ram:NetPriceProductTradePrice></ram:SpecifiedLineTradeAgreement><ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="H87">400</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery><ram:SpecifiedLineTradeSettlement><ram:ApplicableTradeTax><ram:TypeCode>VAT</ram:TypeCode><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19</ram:RateApplicablePercent></ram:ApplicableTradeTax><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>100.00</ram:LineTotalAmount></ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement></ram:IncludedSupplyChainTradeLineItem>
        <ram:ApplicableHeaderTradeAgreement><ram:SellerTradeParty><ram:Name>Imaginary Seller GmbH</ram:Name><ram:PostalTradeAddress><ram:PostcodeCode>10115</ram:PostcodeCode><ram:LineOne>Example Street 1</ram:LineOne><ram:CityName>Berlin</ram:CityName><ram:CountryID>DE</ram:CountryID></ram:PostalTradeAddress>{sellerEndpoint}<ram:SpecifiedTaxRegistration><ram:ID schemeID="FC">00/000/00000</ram:ID></ram:SpecifiedTaxRegistration><ram:SpecifiedTaxRegistration><ram:ID schemeID="VA">DE000000001</ram:ID></ram:SpecifiedTaxRegistration></ram:SellerTradeParty><ram:BuyerTradeParty><ram:Name>Fictional Buyer AG</ram:Name></ram:BuyerTradeParty></ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeDelivery/>
        <ram:ApplicableHeaderTradeSettlement><ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode><ram:SpecifiedTradeSettlementPaymentMeans><ram:TypeCode>58</ram:TypeCode><ram:PayeePartyCreditorFinancialAccount><ram:IBANID>{iban}</ram:IBANID></ram:PayeePartyCreditorFinancialAccount></ram:SpecifiedTradeSettlementPaymentMeans><ram:ApplicableTradeTax><ram:CalculatedAmount>38.00</ram:CalculatedAmount><ram:TypeCode>VAT</ram:TypeCode><ram:BasisAmount>200.00</ram:BasisAmount><ram:CategoryCode>S</ram:CategoryCode><ram:RateApplicablePercent>19.00</ram:RateApplicablePercent></ram:ApplicableTradeTax><ram:SpecifiedTradePaymentTerms><ram:DueDateDateTime><udt:DateTimeString format="102">20261201</udt:DateTimeString></ram:DueDateDateTime></ram:SpecifiedTradePaymentTerms><ram:SpecifiedTradeSettlementHeaderMonetarySummation><ram:LineTotalAmount>200.00</ram:LineTotalAmount><ram:TaxBasisTotalAmount>200.00</ram:TaxBasisTotalAmount><ram:TaxTotalAmount currencyID="EUR">38.00</ram:TaxTotalAmount><ram:GrandTotalAmount>238.00</ram:GrandTotalAmount><ram:DuePayableAmount>238.00</ram:DuePayableAmount></ram:SpecifiedTradeSettlementHeaderMonetarySummation></ram:ApplicableHeaderTradeSettlement>
        </rsm:SupplyChainTradeTransaction></rsm:CrossIndustryInvoice>
        """;

    /// <summary>A one-page text PDF printing the invoice above, written by hand; with <paramref name="xml"/> it carries it compressed as an attached factur-x.xml, as a Factur-X PDF/A-3 does.</summary>
    private static byte[] FacturXPdf(string? xml)
    {
        static byte[] L(string s) => System.Text.Encoding.Latin1.GetBytes(s);
        var content = string.Join(" 0 -16 Td ", new[]
        {
            "Invoice FX-2026-0042", "Imaginary Seller GmbH, Example Street 1, 10115 Berlin, VAT DE000000001", "Bill to Fictional Buyer AG",
            "1 WID-7 Widget 2 x 50.00 19.00 100.00", "2 BLT-3 Bolt 400 x 0.25 19.00 100.00", "Net 200.00 VAT 38.00 Total 238.00 EUR", $"IBAN {PrintedIban}",
        }.Select(line => $"({line}) Tj"));
        content = $"BT /F1 10 Tf 50 800 Td {content} ET";
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal)) z.Write(System.Text.Encoding.UTF8.GetBytes(xml ?? ""));
        var file = packed.ToArray();
        byte[][] objects =
        [
            L("<< /Type /Catalog /Pages 2 0 R" + (xml is null ? "" : " /Names << /EmbeddedFiles << /Names [(factur-x.xml) 5 0 R] >> >> /AF [5 0 R]") + " >>"),
            L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>"),
            L("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            L("<< /Type /Filespec /F (factur-x.xml) /UF (factur-x.xml) /AFRelationship /Data /EF << /F 7 0 R >> >>"),
            L($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"),
            [.. L($"<< /Type /EmbeddedFile /Subtype /text#2Fxml /Filter /FlateDecode /Length {file.Length} >>\nstream\n"), .. file, .. L("\nendstream")],
        ];
        using var pdf = new MemoryStream();
        pdf.Write(L("%PDF-1.7\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Position);
            pdf.Write([.. L($"{i + 1} 0 obj\n"), .. objects[i], .. L("\nendobj\n")]);
        }
        var xref = pdf.Position;
        pdf.Write(L($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n" + string.Concat(offsets.Select(o => $"{o:D10} 00000 n \n"))
            + $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"));
        return pdf.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_factur_x_pdf_is_read_from_its_embedded_invoice_data_not_by_the_reader(bool fieldsConfirmed)
    {
        var (job, db, reader, id) = Arrange(FacturXPdf(FacturXml(PrintedIban.Replace(" ", ""))), fieldsConfirmed: fieldsConfirmed);

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        var read = DocumentJson.Deserialize<ReadResult>((await db.FlowDocuments.AsNoTracking().SingleAsync()).ReadJson);
        string? V(string key) => read.Fields.Single(f => f.Key == key).Value;
        // The confirmed flow's fields carry no meaning: they bind by key. Every value the page prints is found there.
        (V("document_number"), V("sender_name")).Should().Be(("FX-2026-0042", "Imaginary Seller GmbH"));
        read.Fields.Should().OnlyContain(f => f.Confidence == 1.0 && !f.NotOnPage);
        if (fieldsConfirmed)
        {
            read.Fields.Should().HaveCount(2);
            return;
        }
        read.KindLabel.Should().Be("Invoice");
        new[] { V("issue_date"), V("due_date"), V("sender_vat_id"), V("receiver_name"), V("currency"), V("iban"), V("total_net"), V("total_tax"), V("total_gross") }
            .Should().Equal("2026-11-01", "2026-12-01", "DE000000001", "Fictional Buyer AG", "EUR", PrintedIban, "200.00", "38.00", "238.00");
        read.Tables.Single(t => t.Key == "lines").Rows.Select(r => string.Join('|', new[] { "line_number", "line_item_code", "line_description", "line_quantity", "line_unit", "line_unit_price", "line_tax_rate", "line_amount" }.Select(k => r.Cells[k].Value)))
            .Should().Equal("1|WID-7|Widget|2.00|C62|50.00|19.00|100.00", "2|BLT-3|Bolt|400.00|H87|0.25|19.00|100.00");
        read.Tables.Single(t => t.Key == "vat_breakdown").Rows.Single().Cells.Values.Select(c => c.Value).Should().Equal("S", "19.00", "200.00", "38.00");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("broken")]
    [InlineData("dtd")]
    [InlineData("hidden_iban")]
    [InlineData("hidden_endpoint")]
    public async Task A_pdf_without_readable_or_printed_invoice_data_is_read_from_its_page_as_before(string attachment)
    {
        var (job, db, reader, id) = Arrange(FacturXPdf(attachment switch
        {
            "broken" => "<rsm:CrossIndustryInvoice xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"><rsm:ExchangedDocument>",
            "dtd" => "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///nonexistent\">]><x>&e;</x>",
            // The data's IBAN is not the one the page prints: the hidden one must never be taken. (An unconfirmed flow takes every field.)
            "hidden_iban" => FacturXml("DE00999922223333 4444"),
            // A seller endpoint id the page does not print is a code, but not one exempt from the page check.
            "hidden_endpoint" => FacturXml(PrintedIban, "<ram:URIUniversalCommunication><ram:URIID schemeID=\"0088\">9999000000001</ram:URIID></ram:URIUniversalCommunication>"),
            _ => null,
        }), fieldsConfirmed: false);

        await job.ExecuteAsync(id, Org, null, CancellationToken.None);

        var doc = await db.FlowDocuments.AsNoTracking().SingleAsync();
        reader.Verify(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()), Times.Once);
        doc.ReadJson.Should().NotContain("9999");
        DocumentJson.Deserialize<ReadResult>(doc.ReadJson).Fields.Single(f => f.Key == "document_number").Value.Should().Be("4471");
    }
}