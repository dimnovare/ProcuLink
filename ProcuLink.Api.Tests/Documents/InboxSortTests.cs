using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>Each test uses fresh organisations: the factory's database is shared by every test in the class.</summary>
[Collection("postgres-container")]
public sealed class InboxSortTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly string FlowFields = DocumentJson.Serialize(FlowFieldList.Empty);

    private static ReadResult Result(string kindLabel) => new(kindLabel,
        [new ReadField("sender_name", "Sender", "Example Supplies", FieldTypes.Text, FieldMeanings.SenderName, 0.95, null, false, false, false)]);

    private static byte[] TextPdf(int pages)
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
                    p.Content().Text($"Statement page {page} carries line ITEM-{page:000} for the synthetic customer");
                });
            }
        }).GeneratePdf();
    }

    private async Task ReadAsync(Guid org, Guid documentId, ReadResult result, int pages = 1)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var pdf = TextPdf(pages);
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(pdf));
        var reader = new Mock<IDocumentReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(true, result, null, new ReadUsage("test-model", 10, 5)));
        var gate = new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions()));
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db, gate, Mock.Of<IStandardValidator>()), factory.Jobs.Object,
            Mock.Of<ProcuLink.Core.Services.Ocr.IPdfRasterizer>(), Mock.Of<ProcuLink.Core.Services.Ocr.IDocumentOcrService>(), gate,
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(), NullLogger<ReadDocumentJob>.Instance);
        await job.ExecuteAsync(documentId, org, null, CancellationToken.None);
    }

    private static FlowDocument NewDoc(Guid org, Guid flowId, string? kind = null) => new()
    {
        Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, FileName = "synthetic.pdf", ContentType = "application/pdf",
        SourceFileKey = "k", BillingUnits = 1, Kind = kind, Status = kind is null ? DocumentStatus.Received : DocumentStatus.Sent,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>A person flow of <paramref name="org"/>, holding one earlier invoice when <paramref name="holdsInvoice"/>.</summary>
    private async Task<Flow> FlowAsync(Guid org, string name, bool holdsInvoice, bool confirmed = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = org, Name = name, FieldsJson = FlowFields, FieldsConfirmed = confirmed,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Flows.Add(flow);
        if (holdsInvoice) db.FlowDocuments.Add(NewDoc(org, flow.Id, "invoice"));
        await db.SaveChangesAsync();
        return flow;
    }

    private async Task<(Guid InboxId, Guid DocId)> InboxDocAsync(Guid org)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var inbox = await InboxFlow.GetOrCreateAsync(db, org, CancellationToken.None);
        var doc = NewDoc(org, inbox.Id);
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return (inbox.Id, doc.Id);
    }

    private async Task RememberAsync(Guid org, Guid flowId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Set<FlowSortMemory>().Add(new FlowSortMemory
        {
            Id = Guid.NewGuid(), OrgId = org, SenderKey = "name:example supplies", Kind = "invoice", FlowId = flowId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private T Query<T>(Func<ProcuLinkDbContext, T> query)
    {
        using var scope = factory.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>());
    }

    private FlowDocument Doc(Guid id) => Query(db => db.FlowDocuments.AsNoTracking().Single(d => d.Id == id));

    [Fact]
    public async Task Remembered_sort_is_certain_and_the_reread_is_not_billed()
    {
        var org = Guid.NewGuid();
        var f = await FlowAsync(org, "Supplier invoices", holdsInvoice: false);
        await RememberAsync(org, f.Id);
        var (_, id) = await InboxDocAsync(org);
        var (reads, sends) = (factory.Enqueued<ReadDocumentJob>(), factory.Enqueued<SendDocumentJob>());

        await ReadAsync(org, id, Result("Invoice"));

        var doc = Doc(id);
        doc.FlowId.Should().Be(f.Id);
        doc.Status.Should().Be(DocumentStatus.Received);
        doc.SortUnconfirmed.Should().BeFalse();
        doc.Kind.Should().Be("invoice");
        doc.SenderKey.Should().Be("name:example supplies");
        factory.Enqueued<ReadDocumentJob>().Should().Be(reads + 1);
        var sorted = Query(db => db.AuditEvents.AsNoTracking().Where(a => a.EntityId == id && a.Action == "document.sorted").ToList());
        sorted.Should().ContainSingle().Which.Payload!.RootElement.GetProperty("certain").GetBoolean().Should().BeTrue();
        var t1 = doc.ReadAt;

        await ReadAsync(org, id, Result("Invoice"), pages: 25);

        doc = Doc(id);
        doc.ReadAt.Should().Be(t1);
        doc.BillingUnits.Should().Be(1);
        Query(db => db.Set<FlowReadCall>().Count(c => c.DocumentId == id)).Should().Be(4);
        factory.Enqueued<SendDocumentJob>().Should().Be(sends);
    }

    [Fact]
    public async Task A_flow_left_before_its_fields_were_confirmed_never_catches_an_inbox_document()
    {
        var org = Guid.NewGuid();
        var draft = await FlowAsync(org, "Left behind", holdsInvoice: true, confirmed: false);
        await RememberAsync(org, draft.Id);

        var (inbox, a) = await InboxDocAsync(org);
        await ReadAsync(org, a, Result("Invoice"));
        (Doc(a).FlowId, Doc(a).StatusReason).Should().Be((inbox, "new_kind"));

        var live = await FlowAsync(org, "Supplier invoices", holdsInvoice: true);
        var (_, b) = await InboxDocAsync(org);
        await ReadAsync(org, b, Result("Invoice"));
        (Doc(b).FlowId, Doc(b).SortUnconfirmed).Should().Be((live.Id, true));
    }

    [Fact]
    public async Task Resembling_one_flow_is_not_certain_and_two_flows_is_a_new_kind()
    {
        var org = Guid.NewGuid();
        var f = await FlowAsync(org, "Supplier invoices", holdsInvoice: true);

        var (_, a) = await InboxDocAsync(org);
        await ReadAsync(org, a, Result("Invoice"));
        Doc(a).FlowId.Should().Be(f.Id);
        Doc(a).SortUnconfirmed.Should().BeTrue();

        await FlowAsync(org, "More invoices", holdsInvoice: true);
        var (inbox, b) = await InboxDocAsync(org);
        var reads = factory.Enqueued<ReadDocumentJob>();
        await ReadAsync(org, b, Result("Invoice"));
        var docB = Doc(b);
        docB.FlowId.Should().Be(inbox);
        docB.Status.Should().Be(DocumentStatus.NeedsReview);
        docB.StatusReason.Should().Be("choose_flow");
        factory.Enqueued<ReadDocumentJob>().Should().Be(reads);

        var (_, c) = await InboxDocAsync(org);
        await ReadAsync(org, c, Result("Delivery note"));
        Doc(c).StatusReason.Should().Be("new_kind");
    }

    [Fact]
    public async Task A_file_the_workspace_already_holds_is_held_at_the_inbox_door()
    {
        var org = Guid.NewGuid();
        var f = await FlowAsync(org, "Supplier invoices", holdsInvoice: false);
        await RememberAsync(org, f.Id);
        var pdf = TextPdf(1);
        async Task<Guid> UploadAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var inbox = await InboxFlow.GetOrCreateAsync(db, org, CancellationToken.None);
            return (await scope.ServiceProvider.GetRequiredService<DocumentIntakeService>().AcceptAsync(
                org, inbox.Id, new MemoryStream(pdf), "invoice-2292.pdf", "application/pdf", "upload", null, null, CancellationToken.None)).Id;
        }
        var first = await UploadAsync();
        await ReadAsync(org, first, Result("Invoice"));
        Doc(first).FlowId.Should().Be(f.Id);
        var reads = factory.Enqueued<ReadDocumentJob>();

        var second = Doc(await UploadAsync());

        (second.ReadAt, second.StatusReason).Should().Be(((DateTime?)null, "duplicate_file"));
        DocumentJson.Deserialize<List<DocumentCheck>>(second.ChecksJson).Single().Message.Should().Be("This file is already in your workspace.");
        factory.Enqueued<ReadDocumentJob>().Should().Be(reads);
    }

    [Fact]
    public async Task Another_orgs_memory_and_flows_are_never_used()
    {
        var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
        var x = await FlowAsync(orgB, "Supplier invoices", holdsInvoice: true);
        var y = await FlowAsync(orgA, "Item codes", holdsInvoice: false);
        await RememberAsync(orgB, x.Id);
        // Crossed rows: OrgB's memory naming OrgA's flow, and OrgA's memory naming OrgB's flow. Neither is ever used.
        await RememberAsync(orgB, y.Id);
        await RememberAsync(orgA, x.Id);
        var (inbox, id) = await InboxDocAsync(orgA);

        await ReadAsync(orgA, id, Result("Invoice"));

        var doc = Doc(id);
        doc.FlowId.Should().Be(inbox);
        doc.StatusReason.Should().Be("new_kind");
        Query(db => db.AuditEvents.Any(a => a.EntityId == id && a.Action == "document.sorted")).Should().BeFalse();
    }
}
