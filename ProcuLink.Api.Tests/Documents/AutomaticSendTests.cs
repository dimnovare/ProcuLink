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

    private async Task ReadAsync(Guid documentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync("k", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream("a,b\r\n1,2\r\n"u8.ToArray()));
        var reader = new Mock<IDocumentReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), Org, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentReadOutcome(true, new ReadResult("Invoice", []), null));
        var job = new ReadDocumentJob(db, storage.Object, reader.Object, new DocumentRechecker(db), factory.Jobs.Object, NullLogger<ReadDocumentJob>.Instance);
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
            db.FlowDocuments.AddRange(doc, stuckDoc);
            db.Set<FlowDelivery>().AddRange(queuedOld, queuedFresh, deliveringOld, deliveredOld);
            await db.SaveChangesAsync();
        }

        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
        await using (var db = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options))
            await new DocumentDeliverySweepJob(db, jobs.Object).ExecuteAsync(CancellationToken.None);

        var enqueued = jobs.Invocations.Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .Select(i => ((Job)i.Arguments[0]).Args).Select(a => ((Guid)a[0], (Guid)a[1])).ToList();
        enqueued.Should().BeEquivalentTo(new[] { (queuedOld.Id, org), (deliveringOld.Id, org) });

        // A document stuck in "sending" with nothing in flight is repaired; one with a delivery still in flight is left alone.
        await using var check = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options);
        (await check.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == stuckDoc.Id)).Status.Should().Be(DocumentStatus.SendFailed);
        (await check.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id)).Status.Should().Be(DocumentStatus.Sending);
    }
}
