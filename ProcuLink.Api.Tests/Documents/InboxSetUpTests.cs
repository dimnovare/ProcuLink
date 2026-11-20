using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>The class shares one database: only the first test makes flows for OrgA.</summary>
[Collection("postgres-container")]
public sealed class InboxSetUpTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
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

    private async Task<Guid> InboxDocAsync(Guid org, string senderKey, string kind = "invoice")
    {
        factory.ClientFor(org).Dispose();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = (await InboxFlow.GetOrCreateAsync(db, org, CancellationToken.None)).Id,
            FileName = "invoice-2292.pdf", ContentType = "application/pdf", SourceFileKey = "k", BillingUnits = 1, Kind = kind,
            SenderKey = senderKey, Status = DocumentStatus.NeedsReview, StatusReason = DocumentSort.NewKind, ReadJson = Read,
            ReadAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.FlowDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private Task<HttpResponseMessage> SetUp(Guid org, Guid docId) =>
        factory.ClientFor(org).PostAsJsonAsync($"/api/documents/{docId}/set-up", new { });

    private int PersonFlowCount(Guid org) => Db(db => db.Flows.Count(f => f.OrgId == org && !f.IsInbox));

    [Fact]
    public async Task Set_up_makes_one_flow_and_brings_the_others_waiting()
    {
        var a = await InboxDocAsync(OrgA, "name:example supplier ltd");
        var b = await InboxDocAsync(OrgA, "name:sample traders ltd");
        var reads = factory.Enqueued<ReadDocumentJob>();

        var res = await SetUp(OrgA, a);

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = (await res.Content.ReadFromJsonAsync<SetUpResultDto>(DocumentJson.Options))!;
        result.FlowName.Should().Be("Invoice");
        res.Headers.Location!.ToString().Should().Be($"/api/flows/{result.FlowId}");
        var flow = Db(db => db.Flows.AsNoTracking().Single(f => f.Id == result.FlowId));
        flow.FieldsConfirmed.Should().BeTrue();
        flow.Automation.Should().Be(FlowAutomation.Marked);
        DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson).Fields.Should().Contain(f => f.Key == "invoice_number");
        Db(db => db.Set<FlowOutput>().Where(o => o.FlowId == flow.Id).Select(o => new { o.Name, o.Format }).ToList())
            .Should().ContainSingle().Which.Should().Be(new { Name = "CSV file", Format = "csv" });

        var docA = Db(db => db.FlowDocuments.AsNoTracking().Single(d => d.Id == a));
        docA.FlowId.Should().Be(flow.Id);
        docA.StatusReason.Should().NotBe(DocumentSort.NewKind);
        // Checked in the new flow, not left received: the rechecker ran.
        docA.Status.Should().BeOneOf(DocumentStatus.NeedsReview, DocumentStatus.Ready);
        docA.DedupKey.Should().NotBeNull().And.Be(DocumentChecks.DedupKeyOf(DocumentJson.Deserialize<ReadResult>(Read)));
        DocumentJson.Deserialize<List<DocumentCheck>>(docA.ChecksJson).Should().NotContain(c => c.Code == DocumentChecks.FieldsUnconfirmed);
        var docB = Db(db => db.FlowDocuments.AsNoTracking().Single(d => d.Id == b));
        docB.FlowId.Should().Be(flow.Id);
        docB.SortUnconfirmed.Should().BeTrue();
        factory.Enqueued<ReadDocumentJob>().Should().Be(reads + 1);
        PersonFlowCount(OrgA).Should().Be(1);

        var again = await SetUp(OrgA, b);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("not_in_inbox");

        // A name already used by a flow of the organisation gets a number.
        var c = await InboxDocAsync(OrgA, "name:example second sender");
        (await (await SetUp(OrgA, c)).Content.ReadFromJsonAsync<SetUpResultDto>(DocumentJson.Options))!.FlowName.Should().Be("Invoice 2");
    }

    [Fact]
    public async Task Set_up_respects_the_flow_limit_and_creates_nothing()
    {
        var doc = await InboxDocAsync(OrgB, "name:example supplier ltd");
        var cap = PlanConstants.GetFlowLimit(PlanConstants.Pilot)!.Value;
        string plan;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var org = await db.Organisations.SingleAsync(o => o.Id == OrgB);
            plan = org.Plan;
            (org.Plan, org.TrialStartedAt, org.TrialEndsAt) = (PlanConstants.Pilot, DateTime.UtcNow, null); // an active Pilot
            for (var n = PersonFlowCount(OrgB); n < cap; n++) db.Flows.Add(FlowCreation.New(OrgB, $"Flow {n}", FlowFieldList.Empty, confirmed: false, FlowAutomation.Review));
            await db.SaveChangesAsync();
        }
        try
        {
            var res = await SetUp(OrgB, doc);

            res.StatusCode.Should().Be((HttpStatusCode)429);
            var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            body.GetProperty("error").GetString().Should().Be("flow_limit_reached");
            body.GetProperty("limit").GetInt32().Should().Be(cap);
            PersonFlowCount(OrgB).Should().Be(cap);
            var still = Db(db => db.FlowDocuments.AsNoTracking().Single(d => d.Id == doc));
            (still.FlowId, still.StatusReason).Should().Be((Db(db => db.Flows.Single(f => f.OrgId == OrgB && f.IsInbox).Id), DocumentSort.NewKind));
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var org = await db.Organisations.SingleAsync(o => o.Id == OrgB);
            (org.Plan, org.AccountStatus) = (plan, "active");
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Another_org_cannot_set_up_my_document()
    {
        var doc = await InboxDocAsync(OrgA, "name:example cross org sender", "delivery_note");
        var flows = Db(db => db.Flows.Count());

        var res = await SetUp(OrgB, doc);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await res.Content.ReadAsStringAsync()).Should().Contain("document_not_found");
        Db(db => db.Flows.Count()).Should().Be(flows);
    }
}
