using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
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

    [Fact]
    public async Task SendWhen_RoutesToTheMatchingOutput_AndHoldsWhenNoneMatch()
    {
        var http = factory.ClientFor(Org);
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true, Automation = FlowAutomation.Auto,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("supplier", "Supplier", FieldTypes.Text, null, Required: false)])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        FlowDocument Doc(string supplier) => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf", Status = DocumentStatus.Ready,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice", [new ReadField("supplier", "Supplier", supplier, FieldTypes.Text, null, 1, null, false, true, false)], [])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var (acme, gamma) = (Doc("Acme"), Doc("Gamma"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(flow);
            db.FlowDocuments.AddRange(acme, gamma);
            await db.SaveChangesAsync();
        }
        var outputs = new Dictionary<string, Guid>();
        foreach (var (name, value) in new[] { ("A", "Acme"), ("B", "Beta") })
            outputs[name] = (await (await http.PostAsJsonAsync($"/api/flows/{flow.Id}/outputs",
                new { name, format = "json", sendWhen = new { field = "supplier", op = "is", value } })).Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!.Id;

        (await Send(acme.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Deliveries(acme.Id)).Should().ContainSingle().Which.OutputId.Should().Be(outputs["A"]);

        var refused = await Send(gamma.Id);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("no_output_matches");
        (await Deliveries(gamma.Id)).Should().BeEmpty();
        (await Reload(gamma.Id)).Status.Should().Be(DocumentStatus.Ready);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            await new SendDocumentJob(db, scope.ServiceProvider.GetRequiredService<DocumentSender>()).ExecuteAsync(gamma.Id, Org, CancellationToken.None);
        }
        var held = await Reload(gamma.Id);
        (held.Status, held.StatusReason).Should().Be((DocumentStatus.NeedsReview, "no_output_matches"));
        (await Deliveries(gamma.Id)).Should().BeEmpty();

        (await http.PatchAsJsonAsync($"/api/documents/{gamma.Id}/values", new { fields = new[] { new { key = "supplier", value = "Beta" } } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(gamma.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Deliveries(gamma.Id)).Should().ContainSingle().Which.OutputId.Should().Be(outputs["B"]);
    }

    [Fact]
    public async Task ReRouting_a_failed_document_lets_it_finish_sending()
    {
        var http = factory.ClientFor(Org);
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("supplier", "Supplier", FieldTypes.Text, null, Required: false)])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf", Status = DocumentStatus.SendFailed,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice", [new ReadField("supplier", "Supplier", "Beta", FieldTypes.Text, null, 1, null, false, true, false)], [])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Flows.Add(flow);
            db.FlowDocuments.Add(doc);
            await db.SaveChangesAsync();
        }
        var outputs = new Dictionary<string, Guid>();
        foreach (var (name, value) in new[] { ("A", "Acme"), ("B", "Beta") })
            outputs[name] = (await (await http.PostAsJsonAsync($"/api/flows/{flow.Id}/outputs",
                new { name, format = "json", sendWhen = new { field = "supplier", op = "is", value } })).Content.ReadFromJsonAsync<OutputDto>(DocumentJson.Options))!.Id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<FlowDelivery>().Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = doc.Id, OutputId = outputs["A"], OutputName = "A", Protocol = "http",
                Status = FlowDeliveryStatus.DeadLetter, Attempts = 5, LastError = "rejected", DispatchKey = OldKey, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        (await Send(doc.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Reload(doc.Id)).Status.Should().Be(DocumentStatus.Sent);
    }

    private async Task<FlowDocument> Reload(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == id);
    }
}
