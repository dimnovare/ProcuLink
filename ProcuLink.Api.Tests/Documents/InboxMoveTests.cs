using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>The class shares one database: each test uses its own kind, so a resort never sees another test's documents.</summary>
public sealed class InboxMoveTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private static readonly string Read = DocumentJson.Serialize(new ReadResult("Invoice",
        [new ReadField("invoice_number", "Invoice number", "INV-2292", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)]));

    private T Db<T>(Func<ProcuLinkDbContext, T> query)
    {
        using var scope = factory.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>());
    }

    private async Task<Guid> FlowAsync(Guid org, string name, string? holdsKind = null)
    {
        factory.ClientFor(org).Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var flow = FlowCreation.New(org, name, FlowFieldList.Empty, confirmed: true);
        db.Flows.Add(flow);
        if (holdsKind is not null) db.FlowDocuments.Add(NewDoc(org, flow.Id, holdsKind, DocumentStatus.Sent, null, null));
        await db.SaveChangesAsync();
        return flow.Id;
    }

    private static FlowDocument NewDoc(Guid org, Guid flowId, string? kind, string status, string? reason, string? senderKey) => new()
    {
        Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, FileName = "invoice-2292.pdf", ContentType = "application/pdf",
        SourceFileKey = "k", BillingUnits = 1, Kind = kind, SenderKey = senderKey, Status = status, StatusReason = reason,
        ReadJson = Read, ReadAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>A document of <paramref name="org"/> waiting in its inbox, or in <paramref name="flowId"/> when given.</summary>
    private async Task<FlowDocument> DocAsync(Guid org, string? kind = null, string? senderKey = null, Guid? flowId = null, string status = DocumentStatus.NeedsReview)
    {
        factory.ClientFor(org).Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var doc = NewDoc(org, flowId ?? (await InboxFlow.GetOrCreateAsync(db, org, CancellationToken.None)).Id, kind, status, DocumentSort.NewKind, senderKey);
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private FlowDocument Doc(Guid id) => Db(db => db.FlowDocuments.AsNoTracking().Single(d => d.Id == id));

    private Task<HttpResponseMessage> Move(Guid org, Guid docId, Guid flowId) =>
        factory.ClientFor(org).PostAsJsonAsync($"/api/documents/{docId}/move", new { flowId });

    private static async Task ShouldRefuse(HttpResponseMessage res, HttpStatusCode status, string error)
    {
        res.StatusCode.Should().Be(status);
        (await res.Content.ReadAsStringAsync()).Should().Contain(error);
    }

    [Fact]
    public async Task Move_rereads_in_the_target_remembers_and_resorts_the_others_waiting()
    {
        var f = await FlowAsync(OrgA, "Supplier invoices");
        var a = await DocAsync(OrgA, "invoice", "name:example supplier ltd");
        var b = await DocAsync(OrgA, "invoice", "name:sample traders ltd");
        var reads = factory.Enqueued<ReadDocumentJob>();

        (await Move(OrgA, a.Id, f)).StatusCode.Should().Be(HttpStatusCode.OK);

        var movedA = Doc(a.Id);
        movedA.FlowId.Should().Be(f);
        movedA.Status.Should().Be(DocumentStatus.Received);
        movedA.SortUnconfirmed.Should().BeFalse();
        Db(db => db.Set<FlowSortMemory>().Where(m => m.OrgId == OrgA && m.Kind == "invoice").Select(m => new { m.SenderKey, m.FlowId }).ToList())
            .Should().ContainSingle().Which.Should().Be(new { SenderKey = (string?)"name:example supplier ltd", FlowId = f });
        var movedB = Doc(b.Id);
        movedB.FlowId.Should().Be(f);
        movedB.SortUnconfirmed.Should().BeTrue();
        factory.Enqueued<ReadDocumentJob>().Should().Be(reads + 2);
        Db(db => db.AuditEvents.Count(e => e.EntityId == a.Id && e.Action == "document.moved")).Should().Be(1);
        Db(db => db.AuditEvents.Count(e => e.EntityId == b.Id && e.Action == "document.sorted")).Should().Be(1);
    }

    [Fact]
    public async Task Another_org_can_neither_move_in_nor_be_moved_into()
    {
        var theirs = await FlowAsync(OrgB, "Supplier invoices");
        var mine = await DocAsync(OrgA, senderKey: "name:example cross org sender");

        await ShouldRefuse(await Move(OrgA, mine.Id, theirs), HttpStatusCode.NotFound, "flow_not_found");
        var unchanged = Doc(mine.Id);
        (unchanged.FlowId, unchanged.Status, unchanged.UpdatedAt).Should().Be((mine.FlowId, mine.Status, mine.UpdatedAt));
        Db(db => db.Set<FlowSortMemory>().Any(m => m.SenderKey == "name:example cross org sender")).Should().BeFalse();
        await ShouldRefuse(await Move(OrgB, mine.Id, theirs), HttpStatusCode.NotFound, "document_not_found");

        // The flows to offer: this organisation's person flows holding the kind, in name order.
        var zeta = await FlowAsync(OrgA, "Zeta deliveries", "delivery_note");
        var alpha = await FlowAsync(OrgA, "Alpha deliveries", "delivery_note");
        await FlowAsync(OrgA, "Middle flow");
        await FlowAsync(OrgB, "Aardvark deliveries", "delivery_note");
        var note = await DocAsync(OrgA, "delivery_note");
        var offered = await factory.ClientFor(OrgA).GetFromJsonAsync<List<SortFlow>>($"/api/documents/{note.Id}/sort-flows");
        offered.Should().Equal(new SortFlow(alpha, "Alpha deliveries"), new SortFlow(zeta, "Zeta deliveries"));
        (await factory.ClientFor(OrgA).GetFromJsonAsync<List<SortFlow>>($"/api/documents/{mine.Id}/sort-flows")).Should().BeEmpty();
        await ShouldRefuse(await factory.ClientFor(OrgB).GetAsync($"/api/documents/{note.Id}/sort-flows"), HttpStatusCode.NotFound, "document_not_found");
    }

    private sealed record SortFlow(Guid Id, string Name);

    [Fact]
    public async Task Refusals()
    {
        var f = await FlowAsync(OrgA, "Refusal flow");
        var waiting = await DocAsync(OrgA);
        var inbox = waiting.FlowId;
        await ShouldRefuse(await Move(OrgA, waiting.Id, inbox), HttpStatusCode.NotFound, "flow_not_found");
        // Editing a waiting document rechecks it in the inbox, and it keeps waiting for its reason.
        (await factory.ClientFor(OrgA).PatchAsJsonAsync($"/api/documents/{waiting.Id}/values",
            new { fields = new[] { new { key = "invoice_number", value = "INV-2293" } } })).StatusCode.Should().Be(HttpStatusCode.OK);
        Doc(waiting.Id).StatusReason.Should().Be(DocumentSort.NewKind);

        var sent = await DocAsync(OrgA, flowId: await FlowAsync(OrgA, "Sent flow"), status: DocumentStatus.Sent);
        await ShouldRefuse(await Move(OrgA, sent.Id, f), HttpStatusCode.Conflict, "not_movable");

        await SetAccountStatus("past_due");
        try
        {
            await ShouldRefuse(await Move(OrgA, waiting.Id, f), HttpStatusCode.Conflict, "processing_paused");
        }
        finally
        {
            await SetAccountStatus("active");
        }
    }

    private async Task SetAccountStatus(string status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        (await db.Organisations.SingleAsync(o => o.Id == OrgA)).AccountStatus = status;
        await db.SaveChangesAsync();
    }
}
