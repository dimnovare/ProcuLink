using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using ProcuLink.Api.Mcp;
using ProcuLink.Api.Tests.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Mcp;

[Collection("postgres-container")]
public class McpToolsTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private const string Csv = "invoice_no,amount\nINV-SYN-1,10.00\nINV-SYN-2,20.00\n";

    /// <summary>An MCP client over the test host, as an assistant connects with a key.</summary>
    internal static Task<McpClient> Connect(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, McpSetup.Path), TransportMode = HttpTransportMode.StreamableHttp }, http));

    internal static async Task<(bool IsError, string Text)> Call(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args ?? []);
        return (result.IsError == true, string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    private Task<Guid> NewFlow(Guid org, string automation = FlowAutomation.Review)
    {
        factory.ClientFor(org).Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = ProcuLink.Api.Services.Documents.FlowCreation.New(org, "Synthetic flow", FlowFieldList.Empty, confirmed: true, automation);
        db.Flows.Add(flow);
        db.SaveChanges();
        return Task.FromResult(flow.Id);
    }

    private async Task<Guid> Add(McpClient client, Guid flowId, string content)
    {
        var (isError, text) = await Call(client, "add_document", new() { ["fileName"] = $"synthetic-{Guid.NewGuid():N}.csv", ["contentBase64"] = content, ["flowId"] = flowId });
        isError.Should().BeFalse(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("document").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task AddReadList_RoundTrip_WithApiKey()
    {
        var flowId = await NewFlow(OrgA);
        await using var client = await Connect(factory.ApiKeyClientFor(OrgA));

        var flows = JsonDocument.Parse((await Call(client, "list_flows")).Text).RootElement;
        flows.GetProperty("flows").EnumerateArray().Select(f => f.GetProperty("id").GetGuid()).Should().Contain(flowId);
        flows.GetProperty("inboxId").GetGuid().Should().NotBeEmpty();

        var (_, added) = await Call(client, "add_document", new() { ["fileName"] = "synthetic-invoice.csv", ["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Csv)), ["flowId"] = flowId });
        var summary = JsonDocument.Parse(added).RootElement.GetProperty("document");
        summary.GetProperty("status").GetString().Should().Be(DocumentStatus.Received);
        summary.GetProperty("source").GetString().Should().Be("assistant");
        var id = summary.GetProperty("id").GetGuid();

        var (isError, got) = await Call(client, "get_document", new() { ["documentId"] = id });
        isError.Should().BeFalse(got);
        var detail = JsonDocument.Parse(got).RootElement;
        detail.GetProperty("note").GetString().Should().Be(McpResults.UntrustedNote);
        detail.GetProperty("document").GetProperty("document").GetProperty("summary").GetProperty("id").GetGuid().Should().Be(id);

        (await Call(client, "list_documents", new() { ["show"] = "all" })).Text.Should().Contain(id.ToString());
    }

    [Fact]
    public async Task GetDocument_FromAnotherWorkspace_IsNotFound()
    {
        await using var a = await Connect(factory.ApiKeyClientFor(OrgA));
        var id = await Add(a, await NewFlow(OrgA), Convert.ToBase64String(Encoding.UTF8.GetBytes(Csv + "b\n")));
        await using var b = await Connect(factory.ApiKeyClientFor(DocumentEngineTestFactory.OrgB));

        var (isError, text) = await Call(b, "get_document", new() { ["documentId"] = id });
        isError.Should().BeTrue();
        text.Should().StartWith("document_not_found:");
    }

    [Fact]
    public async Task AddDocument_AcceptsWrappedBase64_AndRefusesOversize()
    {
        var flowId = await NewFlow(OrgA);
        await using var client = await Connect(factory.ApiKeyClientFor(OrgA));
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Csv + "wrapped," + new string('x', 200) + "\n"));
        await Add(client, flowId, "data:text/csv;base64," + string.Join("\n", b64.Chunk(76).Select(c => new string(c))));

        var uploads = factory.Storage.Invocations.Count;
        var (isError, text) = await Call(client, "add_document", new()
        {
            ["fileName"] = "big.csv", ["contentBase64"] = Convert.ToBase64String(new byte[IngressLimits.MaxFileBytes + 1]), ["flowId"] = flowId,
        });
        isError.Should().BeTrue();
        text.Should().StartWith("file_too_large:");
        factory.Storage.Invocations.Count.Should().Be(uploads);
    }

    private Guid SeedDoc(Guid flowId, string status, bool sample = false, string checks = "[]")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flowId, FileName = "synthetic.pdf", ContentType = "application/pdf",
            Status = status, IsSample = sample, ChecksJson = checks,
            ReadJson = DocumentJson.Serialize(new ReadResult("Invoice")), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.FlowDocuments.Add(doc);
        db.SaveChanges();
        return doc.Id;
    }

    [Fact]
    public async Task SendDocument_WithAKey_SendsOnlyWhatTheFlowWouldSendByItself()
    {
        var review = SeedDoc(await NewFlow(OrgA), DocumentStatus.Ready);
        var unmarked = SeedDoc(await NewFlow(OrgA, FlowAutomation.Marked), DocumentStatus.NeedsReview);
        await using var client = await Connect(factory.ApiKeyClientFor(OrgA, canSend: true));

        (await Call(client, "send_document", new() { ["documentId"] = review })).Text.Should().StartWith("review_only:");
        (await Call(client, "send_document", new() { ["documentId"] = unmarked })).Text.Should().StartWith("not_marked_ready:");
    }

    [Fact]
    public async Task SendDocument_RefusesHeldAndScopeless_AndSendsAsTheAssistant()
    {
        var flowId = await NewFlow(OrgA, FlowAutomation.Auto);
        (await factory.ClientFor(OrgA).PostAsJsonAsync($"/api/flows/{flowId}/outputs", new { name = "JSON", format = "json" })).EnsureSuccessStatusCode();
        Guid Seed(bool sample = false, string checks = "[]") => SeedDoc(flowId, DocumentStatus.Ready, sample, checks);
        var (ready, sample, repeated) = (Seed(), Seed(sample: true),
            Seed(checks: DocumentJson.Serialize(new[] { new DocumentCheck(DocumentChecks.Duplicate, false, null, "synthetic") })));

        await using (var readOnly = await Connect(factory.ApiKeyClientFor(OrgA)))
            (await Call(readOnly, "send_document", new() { ["documentId"] = ready })).Text.Should().StartWith("key_cannot_send:");

        var keyClient = factory.ApiKeyClientFor(OrgA, canSend: true);
        await using var client = await Connect(keyClient);
        (await Call(client, "send_document", new() { ["documentId"] = sample })).Text.Should().StartWith("sample_document:");
        (await Call(client, "send_document", new() { ["documentId"] = repeated })).Text.Should().StartWith("held_for_person:").And.Contain("already used");
        var (isError, text) = await Call(client, "send_document", new() { ["documentId"] = ready });
        isError.Should().BeFalse(text);

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        (await db.Set<FlowDelivery>().CountAsync(d => d.DocumentId == sample || d.DocumentId == repeated)).Should().Be(0);
        var sent = (await db.AuditEvents.SingleAsync(e => e.EntityId == ready && e.Action == "document.sent")).Payload!.RootElement;
        sent.GetProperty("mode").GetString().Should().Be("assistant");
        sent.GetProperty("by").GetString().Should().StartWith("apikey:");
    }
}
