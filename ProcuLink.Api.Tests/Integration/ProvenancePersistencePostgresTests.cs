using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Launch batch 3 — proves on REAL Postgres (not EF InMemory) that the new provenance and
/// test-evidence columns are REAL persisted columns: the single additive migration
/// (<c>AddProvenanceAndConnectionTestEvidence</c>) creates them, an INSERT round-trips them,
/// and an <c>ExecuteUpdateAsync</c> writes them (the EF-Ignore + ExecuteUpdateAsync
/// silent-drop lesson — an Ignored property would silently vanish here).
/// Docker-gated; skips where Docker is absent.
/// </summary>
public sealed class ProvenancePersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_prov_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [DockerRequiredFact]
    public async Task ProvenanceAndEvidenceColumns_RoundTrip_OnRealPostgres()
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var orderId      = Guid.NewGuid();
        var artifactId   = Guid.NewGuid();
        var attemptId    = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        const string configDigest = "5b3a0e60a4f0f5e4d8a0b2c1d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0";
        const string artifactSha  = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";
        var testedAt  = now.AddMinutes(-1);
        var updatedAt = now.AddMinutes(-2);

        // ── Seed (FKs enforced on Postgres — parents first; circular-FK lesson respected) ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id            = orgId,
                ClerkOrgId    = $"org_prov_{orgId:N}",
                Name          = "Provenance Org",
                Slug          = $"prov-{orgId:N}",
                Plan          = "operations",
                AccountStatus = "active",
                CreatedAt     = now,
            });
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Prov Supplier", CreatedAt = now });
            await db.SaveChangesAsync();

            // Connection first (no active pointer), then the revision — two rows, insertable order.
            db.SupplierConnections.Add(new SupplierConnection
            {
                Id = connectionId, OrgId = orgId, SupplierId = supplierId,
                Name = "Prov Supplier", ActiveRevisionId = null,
                CreatedAt = now, UpdatedAt = now,
            });
            db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
            {
                Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
                VersionNo = 1, Status = "draft", CreatedAt = now, CatalogMode = "live",
            });
            await db.SaveChangesAsync();

            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId,
                PoNumber = "PO-PG-1", OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
                Status = "ready_to_deliver", ConnectionRevisionId = revisionId,
                CreatedAt = now, UpdatedAt = now,
            });
            // INSERT path: provenance columns set at creation (the transform-time write shape).
            db.OutboundArtifacts.Add(new OutboundArtifact
            {
                Id = artifactId, OrderId = orderId, OrgId = orgId,
                Format = "csv", FileKey = "prov/artifact.csv", CreatedAt = now,
                ConnectionRevisionId = revisionId,
                ConfigDigest = configDigest,
                ArtifactSha256 = artifactSha,
            });
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = attemptId, OrderId = orderId, OrgId = orgId,
                Channel = "http", Destination = "https://supplier.example", Status = "success",
                AttemptNumber = 1, AttemptedAt = now,
                ConnectionRevisionId = revisionId,
                ConfigDigest = configDigest,
                ArtifactSha256 = artifactSha,
            });
            await db.SaveChangesAsync();

            // UPDATE path: test evidence written via ExecuteUpdateAsync — exactly the write shape
            // that silently drops EF-Ignored properties. These columns must be REAL.
            await db.SupplierConnectionRevisions
                .Where(r => r.OrgId == orgId && r.Id == revisionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TestPassed, true)
                    .SetProperty(r => r.TestedAt, testedAt)
                    .SetProperty(r => r.TestResultJson, "{\"replay\":{\"passed\":true,\"orderCount\":0}}")
                    .SetProperty(r => r.UpdatedAt, updatedAt));
        }

        // ── Reload through a FRESH context: values must come back from Postgres itself ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var artifact = await db.OutboundArtifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId);
            Assert.Equal(revisionId, artifact.ConnectionRevisionId);
            Assert.Equal(configDigest, artifact.ConfigDigest);
            Assert.Equal(artifactSha, artifact.ArtifactSha256);

            var attempt = await db.DeliveryAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
            Assert.Equal(revisionId, attempt.ConnectionRevisionId);
            Assert.Equal(configDigest, attempt.ConfigDigest);
            Assert.Equal(artifactSha, attempt.ArtifactSha256);

            var revision = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == revisionId);
            Assert.True(revision.TestPassed);
            Assert.NotNull(revision.TestedAt);
            Assert.Equal(testedAt, revision.TestedAt!.Value, TimeSpan.FromSeconds(1));
            Assert.NotNull(revision.UpdatedAt);
            Assert.Equal(updatedAt, revision.UpdatedAt!.Value, TimeSpan.FromSeconds(1));
            // jsonb round-trip (parse, don't substring — jsonb normalises whitespace/key order).
            Assert.NotNull(revision.TestResultJson);
            using var evidence = System.Text.Json.JsonDocument.Parse(revision.TestResultJson!);
            Assert.True(evidence.RootElement.GetProperty("replay").GetProperty("passed").GetBoolean());

            // Old/legacy rows: a second artifact with NO provenance stays fully null — additive.
            var legacyId = Guid.NewGuid();
            db.OutboundArtifacts.Add(new OutboundArtifact
            {
                Id = legacyId, OrderId = orderId, OrgId = orgId,
                Format = "csv", FileKey = "prov/legacy.csv", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            var legacy = await db.OutboundArtifacts.AsNoTracking().SingleAsync(a => a.Id == legacyId);
            Assert.Null(legacy.ConnectionRevisionId);
            Assert.Null(legacy.ConfigDigest);
            Assert.Null(legacy.ArtifactSha256);
        }
    }
}
