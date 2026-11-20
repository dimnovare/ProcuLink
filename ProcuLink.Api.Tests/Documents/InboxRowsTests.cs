using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>The class shares one database: every document here carries a kind unless the backfill test needs it missing.</summary>
[Collection("postgres-container")]
public sealed class InboxRowsTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    private static string ReadOf(string? kindLabel) => DocumentJson.Serialize(new ReadResult(kindLabel));

    private async Task<(Guid Inbox, Guid Flow)> FlowsAsync(ProcuLinkDbContext db)
    {
        var flow = FlowCreation.New(Org, $"Delivery notes {Guid.NewGuid():N}", FlowFieldList.Empty, confirmed: true, FlowAutomation.Review);
        db.Flows.Add(flow);
        return ((await InboxFlow.GetOrCreateAsync(db, Org, CancellationToken.None)).Id, flow.Id);
    }

    private static FlowDocument NewDoc(Guid flowId, string? kindLabel, string? kind, string fileName = "invoice-2292.pdf") => new()
    {
        Id = Guid.NewGuid(), OrgId = Org, FlowId = flowId, FileName = fileName, ContentType = "application/pdf",
        SourceFileKey = "k", BillingUnits = 1, Kind = kind, Status = DocumentStatus.NeedsReview, StatusReason = DocumentSort.NewKind,
        ReadJson = ReadOf(kindLabel), ReadAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private async Task<(Guid Inbox, Guid Flow)> SeedAsync(Func<(Guid Inbox, Guid Flow), FlowDocument[]> docs)
    {
        factory.ClientFor(Org).Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flows = await FlowsAsync(db);
        db.FlowDocuments.AddRange(docs(flows));
        await db.SaveChangesAsync();
        return flows;
    }

    [Fact]
    public async Task Rows_say_what_a_document_is()
    {
        var flows = await SeedAsync(f =>
            [NewDoc(f.Inbox, "  Delivery note ", "delivery_note", "inbox-note.pdf"), NewDoc(f.Flow, "Delivery note", "delivery_note", "flow-note.pdf")]);

        var http = factory.ClientFor(Org);
        JsonElement Row(JsonElement page, string name) => page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == name);
        var inbox = Row(await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flows.Inbox}&page=1"), "inbox-note.pdf");
        var inFlow = Row(await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flows.Flow}&page=1"), "flow-note.pdf");

        (inbox.GetProperty("kind").GetString(), inbox.GetProperty("inInbox").GetBoolean()).Should().Be(("Delivery note", true));
        inFlow.GetProperty("inInbox").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Backfill_stamps_kind_once()
    {
        FlowDocument[] docs = [];
        await SeedAsync(f => docs = [NewDoc(f.Flow, "Invoices", null), NewDoc(f.Flow, "Invoices", null), NewDoc(f.Flow, null, null)]);
        var (a, b, unlabelled) = (docs[0].Id, docs[1].Id, docs[2].Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        (await DocumentSort.BackfillKindsAsync(db, CancellationToken.None)).Should().Be(2);
        (await db.FlowDocuments.AsNoTracking().Where(d => d.Id == a || d.Id == b).Select(d => d.Kind).ToListAsync())
            .Should().Equal("invoice", "invoice");
        (await DocumentSort.BackfillKindsAsync(db, CancellationToken.None)).Should().Be(0);
        (await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == unlabelled)).Kind.Should().BeNull();
    }

    [Fact]
    public async Task History_names_the_new_actions()
    {
        FlowDocument[] docs = [];
        var flows = await SeedAsync(f => docs = [NewDoc(f.Inbox, "Delivery note", "delivery_note", "moved-note.pdf")]);
        var docId = docs[0].Id;
        var http = factory.ClientFor(Org);

        (await http.PostAsJsonAsync($"/api/documents/{docId}/move", new { flowId = flows.Flow })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/history")).EnumerateArray()
            .Select(h => h.GetProperty("action").GetString()).Should().Contain("document.moved");
        var events = (await http.GetFromJsonAsync<JsonElement>("/api/audit?actions=document.moved&pageSize=200")).GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("documentId").GetString() == docId.ToString()).Select(e => e.GetProperty("message").GetString()).ToList();
        events.Should().ContainSingle().Which.Should().Be("Moved to another flow · moved-note.pdf");
    }
}
