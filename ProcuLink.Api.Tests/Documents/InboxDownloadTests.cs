using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class InboxDownloadTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private static ReadResult Read()
    {
        ReadRow Row(string code) => new(new Dictionary<string, ReadCell> { ["code"] = new(code, 0.99, null, false, false) });
        return new ReadResult("Invoice",
            [new ReadField("invoice_number", "Invoice number", "INV-1", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)],
            [new ReadTable("items", "Items", [new ReadColumn("code", "Code", FieldTypes.Text, null)], [Row("A-7"), Row("A-8")], true)]);
    }

    /// <summary>A read document waiting in the inbox, or in a person flow when <paramref name="inFlow"/>.</summary>
    private async Task<Guid> DocAsync(string status = DocumentStatus.NeedsReview, bool inFlow = false)
    {
        factory.ClientFor(OrgA).Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flowId = (await InboxFlow.GetOrCreateAsync(db, OrgA, CancellationToken.None)).Id;
        if (inFlow)
        {
            var flow = FlowCreation.New(OrgA, "Supplier invoices", FlowFieldList.Empty, confirmed: true, FlowAutomation.Review);
            db.Flows.Add(flow);
            flowId = flow.Id;
        }
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flowId, FileName = "invoice-2292.pdf", ContentType = "application/pdf",
            SourceFileKey = "k", BillingUnits = 1, Status = status, StatusReason = DocumentSort.NewKind,
            ReadJson = DocumentJson.Serialize(Read()), ReadAt = status == DocumentStatus.Received ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private Task<HttpResponseMessage> Download(Guid org, Guid id, string format) =>
        factory.ClientFor(org).PostAsJsonAsync($"/api/documents/{id}/download", new { format });

    private int Downloads(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().AuditEvents.Count(a => a.EntityId == id && a.Action == "document.downloaded");
    }

    [Fact]
    public async Task Download_gives_the_read_and_marks_the_document()
    {
        var id = await DocAsync();

        var csv = await Download(OrgA, id, "csv");

        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        (await csv.Content.ReadAsStringAsync()).Should().Contain("INV-1").And.Contain("A-8");
        csv.Content.Headers.ContentDisposition!.FileName.Should().Contain("invoice-2292.csv");
        using (var scope = factory.Services.CreateScope())
        {
            var doc = await scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>().FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == id);
            (doc.Status, doc.StatusReason).Should().Be((DocumentStatus.Sent, "downloaded"));
        }
        Downloads(id).Should().Be(1);
        // Sent by status, never sent this week: the since-date reads the deliveries' DeliveredAt, and a download makes none.
        var since = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-7).ToString("o"));
        (await factory.ClientFor(OrgA).GetStringAsync("/api/documents?status=sent&take=200")).Should().Contain(id.ToString());
        (await factory.ClientFor(OrgA).GetStringAsync($"/api/documents?status=sent&sentFrom={since}")).Should().NotContain(id.ToString());

        var xlsx = await Download(OrgA, id, "xlsx");
        xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.Should().Be(DocumentOutputRenderer.XlsxContentType);
        Downloads(id).Should().Be(1);
    }

    [Fact]
    public async Task Download_refusals()
    {
        var waiting = await DocAsync();
        (HttpResponseMessage Res, HttpStatusCode Status, string Error)[] refusals =
        [
            (await Download(OrgA, await DocAsync(inFlow: true), "csv"), HttpStatusCode.Conflict, "not_in_inbox"),
            (await Download(OrgA, waiting, "pdf"), HttpStatusCode.BadRequest, "invalid_format"),
            // No format at all reaches the controller's own answer, not the framework's validation 400.
            (await factory.ClientFor(OrgA).PostAsJsonAsync($"/api/documents/{waiting}/download", new { }), HttpStatusCode.BadRequest, "invalid_format"),
            (await Download(OrgA, await DocAsync(DocumentStatus.Received), "csv"), HttpStatusCode.Conflict, "not_read"),
            (await Download(OrgB, waiting, "csv"), HttpStatusCode.NotFound, "document_not_found"),
        ];
        foreach (var (res, status, error) in refusals)
        {
            res.StatusCode.Should().Be(status);
            (await res.Content.ReadAsStringAsync()).Should().Contain(error);
        }
    }
}
