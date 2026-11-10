using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class FlowVersionTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private static FlowOutput Output(Guid flowId, string template) => new()
    {
        Id = Guid.NewGuid(), OrgId = Org, FlowId = flowId, Name = "Synthetic output", Format = "json",
        TemplateJson = template, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private async Task<(ProcuLinkDbContext Db, Flow Flow, FlowOutput Output)> Seed(IServiceScope scope)
    {
        factory.ClientFor(Org);
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var output = Output(flow.Id, """{"a":1}""");
        db.Flows.Add(flow);
        db.Set<FlowOutput>().Add(output);
        await db.SaveChangesAsync();
        return (db, flow, output);
    }

    [Fact]
    public async Task Current_config_is_the_live_setup_until_first_publish()
    {
        using var scope = factory.Services.CreateScope();
        var (db, flow, output) = await Seed(scope);

        var config = await FlowConfigLoader.CurrentAsync(db, flow, default);

        config.VersionNo.Should().BeNull();
        config.Outputs.Should().ContainSingle().Which.Id.Should().Be(output.Id);
    }

    [Fact]
    public async Task Current_config_ignores_live_edits_after_publish()
    {
        using var scope = factory.Services.CreateScope();
        var (db, flow, output) = await Seed(scope);
        db.Set<FlowVersion>().Add(new FlowVersion
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, VersionNo = 1, PublishedAt = DateTime.UtcNow,
            SnapshotJson = FlowSnapshot.Of(flow, [output]).Canonical(),
        });
        flow.PublishedVersionNo = 1;
        output.TemplateJson = """{"b":2}""";
        db.Set<FlowOutput>().Add(Output(flow.Id, """{"c":3}"""));
        await db.SaveChangesAsync();

        var config = await FlowConfigLoader.CurrentAsync(db, flow, default);

        config.VersionNo.Should().Be(1);
        config.Outputs.Should().ContainSingle().Which.TemplateJson.Should().Be("""{"a":1}""");
    }

    private static string Template(string column) => DocumentJson.Serialize(new OutputNodeTemplate
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root", OutputNode.FieldOf(column, new OutputFieldRule { OutputPath = column, CanonicalField = "invoice_no" })),
    });

    /// <summary>A flow with one output and one ready document; optionally published as v1 first.</summary>
    private async Task<(Guid DocId, Flow Flow, FlowOutput Output)> SeedDocument(bool publish)
    {
        using var scope = factory.Services.CreateScope();
        var (db, flow, output) = await Seed(scope);
        output.TemplateJson = Template("A");
        if (publish)
        {
            db.Set<FlowVersion>().Add(new FlowVersion
            {
                Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, VersionNo = 1, PublishedAt = DateTime.UtcNow,
                SnapshotJson = FlowSnapshot.Of(flow, [output]).Canonical(),
            });
            flow.PublishedVersionNo = 1;
        }
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf", Status = DocumentStatus.Ready,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice",
                [new ReadField("invoice_no", "Invoice number", "INV-7", FieldTypes.Text, null, 1, null, false, true, false)], [])),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return (doc.Id, flow, output);
    }

    private async Task<(List<FlowDelivery> Deliveries, string Content, FlowDocument Doc, string AuditPayload)> SendAndRead(Guid docId)
    {
        (await factory.ClientFor(Org).PostAsync($"/api/documents/{docId}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var deliveries = await db.Set<FlowDelivery>().AsNoTracking().Where(d => d.DocumentId == docId).ToListAsync();
        // Storage is a mock: read back the bytes handed to UploadAsync for this delivery's key.
        var upload = factory.Storage.Invocations.Last(i => i.Method.Name == "UploadAsync" && (string)i.Arguments[1] == deliveries[0].ArtifactKey);
        var body = (Stream)upload.Arguments[0];
        body.Position = 0;
        var content = await new StreamReader(body).ReadToEndAsync();
        var doc = await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == docId);
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.EntityId == docId && a.Action == "document.sent").Select(a => a.Payload).SingleAsync();
        return (deliveries, content, doc, audit.RootElement.ToString());
    }

    [Fact]
    public async Task Send_uses_the_live_version_not_the_changes()
    {
        var (docId, flow, output) = await SeedDocument(publish: true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<FlowOutput>().Add(Output(flow.Id, Template("C")));
            var live = await db.Set<FlowOutput>().SingleAsync(o => o.Id == output.Id);
            live.TemplateJson = Template("B");
            await db.SaveChangesAsync();
            db.Set<FlowOutput>().Remove(live);
            await db.SaveChangesAsync();
        }

        var (deliveries, content, doc, audit) = await SendAndRead(docId);

        deliveries.Should().ContainSingle().Which.OutputId.Should().Be(output.Id);
        content.Should().Contain("\"A\"").And.NotContain("\"B\"");
        doc.FlowVersion.Should().Be(1);
        audit.Should().Contain("\"version\":1");
        var dto = await factory.ClientFor(Org).GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/documents/{docId}");
        dto.GetProperty("summary").GetProperty("flowVersion").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Unpublished_flow_sends_its_live_outputs()
    {
        var (docId, _, _) = await SeedDocument(publish: false);

        var (deliveries, content, doc, audit) = await SendAndRead(docId);

        deliveries.Should().ContainSingle();
        content.Should().Contain("\"A\"");
        doc.FlowVersion.Should().BeNull();
        audit.Should().Contain("\"version\":null");
    }

    [Fact]
    public async Task Recheck_stamps_the_published_version()
    {
        var (docId, flow, _) = await SeedDocument(publish: true);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var doc = await db.FlowDocuments.SingleAsync(d => d.Id == docId);
        var liveFlow = await db.Flows.SingleAsync(f => f.Id == flow.Id);

        await scope.ServiceProvider.GetRequiredService<DocumentRechecker>()
            .ApplyAsync(doc, liveFlow, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), default);

        doc.FlowVersion.Should().Be(1);
    }

    private static async Task<System.Text.Json.JsonElement> Body(HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

    [Fact]
    public async Task Publish_then_edit_shows_changes_and_restore_clears_them()
    {
        Guid flowId, outputId;
        using (var scope = factory.Services.CreateScope())
        {
            var (_, flow, output) = await Seed(scope);
            (flowId, outputId) = (flow.Id, output.Id);
        }
        var client = factory.ClientFor(Org);

        var published = await Body(await client.PostAsync($"/api/flows/{flowId}/publish", null));
        published.GetProperty("publishedVersion").GetInt32().Should().Be(1);
        published.GetProperty("hasChanges").GetBoolean().Should().BeFalse();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.Set<FlowOutput>().SingleAsync(o => o.Id == outputId)).TemplateJson = """{"a":2}""";
            await db.SaveChangesAsync();
        }
        (await Body(await client.GetAsync($"/api/flows/{flowId}"))).GetProperty("hasChanges").GetBoolean().Should().BeTrue();

        var restored = await Body(await client.PostAsync($"/api/flows/{flowId}/versions/1/restore", null));
        restored.GetProperty("hasChanges").GetBoolean().Should().BeFalse();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.Set<FlowOutput>().AsNoTracking().SingleAsync(o => o.Id == outputId)).TemplateJson.Should().Be("""{"a":1}""");
        }
        var versions = await Body(await client.GetAsync($"/api/flows/{flowId}/versions"));
        versions.GetArrayLength().Should().Be(1);
        versions[0].GetProperty("versionNo").GetInt32().Should().Be(1);
        versions[0].GetProperty("live").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_what_the_live_version_uses_is_refused()
    {
        var destination = new FlowDestination
        {
            Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic destination", Protocol = "http",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var lookupId = Guid.NewGuid();
        Guid flowId, outputId, lookupFlowId;
        using (var scope = factory.Services.CreateScope())
        {
            var (db, flow, output) = await Seed(scope);
            db.Set<FlowDestination>().Add(destination);
            output.DestinationId = destination.Id;
            db.Set<Lookup>().Add(new Lookup { Id = lookupId, OrgId = Org, Name = "Synthetic lookup", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            (flowId, outputId) = (flow.Id, output.Id);

            var (_, lookupFlow, lookupOutput) = await Seed(scope);
            lookupOutput.TemplateJson = DocumentJson.Serialize(new OutputNodeTemplate
            {
                Format = OutputFormat.Json,
                Root = OutputNode.Obj("root", OutputNode.FieldOf("A", new OutputFieldRule
                {
                    OutputPath = "A", CanonicalField = "invoice_no",
                    FieldManipulators = [new ManipulatorEntry { Type = "Lookup", Params = [lookupId.ToString()] }],
                })),
            });
            await db.SaveChangesAsync();
            lookupFlowId = lookupFlow.Id;
        }
        var client = factory.ClientFor(Org);
        (await client.PostAsync($"/api/flows/{flowId}/publish", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/flows/{lookupFlowId}/publish", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            (await db.Set<FlowOutput>().SingleAsync(o => o.Id == outputId)).DestinationId = null;
            await db.SaveChangesAsync();
        }
        var refused = await client.DeleteAsync($"/api/destinations/{destination.Id}");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(refused)).GetProperty("error").GetString().Should().Be("destination_in_use");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<Lookup>().Remove(await db.Set<Lookup>().SingleAsync(l => l.Id == lookupId));
            await db.SaveChangesAsync();
        }
        var restore = await client.PostAsync($"/api/flows/{lookupFlowId}/versions/1/restore", null);
        restore.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(restore)).GetProperty("error").GetString().Should().Be("version_references_missing");
    }
}
