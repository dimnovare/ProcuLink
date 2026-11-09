using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Documents;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class DocumentUploadApiTests : IClassFixture<DocumentEngineTestFactory>
{
    private readonly DocumentEngineTestFactory _factory;
    private readonly HttpClient _http;

    public DocumentUploadApiTests(DocumentEngineTestFactory factory)
    {
        _factory = factory;
        _http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
    }

    private static byte[] Pdf(int pages, string marker)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c =>
        {
            for (var i = 1; i <= pages; i++)
                c.Page(p => p.Content().Text($"Synthetic document {marker} page {i}"));
        }).GeneratePdf();
    }

    private async Task<Guid> NewFlow()
    {
        var created = await _http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Synthetic flow"));
        return (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> Upload(Guid flowId, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
        return _http.PostAsync($"/api/flows/{flowId}/documents", form);
    }

    [Fact]
    public async Task Upload_pdf_stores_counts_units_and_enqueues_read()
    {
        var flowId = await NewFlow();
        var before = _factory.Enqueued<ReadDocumentJob>();

        var res = await Upload(flowId, "thirty-seven.pdf", Pdf(37, "units"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options))!;
        Assert.Equal(37, dto.PageCount);
        Assert.Equal(4, dto.BillingUnits);
        Assert.Equal(DocumentStatus.Received, dto.Status);
        Assert.Equal(before + 1, _factory.Enqueued<ReadDocumentJob>());
        _factory.Storage.Verify(s => s.UploadAsync(It.IsAny<Stream>(),
            It.Is<string>(k => k.StartsWith($"{DocumentEngineTestFactory.OrgA}/documents/")),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Same_file_twice_is_held_as_duplicate_without_read()
    {
        var flowId = await NewFlow();
        var bytes = Pdf(1, "duplicate");
        var before = _factory.Enqueued<ReadDocumentJob>();

        await Upload(flowId, "first.pdf", bytes);
        var second = await Upload(flowId, "second.pdf", bytes);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var dto = (await second.Content.ReadFromJsonAsync<DocumentSummaryDto>(DocumentJson.Options))!;
        Assert.Equal(DocumentStatus.NeedsReview, dto.Status);
        Assert.Equal("duplicate_file", dto.StatusReason);
        Assert.Equal(1, dto.FailedChecks);
        Assert.Equal(before + 1, _factory.Enqueued<ReadDocumentJob>());
    }

    [Fact]
    public async Task Jpg_is_accepted_when_scans_are_on()
    {
        var http = _factory.ClientFor(DocumentEngineTestFactory.OrgB);
        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Scans"));
        var flowId = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
        var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "IMG_0001.JPG" } };

        var res = await http.PostAsync($"/api/flows/{flowId}/documents", form);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Docx_is_415_unsupported_format()
    {
        var res = await Upload(await NewFlow(), "letter.docx", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("unsupported_format", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(["pdf", "csv", "xlsx"], body.RootElement.GetProperty("accepted").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Corrupt_pdf_is_422_unreadable_file()
    {
        var before = _factory.Enqueued<ReadDocumentJob>();
        _factory.Storage.Invocations.Clear();

        var res = await Upload(await NewFlow(), "x.pdf", "not a pdf"u8.ToArray());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("unreadable_file", body.RootElement.GetProperty("error").GetString());
        Assert.Empty(_factory.Storage.Invocations);
        Assert.Equal(before, _factory.Enqueued<ReadDocumentJob>());
    }
}
