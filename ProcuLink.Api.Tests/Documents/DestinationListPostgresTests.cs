using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class DestinationListPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: the newest-per-destination sub-queries must translate to SQL.
    private DocumentEngineTestFactory factory = null!;
    private static readonly Guid Org = DocumentEngineTestFactory.OrgA;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_destinationlist") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    [DockerRequiredFact]
    public async Task The_list_and_its_two_facts_translate_on_postgres()
    {
        var http = factory.ClientFor(Org);
        var now = DateTime.UtcNow;
        var flow = new Flow { Id = Guid.NewGuid(), OrgId = Org, Name = "Synthetic flow", CreatedAt = now, UpdatedAt = now };
        FlowDestination Destination(string name) => new() { Id = Guid.NewGuid(), OrgId = Org, Name = name, Protocol = "http", CreatedAt = now, UpdatedAt = now };
        var (used, tested) = (Destination("Accounting server"), Destination("Kestrel Supplies"));
        var output = new FlowOutput { Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, Name = "Synthetic output", Format = "csv", DestinationId = used.Id, CreatedAt = now, UpdatedAt = now };
        var document = new FlowDocument { Id = Guid.NewGuid(), OrgId = Org, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf", CreatedAt = now, UpdatedAt = now };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.AddRange(flow, used, tested, output, document);
            db.Add(new FlowDelivery
            {
                Id = Guid.NewGuid(), OrgId = Org, DocumentId = document.Id, OutputId = output.Id, DestinationId = used.Id,
                Status = FlowDeliveryStatus.DeadLetter, DispatchKey = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now,
            });
            db.Add(new AuditEvent
            {
                Id = Guid.NewGuid(), OrgId = Org, EntityType = "FlowDestination", EntityId = tested.Id, Action = "destination.tested", CreatedAt = now,
                Payload = JsonSerializer.SerializeToDocument(new { success = true }),
            });
            await db.SaveChangesAsync();
        }

        var rows = (await http.GetFromJsonAsync<JsonElement>("/api/destinations")).EnumerateArray().ToList();
        JsonElement Row(FlowDestination d) => rows.Single(r => r.GetProperty("id").GetGuid() == d.Id);
        (string?, bool, int) Facts(FlowDestination d, string key) => (Row(d).GetProperty(key).GetProperty("kind").GetString(),
            Row(d).GetProperty(key).GetProperty("ok").GetBoolean(), Row(d).GetProperty("usedBy").GetArrayLength());

        Assert.Equal(("document", false, 1), Facts(used, "lastSend"));
        Assert.Equal(("test", true, 0), Facts(tested, "lastTest"));
    }
}
