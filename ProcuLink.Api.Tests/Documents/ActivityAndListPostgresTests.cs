using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class ActivityAndListPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: LIKE wildcards, query translation and foreign keys are real here.
    private DocumentEngineTestFactory factory = null!;
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_activitylist") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    [DockerRequiredFact]
    public async Task Every_new_list_and_activity_parameter_translates_and_filters_on_postgres()
    {
        var http = factory.ClientFor(Org);
        var now = DateTime.UtcNow;
        var t = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", CreatedAt = now, UpdatedAt = now };
        var destination = new FlowDestination { Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic endpoint", Protocol = "http", CreatedAt = now, UpdatedAt = now };
        var otherDestination = new FlowDestination { Id = Guid.NewGuid(), OrgId = Org, Name = "Other endpoint", Protocol = "http", CreatedAt = now, UpdatedAt = now };
        FlowDocument Doc(string name, string status) => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = name, ContentType = "application/pdf", Status = status, CreatedAt = now, UpdatedAt = now,
        };
        var stopped = Doc("rate_100%.pdf", DocumentStatus.SendFailed);
        var lookalike = Doc("rate-100x.pdf", DocumentStatus.NeedsReview);       // matches only if _ or % were wildcards
        var sent = Doc("other.pdf", DocumentStatus.Sent);
        var elsewhere = Doc("elsewhere.pdf", DocumentStatus.SendFailed);        // stopped too, but at the other destination
        AuditEvent Event(FlowDocument d, string action, DateTime at) => new()
        {
            Id = Guid.NewGuid(), OrgId = Org, EntityType = "FlowDocument", EntityId = d.Id, Action = action, CreatedAt = at,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.AddRange(flow, destination, otherDestination, stopped, lookalike, sent, elsewhere);
            db.Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = stopped.Id, DestinationId = destination.Id, OutputId = Guid.NewGuid(),
                Status = FlowDeliveryStatus.DeadLetter, DispatchKey = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now,
            });
            db.Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = elsewhere.Id, DestinationId = otherDestination.Id, OutputId = Guid.NewGuid(),
                Status = FlowDeliveryStatus.DeadLetter, DispatchKey = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now,
            });
            db.Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = sent.Id, OutputId = Guid.NewGuid(), Status = FlowDeliveryStatus.Delivered,
                DeliveredAt = t, DispatchKey = Guid.NewGuid(), CreatedAt = t, UpdatedAt = t,
            });
            db.AddRange(Event(stopped, "document.received", t), Event(stopped, "document.read", t.AddMinutes(1)),
                Event(stopped, "document.delivery_failed", t.AddMinutes(2)), Event(lookalike, "document.read", t.AddMinutes(1)),
                Event(stopped, "document.read", t.AddDays(2)));
            await db.SaveChangesAsync();
        }

        async Task<JsonElement> List(string query) =>
            await http.GetFromJsonAsync<JsonElement>($"/api/documents?flowId={flow.Id}&{query}&page=1&pageSize=5");
        static (int, string?) Only(JsonElement page) => (page.GetProperty("totalCount").GetInt32(), page.GetProperty("items")[0].GetProperty("fileName").GetString());
        Assert.Equal((1, "rate_100%.pdf"), Only(await List("q=_100%25&status=send_failed,needs_review")));
        Assert.Equal((1, "rate_100%.pdf"), Only(await List($"waitingOnDestination={destination.Id}")));
        var second = await http.GetFromJsonAsync<PaginatedResult<DocumentSummaryDto>>(
            $"/api/documents?flowId={flow.Id}&status=send_failed,needs_review&page=2&pageSize=1");
        Assert.Equal((3, 1), (second!.TotalCount, second.Items.Count));
        var between = await List("status=sent&sentFrom=2026-04-01T00:00:00Z&sentTo=2026-04-02T00:00:00Z");
        Assert.Equal((1, "other.pdf"), Only(between));
        Assert.Equal(t, between.GetProperty("items")[0].GetProperty("sentAt").GetDateTime());

        var activity = await http.GetFromJsonAsync<JsonElement>(
            $"/api/audit?flowId={flow.Id}&actions=document.read&actions=document.delivery_failed&from=2026-04-01T00:00:00Z&to=2026-04-02T00:00:00Z&q=_100%25");
        Assert.Equal(2, activity.GetProperty("total").GetInt32());
        Assert.Equal([stopped.Id.ToString()],
            activity.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("documentId").GetString()).Distinct());
    }
}
