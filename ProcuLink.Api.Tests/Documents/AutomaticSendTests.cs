using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class AutomaticSendTests(DocumentEngineTestFactory factory, PostgresContainerFixture postgres)
    : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private Task<HttpResponseMessage> Patch(Guid flowId, object body) =>
        factory.ClientFor(Org).PatchAsync($"/api/flows/{flowId}", JsonContent.Create(body));

    private async Task ReadAsync(Guid documentId, ReadResult? result = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync("k", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream("a,b\r\n1,2\r\n"u8.ToArray()));
        var reader = new Mock<IDocumentReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(true, result ?? new ReadResult("Invoice", []), null));
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), Moq.Mock.Of<ProcuLink.Core.Documents.IStandardValidator>()), factory.Jobs.Object, Mock.Of<ProcuLink.Core.Services.Ocr.IPdfRasterizer>(), Mock.Of<ProcuLink.Core.Services.Ocr.IDocumentOcrService>(), new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), new ProcuLink.Transform.Tokenizing.SourceTokenizer(), NullLogger<ReadDocumentJob>.Instance);
        await job.ExecuteAsync(documentId, Org, null, CancellationToken.None);
    }

    [Fact]
    public async Task Only_an_automatic_flow_sends_once_read()
    {
        var now = DateTime.UtcNow;
        // A required field the reader never returns, so the read ends in needs_review: a failed check must not stop an automatic send.
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("invoice_no", "Invoice number", FieldTypes.Text, null, Required: true)])),
        };
        FlowDocument NewDoc() => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.csv", ContentType = "text/csv",
            SourceFileKey = "k", Status = DocumentStatus.Received, CreatedAt = now, UpdatedAt = now,
        };
        var (docA, docB) = (NewDoc(), NewDoc());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(flow);
            db.FlowDocuments.AddRange(docA, docB);
            await db.SaveChangesAsync();
        }
        var before = factory.Enqueued<SendDocumentJob>();

        (await Patch(flow.Id, new { automation = "auto" })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ReadAsync(docA.Id);
        factory.Enqueued<SendDocumentJob>().Should().Be(before + 1);

        (await Patch(flow.Id, new { automation = "review" })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ReadAsync(docB.Id);
        factory.Enqueued<SendDocumentJob>().Should().Be(before + 1);

        var invalid = await Patch(flow.Id, new { automation = "always" });
        invalid.StatusCode.Should().Be((HttpStatusCode)422);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("invalid_automation");
    }

    [Fact]
    public async Task A_sample_is_never_sent_or_announced_by_itself_and_never_holds_a_real_document()
    {
        var now = DateTime.UtcNow;
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true, Automation = FlowAutomation.Auto, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("invoice_no", "Invoice number", FieldTypes.Text, null, Required: false)])),
        };
        FlowDocument NewDoc(bool sample, DateTime at) => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.csv", ContentType = "text/csv",
            SourceFileKey = "k", Status = DocumentStatus.Received, IsSample = sample, CreatedAt = at, UpdatedAt = at,
        };
        var (sample, real) = (NewDoc(true, now.AddMinutes(-1)), NewDoc(false, now));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(flow);
            db.FlowIntakes.Add(new FlowIntake { Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, Kind = IntakeKind.Api, ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""", CreatedAt = now, UpdatedAt = now });
            db.FlowDocuments.AddRange(sample, real);
            await db.SaveChangesAsync();
        }
        var numbered = new ReadResult("Invoice", [new ReadField("invoice_no", "Invoice number", "INV-2250", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)]);
        var before = (factory.Enqueued<SendDocumentJob>(), factory.Enqueued<FlowWebhookJob>());

        await ReadAsync(sample.Id, numbered);
        (factory.Enqueued<SendDocumentJob>(), factory.Enqueued<FlowWebhookJob>()).Should().Be(before);

        // The same number, read later: the sample before it does not make it a repeat, so it sends and is announced.
        await ReadAsync(real.Id, numbered);
        (factory.Enqueued<SendDocumentJob>(), factory.Enqueued<FlowWebhookJob>()).Should().Be((before.Item1 + 1, before.Item2 + 1));

        // A send queued before the route flagged it: the job reloads the row, sees a sample and makes no delivery.
        (await factory.ClientFor(Org).PostAsJsonAsync($"/api/flows/{flow.Id}/outputs", new { name = "JSON", format = "json" })).StatusCode.Should().Be(HttpStatusCode.Created);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.FlowDocuments.SingleAsync(d => d.Id == sample.Id)).Status.Should().Be(DocumentStatus.Ready);
            await new SendDocumentJob(db, scope.ServiceProvider.GetRequiredService<DocumentSender>()).ExecuteAsync(sample.Id, Org, CancellationToken.None);
            (await db.Set<FlowDelivery>().AnyAsync(d => d.DocumentId == sample.Id)).Should().BeFalse();
        }
    }

    [Fact]
    public void An_automatic_flow_holds_a_repeated_document_number_for_a_person()
    {
        var flow = new Flow { Automation = FlowAutomation.Auto };
        var doc = new FlowDocument
        {
            Status = DocumentStatus.NeedsReview,
            ChecksJson = DocumentJson.Serialize(new[] { new DocumentCheck(DocumentChecks.Duplicate, false, null, "A document with this number was already received.") }),
        };
        var jobs = new Mock<IBackgroundJobClient>();

        SendDocumentJob.EnqueueIfAutomatic(jobs.Object, doc, flow);
        jobs.Invocations.Should().BeEmpty();

        // Control: any other failed check still sends.
        doc.ChecksJson = DocumentJson.Serialize(new[] { new DocumentCheck(DocumentChecks.RequiredMissing, false, "invoice_no", "Missing.") });
        SendDocumentJob.EnqueueIfAutomatic(jobs.Object, doc, flow);
        jobs.Invocations.Should().ContainSingle(i => i.Method.Name == nameof(IBackgroundJobClient.Create));
    }

    /// <summary>
    /// An automatic flow with one output, holding one earlier document of a kind no other test uses (the class shares OrgA's data),
    /// and an inbox document of that kind from a new sender. Reads it twice: in the inbox, then in the flow it was sorted into.
    /// </summary>
    private async Task<(Guid FlowId, Guid DocId, string Kind, string SenderKey)> SortIntoAutomaticFlowAsync(
        bool remembered, string? sameFile = null, string? sameNumber = null)
    {
        var now = DateTime.UtcNow;
        List<ReadField> fields = [new("sender_name", "Sender", "Alex Example", FieldTypes.Text, FieldMeanings.SenderName, 0.95, null, false, false, false)];
        if (sameNumber is not null)
            fields.Add(new ReadField("invoice_no", "Invoice number", sameNumber, FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false));
        var result = new ReadResult($"Invoice {Guid.NewGuid():N}", fields);
        var (kind, senderKey) = (DocumentKind.Normalize(result.KindLabel)!, DocumentKind.SenderKeyOf(result, "upload", null)!);
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Supplier invoices", FieldsConfirmed = true, Automation = FlowAutomation.Auto, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("invoice_no", "Invoice number", FieldTypes.Text, null, Required: false)])),
        };
        FlowDocument NewDoc(Guid flowId, string? docKind, string status) => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flowId, FileName = "synthetic.csv", ContentType = "text/csv",
            SourceFileKey = "k", Kind = docKind, Status = status, CreatedAt = now, UpdatedAt = now,
        };
        Guid docId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var inbox = await InboxFlow.GetOrCreateAsync(db, Org, CancellationToken.None);
            db.Flows.Add(flow);
            var earlier = NewDoc(flow.Id, kind, DocumentStatus.Sent);
            earlier.Sha256 = sameFile ?? "";
            earlier.DedupKey = sameNumber is null ? null : DocumentChecks.DedupKeyOf(result);
            db.FlowDocuments.Add(earlier);
            var doc = NewDoc(inbox.Id, null, DocumentStatus.Received);
            doc.Sha256 = sameFile ?? "";
            doc.CreatedAt = now.AddSeconds(1);
            db.FlowDocuments.Add(doc);
            docId = doc.Id;
            if (remembered)
                db.Set<FlowSortMemory>().Add(new FlowSortMemory { Id = Guid.NewGuid(), OrgId = Org, SenderKey = senderKey, Kind = kind, FlowId = flow.Id, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        (await factory.ClientFor(Org).PostAsJsonAsync($"/api/flows/{flow.Id}/outputs", new { name = "JSON", format = "json" })).StatusCode.Should().Be(HttpStatusCode.Created);

        await ReadAsync(docId, result);
        await ReadAsync(docId, result);
        return (flow.Id, docId, kind, senderKey);
    }

    [Fact]
    public async Task Not_certain_sort_into_an_automatic_flow_sends_nothing()
    {
        var before = factory.Enqueued<SendDocumentJob>();
        var (flowId, docId, _, _) = await SortIntoAutomaticFlowAsync(remembered: false);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == docId);
        doc.FlowId.Should().Be(flowId);
        doc.Status.Should().Be(DocumentStatus.NeedsReview);
        doc.StatusReason.Should().Be("check_sort");
        factory.Enqueued<SendDocumentJob>().Should().Be(before);

        // A send already queued, or a status changed by hand: the job itself still refuses the held document.
        doc.Status = DocumentStatus.Ready;
        await db.SaveChangesAsync();
        await new SendDocumentJob(db, scope.ServiceProvider.GetRequiredService<DocumentSender>()).ExecuteAsync(docId, Org, CancellationToken.None);
        (await db.Set<FlowDelivery>().AnyAsync(d => d.DocumentId == docId)).Should().BeFalse();
        (await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == docId)).Status.Should().NotBe(DocumentStatus.Sent);
    }

    [Fact]
    public async Task Certain_sort_into_an_automatic_flow_sends_once()
    {
        var before = factory.Enqueued<SendDocumentJob>();
        await SortIntoAutomaticFlowAsync(remembered: true);
        factory.Enqueued<SendDocumentJob>().Should().Be(before + 1);

        // A different file carrying a number the flow already holds is read there, and held as a repeat.
        var (_, repeatId, _, _) = await SortIntoAutomaticFlowAsync(remembered: true, sameNumber: "INV-7781");
        factory.Enqueued<SendDocumentJob>().Should().Be(before + 1);
        using var scope = factory.Services.CreateScope();
        var repeat = await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().FlowDocuments.SingleAsync(d => d.Id == repeatId);
        repeat.Status.Should().Be(DocumentStatus.NeedsReview);
        DocumentJson.Deserialize<List<DocumentCheck>>(repeat.ChecksJson).Should().Contain(c => c.Code == DocumentChecks.Duplicate && !c.Passed);
    }

    [Fact]
    public async Task Certain_sort_of_a_file_already_in_the_flow_is_held_not_sent()
    {
        var (sends, reads) = (factory.Enqueued<SendDocumentJob>(), factory.Enqueued<ReadDocumentJob>());
        var (flowId, docId, _, _) = await SortIntoAutomaticFlowAsync(remembered: true, sameFile: new string('e', 64));

        factory.Enqueued<SendDocumentJob>().Should().Be(sends);
        factory.Enqueued<ReadDocumentJob>().Should().Be(reads);
        using var scope = factory.Services.CreateScope();
        var doc = await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().FlowDocuments.SingleAsync(d => d.Id == docId);
        doc.FlowId.Should().Be(flowId);
        doc.Status.Should().Be(DocumentStatus.NeedsReview);
        doc.StatusReason.Should().Be("duplicate_file");
        DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Should().ContainSingle(c => c.Code == DocumentChecks.Duplicate && !c.Passed);
    }

    [Fact]
    public async Task A_persons_send_confirms_the_sort_and_teaches_memory()
    {
        var (flowId, docId, kind, senderKey) = await SortIntoAutomaticFlowAsync(remembered: false);

        (await factory.ClientFor(Org).PostAsync($"/api/documents/{docId}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        (await db.FlowDocuments.SingleAsync(d => d.Id == docId)).SortUnconfirmed.Should().BeFalse();
        (await db.Set<FlowSortMemory>().Where(m => m.OrgId == Org && m.SenderKey == senderKey && m.Kind == kind).ToListAsync())
            .Should().ContainSingle().Which.FlowId.Should().Be(flowId);
    }

    [DockerRequiredFact]
    public async Task The_sweep_requeues_stranded_deliveries_only()
    {
        var cs = await postgres.CreateDatabaseAsync("proculink_documentsweep");
        var now = DateTime.UtcNow;
        var org = Guid.NewGuid();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Synthetic flow", CreatedAt = now, UpdatedAt = now };
        FlowDocument NewDoc(DateTime updated) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf",
            SourceFileKey = "k", Sha256 = new string('a', 64), Status = DocumentStatus.Sending, CreatedAt = now, UpdatedAt = updated,
        };
        var doc = NewDoc(now.AddMinutes(-11));
        var stuckDoc = NewDoc(now.AddMinutes(-11));
        FlowDelivery NewDelivery(string status, DateTime updated, DateTime? claimed = null) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, DocumentId = doc.Id, OutputName = "CSV", Protocol = "http", Status = status,
            ClaimedAt = claimed, ContentType = "text/csv", FileName = "a.csv", CreatedAt = now, UpdatedAt = updated,
        };
        var queuedOld = NewDelivery(FlowDeliveryStatus.Queued, now.AddMinutes(-11));
        var queuedFresh = NewDelivery(FlowDeliveryStatus.Queued, now.AddMinutes(-1));
        var deliveringOld = NewDelivery(FlowDeliveryStatus.Delivering, now.AddMinutes(-1), now.AddMinutes(-11));
        var deliveredOld = NewDelivery(FlowDeliveryStatus.Delivered, now.AddMinutes(-11));
        await using (var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options))
        {
            db.Organisations.Add(new Organisation { Id = org, Name = "Test Org A", Slug = "acme-" + org.ToString("N") });
            db.Flows.Add(flow);
            db.FlowIntakes.Add(new FlowIntake { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Kind = IntakeKind.Api, ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""", CreatedAt = now, UpdatedAt = now });
            db.FlowDocuments.AddRange(doc, stuckDoc);
            db.Set<FlowDelivery>().AddRange(queuedOld, queuedFresh, deliveringOld, deliveredOld);
            await db.SaveChangesAsync();
        }

        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        await using (var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options))
            await new DocumentDeliverySweepJob(db, jobs.Object, NullLogger<DocumentDeliverySweepJob>.Instance).ExecuteAsync(CancellationToken.None);

        var enqueued = jobs.Invocations.Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create)).Select(i => (Job)i.Arguments[0])
            .Where(j => j.Type == typeof(DeliverDocumentJob)).Select(j => j.Args).Select(a => ((Guid)a[0], (Guid)a[1])).ToList();
        enqueued.Should().BeEquivalentTo(new[] { (queuedOld.Id, org), (deliveringOld.Id, org) });

        // A document stuck in "sending" with nothing in flight is repaired; one with a delivery still in flight is left alone.
        await using var check = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options);
        (await check.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == stuckDoc.Id)).Status.Should().Be(DocumentStatus.SendFailed);
        (await check.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).Status.Should().Be(DocumentStatus.Sending);
        (await check.AuditEvents.CountAsync(a => a.EntityId == stuckDoc.Id && a.Action == "document.delivery_failed")).Should().Be(1);
        (await check.AuditEvents.CountAsync(a => a.EntityId == doc.Id)).Should().Be(0);
        jobs.Invocations.Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create)).Select(i => (Job)i.Arguments[0])
            .Where(j => j.Type == typeof(FlowWebhookJob)).Select(j => ((Guid)j.Args[1], (string)j.Args[2]))
            .Should().Equal((stuckDoc.Id, FlowWebhookEvent.SendFailed));
    }
}
