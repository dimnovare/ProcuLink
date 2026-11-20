using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class DocumentRemoveApiTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: the eraser runs in a transaction with ExecuteDelete.
    private DocumentEngineTestFactory factory = null!;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_docremove") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    private static readonly Guid OrgA = DocumentEngineTestFactory.OrgA;
    private static readonly Guid OrgB = DocumentEngineTestFactory.OrgB;

    private ProcuLinkDbContext Db() => factory.Services.CreateScope().ServiceProvider.GetRequiredService<ProcuLinkDbContext>();

    private HttpClient Member(Guid org)
    {
        var http = factory.ClientFor(org);
        http.DefaultRequestHeaders.Remove(RbacTestAuthHandler.RoleHeader);
        http.DefaultRequestHeaders.Add(RbacTestAuthHandler.RoleHeader, "org:member");
        return http;
    }

    private async Task<int> OrdersThisMonth(Guid org)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IBillingService>().GetStatusAsync(org)).OrdersThisMonth;
    }

    private async Task<FlowDocument> Seed(Guid org, string status, string? reason = null, DateTime? readAt = null, int units = 0, string? delivery = null)
    {
        factory.ClientFor(org).Dispose();
        var now = DateTime.UtcNow;
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Supplier invoices", CreatedAt = now, UpdatedAt = now };
        var doc = new FlowDocument
        {
            Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "invoice-1042.pdf", ContentType = "application/pdf",
            SourceFileKey = $"docs/{Guid.NewGuid():N}.pdf", Status = status, StatusReason = reason, ReadAt = readAt, BillingUnits = units,
            CreatedAt = now, UpdatedAt = now,
        };
        await using var db = Db();
        db.Flows.Add(flow);
        db.FlowDocuments.Add(doc);
        if (delivery is not null)
            db.Set<FlowDelivery>().Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = org, DocumentId = doc.Id, OutputName = "CSV", Status = delivery, DispatchKey = Guid.NewGuid(),
                ArtifactKey = $"out/{Guid.NewGuid():N}.csv", FileName = "invoice-1042.csv", CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();
        return doc;
    }

    private async Task<bool> Exists(Guid id)
    {
        await using var db = Db();
        return await db.FlowDocuments.AnyAsync(d => d.Id == id);
    }

    private async Task<List<AuditEvent>> Removals()
    {
        await using var db = Db();
        return await db.AuditEvents.AsNoTracking().Where(a => a.OrgId == OrgA && a.Action == DocumentRemoval.Action).ToListAsync();
    }

    private async Task<(bool Detail, bool Row)> CanRemove(HttpClient http, FlowDocument doc)
    {
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{doc.Id}");
        var page = await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={doc.FlowId}&page=1&pageSize=50");
        return (detail.GetProperty("canRemove").GetBoolean(), page.GetProperty("items")[0].GetProperty("canRemove").GetBoolean());
    }

    [DockerRequiredFact]
    public async Task An_admin_removes_a_sent_document_the_month_still_counts_it_and_its_flow_history_keeps_it()
    {
        var doc = await Seed(OrgA, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1, delivery: FlowDeliveryStatus.Stored);
        var before = await OrdersThisMonth(OrgA);
        var admin = factory.ClientFor(OrgA);
        Assert.Equal((false, false), await CanRemove(Member(OrgA), doc));
        Assert.Equal((true, true), await CanRemove(admin, doc));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{doc.Id}")).StatusCode);
        Assert.Equal(before, await OrdersThisMonth(OrgA));

        var audit = Assert.Single(await Removals());
        Assert.Equal(("Flow", doc.FlowId), (audit.EntityType, audit.EntityId));
        var payload = audit.Payload!.RootElement;
        Assert.Equal("Supplier invoices", payload.GetProperty("flow").GetString());
        Assert.Equal("user_DOC_TEST", payload.GetProperty("by").GetString());
        Assert.DoesNotContain("invoice-1042", JsonSerializer.Serialize(audit.Payload));
    }

    [DockerRequiredFact]
    public async Task Removing_refuses_a_document_being_read_or_sent()
    {
        var http = factory.ClientFor(OrgA);
        foreach (var (status, delivery) in new[]
                 {
                     (DocumentStatus.Received, (string?)null), (DocumentStatus.Reading, null), (DocumentStatus.Sending, null),
                     (DocumentStatus.Ready, FlowDeliveryStatus.Queued),
                 })
        {
            var doc = await Seed(OrgA, status, delivery: delivery);
            var res = await http.DeleteAsync($"/api/documents/{doc.Id}");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("document_busy", JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
            Assert.True(await Exists(doc.Id));
        }
        Assert.Empty(await Removals());
    }

    [DockerRequiredFact]
    public async Task A_member_removes_an_unread_duplicate_but_never_what_was_sent_or_another_workspaces()
    {
        var member = Member(OrgA);
        var sent = await Seed(OrgA, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1, delivery: FlowDeliveryStatus.Delivered);
        var refused = await member.DeleteAsync($"/api/documents/{sent.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(RequireOrgAdminAttribute.ErrorCode, await refused.Content.ReadAsStringAsync());
        Assert.True(await Exists(sent.Id));
        // Downloaded: sent with no delivery row. Still refused.
        var downloaded = await Seed(OrgA, DocumentStatus.Sent, reason: DocumentSort.Downloaded, readAt: DateTime.UtcNow, units: 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/documents/{downloaded.Id}")).StatusCode);
        Assert.True(await Exists(downloaded.Id));
        Assert.Empty(await Removals());

        var duplicate = await Seed(OrgA, DocumentStatus.NeedsReview, reason: "duplicate_file");
        Assert.Equal((true, true), await CanRemove(member, duplicate));
        var counted = await OrdersThisMonth(OrgA);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/documents/{duplicate.Id}")).StatusCode);
        await using (var db = Db())
            Assert.False(await db.ErasedDocumentUnits.AnyAsync(u => u.OrgId == OrgA));
        Assert.Equal(counted, await OrdersThisMonth(OrgA));
        Assert.Single(await Removals());

        var theirs = await Seed(OrgB, DocumentStatus.Sent, readAt: DateTime.UtcNow, units: 1);
        var missing = await member.DeleteAsync($"/api/documents/{theirs.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("document_not_found", await missing.Content.ReadAsStringAsync());
        Assert.True(await Exists(theirs.Id));
    }

    [DockerRequiredFact]
    public async Task A_removed_sent_document_still_holds_back_its_next_copy_and_its_number()
    {
        var bytes = "a,b\r\n1,2\r\n"u8.ToArray();
        var read = new ReadResult("Invoice",
            [new ReadField("invoice_no", "Invoice number", "INV-7310", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.99, null, false, true, false)]);
        var key = DocumentChecks.DedupKeyOf(read)!;
        var now = DateTime.UtcNow;
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = OrgA, Name = "Supplier invoices", FieldsConfirmed = true, Automation = FlowAutomation.Marked, CreatedAt = now, UpdatedAt = now,
            FieldsJson = DocumentJson.Serialize(new FlowFieldList([new FlowField("invoice_no", "Invoice number", FieldTypes.Text, null, Required: false)])),
        };
        FlowDocument NewDoc(string status, string sha) => new()
        {
            Id = Guid.NewGuid(), OrgId = OrgA, FlowId = flow.Id, FileName = "invoice-7310.csv", ContentType = "text/csv", SourceFileKey = $"docs/{Guid.NewGuid():N}.csv",
            Sha256 = sha, Status = status, CreatedAt = now.AddMinutes(-5), UpdatedAt = now,
        };
        var sent = NewDoc(DocumentStatus.Sent, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        (sent.DedupKey, sent.ReadAt, sent.BillingUnits) = (key, now, 1);
        var later = NewDoc(DocumentStatus.Received, new string('f', 64));
        factory.ClientFor(OrgA).Dispose();
        await using (var db = Db())
        {
            db.Flows.Add(flow);
            db.FlowDocuments.Add(sent);
            await db.SaveChangesAsync();
        }
        var admin = factory.ClientFor(OrgA);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/documents/{sent.Id}")).StatusCode);
        await using (var db = Db())
        {
            var print = await db.RemovedDocumentFingerprints.AsNoTracking().SingleAsync(f => f.FlowId == flow.Id);
            Assert.Equal((sent.Sha256, RemovedDocumentFingerprint.HashOf(key)), (print.Sha256, print.DedupKeyHash));
            var stored = JsonSerializer.Serialize(print);
            Assert.DoesNotContain("invoice-7310", stored);
            Assert.DoesNotContain(key, stored);
        }
        var (reads, sends) = (factory.Enqueued<ReadDocumentJob>(), factory.Enqueued<SendDocumentJob>());

        // (i) The same file again: held at the door, unread.
        var res = await admin.PostAsync($"/api/flows/{flow.Id}/documents", new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "again.csv" } });
        var again = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("statusReason").GetString();
        Assert.Equal("duplicate_file", again);
        Assert.Equal(reads, factory.Enqueued<ReadDocumentJob>());

        // (ii) Another file with the same number: read, and waits on the failed duplicate check.
        await using (var db = Db())
        {
            db.FlowDocuments.Add(later);
            await db.SaveChangesAsync();
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            var storage = new Mock<IFileStorageService>();
            storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(bytes));
            var reader = new Mock<IDocumentReader>();
            reader.Setup(r => r.ReadAsync(It.IsAny<DocumentReadInput>(), OrgA, It.IsAny<CancellationToken>())).ReturnsAsync(new DocumentReadOutcome(true, read, null));
            var gate = new DocumentEngineGate(Microsoft.Extensions.Options.Options.Create(new DocumentEngineOptions()));
            await new ReadDocumentJob(db, storage.Object, reader.Object, scope.ServiceProvider.GetRequiredService<DocumentRechecker>(), factory.Jobs.Object,
                    Mock.Of<ProcuLink.Core.Services.Ocr.IPdfRasterizer>(), Mock.Of<ProcuLink.Core.Services.Ocr.IDocumentOcrService>(), gate,
                    new ProcuLink.Transform.Tokenizing.SourceTokenizer(), NullLogger<ReadDocumentJob>.Instance)
                .ExecuteAsync(later.Id, OrgA, null, CancellationToken.None);
        }
        await using (var db = Db())
        {
            var waiting = await db.FlowDocuments.AsNoTracking().SingleAsync(d => d.Id == later.Id);
            Assert.Equal(DocumentStatus.NeedsReview, waiting.Status);
            Assert.Contains(DocumentJson.Deserialize<List<DocumentCheck>>(waiting.ChecksJson), c => c.Code == DocumentChecks.Duplicate && !c.Passed);
        }
        Assert.Equal(sends, factory.Enqueued<SendDocumentJob>());
    }
}