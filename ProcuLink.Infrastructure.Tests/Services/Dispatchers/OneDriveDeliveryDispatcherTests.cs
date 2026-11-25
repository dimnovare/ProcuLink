using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Microsoft;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class OneDriveDeliveryDispatcherTests
{
    /// <summary>Stands in for Graph behind the "microsoft" client and for the token store.</summary>
    private sealed class FakeGraph(HttpResponseMessage answer) : HttpMessageHandler, IHttpClientFactory, ILinkedAccountTokens
    {
        public readonly List<(HttpRequestMessage Request, byte[] Body)> Requests = [];
        public string? Provider;

        public HttpClient CreateClient(string name) =>
            name == MicrosoftOAuthClient.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        public Task<LinkedAccessToken> GetAccessTokenAsync(Guid orgId, Guid accountId, string provider, CancellationToken ct)
        {
            Provider = provider;
            return Task.FromResult(new LinkedAccessToken("at-1", null));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, await request.Content!.ReadAsByteArrayAsync(ct)));
            return answer;
        }
    }

    private static readonly byte[] Csv = "a,b\r\n1,2\r\n"u8.ToArray();

    private static Task<DeliveryResult> Send(FakeGraph graph, string fileName = "x.csv") =>
        new OneDriveDeliveryDispatcher(graph, graph, NullLogger<OneDriveDeliveryDispatcher>.Instance).DispatchAsync(
            Csv, fileName, "text/csv",
            new SupplierDeliveryConfig
            {
                OrgId = Guid.NewGuid(), Protocol = DeliveryProtocolConstants.MicrosoftOneDrive,
                ConfigJson = $$"""{"accountId":"{{Guid.NewGuid()}}","driveId":"drive-1","itemId":"fold-1","itemName":"Orders"}""",
            }, "", default);

    [Fact]
    public async Task Upload_PutsToFolderByName_Replace()
    {
        var graph = new FakeGraph(new HttpResponseMessage(HttpStatusCode.Created));

        var result = await Send(graph, "a:b c.csv");

        result.Success.Should().BeTrue();
        result.ResponseCode.Should().Be(201);
        var (request, body) = graph.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.RequestUri!.PathAndQuery.Should().Be("/v1.0/drives/drive-1/items/fold-1:/a_b%20c.csv:/content?@microsoft.graph.conflictBehavior=replace");
        request.Headers.Authorization!.ToString().Should().Be("Bearer at-1");
        body.Should().Equal(Csv);
        graph.Provider.Should().Be(LinkedAccountProviders.Microsoft);
    }

    [Fact]
    public async Task FolderGone_IsNotRetryable()
    {
        var result = await Send(new FakeGraph(new HttpResponseMessage(HttpStatusCode.NotFound)));

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        result.ErrorMessage.Should().Be("The OneDrive or SharePoint folder is gone or ProcuLink can no longer reach it. Paste the folder's link again.");
    }

    [Fact]
    public async Task Throttled_IsRetryable_WithRetryAfter()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.TryAddWithoutValidation("Retry-After", "7");

        var result = await Send(new FakeGraph(throttled));

        result.Outcome.Should().Be(DeliveryOutcome.Dispatched);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        result.ErrorMessage.Should().Be("OneDrive is not accepting uploads right now (HTTP 429).");
    }
}
