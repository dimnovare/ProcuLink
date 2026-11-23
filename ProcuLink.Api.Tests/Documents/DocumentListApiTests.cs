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
        Assert.Empty(await Names(Org, "q=12"));                                             // a file hash is not a number
        Assert.Empty(await Names(Org, "q=%25"));                                            // % is a plain character
        Assert.Equal(["scan_0042.pdf"], await Names(Org, "q=_"));                           // so is _
        Assert.Equal(3, (await Names(Org, $"flowId={flow}&q=%20%20")).Count);               // a blank term is no filter
    }

    [Fact]
    public async Task Search_finds_the_sender_from_three_characters()
    {
        var flow = await Flow(Org);
        var doc = await Doc(Org, flow, "receipt.pdf");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.Attach(doc).Entity.ReadJson = DocumentJson.Serialize(new ReadResult("Receipt",
                [new ReadField("seller", "Müüja", "Kohvik Näide OÜ", FieldTypes.Text, FieldMeanings.SenderName, 1, null, false, true, false)], []));
            await db.SaveChangesAsync();
        }

        Assert.Equal(["receipt.pdf"], await Names(Org, $"flowId={flow}&q=kohvik"));
        Assert.Empty(await Names(Org, $"flowId={flow}&q=ko"));                              // two characters never search senders
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

    [Fact]
    public async Task Sent_between_reads_when_the_last_output_landed()
    {
        var flow = await Flow(Org);
        var theirFlow = await Flow(OrgB);
        var monday = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc);
        async Task Sent(Guid org, Guid flowId, string name, DateTime landed, DateTime? touched = null, string status = DocumentStatus.Sent, DateTime? more = null)
        {
            var d = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, FileName = name, ContentType = "application/pdf", Status = status,
                CreatedAt = monday.AddDays(-30), UpdatedAt = touched ?? landed,
            };
            await Save(d);
            await Save(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = org, DocumentId = d.Id, OutputId = Guid.NewGuid(), Status = FlowDeliveryStatus.Delivered,
                DeliveredAt = landed, DispatchKey = Guid.NewGuid(), CreatedAt = landed, UpdatedAt = landed,
            });
            if (more is { } later)
                await Save(new FlowDelivery
                {
                    Id = Guid.NewGuid(), OrgId = org, DocumentId = d.Id, OutputId = Guid.NewGuid(), Status = FlowDeliveryStatus.Delivered,
                    DeliveredAt = later, DispatchKey = Guid.NewGuid(), CreatedAt = later, UpdatedAt = later,
                });
        }
        await Sent(Org, flow, "first-instant.pdf", monday);
        await Sent(Org, flow, "midweek.pdf", monday.AddDays(2));
        await Sent(Org, flow, "last-instant.pdf", monday.AddDays(7));                                      // the end is not part of the week
        await Sent(Org, flow, "last-week-touched-now.pdf", monday.AddDays(-3), touched: monday.AddDays(1)); // the row changed this week; the send did not
        await Sent(Org, flow, "still-sending.pdf", monday.AddDays(1), status: DocumentStatus.Sending);     // one output landed, another has not
        await Sent(OrgB, theirFlow, "theirs.pdf", monday.AddDays(2));
        await Sent(Org, flow, "two-deliveries.pdf", monday.AddDays(2), more: monday.AddDays(9));             // the first output landed in the week, the last one after it: judged by the last

        var week = $"sentFrom={monday:o}&sentTo={monday.AddDays(7):o}";
        Assert.Equal(["first-instant.pdf", "midweek.pdf"], (await Names(Org, $"flowId={flow}&{week}")).Order().ToList());
        Assert.Equal(2, (await Page(Org, $"flowId={flow}&{week}&page=1&pageSize=1")).TotalCount);           // "n were sent this week" is this total
        Assert.Equal(4, (await Names(Org, $"flowId={flow}&sentFrom={monday:o}")).Count);                   // no end: the last instant is in
        Assert.Empty(await Names(Org, $"flowId={theirFlow}&{week}"));                                      // another workspace's flow
    }

    [Fact]
    public async Task Paged_rows_say_the_number_the_sender_and_when_it_was_sent_or_stopped()
    {
        var flow = await Flow(Org);
        var t = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        string Read(string? number, string? sender) => DocumentJson.Serialize(new ReadResult("Invoice",
        [
            new ReadField("no", "Number", number, FieldTypes.Text, FieldMeanings.DocumentNumber, 0.9, null, false, true, false),
            new ReadField("from", "Supplier", sender, FieldTypes.Text, FieldMeanings.SenderName, 0.9, null, false, true, false),
        ]));
        async Task<FlowDocument> Seed(Guid org, Guid flowId, string name, string status, string read, string checks = "[]", DateTime? updatedAt = null, bool isSample = false)
        {
            var d = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = org, FlowId = flowId, FileName = name, ContentType = "application/pdf", Status = status,
                ReadJson = read, ChecksJson = checks, CreatedAt = t, UpdatedAt = updatedAt ?? t, IsSample = isSample,
            };
            await Save(d);
            return d;
        }
        Task Landed(FlowDocument d, DateTime at) => Save(new FlowDelivery
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, DocumentId = d.Id, OutputId = Guid.NewGuid(), Status = FlowDeliveryStatus.Delivered,
            DeliveredAt = at, DispatchKey = Guid.NewGuid(), CreatedAt = t, UpdatedAt = at,
        });
        Task Stopped(FlowDocument d, Guid destination, DateTime at, string error, Guid? outputId) => Save(new FlowDelivery
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, DocumentId = d.Id, DestinationId = destination, OutputId = outputId,
            Status = FlowDeliveryStatus.DeadLetter, LastError = error, DispatchKey = Guid.NewGuid(), CreatedAt = t, UpdatedAt = at,
        });
        var ours = new FlowDestination { Id = Guid.NewGuid(), OrgId = Org, Name = "Accounting server", Protocol = "sftp", CreatedAt = t, UpdatedAt = t };
        var theirs = new FlowDestination { Id = Guid.NewGuid(), OrgId = OrgB, Name = "Foreign server", Protocol = "sftp", CreatedAt = t, UpdatedAt = t };
        await Save(ours);
        await Save(theirs);
        var archive = new FlowDestination { Id = Guid.NewGuid(), OrgId = Org, Name = "Archive server", Protocol = "sftp", CreatedAt = t, UpdatedAt = t };
        await Save(archive);

        var sent = await Seed(Org, flow, "sent.pdf", DocumentStatus.Sent, Read(" INV 2291 ", "Kestrel Supplies"));
        await Landed(sent, t.AddMinutes(5));
        await Landed(sent, t.AddMinutes(9));
        var half = await Seed(Org, flow, "half.pdf", DocumentStatus.SendFailed, Read("INV-2290", null));
        await Landed(half, t.AddMinutes(1));
        await Stopped(half, ours.Id, t.AddMinutes(12), "Login refused", Guid.NewGuid());
        await Stopped(half, archive.Id, t.AddMinutes(20), "Quota exceeded", Guid.NewGuid());               // the newest stop that still has its output
        await Stopped(half, ours.Id, t.AddMinutes(30), "An output that was deleted", outputId: null);      // no longer counts
        var odd = await Seed(Org, flow, "odd.pdf", DocumentStatus.SendFailed, Read(null, null));
        await Stopped(odd, theirs.Id, t.AddMinutes(3), "Refused", Guid.NewGuid());                         // an id from another workspace
        await Seed(Org, flow, "bare.pdf", DocumentStatus.NeedsReview, "{}", checks:
            """[{"code":"required_missing","passed":false,"fieldKey":"a","message":"m"},{"code":"low_confidence","passed":false,"fieldKey":"b","message":"m"},{"code":"required_missing","passed":false,"fieldKey":"c","message":"m"}]""");
        await Seed(Org, flow, "unread.pdf", DocumentStatus.ReadFailed, "{}", updatedAt: t.AddMinutes(7), isSample: true);
        await Seed(OrgB, await Flow(OrgB), "sent.pdf", DocumentStatus.Sent, Read("INV 2291", "Kestrel Supplies"));

        var http = factory.ClientFor(Org);
        var page = await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flow}&page=1&pageSize=50");
        Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
        JsonElement Row(string name) => page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == name);
        static bool IsNull(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null;

        var s = Row("sent.pdf");
        Assert.Equal(("INV 2291", "Kestrel Supplies", t.AddMinutes(9)),
            (s.GetProperty("number").GetString(), s.GetProperty("sender").GetString(), s.GetProperty("sentAt").GetDateTime()));
        Assert.True(IsNull(s, "failedAt") && IsNull(s, "stop"));

        var h = Row("half.pdf");                                                   // one output landed, one stopped: not sent
        Assert.True(IsNull(h, "sentAt") && IsNull(h, "sender"));
        Assert.Equal(t.AddMinutes(20), h.GetProperty("failedAt").GetDateTime());   // the stopped output that still exists
        Assert.Equal((archive.Id, "Archive server", "Quota exceeded"),
            (h.GetProperty("stop").GetProperty("destinationId").GetGuid(), h.GetProperty("stop").GetProperty("destination").GetString(),
             h.GetProperty("stop").GetProperty("error").GetString()));
        Assert.Equal(JsonValueKind.Null, Row("odd.pdf").GetProperty("stop").GetProperty("destination").ValueKind);

        var b = Row("bare.pdf");
        Assert.True(IsNull(b, "number") && IsNull(b, "sender") && IsNull(b, "sentAt") && IsNull(b, "failedAt"));
        Assert.Equal(["required_missing", "low_confidence"], b.GetProperty("checks").EnumerateArray().Select(c => c.GetString()!).ToList());
        Assert.Equal(t.AddMinutes(7), Row("unread.pdf").GetProperty("failedAt").GetDateTime());
        Assert.True(Row("unread.pdf").GetProperty("isSample").GetBoolean());
        Assert.False(s.GetProperty("isSample").GetBoolean());

        // What the file API shares is unchanged, and so is the list without a page.
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{sent.Id}");
        Assert.False(detail.GetProperty("summary").TryGetProperty("number", out _));
        var bareList = await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flow}&take=1");
        Assert.False(bareList[0].TryGetProperty("number", out _));
    }

    [Fact]
    public async Task A_sent_row_lists_its_files_and_where_each_went()
    {
        var flow = await Flow(Org);
        var sent = await Doc(Org, flow, "bill-1042.pdf");
        var waiting = await Doc(Org, flow, "bill-1043.pdf", status: DocumentStatus.NeedsReview);
        var folder = new FlowDestination { Id = Guid.NewGuid(), OrgId = Org, Name = "Accounting folder", Protocol = "sftp", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        await Save(folder);
        var at = DateTime.UtcNow;
        async Task<Guid> File(FlowDocument doc, string status, Guid? destinationId, string? artifact, string fileName, int minute)
        {
            var d = new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = doc.Id, OutputId = Guid.NewGuid(), OutputName = "CSV", DestinationId = destinationId,
                Status = status, ArtifactKey = artifact, FileName = fileName, DispatchKey = Guid.NewGuid(), CreatedAt = at.AddMinutes(minute), UpdatedAt = at,
            };
            await Save(d);
            return d.Id;
        }
        var stored = await File(sent, FlowDeliveryStatus.Stored, null, "k1", "invoice-1042.csv", 1);
        var delivered = await File(sent, FlowDeliveryStatus.Delivered, folder.Id, "k2", "invoice-1042.xml", 2);
        var orphaned = await File(sent, FlowDeliveryStatus.Delivered, Guid.NewGuid(), "k3", "invoice-1042.json", 3);
        await File(sent, FlowDeliveryStatus.Queued, folder.Id, "k4", "queued.csv", 4);
        await File(sent, FlowDeliveryStatus.Delivered, folder.Id, null, "no-artifact.csv", 5);
        await File(waiting, FlowDeliveryStatus.Stored, null, "k6", "invoice-1043.csv", 1);

        var page = await factory.ClientFor(Org).GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flow}&page=1&pageSize=50");
        JsonElement Row(string name) => page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == name);
        var files = Row("bill-1042.pdf").GetProperty("files").EnumerateArray()
            .Select(f => (f.GetProperty("deliveryId").GetGuid(), f.GetProperty("fileName").GetString(), f.GetProperty("delivered").GetBoolean(),
                f.GetProperty("destination").GetString()))
            .ToList();
        Assert.Equal(new (Guid, string?, bool, string?)[] { (stored, "invoice-1042.csv", false, null), (delivered, "invoice-1042.xml", true, "Accounting folder"), (orphaned, "invoice-1042.json", true, null) }, files);
        Assert.Empty(Row("bill-1043.pdf").GetProperty("files").EnumerateArray());

        var deliveries = await factory.ClientFor(Org).GetFromJsonAsync<JsonElement>($"/api/documents/{sent.Id}/deliveries");
        Assert.Equal("Accounting folder", deliveries.EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == delivered).GetProperty("destination").GetString());
    }

    [Fact]
    public async Task A_duplicate_row_says_when_the_same_file_came_before()
    {
        var flow = await Flow(Org);
        var (t0, t1) = (DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        var sha = Guid.NewGuid().ToString("N");
        async Task<FlowDocument> Seed(string name, string status, DateTime createdAt, string? reason = null, DateTime? readAt = null, int units = 0, bool sample = false)
        {
            var d = new FlowDocument
            {
                Id = Guid.NewGuid(), OrgId = Org, FlowId = flow, FileName = name, ContentType = "application/pdf", Status = status, StatusReason = reason,
                Sha256 = sha, ReadAt = readAt, BillingUnits = units, IsSample = sample, CreatedAt = createdAt, UpdatedAt = createdAt,
            };
            await Save(d);
            return d;
        }
        await Seed("first.pdf", DocumentStatus.Sent, t0, readAt: DateTime.UtcNow, units: 1);
        await Seed("again.pdf", DocumentStatus.NeedsReview, t1, reason: "duplicate_file");
        await Seed("sample.pdf", DocumentStatus.Sent, t1, readAt: DateTime.UtcNow, units: 1, sample: true);

        var page = await factory.ClientFor(Org).GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flow}&page=1&pageSize=50");
        JsonElement Row(string name) => page.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("fileName").GetString() == name);
        Assert.Equal(t0, Row("again.pdf").GetProperty("duplicateOfAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(JsonValueKind.Null, Row("first.pdf").GetProperty("duplicateOfAt").ValueKind);
        Assert.True(Row("first.pdf").GetProperty("countsThisMonth").GetBoolean());
        Assert.False(Row("again.pdf").GetProperty("countsThisMonth").GetBoolean());
        Assert.False(Row("sample.pdf").GetProperty("countsThisMonth").GetBoolean());
    }
}