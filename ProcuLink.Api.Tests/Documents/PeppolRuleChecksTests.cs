using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class PeppolRuleChecksTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private const string Standard = "peppol-bis-billing-3";

    private async Task<(Guid FlowId, Guid DocId)> SeedDocument(Guid org)
    {
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = org, Name = "Synthetic flow", FieldsConfirmed = true,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([])), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf",
            Status = DocumentStatus.NeedsReview, ReadJson = DocumentJson.Serialize(PeppolRulesProofTests.Read()), ReadAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return (flow.Id, doc.Id);
    }

    private async Task<Guid> AddOutput(Guid org, Guid flowId)
    {
        var output = new FlowOutput
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, Name = "Peppol invoice", Format = FlowOutputFormats.Xml,
            Standard = Standard, TableKey = "lines", TemplateJson = "{}", CreatedAt = DateTime.UtcNow,
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Set<FlowOutput>().Add(output);
        await db.SaveChangesAsync();
        return output.Id;
    }

    private async Task<(List<DocumentCheck> Checks, string Status)> Recheck(Guid flowId, Guid docId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var doc = await db.FlowDocuments.FirstAsync(d => d.Id == docId);
        var flow = await db.Flows.FirstAsync(f => f.Id == flowId);
        await scope.ServiceProvider.GetRequiredService<DocumentRechecker>().ApplyAsync(doc, flow, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), CancellationToken.None);
        await db.SaveChangesAsync();
        return (DocumentJson.Deserialize<List<DocumentCheck>>(doc.ChecksJson), doc.Status);
    }

    [Fact]
    public async Task A_broken_rule_becomes_a_check_and_never_blocks()
    {
        var org = DocumentEngineTestFactory.OrgA;
        var (flowId, docId) = await SeedDocument(org);
        await AddOutput(org, flowId);
        factory.Validator.Outcome = new(ValidationStatus.Failed,
            [.. Enumerable.Range(1, 4).Select(i => new ValidationIssue($"R-{i}", "error", $"text {i}")), new ValidationIssue("W-1", "warning", "ignored")]);

        var (checks, status) = await Recheck(flowId, docId);

        checks.Should().ContainSingle(c => c.Code == DocumentChecks.OutputRules).Which.Message
            .Should().Be("Peppol invoice doesn't meet 4 Peppol rule(s): R-1 text 1; R-2 text 2; R-3 text 3 and 1 more");
        status.Should().Be(DocumentStatus.Ready);
        (await factory.ClientFor(org).PostAsync($"/api/documents/{docId}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unavailable_adds_nothing()
    {
        var org = DocumentEngineTestFactory.OrgA;
        var (flowId, docId) = await SeedDocument(org);
        await AddOutput(org, flowId);
        factory.Validator.Outcome = new(ValidationStatus.Unavailable, []);

        var (checks, status) = await Recheck(flowId, docId);

        checks.Should().BeEmpty();
        status.Should().Be(DocumentStatus.Ready);
        (await factory.ClientFor(org).PostAsync($"/api/documents/{docId}/send", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Flag_off_refuses_peppol()
    {
        var org = DocumentEngineTestFactory.OrgB;
        var http = factory.ClientFor(org);
        var (flowId, docId) = await SeedDocument(org);
        var outputId = await AddOutput(org, flowId);

        var create = await http.PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "Peppol", format = "", standard = Standard });
        var send = await http.PostAsync($"/api/documents/{docId}/send", null);
        var check = await http.PostAsync($"/api/flows/{flowId}/outputs/{outputId}/check?documentId={docId}", null);
        var (checks, _) = await Recheck(flowId, docId);

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await create.Content.ReadAsStringAsync()).Should().Contain("peppol_off");
        send.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await send.Content.ReadAsStringAsync()).Should().Contain("output_invalid");
        check.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        checks.Should().NotContain(c => c.Code == DocumentChecks.OutputRules);
    }
}
