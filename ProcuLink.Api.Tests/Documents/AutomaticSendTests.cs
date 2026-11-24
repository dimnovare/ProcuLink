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

            // A person cannot send the sample either, and its public sender never teaches sort memory.
            (await db.FlowDocuments.SingleAsync(d => d.Id == sample.Id)).SenderKey = "name:example sample sender";
            await db.SaveChangesAsync();
            (await factory.ClientFor(Org).PostAsync($"/api/documents/{sample.Id}/send", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await db.Set<FlowSortMemory>().AnyAsync(m => m.OrgId == Org && m.SenderKey == "name:example sample sender")).Should().BeFalse();
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

    [Fact]
    public void A_new_sender_is_not_sent_automatically()
    {
        var doc = new FlowDocument
        {
            Status = DocumentStatus.NeedsReview,
            ChecksJson = DocumentJson.Serialize(new[] { new DocumentCheck(DocumentChecks.NewSender, false, null, "First document from this sender. Check it before sending.") }),
        };
        var jobs = new Mock<IBackgroundJobClient>();

        SendDocumentJob.EnqueueIfAutomatic(jobs.Object, doc, new Flow { Automation = FlowAutomation.Auto });
        jobs.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void A_marked_flow_sends_a_document_with_nothing_marked_and_holds_one_with_a_mark()
    {
        var flow = new Flow { Automation = FlowAutomation.Marked };
        var jobs = new Mock<IBackgroundJobClient>();
        int Sends() => jobs.Invocations.Count(i => i.Method.Name == nameof(IBackgroundJobClient.Create));

        SendDocumentJob.EnqueueIfAutomatic(jobs.Object, new FlowDocument { Status = DocumentStatus.Ready }, flow);
        Sends().Should().Be(1);

        var marked = new FlowDocument
        {
            Status = DocumentStatus.NeedsReview,
            ChecksJson = DocumentJson.Serialize(new[] { new DocumentCheck(DocumentChecks.LowConfidence, false, "invoice_no", "Hard to read.") }),
        };
        SendDocumentJob.EnqueueIfAutomatic(jobs.Object, marked, flow);
        SendDocumentJob.EnqueueIfAutomatic(jobs.Object, new FlowDocument { Status = DocumentStatus.Ready, IsSample = true }, flow);
        Sends().Should().Be(1);
    }

    [Fact]
    public async Task A_first_document_from_an_email_address_waits_on_a_marked_flow()
    {
        var now = DateTime.UtcNow;
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true, Automation = FlowAutomation.Marked, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty),
        };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.csv", ContentType = "text/csv",
            SourceFileKey = "k", Source = IntakeKind.Email, SourceRef = $"marked-{Guid.NewGuid():N}@example.test", Status = DocumentStatus.Received, CreatedAt = now, UpdatedAt = now,
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        await new DocumentRechecker(db, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), Mock.Of<IStandardValidator>())
            .ApplyAsync(doc, flow, new ReadResult("Invoice", []), CancellationToken.None);
        await db.SaveChangesAsync();

        DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Should().Contain(c => c.Code == DocumentChecks.NewSender && !c.Passed);
        doc.Status.Should().Be(DocumentStatus.NeedsReview);
    }

    [Fact]
    public async Task A_first_document_from_an_email_address_waits_on_an_automatic_flow_only()
    {
        var now = DateTime.UtcNow;
        Flow NewFlow(Guid org, string automation) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, Name = "Synthetic flow", FieldsConfirmed = true, Automation = automation, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty),
        };
        var (auto, manual, otherOrg) = (NewFlow(Org, FlowAutomation.Auto), NewFlow(Org, FlowAutomation.Review), NewFlow(DocumentEngineTestFactory.OrgB, FlowAutomation.Auto));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Flows.AddRange(auto, manual, otherOrg);
        var rechecker = new DocumentRechecker(db, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), Mock.Of<IStandardValidator>());

        async Task<FlowDocument> CheckAsync(Flow flow, string source, string? from, string status = DocumentStatus.Received)
        {
            var doc = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = flow.OrgId, FlowId = flow.Id, FileName = "synthetic.csv", ContentType = "text/csv",
                SourceFileKey = "k", Source = source, SourceRef = from, Status = status, CreatedAt = now, UpdatedAt = now,
            };
            db.FlowDocuments.Add(doc);
            if (status == DocumentStatus.Received) await rechecker.ApplyAsync(doc, flow, new ReadResult("Invoice", []), CancellationToken.None);
            await db.SaveChangesAsync();
            return doc;
        }
        bool Held(FlowDocument doc) => DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Any(c => c.Code == DocumentChecks.NewSender && !c.Passed);

        var d1 = await CheckAsync(auto, IntakeKind.Email, "Bills@Example.test ");
        Held(d1).Should().BeTrue();
        d1.Status.Should().Be(DocumentStatus.NeedsReview);

        d1.Status = DocumentStatus.Sent;
        await db.SaveChangesAsync();
        // Its own sent row never trusts it.
        await rechecker.ApplyAsync(d1, auto, new ReadResult("Invoice", []), CancellationToken.None);
        Held(d1).Should().BeTrue();
        d1.Status = DocumentStatus.Sent;
        await db.SaveChangesAsync();
        Held(await CheckAsync(auto, IntakeKind.Email, " BILLS@example.test")).Should().BeFalse();
        Held(await CheckAsync(manual, IntakeKind.Email, "stranger@example.test")).Should().BeFalse();
        Held(await CheckAsync(auto, "upload", null)).Should().BeFalse();

        // Another workspace's trust is not this one's.
        await CheckAsync(otherOrg, IntakeKind.Email, "other@example.test", DocumentStatus.Sent);
        Held(await CheckAsync(auto, IntakeKind.Email, "other@example.test")).Should().BeTrue();
        Held(await CheckAsync(auto, IntakeKind.Imap, null)).Should().BeTrue();

        // A file pickup named like an address trusts nobody.
        await CheckAsync(auto, IntakeKind.Sftp, "pickup@example.test", DocumentStatus.Sent);
        Held(await CheckAsync(auto, IntakeKind.Email, "pickup@example.test")).Should().BeTrue();
    }

    [Fact]
    public async Task An_unconfirmed_sender_waits_even_when_known_and_sending_it_trusts_nobody()
    {
        var now = DateTime.UtcNow;
        var (from, stranger) = ($"signed-{Guid.NewGuid():N}@example.test", $"forged-{Guid.NewGuid():N}@example.test");
        var auto = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true, Automation = FlowAutomation.Auto, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty),
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Flows.Add(auto);
        var rechecker = new DocumentRechecker(db, new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions())), Mock.Of<IStandardValidator>());

        async Task<FlowDocument> CheckAsync(string sender, bool unconfirmed, string status = DocumentStatus.Received)
        {
            var doc = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = Org, FlowId = auto.Id, FileName = "synthetic.csv", ContentType = "text/csv", SourceFileKey = "k",
                Source = IntakeKind.Email, SourceRef = sender, SenderUnconfirmed = unconfirmed, Status = status, CreatedAt = now, UpdatedAt = now,
            };
            db.FlowDocuments.Add(doc);
            if (status == DocumentStatus.Received) await rechecker.ApplyAsync(doc, auto, new ReadResult("Invoice", []), CancellationToken.None);
            await db.SaveChangesAsync();
            return doc;
        }
        string? Hold(FlowDocument doc) => DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).SingleOrDefault(c => c.Code == DocumentChecks.NewSender && !c.Passed)?.Message;

        // A known address: confirmed passes, unconfirmed (a forged From of it) waits with its own sentence.
        await CheckAsync(from, unconfirmed: false, DocumentStatus.Sent);
        Hold(await CheckAsync(from, unconfirmed: false)).Should().BeNull();
        var forged = await CheckAsync(from, unconfirmed: true);
        Hold(forged).Should().Be($"We couldn't confirm this email really came from {from}. Check it before sending.");
        forged.Status.Should().Be(DocumentStatus.NeedsReview);

        // An unconfirmed document a person sent makes nobody trusted.
        await CheckAsync(stranger, unconfirmed: true, DocumentStatus.Sent);
        Hold(await CheckAsync(stranger, unconfirmed: false)).Should().StartWith("First document from this email address");
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
            db.FlowIntakes.Add(new FlowIntake { Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, Kind = IntakeKind.Api, ConfigJson = """{"webhookUrl":"https://hooks.example.test/in"}""", CreatedAt = now, UpdatedAt = now });
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
        var (before, hooks) = (factory.Enqueued<SendDocumentJob>(), factory.Enqueued<FlowWebhookJob>());
        var (flowId, docId, _, _) = await SortIntoAutomaticFlowAsync(remembered: false);
        factory.Enqueued<FlowWebhookJob>().Should().Be(hooks);

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

        // A failed read again of the held document (a file that cannot be opened) announces nothing either.
        async Task FailedReadAsync(bool confirmed)
        {
            db.ChangeTracker.Clear(); // the read runs in its own scope
            var held = await db.FlowDocuments.SingleAsync(d => d.Id == docId);
            (held.Status, held.FileName, held.SortUnconfirmed) = (DocumentStatus.Received, "synthetic.pdf", !confirmed);
            await db.SaveChangesAsync();
            await ReadAsync(docId);
        }
        await FailedReadAsync(confirmed: false);
        (await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == docId)).Status.Should().Be(DocumentStatus.ReadFailed);
        factory.Enqueued<FlowWebhookJob>().Should().Be(hooks);
        // Control: once the sort is confirmed, a failed read is announced.
        await FailedReadAsync(confirmed: true);
        factory.Enqueued<FlowWebhookJob>().Should().Be(hooks + 1);
    }

    [Fact]
    public async Task Certain_sort_into_an_automatic_flow_sends_once()
    {
        var before = factory.Enqueued<SendDocumentJob>();
        var hooks = factory.Enqueued<FlowWebhookJob>();
        await SortIntoAutomaticFlowAsync(remembered: true);
        factory.Enqueued<SendDocumentJob>().Should().Be(before + 1);
        factory.Enqueued<FlowWebhookJob>().Should().Be(hooks + 1);

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

        // Held as not certain and rechecked (a fields change or a publish): left exactly as the hold wrote it, never check_sort.
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        doc.SortUnconfirmed = true;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<DocumentRechecker>()
            .RecheckPendingAsync(await db.Flows.SingleAsync(f => f.Id == flowId), CancellationToken.None);
        (doc.Status, doc.StatusReason).Should().Be((DocumentStatus.NeedsReview, "duplicate_file"));
        DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson).Should().ContainSingle(c => c.Code == DocumentChecks.Duplicate && !c.Passed);
    }

    [Theory]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.Sending)]
    public async Task A_number_the_flow_already_sent_is_a_repeat_even_when_it_arrived_later(string twinStatus)
    {
        var now = DateTime.UtcNow;
        var result = new ReadResult($"Invoice {Guid.NewGuid():N}",
            [new ReadField("invoice_no", "Invoice number", "INV-9901", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)]);
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Supplier invoices", FieldsConfirmed = true, Automation = FlowAutomation.Auto, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("invoice_no", "Invoice number", FieldTypes.Text, null, Required: false)])),
        };
        FlowDocument NewDoc(Guid flowId, DateTime at) => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flowId, FileName = "invoice-2292.csv", ContentType = "text/csv",
            SourceFileKey = "k", Status = DocumentStatus.Received, CreatedAt = at, UpdatedAt = at,
        };
        FlowDocument waiting, copy;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            waiting = NewDoc((await InboxFlow.GetOrCreateAsync(db, Org, CancellationToken.None)).Id, now.AddMinutes(-1));
            copy = NewDoc(flow.Id, now);
            db.Flows.Add(flow);
            db.FlowDocuments.AddRange(waiting, copy);
            await db.SaveChangesAsync();
        }
        await ReadAsync(waiting.Id, result); // waits in the inbox: no flow holds its kind yet
        var sends = factory.Enqueued<SendDocumentJob>();
        await ReadAsync(copy.Id, result);
        factory.Enqueued<SendDocumentJob>().Should().Be(sends + 1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.FlowDocuments.SingleAsync(d => d.Id == copy.Id)).Status = twinStatus;
            await db.SaveChangesAsync();
        }

        (await factory.ClientFor(Org).PostAsJsonAsync($"/api/documents/{waiting.Id}/move", new { flowId = flow.Id })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ReadAsync(waiting.Id, result);

        factory.Enqueued<SendDocumentJob>().Should().Be(sends + 1);
        using var check = factory.Services.CreateScope();
        var moved = await check.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == waiting.Id);
        moved.Status.Should().Be(DocumentStatus.NeedsReview);
        DocumentJson.Deserialize<List<DocumentCheck>>(moved.ChecksJson).Should().Contain(c => c.Code == DocumentChecks.Duplicate && !c.Passed);
    }

    [Fact]
    public async Task A_persons_send_confirms_the_sort_and_teaches_memory()
    {
        var (flowId, docId, kind, senderKey) = await SortIntoAutomaticFlowAsync(remembered: false);

        (await factory.ClientFor(Org).PostAsync($"/api/documents/{docId}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var sent = await db.FlowDocuments.SingleAsync(d => d.Id == docId);
        sent.SortUnconfirmed.Should().BeFalse();
        sent.StatusReason.Should().BeNull();
        (await db.Set<FlowSortMemory>().Where(m => m.OrgId == Org && m.SenderKey == senderKey && m.Kind == kind).ToListAsync())
            .Should().ContainSingle().Which.FlowId.Should().Be(flowId);

        // Confirming without a send lifts the hold and its reason too.
        var (_, heldId, _, _) = await SortIntoAutomaticFlowAsync(remembered: false);
        var held = await db.FlowDocuments.SingleAsync(d => d.Id == heldId);
        held.StatusReason.Should().Be(DocumentSort.CheckSort);
        await DocumentSort.ConfirmAsync(db, held, CancellationToken.None);
        await DocumentSort.SaveRememberedAsync(db, held, CancellationToken.None);
        (await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == heldId)).StatusReason.Should().BeNull();
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
