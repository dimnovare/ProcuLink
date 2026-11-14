using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class DocumentListApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private async Task Save(object entity)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> Flow(Guid org)
    {
        factory.ClientFor(org).Dispose();
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Synthetic flow", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        await Save(flow);
        return flow.Id;
    }

    private async Task<FlowDocument> Doc(Guid org, Guid flowId, string fileName, string? dedupKey = null,
        string status = DocumentStatus.Sent, DateTime? createdAt = null)
    {
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, FileName = fileName, ContentType = "application/pdf",
            DedupKey = dedupKey, Status = status, CreatedAt = createdAt ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        await Save(doc);
        return doc;
    }

    private Task Delivery(FlowDocument doc, Guid destinationId, string status, Guid? outputId) => Save(new FlowDelivery
    {
        Id = Guid.NewGuid(), OrgId = doc.OrgId, DocumentId = doc.Id, DestinationId = destinationId, OutputId = outputId,
        Status = status, DispatchKey = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    });

    private async Task<List<string>> Names(Guid org, string query) =>
        (await factory.ClientFor(org).GetFromJsonAsync<List<DocumentSummaryDto>>($"/api/documents?{query}"))!.Select(d => d.FileName).ToList();

    private async Task<PaginatedResult<DocumentSummaryDto>> Page(Guid org, string query) =>
        (await factory.ClientFor(org).GetFromJsonAsync<PaginatedResult<DocumentSummaryDto>>($"/api/documents?{query}"))!;

    [Fact]
    public async Task Search_matches_file_name_or_number_and_nothing_else()
    {
        var flow = await Flow(Org);
        await Doc(Org, flow, "Invoice-2291.pdf", dedupKey: "INV2291");
        await Doc(Org, flow, "scan_0042.pdf", dedupKey: "PO48213");
        await Doc(Org, flow, "notes.csv", dedupKey: "sha256:ab12ab12");
        await Doc(OrgB, await Flow(OrgB), "invoice-2291.pdf", dedupKey: "INV2291");

        Assert.Equal(["Invoice-2291.pdf"], await Names(Org, "q=invoice"));                  // any case, this workspace only
        Assert.Equal(["Invoice-2291.pdf"], await Names(Org, "q=inv%202291"));               // the number; spaces and case ignored
        Assert.Equal(["scan_0042.pdf"], await Names(Org, "q=48213"));
        Assert.Empty(await Names(Org, "q=ab12"));                                           // a file hash is not a number
        Assert.Empty(await Names(Org, "q=%25"));                                            // % is a plain character
        Assert.Equal(["scan_0042.pdf"], await Names(Org, "q=_"));                           // so is _
        Assert.Equal(3, (await Names(Org, $"flowId={flow}&q=%20%20")).Count);               // a blank term is no filter
    }

    [Fact]
    public async Task Paging_gives_a_total_and_each_document_once()
    {
        var flow = await Flow(Org);
        var at = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            await Doc(Org, flow, $"same-instant-{i}.pdf", status: i < 2 ? DocumentStatus.NeedsReview : DocumentStatus.Sent, createdAt: at);

        var seen = new List<Guid>();
        foreach (var page in new[] { 1, 2, 3, 4 })
        {
            var result = await Page(Org, $"flowId={flow}&page={page}&pageSize=2");
            Assert.Equal(5, result.TotalCount);
            seen.AddRange(result.Items.Select(d => d.Id));
        }
        Assert.Equal((5, 5), (seen.Count, seen.Distinct().Count()));                        // page 4 is empty; nothing repeats
        var absurd = await Page(Org, $"flowId={flow}&page=2000000000&pageSize=2");          // no overflowed Skip
        Assert.Equal((0, 5), (absurd.Items.Count, absurd.TotalCount));

        Assert.Equal(2, (await Page(Org, $"flowId={flow}&status=read_failed,%20needs_review&page=1")).TotalCount);   // values are trimmed
        var bare = await factory.ClientFor(Org).GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flow}&take=3");
        Assert.Equal((JsonValueKind.Array, 3), (bare.ValueKind, bare.GetArrayLength()));     // without page: the list as before
    }

    [Fact]
    public async Task Waiting_on_a_destination_lists_the_documents_that_stopped_there()
    {
        var flow = await Flow(Org);
        var (here, elsewhere) = (Guid.NewGuid(), Guid.NewGuid());
        var stopped  = await Doc(Org, flow, "stopped.pdf",  status: DocumentStatus.SendFailed);
        var other    = await Doc(Org, flow, "other.pdf",    status: DocumentStatus.SendFailed);
        var retrying = await Doc(Org, flow, "retrying.pdf", status: DocumentStatus.Sending);
        var detached = await Doc(Org, flow, "detached.pdf", status: DocumentStatus.SendFailed);
        var notDead  = await Doc(Org, flow, "not-dead.pdf", status: DocumentStatus.SendFailed);
        var moving   = await Doc(Org, flow, "moving.pdf",   status: DocumentStatus.Sending);
        var foreign  = await Doc(OrgB, await Flow(OrgB), "foreign.pdf", status: DocumentStatus.SendFailed);
        await Delivery(stopped,  here,      FlowDeliveryStatus.DeadLetter, outputId: Guid.NewGuid());
        await Delivery(other,    elsewhere, FlowDeliveryStatus.DeadLetter, outputId: Guid.NewGuid());
        await Delivery(retrying, here,      FlowDeliveryStatus.Failed,     outputId: Guid.NewGuid());
        await Delivery(notDead,  here,      FlowDeliveryStatus.Failed,     outputId: Guid.NewGuid());   // only the delivery status fails
        await Delivery(moving,   here,      FlowDeliveryStatus.DeadLetter, outputId: Guid.NewGuid());   // only the document status fails
        await Delivery(detached, here,      FlowDeliveryStatus.DeadLetter, outputId: null);   // its output was deleted
        await Delivery(foreign,  here,      FlowDeliveryStatus.DeadLetter, outputId: Guid.NewGuid());

        Assert.Equal(["stopped.pdf"], await Names(Org, $"waitingOnDestination={here}"));
    }
}
