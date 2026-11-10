using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
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
}
