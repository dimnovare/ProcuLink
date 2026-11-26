using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.Integration;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Alerting;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

[Collection("postgres-container")]
public sealed class FlowDeliveryHealthProbePostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // Postgres, not InMemory: the per-kind grouping and the org-slug join must translate to SQL.
    private DocumentEngineTestFactory factory = null!;

    public async Task InitializeAsync() =>
        factory = new DocumentEngineTestFactory { PostgresConnection = await postgres.CreateDatabaseAsync("proculink_flowhealthprobe") };

    public Task DisposeAsync() { factory.Dispose(); return Task.CompletedTask; }

    [DockerRequiredFact]
    public async Task Dead_letters_in_the_window_are_counted_per_kind_and_workspace_and_named_by_slug()
    {
        var (a, b) = (DocumentEngineTestFactory.OrgA, DocumentEngineTestFactory.OrgB);
        factory.ClientFor(a);
        factory.ClientFor(b);
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        FlowDocument Document(Guid org)
        {
            var flow = new Flow { Id = Guid.NewGuid(), OrgId = org, Name = "Synthetic flow", CreatedAt = now, UpdatedAt = now };
            var doc = new FlowDocument { Id = Guid.NewGuid(), OrgId = org, FlowId = flow.Id, FileName = "a.pdf", ContentType = "application/pdf", CreatedAt = now, UpdatedAt = now };
            db.AddRange(flow, doc);
            return doc;
        }
        void Delivery(FlowDocument doc, string status, string? kind, DateTime at) => db.Add(new FlowDelivery
        {
            Id = Guid.NewGuid(), OrgId = doc.OrgId, DocumentId = doc.Id, OutputId = Guid.NewGuid(), Protocol = "sftp", Status = status, FailureKind = kind,
            LastError = "Connection timed out.", DispatchKey = Guid.NewGuid(), CreatedAt = at, UpdatedAt = at,
        });
        var (docA, docB) = (Document(a), Document(b));
        Delivery(docA, FlowDeliveryStatus.DeadLetter, FlowDeliveryFailureKind.Refused, now);
        Delivery(docA, FlowDeliveryStatus.DeadLetter, null, now.AddMinutes(-1));
        Delivery(docB, FlowDeliveryStatus.DeadLetter, FlowDeliveryFailureKind.Setup, now.AddMinutes(-2));
        Delivery(docB, FlowDeliveryStatus.DeadLetter, FlowDeliveryFailureKind.Internal, now.AddHours(-2));   // outside the window
        Delivery(docB, FlowDeliveryStatus.Delivered, null, now);
        await db.SaveChangesAsync();

        var signal = await new FlowDeliveryHealthProbe(db, new WorkerHealthAlertOptions()).GetSignalAsync(default);

        Assert.Equal(
            new[] { (FlowDeliveryFailureKind.Refused, a), ((string?)null, a), (FlowDeliveryFailureKind.Setup, b) }.OrderBy(x => x.ToString()),
            signal.DeadLetters.Select(d => (d.Kind, d.OrgId)).OrderBy(x => x.ToString()));
        Assert.Equal(new[] { FlowDeliveryFailureKind.Refused, null, FlowDeliveryFailureKind.Setup }, signal.Latest.Select(r => r.Kind));
        Assert.Equal(db.Organisations.Single(o => o.Id == b).Slug, signal.Latest[2].OrgSlug);
    }
}
