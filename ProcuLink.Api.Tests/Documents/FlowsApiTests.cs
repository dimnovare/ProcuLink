using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.Documents;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public class FlowsApiTests(DocumentEngineTestFactory factory) : IClassFixture<DocumentEngineTestFactory>
{
    private static FlowFieldList Fields(params string[] keys) =>
        new(keys.Select(k => new FlowField(k, k, FieldTypes.Money, null, Required: true)).ToList());

    [Fact]
    public async Task Create_list_get_and_confirm_fields()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);

        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("  Supplier invoices  "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flow = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;
        Assert.Equal("Supplier invoices", flow.Name);

        var list = (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!;
        Assert.Contains(list, f => f.Id == flow.Id);

        var put = await http.PutAsJsonAsync($"/api/flows/{flow.Id}/fields", Fields("total_gross"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await put.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.FieldsConfirmed);

        var dup = await http.PutAsJsonAsync($"/api/flows/{flow.Id}/fields", Fields("total_gross", "total_gross"));
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Contains("invalid_fields", await dup.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_flow_starts_on_check_only_what_we_marked_only_when_an_admin_makes_it()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var member = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        member.DefaultRequestHeaders.Remove(ProcuLink.Api.Tests.Integration.RbacTestAuthHandler.RoleHeader);
        member.DefaultRequestHeaders.Add(ProcuLink.Api.Tests.Integration.RbacTestAuthHandler.RoleHeader, "org:member");

        // A mode that sends by itself is an admin's choice (PATCH refuses a member): a member's new flow starts on review,
        // through the flows list and through setting one up from an inbox document.
        var byMember = (await (await member.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Member flow"))).Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;
        Assert.Equal(FlowAutomation.Review, byMember.Automation);
        Guid inboxDoc;
        using (var seed = factory.Services.CreateScope())
        {
            var sdb = seed.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var doc = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = DocumentEngineTestFactory.OrgA, FlowId = (await InboxFlow.GetOrCreateAsync(sdb, DocumentEngineTestFactory.OrgA, CancellationToken.None)).Id,
                FileName = "invoice-1042.pdf", ContentType = "application/pdf", SourceFileKey = "k", BillingUnits = 1, Kind = "invoice",
                SenderKey = "name:example supplier ltd", Status = DocumentStatus.NeedsReview, StatusReason = DocumentSort.NewKind, ReadAt = DateTime.UtcNow,
                ReadJson = DocumentJson.Serialize(new ReadResult("Invoice", [new ReadField("invoice_number", "Invoice number", "INV-1042", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)])),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            sdb.FlowDocuments.Add(doc);
            await sdb.SaveChangesAsync();
            inboxDoc = doc.Id;
        }
        var setUp = await member.PostAsJsonAsync($"/api/documents/{inboxDoc}/set-up", new { });
        Assert.Equal(HttpStatusCode.Created, setUp.StatusCode);
        var setUpFlowId = (await setUp.Content.ReadFromJsonAsync<SetUpResultDto>(DocumentJson.Options))!.FlowId;
        Assert.Equal(FlowAutomation.Review, (await http.GetFromJsonAsync<FlowDto>($"/api/flows/{setUpFlowId}", DocumentJson.Options))!.Automation);

        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Marked mode"));
        var flow = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;
        Assert.Equal(FlowAutomation.Marked, flow.Automation);

        foreach (var mode in new[] { FlowAutomation.Review, FlowAutomation.Marked })
        {
            Assert.Equal(HttpStatusCode.OK, (await http.PatchAsync($"/api/flows/{flow.Id}", JsonContent.Create(new { automation = mode }))).StatusCode);
            Assert.Equal(mode, (await http.GetFromJsonAsync<FlowDto>($"/api/flows/{flow.Id}", DocumentJson.Options))!.Automation);
        }

        Assert.Equal(FlowAutomation.Review, new Flow().Automation);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        Assert.Equal(FlowAutomation.Review, (await InboxFlow.GetOrCreateAsync(db, DocumentEngineTestFactory.OrgA, CancellationToken.None)).Automation);
    }

    [Fact]
    public async Task A_flow_is_download_only_until_one_of_its_files_has_a_destination()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var created = (await (await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Download only"))).Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;
        Assert.True(created.DownloadOnly);
        async Task<bool> DownloadOnly()
        {
            var single = (await http.GetFromJsonAsync<FlowDto>($"/api/flows/{created.Id}", DocumentJson.Options))!.DownloadOnly;
            var listed = (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!.Single(f => f.Id == created.Id).DownloadOnly;
            Assert.Equal(single, listed);
            return single;
        }

        Assert.True(await DownloadOnly());
        await http.PutAsJsonAsync($"/api/flows/{created.Id}/fields", Fields("total_gross"));
        var output = await http.PostAsJsonAsync($"/api/flows/{created.Id}/outputs", new { name = "CSV", format = "csv" });
        Assert.Equal(HttpStatusCode.Created, output.StatusCode);
        var outputId = (await output.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True(await DownloadOnly());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var destination = new FlowDestination { Id = Guid.NewGuid(), OrgId = DocumentEngineTestFactory.OrgA, Name = "Accounting folder", Protocol = "http", ConfigJson = """{"url":"https://example.com/in"}""" };
            db.Set<FlowDestination>().Add(destination);
            db.Set<FlowOutput>().Single(o => o.Id == outputId).DestinationId = destination.Id;
            await db.SaveChangesAsync();
        }
        Assert.False(await DownloadOnly());
    }

    [Fact]
    public async Task Single_flow_says_whether_it_can_be_deleted()
    {
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);
        var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Standards test"));
        var id = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
        async Task<bool> CanDelete() => (await http.GetFromJsonAsync<FlowDto>($"/api/flows/{id}", DocumentJson.Options))!.CanDelete;

        Assert.True(await CanDelete());
        await http.PostAsync($"/api/flows/{id}/documents/sample", null);
        Assert.True(await CanDelete());
        using (var scope = factory.Services.CreateScope())
        {
            // A sent document that was removed: the flow sent, so it stays.
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.RemovedDocumentFingerprints.Add(new RemovedDocumentFingerprint
            {
                Id = Guid.NewGuid(), OrgId = DocumentEngineTestFactory.OrgA, FlowId = id, Sha256 = new string('a', 64),
                DocumentCreatedAt = DateTime.UtcNow, RemovedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        Assert.False(await CanDelete());
        await http.PutAsJsonAsync($"/api/flows/{id}/fields", Fields("total_gross"));
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/flows/{id}/publish", null)).StatusCode);
        Assert.False(await CanDelete());
    }

    [Fact]
    public async Task Flow_reports_whether_scans_are_on()
    {
        foreach (var (org, expected) in new[] { (DocumentEngineTestFactory.OrgB, true), (DocumentEngineTestFactory.OrgA, false) })
        {
            var http = factory.ClientFor(org);
            var created = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Scans flag"));
            var id = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!.Id;
            var flow = (await http.GetFromJsonAsync<FlowDto>($"/api/flows/{id}", DocumentJson.Options))!;
            Assert.Equal(expected, flow.ScansOn);
        }
    }

    [Fact]
    public async Task Other_org_flow_is_404_and_disabled_org_is_403()
    {
        var created = await factory.ClientFor(DocumentEngineTestFactory.OrgA).PostAsJsonAsync("/api/flows", new CreateFlowRequest("Mine"));
        var flow = (await created.Content.ReadFromJsonAsync<FlowDto>(DocumentJson.Options))!;

        var other = await factory.ClientFor(DocumentEngineTestFactory.OrgB).GetAsync($"/api/flows/{flow.Id}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Contains("flow_not_found", await other.Content.ReadAsStringAsync());

        var off = await factory.ClientFor(DocumentEngineTestFactory.OrgOff).GetAsync("/api/flows");
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Contains("document_engine_not_enabled", await off.Content.ReadAsStringAsync());
    }

    /// <summary>A fresh enabled org on <paramref name="plan"/> that already owns <paramref name="flows"/> flows.</summary>
    private HttpClient OrgOnPlan(string plan, int flows)
    {
        var org = Guid.NewGuid();
        var http = factory.ClientFor(org);
        using var scope = factory.Services.CreateScope();
        var on = scope.ServiceProvider.GetRequiredService<IOptions<DocumentEngineOptions>>().Value;
        on.EnabledOrgIds = [.. on.EnabledOrgIds, org.ToString()];
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        db.Organisations.Single(o => o.Id == org).Plan = plan;
        for (var n = 0; n < flows; n++)
            db.Flows.Add(new Flow { Id = Guid.NewGuid(), OrgId = org, Name = $"F{n}", FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return http;
    }

    [Fact]
    public async Task Create_IsRefusedAtTheTrialsTwoFlows()
    {
        var http = OrgOnPlan("pilot", 2);

        var res = await http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("Third"));

        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"flow_limit_reached\"", body);
        Assert.Contains("\"limit\":2", body);
        Assert.Contains("\"plan\":\"pilot\"", body);
        Assert.Equal(2, (await http.GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!.Count);
    }

    [Fact]
    public async Task Create_HasNoCapOnScale()
    {
        var res = await OrgOnPlan("scale", 30).PostAsJsonAsync("/api/flows", new CreateFlowRequest("Thirty-first"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    /// <summary>A flow for <paramref name="org"/> with documents in <paramref name="statuses"/> (one second apart, the last
    /// newest), the given intakes, and one csv output named <paramref name="output"/> sent to <paramref name="destination"/>.</summary>
    private async Task<(Flow Flow, FlowOutput Output, DateTime Newest)> SeedSummaryFlow(
        Guid org, string[] statuses, (string Kind, bool Enabled)[] intakes, string output, string destination)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Supplier invoices", FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty), CreatedAt = now, UpdatedAt = now };
        var dest = new FlowDestination { Id = Guid.NewGuid(), OrgId = org, Name = destination, Protocol = "http", CreatedAt = now, UpdatedAt = now };
        var o = new FlowOutput { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Name = output, Format = "csv", DestinationId = dest.Id, CreatedAt = now, UpdatedAt = now };
        db.Flows.Add(flow);
        db.Set<FlowDestination>().Add(dest);
        db.Set<FlowOutput>().Add(o);
        foreach (var (kind, enabled) in intakes)
            db.Set<FlowIntake>().Add(new FlowIntake { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, Kind = kind, IsEnabled = enabled, CreatedAt = now, UpdatedAt = now });
        for (var n = 0; n < statuses.Length; n++)
            db.FlowDocuments.Add(new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "synthetic.pdf", ContentType = "application/pdf",
                Status = statuses[n], CreatedAt = now.AddSeconds(n), UpdatedAt = now,
            });
        await db.SaveChangesAsync();
        return (flow, o, now.AddSeconds(statuses.Length - 1));
    }

    [Fact]
    public async Task List_carries_each_flows_summary_and_never_another_orgs()
    {
        var (mine, _, newest) = await SeedSummaryFlow(DocumentEngineTestFactory.OrgA,
            [DocumentStatus.NeedsReview, DocumentStatus.Ready, DocumentStatus.SendFailed, DocumentStatus.ReadFailed, DocumentStatus.Sent],
            [(IntakeKind.Sftp, true), (IntakeKind.Imap, false)], "Invoice CSV", "Accounting server");
        var (theirs, _, _) = await SeedSummaryFlow(DocumentEngineTestFactory.OrgB,
            [DocumentStatus.NeedsReview, DocumentStatus.NeedsReview], [(IntakeKind.S3, true)], "Their CSV", "Other server");
        var http = factory.ClientFor(DocumentEngineTestFactory.OrgA);

        var body = await http.GetStringAsync("/api/flows");
        var summary = JsonSerializer.Deserialize<List<FlowDto>>(body, DocumentJson.Options)!.Single(f => f.Id == mine.Id).Summary!;

        Assert.Equal(2, summary.NeedsYou);
        Assert.Equal(1, summary.CouldNotSend);
        Assert.Equal(1, summary.CouldNotRead);
        Assert.Equal(newest, summary.LastDocumentAt);
        Assert.Equal(["sftp"], summary.WaysIn);
        Assert.Equal("Accounting server", Assert.Single(summary.Outputs).Destination);
        Assert.DoesNotContain(theirs.Id.ToString(), body);
        Assert.DoesNotContain("Other server", body);
        Assert.Null((await http.GetFromJsonAsync<FlowDto>($"/api/flows/{mine.Id}", DocumentJson.Options))!.Summary);
    }

    [Fact]
    public async Task List_summary_reads_the_live_version_outputs_once_published()
    {
        var (flow, output, _) = await SeedSummaryFlow(DocumentEngineTestFactory.OrgA, [], [], "Invoice CSV", "Accounting server");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Set<FlowVersion>().Add(new FlowVersion
            {
                Id = Guid.NewGuid(), OrgId = flow.OrgId, FlowId = flow.Id, VersionNo = 1, PublishedAt = DateTime.UtcNow,
                SnapshotJson = FlowSnapshot.Of(flow, [output]).Canonical(),
            });
            db.Flows.Single(f => f.Id == flow.Id).PublishedVersionNo = 1;
            db.Set<FlowOutput>().Single(o => o.Id == output.Id).Name = "Invoice CSV v2";
            await db.SaveChangesAsync();
        }

        var list = (await factory.ClientFor(DocumentEngineTestFactory.OrgA).GetFromJsonAsync<List<FlowDto>>("/api/flows", DocumentJson.Options))!;

        Assert.Equal("Invoice CSV", list.Single(f => f.Id == flow.Id).Summary!.Outputs[0].Name);
    }
}
