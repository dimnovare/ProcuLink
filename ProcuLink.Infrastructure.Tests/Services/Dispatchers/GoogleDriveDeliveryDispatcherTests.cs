using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class GoogleDriveDeliveryDispatcherTests
{
    private const string Upload = "https://upload.example.test/session-1";

    /// <summary>Stands in for Google behind the "google" client; records every request.</summary>
    private sealed class FakeDrive(string lookupJson, HttpStatusCode openStatus = HttpStatusCode.OK, string openBody = "") : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Requests = [];

        public HttpClient CreateClient(string name) =>
            name == GoogleOAuthClient.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((request.Method, url, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, lookupJson);
            if (request.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.OK);
            var open = Json(openStatus, openBody);
            if (openStatus == HttpStatusCode.OK) open.Headers.Location = new Uri(Upload);
            return open;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeTokens : ILinkedAccountTokens
    {
        public Task<LinkedAccessToken> GetGoogleAccessTokenAsync(Guid orgId, Guid accountId, CancellationToken ct) =>
            Task.FromResult(new LinkedAccessToken("at-1", null));
    }

    private static Task<DeliveryResult> Send(FakeDrive drive, string fileName) =>
        new GoogleDriveDeliveryDispatcher(drive, new FakeTokens(), NullLogger<GoogleDriveDeliveryDispatcher>.Instance).DispatchAsync(
            "a,b\r\n1,2\r\n"u8.ToArray(), fileName, "text/csv",
            new SupplierDeliveryConfig
            {
                OrgId = Guid.NewGuid(), Protocol = DeliveryProtocolConstants.GoogleDrive,
                ConfigJson = $$"""{"accountId":"{{Guid.NewGuid()}}","folderId":"fold-1","folderName":"Invoices"}""",
            }, "", default);

    [Fact]
    public async Task Upload_NewFile_CreatesInFolder()
    {
        var drive = new FakeDrive("""{"files":[]}""");

        var result = await Send(drive, "it's-a1b2c3d4.csv");

        result.Success.Should().BeTrue();
        drive.Requests[0].Url.Should().Contain("q=name = 'it\\'s-a1b2c3d4.csv' and 'fold-1' in parents and trashed = false");
        drive.Requests[1].Method.Should().Be(HttpMethod.Post);
        drive.Requests[1].Body.Should().Contain("\"parents\":[\"fold-1\"]");
        drive.Requests[2].Should().Be((HttpMethod.Put, Upload, "a,b\r\n1,2\r\n"));
    }

    [Fact]
    public async Task Upload_ExistingName_UpdatesThatFile()
    {
        var drive = new FakeDrive("""{"files":[{"id":"f-9"}]}""");

        (await Send(drive, "x.csv")).Success.Should().BeTrue();

        drive.Requests[1].Method.Should().Be(HttpMethod.Patch);
        drive.Requests[1].Url.Should().Contain("/upload/drive/v3/files/f-9?uploadType=resumable");
    }

    [Fact]
    public async Task FolderGone_IsNotRetryable()
    {
        var result = await Send(new FakeDrive("""{"files":[]}""", HttpStatusCode.NotFound), "x.csv");

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        result.ErrorMessage.Should().Be("The Google Drive folder is gone or ProcuLink can no longer see it. Choose the folder again.");
    }
}
