using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Documents.Intake;
using ProcuLink.Api.Services.Google;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Google;
using ProcuLink.Infrastructure.Services.Microsoft;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class OutlookPickupSourceTests
{

    /// <summary>Stands in for Graph: answers by method and path, and records every request.</summary>
    private sealed class FakeGraph : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(HttpMethod Method, string Url, string? Auth, string Prefer, string? Body)> Requests = [];

        public HttpClient CreateClient(string name) =>
            name == MicrosoftOAuthClient.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((request.Method, url, request.Headers.Authorization?.ToString(),
                string.Join(",", request.Headers.TryGetValues("Prefer", out var p) ? p : []),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            if (request.Method == HttpMethod.Patch) return new HttpResponseMessage(HttpStatusCode.OK);
            var path = request.RequestUri.AbsolutePath;
            return path switch
            {
                "/v1.0/me/mailFolders/inbox/messages" when url.Contains("$skip=50") => Json("""
                    {"value":[{"id":"m-1","subject":"Invoice","hasAttachments":true,"from":{"emailAddress":{"address":"billing@example.com"}},
                      "internetMessageHeaders":[{"name":"Authentication-Results","value":"spf=pass smtp.mailfrom=example.com; dmarc=pass action=none header.from=example.com"}]}]}
                    """),
                "/v1.0/me/mailFolders/inbox/messages" => Json("""
                    {"value":[{"id":"m-0","subject":"News","hasAttachments":true,"from":{"emailAddress":{"address":"news@other.test"}}}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$skip=50"}
                    """),
                "/v1.0/me/messages/m-1/attachments" => Json("""
                    {"value":[
                      {"@odata.type":"#microsoft.graph.fileAttachment","id":"a-1","name":"invoice.pdf","size":6,"isInline":false},
                      {"@odata.type":"#microsoft.graph.fileAttachment","id":"a-2","name":"logo.png","size":9,"isInline":true},
                      {"@odata.type":"#microsoft.graph.itemAttachment","id":"a-3","name":"forwarded.pdf","size":9,"isInline":false}]}
                    """),
                "/v1.0/me/messages/m-1/attachments/a-1/$value" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1"u8.ToArray()) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeTokens : ILinkedAccountTokens
    {
        public Task<LinkedAccessToken> GetAccessTokenAsync(Guid orgId, Guid accountId, string provider, CancellationToken ct) =>
            Task.FromResult(new LinkedAccessToken(provider == LinkedAccountProviders.Outlook ? "at-1" : null, null));
    }

    [Fact]
    public async Task Outlook_session_lists_matching_unread_mail_across_pages_and_marks_only_done_mail_read()
    {
        var org = Guid.NewGuid();
        var gate = new DocumentEngineGate(Options.Create(new DocumentEngineOptions { EnabledOrgIds = [org.ToString()], NativeConnectorOrgIds = [org.ToString()] }));
        var outlook = new MicrosoftOAuthClient(MicrosoftProfile.Mailbox, null!, Options.Create(new MicrosoftOptions { ClientId = "c", ClientSecret = "s" }));
        var graph = new FakeGraph();
        var source = new OutlookPickupSource(graph, new FakeTokens(), new LinkedAccountAvailability(gate, [outlook], Options.Create(new GoogleOptions())), gate);
        var intake = new FlowIntake
        {
            Id = Guid.NewGuid(), OrgId = org, Kind = IntakeKind.Outlook,
            ConfigJson = $$"""{"accountId":"{{Guid.NewGuid()}}","folder":"Inbox","fromFilter":"example.com","subjectFilter":""}""",
        };

        using var session = await source.OpenAsync(intake, JsonDocument.Parse("{}").RootElement, default);
        var files = await session.ListAsync(default);

        graph.Requests[0].Url.Should().Contain("$filter=isRead eq false");
        files.Should().Equal(new PickupFile("outlook:m-1#a-1", "invoice.pdf", "billing@example.com"));
        (await session.DownloadAsync(files[0], 1024, default)).ToArray().Should().Equal("%PDF-1"u8.ToArray());
        await session.MarkDoneAsync(files[0], default);

        graph.Requests.Should().OnlyContain(r => r.Auth == "Bearer at-1" && r.Prefer.Contains("IdType=\"ImmutableId\""));
        graph.Requests.Where(r => r.Method == HttpMethod.Patch).Should().ContainSingle()
            .Which.Should().Match<(HttpMethod, string Url, string?, string, string? Body)>(r =>
                r.Url == "https://graph.microsoft.com/v1.0/me/messages/m-1" && r.Body == """{"isRead":true}""");
        graph.Requests.Should().NotContain(r => r.Url.Contains("m-0"), "a mail the filters skip is never opened or marked read");
    }
}
