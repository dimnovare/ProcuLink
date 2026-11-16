using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class InboxFlowTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;

    private async Task<Guid> InboxIdAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        return (await InboxFlow.GetOrCreateAsync(db, OrgA, CancellationToken.None)).Id;
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    [Fact]
    public async Task Inbox_is_never_listed_or_counted()
    {
        var http = factory.ClientFor(OrgA);
        var inboxId = await InboxIdAsync();
        Assert.Equal(inboxId, await InboxIdAsync());

        var list = (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!;
        Assert.DoesNotContain(list, f => f.Id == inboxId);
        Assert.DoesNotContain(list, f => f.Name == InboxFlow.Name);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var org = await db.Organisations.SingleAsync(o => o.Id == OrgA);
        var plan = org.Plan;
        org.Plan = PlanConstants.Pilot;
        try
        {
            var cap = PlanConstants.GetFlowLimit(PlanConstants.Pilot)!.Value;
            var existing = await db.Flows.CountAsync(f => f.OrgId == OrgA && !f.IsInbox);
            for (var i = existing; i < cap - 1; i++)
                db.Flows.Add(FlowCreation.New(OrgA, $"Supplier invoices {i}", FlowFieldList.Empty, confirmed: false));
            await db.SaveChangesAsync();

            Assert.Null(await FlowCreation.LimitReachedAsync(db, OrgA, CancellationToken.None));
        }
        finally
        {
            org.Plan = plan;
            await db.SaveChangesAsync();
        }
        Assert.Equal(plan, (await db.Organisations.AsNoTracking().SingleAsync(o => o.Id == OrgA)).Plan);
    }

    [Fact]
    public async Task Flow_routes_refuse_the_inbox()
    {
        var http = factory.ClientFor(OrgA);
        var id = await InboxIdAsync();

        foreach (var res in new[]
        {
            await http.GetAsync($"/api/flows/{id}"),
            await http.PutAsJsonAsync($"/api/flows/{id}/fields", FlowFieldList.Empty, DocumentJson.Options),
            await http.PostAsync($"/api/flows/{id}/publish", null),
            await http.GetAsync($"/api/flows/{id}/outputs"),
            await http.GetAsync($"/api/flows/{id}/intakes"),
            await http.PostAsync($"/api/flows/{id}/documents/sample", null),
            await http.PatchAsJsonAsync($"/api/flows/{id}", new { automation = "auto" }),
            await factory.ApiKeyClientFor(OrgA).PostAsync($"/api/v1/flows/{id}/documents",
                new MultipartFormDataContent { { new ByteArrayContent("sku,qty\nA-1,2\n"u8.ToArray()), "file", "orders.csv" } }),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal("flow_not_found", await ErrorOf(res));
        }

        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = Document.Create(c => c.Page(p => p.Content().Text("Synthetic inbox document"))).GeneratePdf();
        var upload = await http.PostAsync($"/api/flows/{id}/documents",
            new MultipartFormDataContent { { new ByteArrayContent(pdf), "file", "inbox.pdf" } });
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        Guid docId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var doc = await db.FlowDocuments.SingleAsync(d => d.OrgId == OrgA && d.FlowId == id);
            doc.Status = DocumentStatus.NeedsReview;
            await db.SaveChangesAsync();
            docId = doc.Id;
        }

        var teach = await http.PostAsJsonAsync($"/api/documents/{docId}/teach",
            new TeachRequest("total", null, 1, new Box(0.1, 0.1, 0.2, 0.1), null, null), DocumentJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, teach.StatusCode);
        Assert.Equal("not_in_flow", await ErrorOf(teach));
    }
}
