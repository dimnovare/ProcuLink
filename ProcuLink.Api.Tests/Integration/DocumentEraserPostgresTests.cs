using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// <see cref="DocumentEraser"/> against real Postgres: the cascades, the org AND, the intake marker and the
/// timestamptz cutoff are all provider behaviour that an in-memory store would not show.
/// </summary>
[Collection("postgres-container")]
public sealed class DocumentEraserPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;
        _connectionString = await postgres.CreateDatabaseAsync("proculink_docerase");
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_connectionString);

    private sealed record Seeded(Guid A, Guid B, Guid A1, Guid A2, Guid A3, Guid B1, DateTime A1ReadAt,
        string A1Source, string A1Artifact, Guid ScopeId, string ItemKey);

    [DockerRequiredFact]
    public async Task Erase_ByIds_RemovesTheDocumentAndEverythingOffIt_AndNothingElse()
    {
        var s = await SeedAsync();
        var ct = CancellationToken.None;
        var oldOutput = $"{s.A}/documents/{s.A1}/outputs/old.pdf";
        var deletedKeys = new List<string>();
        var storage = new Mock<IFileStorageService>();
        storage.Setup(x => x.ListKeysAsync($"{s.A}/documents/{s.A1}/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { s.A1Source, s.A1Artifact, oldOutput });
        storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((k, _) => deletedKeys.Add(k)).Returns(Task.CompletedTask);

        await using var db = new ProcuLinkDbContext(_options!);
        // Sending: output files may be uploaded before the delivery rows exist.
        var a4 = Guid.NewGuid();
        var flowId = await db.FlowDocuments.Where(d => d.Id == s.A3).Select(d => d.FlowId).SingleAsync();
        db.FlowDocuments.Add(new FlowDocument
        {
            Id = a4, OrgId = s.A, FlowId = flowId,
            FileName = "synthetic-a4.pdf", SourceFileKey = $"{s.A}/documents/{a4}/source.pdf", Sha256 = Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Sending, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // A document that starts being read after the check stops the erase, and nothing of it is kept.
        await using (var racing = new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>(_options!)
            .AddInterceptors(new StartReadingInsideTheErase(_options!, s.A1)).Options))
        {
            var race = () => new DocumentEraser(racing, storage.Object, NullLogger<DocumentEraser>.Instance)
                .EraseAsync(s.A, new(Ids: [s.A1]), "test", ct);
            await race.Should().ThrowAsync<InvalidOperationException>();
        }
        (await db.AuditEvents.AnyAsync(e => e.EntityId == s.A1)).Should().BeTrue();
        (await db.FlowIntakeItems.AsNoTracking().SingleAsync(i => i.OrgId == s.A)).DocumentId.Should().Be(s.A1);
        (await db.ErasedDocumentUnits.AnyAsync() || await db.PlatformAuditEvents.AnyAsync()).Should().BeFalse();
        storage.Invocations.Should().BeEmpty();
        await db.FlowDocuments.Where(d => d.Id == s.A1).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Sent));

        var r = await new DocumentEraser(db, storage.Object, NullLogger<DocumentEraser>.Instance)
            .EraseAsync(s.A, new(Ids: [s.A1, s.A3, a4, s.B1]), "test", ct);

        r.DocumentsErased.Should().Be(1);
        r.SkippedInFlight.Should().BeEquivalentTo([s.A3, a4]);
        (await db.FlowDocuments.Where(d => d.OrgId == s.A).Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([s.A2, s.A3, a4]);
        (await db.Set<FlowDelivery>().AnyAsync(d => d.DocumentId == s.A1)).Should().BeFalse();
        (await db.Set<FlowReadCall>().AnyAsync(c => c.DocumentId == s.A1)).Should().BeFalse();
        (await db.AuditEvents.AnyAsync(e => e.EntityId == s.A1)).Should().BeFalse();
        (await db.FlowIntakeItems.SingleAsync(i => i.OrgId == s.A)).DocumentId.Should().Be(Guid.Empty);
        (await IntakeLedger.ClaimAsync(db, s.A, s.ScopeId, s.ItemKey, ct)).Should().BeNull();
        (await db.FlowDocuments.CountAsync(d => d.OrgId == s.B)).Should().Be(2);           // b1 untouched
        var unit = await db.ErasedDocumentUnits.SingleAsync();
        (unit.OrgId, unit.BillingUnits).Should().Be((s.A, 3));
        unit.ReadAt.Should().BeCloseTo(s.A1ReadAt, TimeSpan.FromMilliseconds(1));
        deletedKeys.Should().BeEquivalentTo([s.A1Source, s.A1Artifact, oldOutput]);
        var receipt = await db.PlatformAuditEvents.SingleAsync(e => e.Action == DocumentEraser.AuditAction);
        (receipt.SubjectOrgId, receipt.SubjectClerkOrgId).Should().Be((s.A, (string?)null));
        receipt.Payload.Should().NotContain("synthetic-a1.pdf");
    }

    [DockerRequiredFact]
    public async Task Erase_WithAnEmptyFilter_DeletesNothing_AndAcceptsAnUnspecifiedCutoff()
    {
        var s = await SeedAsync();
        var ct = CancellationToken.None;
        var storage = new Mock<IFileStorageService>();
        await using var db = new ProcuLinkDbContext(_options!);
        var eraser = new DocumentEraser(db, storage.Object, NullLogger<DocumentEraser>.Instance);

        foreach (var empty in new DocumentEraseFilter[] { new(), new(Ids: []) })
            (await eraser.EraseAsync(s.A, empty, "test", ct)).DocumentsErased.Should().Be(0);
        (await db.PlatformAuditEvents.AnyAsync()).Should().BeFalse();
        storage.Invocations.Should().BeEmpty();

        var cutoff = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-1), DateTimeKind.Unspecified);
        (await eraser.EraseAsync(s.A, new(ReceivedBefore: cutoff), "test", ct)).DocumentsErased.Should().Be(1);
        (await db.FlowDocuments.Where(d => d.OrgId == s.A).Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([s.A2, s.A3]);
    }

    /// <summary>Starts reading a document, on its own connection, at the erase's first statement.</summary>
    private sealed class StartReadingInsideTheErase(DbContextOptions<ProcuLinkDbContext> options, Guid docId) : DbCommandInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_done || eventData.Context?.Database.CurrentTransaction is null) return result;
            _done = true;
            await using var other = new ProcuLinkDbContext(options);
            await other.FlowDocuments.Where(d => d.Id == docId)
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Reading), cancellationToken);
            return result;
        }
    }

    [DockerRequiredFact]
    public async Task Erasing_a_part_of_a_split_file_deletes_the_whole_file_too()
    {
        var s = await SeedAsync();
        var whole = $"{s.A}/documents/{s.A1}/synthetic-a1.pdf";
        var deletedKeys = new List<string>();
        var storage = new Mock<IFileStorageService>();
        storage.Setup(x => x.ListKeysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<string>());
        storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((k, _) => deletedKeys.Add(k)).Returns(Task.CompletedTask);
        await using var db = new ProcuLinkDbContext(_options!);
        await db.FlowDocuments.Where(d => d.Id == s.A2).ExecuteUpdateAsync(x => x.SetProperty(d => d.SplitFileKey, whole));

        await new DocumentEraser(db, storage.Object, NullLogger<DocumentEraser>.Instance)
            .EraseAsync(s.A, new(Ids: [s.A2]), "test", CancellationToken.None);

        deletedKeys.Should().Contain(whole);
    }

    [DockerRequiredFact]
    public async Task Erase_by_date_forgets_the_workspaces_older_fingerprints_only()
    {
        var s = await SeedAsync();
        var now = DateTime.UtcNow;
        await using var db = new ProcuLinkDbContext(_options!);
        RemovedDocumentFingerprint Print(Guid org, DateTime created) => new()
        {
            Id = Guid.NewGuid(), OrgId = org, Sha256 = new string('c', 64), DocumentCreatedAt = created, RemovedAt = now,
            FlowId = db.FlowDocuments.Where(d => d.OrgId == org).Select(d => d.FlowId).First(),
        };
        var (older, newer, theirs) = (Print(s.A, now.AddDays(-10)), Print(s.A, now), Print(s.B, now.AddDays(-10)));
        db.RemovedDocumentFingerprints.AddRange(older, newer, theirs);
        await db.SaveChangesAsync();

        var r = await new DocumentEraser(db, Mock.Of<IFileStorageService>(), NullLogger<DocumentEraser>.Instance)
            .EraseAsync(s.A, new(ReceivedBefore: now.AddDays(-1)), "test", CancellationToken.None);

        r.RowsDeleted["removed_document_fingerprints"].Should().Be(1);
        (await db.RemovedDocumentFingerprints.Select(f => f.Id).ToListAsync()).Should().BeEquivalentTo([newer.Id, theirs.Id]);
    }

    [DockerRequiredFact]
    public async Task Erase_NeverDelivered_keeps_a_document_with_a_delivery()
    {
        var s = await SeedAsync();
        await using var db = new ProcuLinkDbContext(_options!);
        // Ready, not sent: only the delivery row can keep it.
        db.Set<FlowDelivery>().Add(new FlowDelivery
        {
            Id = Guid.NewGuid(), OrgId = s.A, DocumentId = s.A2, OutputName = "Order", Status = FlowDeliveryStatus.Stored,
            DispatchKey = Guid.NewGuid(), ArtifactKey = $"{s.A}/documents/{s.A2}/outputs/kept.csv", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var r = await new DocumentEraser(db, Mock.Of<IFileStorageService>(), NullLogger<DocumentEraser>.Instance)
            .EraseAsync(s.A, new(Ids: [s.A2], NeverDelivered: true), "test", CancellationToken.None);

        r.DocumentsErased.Should().Be(0);
        (await db.FlowDocuments.AnyAsync(d => d.Id == s.A2)).Should().BeTrue();
        (await db.ErasedDocumentUnits.AnyAsync()).Should().BeFalse();
    }

    private async Task<Seeded> SeedAsync()
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var now = DateTime.UtcNow;
        var readAt = now.AddDays(-40);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var ids = new Dictionary<string, Guid>();
        var flows = new Dictionary<Guid, Guid>();
        foreach (var (org, tag) in new[] { (a, "a"), (b, "b") })
        {
            var flowId = flows[org] = Guid.NewGuid();
            db.Organisations.Add(new Organisation
            {
                Id = org, ClerkOrgId = $"org_{org:N}", Name = "Example Org", Slug = $"org-{org:N}",
                AccountStatus = AccountStatusConstants.Active, CreatedAt = now, EmailConfigJson = "{}",
            });
            db.Flows.Add(new Flow { Id = flowId, OrgId = org, Name = "Orders", CreatedAt = now, UpdatedAt = now });
            FlowDocument Doc(string name, DateTime created, DateTime? read, int units, string status)
            {
                var id = ids[name] = Guid.NewGuid();
                var d = new FlowDocument
                {
                    Id = id, OrgId = org, FlowId = flowId, FileName = $"synthetic-{name}.pdf", ContentType = "application/pdf",
                    SourceFileKey = $"{org}/documents/{id}/source.pdf", Sha256 = Guid.NewGuid().ToString("N"),
                    BillingUnits = units, Status = status, CreatedAt = created, UpdatedAt = created, ReadAt = read,
                };
                db.FlowDocuments.Add(d);
                return d;
            }
            Doc($"{tag}1", readAt, readAt, 3, DocumentStatus.Sent);
            Doc($"{tag}2", now, now, 1, DocumentStatus.Ready);
        }
        var a3 = Guid.NewGuid();
        db.FlowDocuments.Add(new FlowDocument
        {
            Id = a3, OrgId = a, FlowId = flows[a],
            FileName = "synthetic-a3.pdf", SourceFileKey = $"{a}/documents/{a3}/source.pdf", Sha256 = Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Reading, CreatedAt = now, UpdatedAt = now,
        });

        var a1 = ids["a1"];
        var artifact = $"{a}/documents/{a1}/outputs/{Guid.NewGuid()}.xml";
        db.Set<FlowDelivery>().Add(new FlowDelivery
        {
            Id = Guid.NewGuid(), OrgId = a, DocumentId = a1, OutputName = "Order", Status = FlowDeliveryStatus.Delivered,
            DispatchKey = Guid.NewGuid(), ArtifactKey = artifact, CreatedAt = readAt, UpdatedAt = readAt,
        });
        db.Set<FlowReadCall>().Add(new FlowReadCall
        {
            Id = Guid.NewGuid(), OrgId = a, DocumentId = a1, Part = 1, Model = "test-model", CreatedAt = readAt,
        });
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = a, EntityType = "FlowDocument", EntityId = a1, Action = "document.read", CreatedAt = readAt,
        });
        var scopeId = Guid.NewGuid();
        const string itemKey = "item-a1";
        db.FlowIntakeItems.Add(new FlowIntakeItem
        {
            Id = Guid.NewGuid(), OrgId = a, ScopeId = scopeId, ItemKey = itemKey, DocumentId = a1, CreatedAt = now.AddHours(-1),
        });
        await db.SaveChangesAsync();
        return new Seeded(a, b, a1, ids["a2"], a3, ids["b1"], readAt, $"{a}/documents/{a1}/source.pdf", artifact, scopeId, itemKey);
    }
}
