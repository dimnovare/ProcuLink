using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V1 backfill tests, incl. the prime directive: a backfilled "revision 1" reproduces
/// the supplier's CURRENT loose config BYTE-IDENTICAL (same mapping JSON, same output format,
/// same delivery config, same item-mapping rows, bound active acceptance profile), with the
/// connection's active pointer set to it — and re-running the backfill is a no-op (idempotent).
/// </summary>
public class ConnectionBackfillServiceTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private const string PoMappingJson =
        "{\"Header\":{\"PoNumber\":\"po_number\"},\"Lines\":[{\"BuyerItemCode\":\"sku\"}]}";
    private const string DeliveryJson = "{\"url\":\"https://supplier.test/orders\",\"timeoutSeconds\":30}";

    private static async Task<(Guid orgId, Guid supplierId)> SeedFullConfig(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Northwind", CreatedAt = now });
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            ConfigJson = PoMappingJson, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", AutoDeliver = true, ConfigJson = DeliveryJson,
            EncryptedCredentials = "ENC-PAYLOAD", OutputFormat = "cxml",
            CreatedAt = now, UpdatedAt = now,
        });
        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = "B-100", SupplierItemCode = "S-200", Confidence = 0.9f,
            Source = "manual", CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 3, Status = "active", CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    [Fact]
    public async Task Backfill_CreatesPublishedRev1_ByteIdenticalToCurrentConfig()
    {
        var db = MakeDb();
        var svc = new ConnectionBackfillService(db);
        var (orgId, supplierId) = await SeedFullConfig(db);

        var created = await svc.BackfillAllAsync(CancellationToken.None);
        Assert.Equal(1, created);

        var connection = await db.SupplierConnections.SingleAsync();
        Assert.Equal(orgId, connection.OrgId);
        Assert.Equal(supplierId, connection.SupplierId);
        Assert.Equal("Northwind", connection.Name);
        Assert.NotNull(connection.ActiveRevisionId);

        var rev = await db.SupplierConnectionRevisions
            .Include(r => r.ItemMappings)
            .SingleAsync();
        Assert.Equal(connection.ActiveRevisionId, rev.Id);   // active pointer set to rev-1
        Assert.Equal(1, rev.VersionNo);
        Assert.Equal("published", rev.Status);
        Assert.NotNull(rev.EffectiveFrom);
        Assert.NotNull(rev.PublishedAt);

        // ── Prime directive: every snapshotted value matches the live config verbatim ──
        Assert.Equal(PoMappingJson, rev.InputMappingJson);       // byte-identical mapping JSON
        Assert.Null(rev.OutputMappingJson);                       // fixed transformer reproduced (null)
        Assert.Equal("cxml", rev.OutputFormat);                   // from SupplierDeliveryConfig
        Assert.Equal("http", rev.DeliveryProtocol);
        Assert.Equal(DeliveryJson, rev.DeliveryConfigJson);       // byte-identical delivery JSON
        Assert.True(rev.DeliveryAutoDeliver);
        Assert.Equal("ENC-PAYLOAD", rev.CredentialsRef);
        Assert.Equal("live", rev.CatalogMode);
        Assert.Equal(3, rev.AcceptanceVersionNo);                 // active acceptance bound, not copied

        var mapping = Assert.Single(rev.ItemMappings);
        Assert.Equal("B-100", mapping.BuyerItemCode);
        Assert.Equal("S-200", mapping.SupplierItemCode);
        Assert.Equal(0.9f, mapping.Confidence);
        Assert.Equal("manual", mapping.Source);

        // Source tables are left untouched.
        Assert.Equal(1, db.SupplierPoMappings.Count());
        Assert.Equal(1, db.ItemMappings.Count());
        Assert.Equal(1, db.SupplierDeliveryConfigs.Count());
    }

    [Fact]
    public async Task Backfill_IsIdempotent_ReRunCreatesNothing()
    {
        var db = MakeDb();
        var svc = new ConnectionBackfillService(db);
        await SeedFullConfig(db);

        var first = await svc.BackfillAllAsync(CancellationToken.None);
        var second = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // re-run is a no-op
        Assert.Equal(1, db.SupplierConnections.Count());
        Assert.Equal(1, db.SupplierConnectionRevisions.Count());
    }

    [Fact]
    public async Task Backfill_SupplierWithOnlyPoMapping_StillBackfills()
    {
        var db = MakeDb();
        var svc = new ConnectionBackfillService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "MapOnly", CreatedAt = now });
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            ConfigJson = PoMappingJson, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var created = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.Equal(1, created);
        var rev = await db.SupplierConnectionRevisions.SingleAsync();
        Assert.Equal(PoMappingJson, rev.InputMappingJson);
        Assert.Null(rev.OutputFormat);      // no delivery config → null format (fixed transformer)
        Assert.Null(rev.DeliveryProtocol);
    }

    [Fact]
    public async Task Backfill_SupplierWithNoConfig_IsSkipped()
    {
        var db = MakeDb();
        var svc = new ConnectionBackfillService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Empty", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var created = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.Equal(0, created);
        Assert.Equal(0, db.SupplierConnections.Count());
        // A zero-config supplier resolves to null → orders fall back to live config (unchanged behaviour).
        Assert.Null(await new ConnectionResolver(db).ResolveActiveRevisionAsync(orgId, supplierId, CancellationToken.None));
    }

    [Fact]
    public async Task Backfill_OnlyArchivedAcceptance_DoesNotBindIt()
    {
        var db = MakeDb();
        var svc = new ConnectionBackfillService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "ArchivedOnly", CreatedAt = now });
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            ConfigJson = "{}", CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "archived", CreatedAt = now,
        });
        await db.SaveChangesAsync();

        await svc.BackfillAllAsync(CancellationToken.None);

        var rev = await db.SupplierConnectionRevisions.SingleAsync();
        Assert.Null(rev.AcceptanceProfileId);  // only ACTIVE acceptance is bound
        Assert.Null(rev.AcceptanceVersionNo);
    }

    [Fact]
    public async Task Backfill_IsOrgScoped_SeparateConnectionsPerOrg()
    {
        var db = MakeDb();
        var svc = new ConnectionBackfillService(db);
        var now = DateTime.UtcNow;

        var orgA = Guid.NewGuid(); var supplierA = Guid.NewGuid();
        var orgB = Guid.NewGuid(); var supplierB = Guid.NewGuid();
        db.Suppliers.AddRange(
            new Supplier { Id = supplierA, OrgId = orgA, Name = "A", CreatedAt = now },
            new Supplier { Id = supplierB, OrgId = orgB, Name = "B", CreatedAt = now });
        db.SupplierPoMappings.AddRange(
            new SupplierPoMapping { Id = Guid.NewGuid(), OrgId = orgA, SupplierId = supplierA, ConfigJson = "{\"a\":1}", CreatedAt = now, UpdatedAt = now },
            new SupplierPoMapping { Id = Guid.NewGuid(), OrgId = orgB, SupplierId = supplierB, ConfigJson = "{\"b\":2}", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var created = await svc.BackfillAllAsync(CancellationToken.None);

        Assert.Equal(2, created);
        var connA = await db.SupplierConnections.SingleAsync(c => c.OrgId == orgA);
        var connB = await db.SupplierConnections.SingleAsync(c => c.OrgId == orgB);
        Assert.NotEqual(connA.Id, connB.Id);
        var revA = await db.SupplierConnectionRevisions.SingleAsync(r => r.OrgId == orgA);
        Assert.Equal("{\"a\":1}", revA.InputMappingJson);
    }
}
