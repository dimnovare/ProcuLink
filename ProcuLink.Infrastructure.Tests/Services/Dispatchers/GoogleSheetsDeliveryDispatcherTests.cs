using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

public class GoogleSheetsDeliveryDispatcherTests
{
    /// <summary>Stands in for Sheets behind the "google" client; records every request.</summary>
    private sealed class FakeSheets(string rowOneJson, string metaJson = "{}") : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Requests = [];

        public HttpClient CreateClient(string name) =>
            name == GoogleOAuthClient.HttpClientName ? new HttpClient(this, disposeHandler: false) : throw new InvalidOperationException(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((request.Method, url, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var json = request.Method == HttpMethod.Post ? "{}" : url.Contains("/values/") ? rowOneJson : metaJson;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeTokens : ILinkedAccountTokens
    {
        public Task<LinkedAccessToken> GetGoogleAccessTokenAsync(Guid orgId, Guid accountId, CancellationToken ct) =>
            Task.FromResult(new LinkedAccessToken("at-1", null));
    }

    private static Task<DeliveryResult> Send(FakeSheets sheets, string csv, string contentType, bool checkOnly = false) =>
        new GoogleSheetsDeliveryDispatcher(sheets, new FakeTokens(), NullLogger<GoogleSheetsDeliveryDispatcher>.Instance).DispatchAsync(
            Encoding.UTF8.GetBytes(csv), contentType == "text/csv" ? "out.csv" : "out.pdf", contentType,
            new SupplierDeliveryConfig
            {
                OrgId = Guid.NewGuid(), Protocol = DeliveryProtocolConstants.GoogleSheets,
                ConfigJson = $$"""{"accountId":"{{Guid.NewGuid()}}","spreadsheetId":"sh-1","spreadsheetName":"Orders","sheetName":"Q4 'test'","checkOnly":{{checkOnly.ToString().ToLowerInvariant()}}}""",
            }, "", default);

    [Fact]
    public async Task Append_SkipsMatchingHeader_AsRaw()
    {
        var sheets = new FakeSheets("""{"values":[["Number","Total"]]}""");

        var result = await Send(sheets, "Number,Total,\r\nA-1,=1+1,12.50,007,1234567890123456\r\n", "text/csv");

        result.Success.Should().BeTrue();
        var append = sheets.Requests.Single(r => r.Url.Contains(":append"));
        append.Url.Split('?')[0].Should().EndWith("values/'Q4 ''test'''!A1:append");
        append.Url.Should().Contain("valueInputOption=RAW").And.Contain("insertDataOption=INSERT_ROWS");
        var cells = JsonNode.Parse(append.Body)!["values"]![0]!.AsArray();
        cells[0]!.GetValue<string>().Should().Be("A-1");
        cells[1]!.GetValue<string>().Should().Be("=1+1");
        cells[2]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Number);
        cells[2]!.ToJsonString().Should().Be("12.50");
        cells[3]!.GetValue<string>().Should().Be("007");
        cells[4]!.GetValue<string>().Should().Be("1234567890123456");
    }

    [Fact]
    public async Task CheckOnly_NeverAppends()
    {
        var sheets = new FakeSheets("{}", """{"sheets":[{"properties":{"title":"Q4 'test'"}}]}""");

        var result = await Send(sheets, "a,b\r\n1,2\r\n", "text/csv", checkOnly: true);

        result.Success.Should().BeTrue();
        sheets.Requests.Should().NotContain(r => r.Url.Contains(":append"));
    }

    [Fact]
    public async Task NonCsv_IsNotRetryable()
    {
        var result = await Send(new FakeSheets("{}"), "%PDF", "application/pdf");

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        result.ErrorMessage.Should().Be("Google Sheets needs a CSV output. Change this output's format to CSV.");
    }
}
