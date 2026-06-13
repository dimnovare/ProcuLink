using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Conformance;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// "Honest + route-to-versioned" on REAL Postgres (the live in×out the prod incident exposed).
///
/// <para>The 2026-06-13 incident: an HTTP supplier's delivery endpoint was edited via
/// <c>PUT /delivery-config</c> and re-GET returned the NEW url, yet a later delivery POSTed to the
/// OLD url. Root cause was NOT duplicate rows / un-ordered FirstOrDefault (the
/// <c>(org_id, supplier_id)</c> unique index makes duplicates impossible) — it was REVISION
/// AUTHORITY working as designed: the order was pinned to a published connection revision whose
/// delivery snapshot still held the old url, and the legacy live editor never republished a
/// revision. These tests prove the fix: a live delivery edit on a revision-governed supplier
/// publishes a NEW revision snapshotting the edit, so future orders route to the new endpoint —
/// and that this exercises the real archive path past the published-immutability trigger (which
/// EF InMemory cannot run). Docker-gated; skips where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class DeliveryConfigRepublishPostgresTests : IAsyncLifetime
{
    private const string OldUrlJson = """{"url":"https://example.test/old","method":"POST"}""";
    private const string NewUrlJson = """{"url":"https://example.test/new","method":"POST"}""";

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_republish_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
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
    public async Task LiveDeliveryEdit_OnGovernedSupplier_PublishesNewRevision_AndFutureOrdersRouteToNewEndpoint()
    {
        var (orgId, supplierId, connectionId, v1Id) = await SeedGovernedSupplierAsync();

        // The operator edits the live delivery endpoint to the NEW url.
        await SetLiveDeliveryConfigAsync(orgId, supplierId, NewUrlJson);

        // Route the edit into the versioned connection.
        DeliveryRepublishOutcome outcome;
        await using (var db = new ProcuLinkDbContext(_options!))
            outcome = await MakeSvc(db, flagOn: true).RepublishLiveDeliveryAsync(orgId, supplierId, "tester", default);

        Assert.Equal(DeliveryRepublishStatus.Republished, outcome.Status);
        Assert.Equal(2, outcome.NewVersionNo);

        // v1 archived (old url, intact); v2 published with the new url; active pointer moved; the
        // non-delivery bundle + item mappings cloned forward.
        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var conn = await verify.SupplierConnections.AsNoTracking().SingleAsync(c => c.Id == connectionId);
            var v1   = await verify.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == v1Id);
            var v2   = await verify.SupplierConnectionRevisions.AsNoTracking()
                .SingleAsync(r => r.ConnectionId == connectionId && r.VersionNo == 2);

            Assert.Equal(v2.Id, conn.ActiveRevisionId);
            Assert.Equal("archived", v1.Status);
            Assert.Contains("example.test/old", v1.DeliveryConfigJson); // old snapshot intact (immutable)

            Assert.Equal("published", v2.Status);
            Assert.Contains("example.test/new", v2.DeliveryConfigJson);
            Assert.Equal("http", v2.DeliveryProtocol);
            Assert.True(v2.DeliveryAutoDeliver);
            Assert.Contains("PO Number", v2.InputMappingJson); // non-delivery bundle cloned (jsonb normalizes spacing)

            var v2Mappings = await verify.ConnectionRevisionItemMappings.AsNoTracking()
                .Where(m => m.RevisionId == v2.Id).ToListAsync();
            Assert.Single(v2Mappings);
            Assert.Equal("BUY-A", v2Mappings[0].BuyerItemCode);
            Assert.Equal("SUP-A", v2Mappings[0].SupplierItemCode);
        }

        // End-to-end routing: a NEW order resolves the active revision (v2) and the effective
        // delivery config the dispatcher reads is the NEW endpoint — the exact "deliver uses the
        // new endpoint" guarantee, via the correct mechanism (republish), not FirstOrDefault order.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var activeRev = await new ConnectionResolver(db).ResolveActiveRevisionAsync(orgId, supplierId, default);
            Assert.NotNull(activeRev);

            var resolver  = new EffectiveConnectionConfigResolver(db, FlagConfig(true));
            var effective = await resolver.ResolveAsync(orgId, activeRev, default);

            Assert.True(effective.IsRevision);
            Assert.Equal("http", effective.DeliveryProtocol);
            Assert.Contains("example.test/new", effective.DeliveryConfigJson!);
            Assert.DoesNotContain("example.test/old", effective.DeliveryConfigJson!);
        }

        // Idempotent: re-running with no live change does NOT spawn an identical v3.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var again = await MakeSvc(db, flagOn: true).RepublishLiveDeliveryAsync(orgId, supplierId, "tester", default);
            Assert.Equal(DeliveryRepublishStatus.Unchanged, again.Status);
        }
        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var count = await verify.SupplierConnectionRevisions.AsNoTracking()
                .CountAsync(r => r.ConnectionId == connectionId);
            Assert.Equal(2, count); // v1 + v2 only
        }
    }

    [DockerRequiredFact]
    public async Task RevisionAuthorityOff_RepublishIsNoOp_AndActivePointerUnchanged()
    {
        var (orgId, supplierId, connectionId, v1Id) = await SeedGovernedSupplierAsync();
        await SetLiveDeliveryConfigAsync(orgId, supplierId, NewUrlJson);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var outcome = await MakeSvc(db, flagOn: false).RepublishLiveDeliveryAsync(orgId, supplierId, "tester", default);
            Assert.Equal(DeliveryRepublishStatus.NotGoverned, outcome.Status);
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var conn = await verify.SupplierConnections.AsNoTracking().SingleAsync(c => c.Id == connectionId);
            Assert.Equal(v1Id, conn.ActiveRevisionId); // untouched
            Assert.Equal(1, await verify.SupplierConnectionRevisions.AsNoTracking()
                .CountAsync(r => r.ConnectionId == connectionId));
        }
    }

    [DockerRequiredFact]
    public async Task Governance_ReportsVersionAndSyncState()
    {
        var (orgId, supplierId, _, _) = await SeedGovernedSupplierAsync();

        // Live row matches the v1 snapshot (same old url) → in sync.
        await SetLiveDeliveryConfigAsync(orgId, supplierId, OldUrlJson);
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var gov = await MakeSvc(db, flagOn: true).DescribeDeliveryGovernanceAsync(orgId, supplierId, default);
            Assert.True(gov.RevisionGoverned);
            Assert.Equal(1, gov.ActiveVersionNo);
            Assert.True(gov.LiveMatchesActiveDelivery);
        }

        // Operator edits the live url → now OUT of sync until republished.
        await SetLiveDeliveryConfigAsync(orgId, supplierId, NewUrlJson);
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var gov = await MakeSvc(db, flagOn: true).DescribeDeliveryGovernanceAsync(orgId, supplierId, default);
            Assert.True(gov.RevisionGoverned);
            Assert.False(gov.LiveMatchesActiveDelivery);
        }

        // Flag off → not governed (the live row drives delivery directly).
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var gov = await MakeSvc(db, flagOn: false).DescribeDeliveryGovernanceAsync(orgId, supplierId, default);
            Assert.False(gov.RevisionGoverned);
            Assert.Null(gov.LiveMatchesActiveDelivery);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IConfiguration FlagConfig(bool on) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = on ? "true" : "false",
            })
            .Build();

    private static SupplierConnectionService MakeSvc(ProcuLinkDbContext db, bool flagOn) =>
        new(db,
            new ReplayService(db, Array.Empty<ITransformService>()),
            new ConformanceService(),
            FlagConfig(flagOn));

    private async Task SetLiveDeliveryConfigAsync(Guid orgId, Guid supplierId, string configJson)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var existing = await db.SupplierDeliveryConfigs
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.SupplierId == supplierId);
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Protocol = "http", AutoDeliver = true, ConfigJson = configJson,
                EncryptedCredentials = "", OutputFormat = "json", CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            existing.ConfigJson = configJson;
            existing.UpdatedAt  = now;
        }
        await db.SaveChangesAsync();
    }

    private async Task<(Guid orgId, Guid supplierId, Guid connectionId, Guid v1Id)> SeedGovernedSupplierAsync()
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var v1Id         = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_rp_{orgId:N}", Name = "Republish Org",
            Slug = $"rp-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme HTTP", CreatedAt = now });
        await db.SaveChangesAsync();

        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId,
            Name = "Acme HTTP", ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Published v1 snapshotting the OLD url, with one item mapping (must clone forward).
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = v1Id, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", PublishedAt = now, EffectiveFrom = now,
            CreatedAt = now, CatalogMode = "live",
            InputMappingJson    = """{"columns":{"po":"PO Number"}}""",
            OutputFormat        = "json",
            DeliveryProtocol    = "http",
            DeliveryConfigJson  = OldUrlJson,
            DeliveryAutoDeliver = true,
            ItemMappings =
            {
                new ConnectionRevisionItemMapping
                {
                    Id = Guid.NewGuid(), BuyerItemCode = "BUY-A", SupplierItemCode = "SUP-A",
                    Confidence = 1.0f, Source = "manual",
                },
            },
        });
        await db.SaveChangesAsync();

        // Point the connection at v1 (circular FK: revision exists first).
        var conn = await db.SupplierConnections.SingleAsync(c => c.Id == connectionId);
        conn.ActiveRevisionId = v1Id;
        await db.SaveChangesAsync();

        return (orgId, supplierId, connectionId, v1Id);
    }
}
