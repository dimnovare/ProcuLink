using System.Net;
using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services.Google;
using ProcuLink.Core.Services.Microsoft;
using ProcuLink.Infrastructure.Services.Microsoft;

namespace ProcuLink.Infrastructure.Tests.Services.OneDrive;

public class MicrosoftDriveLinksTests
{
    /// <summary>Stands in for Graph behind the "microsoft" client and for the token store; records every request.</summary>
    private sealed class FakeGraph(string answer) : HttpMessageHandler, IHttpClientFactory, ILinkedAccountTokens
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public string? Provider;

        public HttpClient CreateClient(string name) =>
            name == MicrosoftOAuthClient.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        public Task<LinkedAccessToken> GetAccessTokenAsync(Guid orgId, Guid accountId, string provider, CancellationToken ct)
        {
            Provider = provider;
            return Task.FromResult(new LinkedAccessToken("at-1", null));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
        }
    }

    private static Task<DriveItemRef> Resolve(FakeGraph graph, string link) =>
        new MicrosoftDriveLinks(graph, graph).ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), link, default);

    [Theory]
    [InlineData("https://contoso.sharepoint.com/:f:/s/team/EaBc1?e=a~b", "/v1.0/shares/u!aHR0cHM6Ly9jb250b3NvLnNoYXJlcG9pbnQuY29tLzpmOi9zL3RlYW0vRWFCYzE_ZT1hfmI/driveItem")]
    [InlineData("https://contoso.sharepoint.com/:f:/s/team/Ea~~?e=1", "/v1.0/shares/u!aHR0cHM6Ly9jb250b3NvLnNoYXJlcG9pbnQuY29tLzpmOi9zL3RlYW0vRWF-fj9lPTE/driveItem")]
    public async Task Resolve_Folder_EncodesAndRedeemsTheLink(string link, string path)
    {
        var graph = new FakeGraph("""{"id":"item-1","name":"Orders","folder":{},"parentReference":{"driveId":"drive-1"}}""");

        var result = await Resolve(graph, link);

        result.Should().Be(new DriveItemRef("drive-1", "item-1", "Orders"));
        var request = graph.Requests.Should().ContainSingle().Subject;
        request.RequestUri!.AbsolutePath.Should().Be(path);
        Uri.UnescapeDataString(request.RequestUri.Query).Should().Contain("$select=id,name,folder,file,parentReference");
        request.Headers.GetValues("Prefer").Should().Equal("redeemSharingLink");
        request.Headers.Authorization!.ToString().Should().Be("Bearer at-1");
        graph.Provider.Should().Be("microsoft");
    }

    [Fact]
    public async Task Resolve_RefusesWrongKindAndForeignHost()
    {
        const string wrongHost = "Paste a OneDrive or SharePoint link. In OneDrive or SharePoint, choose Share, then Copy link.";
        var graph = new FakeGraph("{}");
        foreach (var link in new[] { "https://evil.example.com/x", "http://contoso.sharepoint.com/:f:/s/x", "https://contoso.sharepoint.us/:f:/s/x" })
            (await FluentActions.Awaiting(() => Resolve(graph, link)).Should().ThrowAsync<DriveLinkException>()).Which.Message.Should().Be(wrongHost);
        graph.Requests.Should().BeEmpty();

        var file = new FakeGraph("""{"id":"item-1","name":"a.pdf","file":{},"parentReference":{"driveId":"drive-1"}}""");
        (await FluentActions.Awaiting(() => Resolve(file, "https://1drv.ms/f/s!abc")).Should().ThrowAsync<DriveLinkException>())
            .Which.Message.Should().Be("That link opens a file. Paste the link to a folder.");
    }
}
