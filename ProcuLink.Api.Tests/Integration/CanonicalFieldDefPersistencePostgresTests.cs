using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Phase 2 (extensible canonical) — proves on REAL Postgres (not EF InMemory) that the
/// <c>canonical_field_defs</c> table created by migration <c>AddCanonicalFieldDefs</c> is REAL
/// persisted schema: a <see cref="CanonicalFieldDef"/> survives INSERT → fresh-context reload with
/// every column intact (key/label/scope/type/standards_ref/display_order), and a SOFT DELETE
/// (stamping <c>DeletedAt</c>) removes it from the ACTIVE set while the row physically survives — so
/// a pinned connection revision keeps a stable view of the field set. Also round-trips the two new
/// price-variance-guard columns on <c>supplier_connections</c>. An EF-Ignored property or a forgotten
/// migration column would silently vanish here (the column-vs-JSON trap Phase 1 hit). Docker-gated;
/// skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class CanonicalFieldDefPersistencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_cfd_{Guid.NewGuid():N}")
            .WithUsername("postgres").WithPassword("postgres").Build();
        await _pg.StartAsync();

        var cs = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrate = new ProcuLinkDbContext(_options);
        await migrate.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [DockerRequiredFact]
    public async Task Def_persists_reloads_and_soft_deletes()
    {
        var orgId  = Guid.NewGuid();
        var connId = Guid.NewGuid();
        var defId  = Guid.NewGuid();
        var now    = DateTime.UtcNow;

        // ── Insert one definition (org + connection scoped) ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.CanonicalFieldDefs.Add(new CanonicalFieldDef
            {
                Id = defId, OrgId = orgId, ConnectionId = connId,
                Key = "incoterms2", Label = "Incoterms (extra)", Scope = "header", Type = "string",
                StandardsRef = "cbc:CustomizationID", Order = 7,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // ── Reload through a FRESH context: every column must come back from Postgres itself ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var d = await db.CanonicalFieldDefs.AsNoTracking().SingleAsync(x => x.Id == defId);
            Assert.Equal(orgId, d.OrgId);
            Assert.Equal(connId, d.ConnectionId);
            Assert.Equal("incoterms2", d.Key);
            Assert.Equal("Incoterms (extra)", d.Label);
            Assert.Equal("header", d.Scope);
            Assert.Equal("string", d.Type);
            Assert.Equal("cbc:CustomizationID", d.StandardsRef);
            Assert.Equal(7, d.Order);                 // maps to display_order column
            Assert.Null(d.DeletedAt);
        }

        // The ACTIVE set (DeletedAt == null) sees the def before deletion.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var activeCount = await db.CanonicalFieldDefs
                .CountAsync(x => x.OrgId == orgId && x.DeletedAt == null && x.Id == defId);
            Assert.Equal(1, activeCount);
        }

        // ── Soft-delete: stamp DeletedAt; the row must SURVIVE so pinned revisions still see it ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var d = await db.CanonicalFieldDefs.SingleAsync(x => x.Id == defId);
            d.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            // Excluded from the ACTIVE set …
            var activeCount = await db.CanonicalFieldDefs
                .CountAsync(x => x.OrgId == orgId && x.DeletedAt == null);
            Assert.Equal(0, activeCount);

            // … but the row PHYSICALLY survives (soft delete, not a hard delete).
            var d = await db.CanonicalFieldDefs.AsNoTracking().SingleAsync(x => x.Id == defId);
            Assert.NotNull(d.DeletedAt);
            Assert.Equal("incoterms2", d.Key); // all data still present on the soft-deleted row
        }
    }

    [DockerRequiredFact]
    public async Task Org_scoping_isolates_defs_between_tenants()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var now  = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.CanonicalFieldDefs.Add(new CanonicalFieldDef
            {
                Id = Guid.NewGuid(), OrgId = orgA, ConnectionId = null,
                Key = "orgA_field", Label = "A", Scope = "header", Type = "string",
                Order = 1, CreatedAt = now, UpdatedAt = now,
            });
            db.CanonicalFieldDefs.Add(new CanonicalFieldDef
            {
                Id = Guid.NewGuid(), OrgId = orgB, ConnectionId = null,
                Key = "orgB_field", Label = "B", Scope = "line", Type = "number",
                Order = 1, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var aOnly = await db.CanonicalFieldDefs.AsNoTracking()
                .Where(x => x.OrgId == orgA).ToListAsync();
            Assert.Single(aOnly);
            Assert.Equal("orgA_field", aOnly[0].Key);
            // org-wide def (null connection) round-trips its null too.
            Assert.Null(aOnly[0].ConnectionId);
        }
    }

    [DockerRequiredFact]
    public async Task Guard_columns_round_trip_on_supplier_connection()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var connId     = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        // Seed the FK parents the connection requires (FKs enforced on Postgres; mirrors
        // LosslessCapturePersistencePostgresTests / PriceVarianceGuardPostgresTests).
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id = orgId, ClerkOrgId = $"org_cfd_{orgId:N}", Name = "CFD Org",
                Slug = $"cfd-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
            });
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "CFD Supplier", CreatedAt = now });
            await db.SaveChangesAsync();

            db.SupplierConnections.Add(new SupplierConnection
            {
                Id = connId, OrgId = orgId, SupplierId = supplierId, Name = "Acme",
                ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
                PriceVarianceGuardEnabled = true, PriceVarianceThresholdPercent = 20m,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var c = await db.SupplierConnections.AsNoTracking().SingleAsync(x => x.Id == connId);
            Assert.True(c.PriceVarianceGuardEnabled);
            Assert.Equal(20m, c.PriceVarianceThresholdPercent);
        }
    }
}
