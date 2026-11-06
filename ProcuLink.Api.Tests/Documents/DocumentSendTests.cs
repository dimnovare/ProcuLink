using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class DocumentSendTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private sealed record DeliveryRow(Guid Id, string OutputName, string Status);

    /// <summary>A flow with a CSV output sent to an http destination and a JSON output with no destination, plus one document.</summary>
    private async Task<(Guid DocId, Guid CsvOutput, Guid JsonOutput)> Seed(string status)
    {
        var http = factory.ClientFor(Org);
        var destinationId = Guid.NewGuid();
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("invoice_no", "Invoice number", FieldTypes.Text, null, Required: false)])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf", Status = status,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice",
                [new ReadField("invoice_no", "Invoice number", "INV-7", FieldTypes.Text, null, 1, null, false, true, false)], [])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<FlowDestination>().Add(new FlowDestination
            {
                Id = destinationId, OrgId = Org, Name = "Synthetic endpoint", Protocol = "http",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.Flows.Add(flow);
            db.FlowDocuments.Add(doc);
            await db.SaveChangesAsync();
        }

        async Task<Guid> Output(string name, string format, Guid? destination) =>
            (await (await http.PostAsJsonAsync($"/api/flows/{flow.Id}/outputs", new { name, format, destinationId = destination }))
                .Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!.Id;
        return (doc.Id, await Output("CSV", "csv", destinationId), await Output("JSON", "json", null));
    }

    private Task<HttpResponseMessage> Send(Guid docId) => factory.ClientFor(Org).PostAsync($"/api/documents/{docId}/send", null);

    private async Task<List<FlowDelivery>> Deliveries(Guid docId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        return await db.Set<FlowDelivery>().AsNoTracking().Where(d => d.DocumentId == docId).OrderBy(d => d.OutputName).ToListAsync();
    }

    [Fact]
    public async Task Send_twice_makes_one_delivery_per_output()
    {
        var (doc, _, _) = await Seed(DocumentStatus.Ready);
        var queuedBefore = factory.Enqueued<DeliverDocumentJob>();

        (await Send(doc)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(doc)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var d = await Deliveries(doc);
        d.Should().HaveCount(2);
        d.Single(x => x.OutputName == "JSON").Status.Should().Be("stored");
        d.Single(x => x.OutputName == "CSV").Status.Should().Be("queued");
        d.Single(x => x.OutputName == "CSV").ArtifactKey.Should().Be($"{Org}/documents/{doc}/outputs/{d.Single(x => x.OutputName == "CSV").DispatchKey}.csv");
        factory.Enqueued<DeliverDocumentJob>().Should().Be(queuedBefore + 1);
        var list = await factory.ClientFor(Org).GetFromJsonAsync<List<DeliveryRow>>($"/api/documents/{doc}/deliveries", DocumentJson.Options);
        list.Should().HaveCount(2);
    }

    private static readonly Guid OldKey = Guid.NewGuid();

    [Fact]
    public async Task Resending_after_a_failure_requeues_only_what_was_not_accepted()
    {
        var (doc, csv, json) = await Seed(DocumentStatus.SendFailed);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            FlowDelivery Row(Guid output, string name, string status, string? key) => new()
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = doc, OutputId = output, OutputName = name, Protocol = "http",
                Status = status, Attempts = 5, LastError = "rejected", ArtifactKey = key, DispatchKey = OldKey, FileName = "old-name.csv", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Set<FlowDelivery>().AddRange(Row(json, "JSON", FlowDeliveryStatus.Delivered, "kept-key"), Row(csv, "CSV", FlowDeliveryStatus.DeadLetter, "old-key"));
            await db.SaveChangesAsync();
        }

        (await Send(doc)).StatusCode.Should().Be(HttpStatusCode.OK);

        var d = await Deliveries(doc);
        var a = d.Single(x => x.OutputName == "JSON");
        a.Status.Should().Be("delivered");
        a.ArtifactKey.Should().Be("kept-key");
        a.Attempts.Should().Be(5);
        var b = d.Single(x => x.OutputName == "CSV");
        b.Status.Should().Be("queued");
        b.Attempts.Should().Be(0);
        b.LastError.Should().BeNull();
        b.DispatchKey.Should().NotBe(OldKey);
        b.ArtifactKey.Should().Be($"{Org}/documents/{doc}/outputs/{b.DispatchKey}.csv");
        b.FileName.Should().NotBe("old-name.csv").And.Contain(b.DispatchKey.ToString("N")[..8]);
    }

    [Fact]
    public async Task Failed_checks_never_block_but_an_unread_document_cannot_be_sent()
    {
        var (needsReview, _, _) = await Seed(DocumentStatus.NeedsReview);
        var (reading, _, _) = await Seed(DocumentStatus.Reading);

        (await Send(needsReview)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(reading)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.AuditEvents.Should().ContainSingle(a => a.EntityId == needsReview && a.Action == "document.sent");
        db.AuditEvents.Should().NotContain(a => a.EntityId == reading && a.Action == "document.sent");
    }
}
