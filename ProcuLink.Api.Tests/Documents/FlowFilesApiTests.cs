using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class FlowFilesApiTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: the claim-first ledger relies on a unique index and on ExecuteUpdate.
    private DocumentEngineTestFactory factory = null!;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_flowfiles") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    private static readonly byte[] Csv = "sku,qty\nA-1,2\n"u8.ToArray();

    private async Task<Guid> NewFlow(string name = "Synthetic flow")
    {
        var created = await factory.ClientFor(DocumentEngineTestFactory.OrgA).PostAsJsonAsync("/api/flows", new CreateFlowRequest(name));
        return (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, Guid flowId, byte[] bytes, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/flows/{flowId}/documents")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "orders.csv" } },
        };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return http.SendAsync(req);
    }

    [DockerRequiredFact]
    public async Task A_file_posted_with_an_api_key_is_read_and_fetchable()
    {
        var http = factory.ApiKeyClientFor(DocumentEngineTestFactory.OrgA);
        var res = await Post(http, await NewFlow(), Csv);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var doc = (await res.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options))!;
        Assert.Equal("api", doc.Source);

        var got = await http.GetAsync($"/api/v1/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Contains(doc.Id.ToString(), await got.Content.ReadAsStringAsync());

        var other = await factory.ApiKeyClientFor(DocumentEngineTestFactory.OrgB).GetAsync($"/api/v1/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Contains("document_not_found", await other.Content.ReadAsStringAsync());
    }

    [DockerRequiredFact]
    public async Task The_same_idempotency_key_twice_returns_the_same_document()
    {
        var http = factory.ApiKeyClientFor(DocumentEngineTestFactory.OrgA);
        var flowId = await NewFlow();

        var first = await Post(http, flowId, Csv, "retry-1");
        var second = await Post(http, flowId, Csv, "retry-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replay").Single());
        var a = await first.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options);
        var b = await second.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options);
        Assert.Equal(a!.Id, b!.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLink.Infrastructure.ProcuLinkDbContext>();
        Assert.Equal(1, db.FlowDocuments.IgnoreQueryFilters().Count(d => d.FlowId == flowId));
    }

    [DockerRequiredFact]
    public async Task A_retry_after_a_failed_submit_creates_the_document()
    {
        var http = factory.ApiKeyClientFor(DocumentEngineTestFactory.OrgA);
        var flowId = await NewFlow();
        factory.Storage.SetupSequence(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage down"))
            .ReturnsAsync("key");

        var failed = await Post(http, flowId, Csv, "retry-2");
        var retried = await Post(http, flowId, Csv, "retry-2");

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }
}
