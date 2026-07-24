using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// Tests org-scoped schema fingerprinting: the parse-time upsert (create / increment /
/// idempotent-on-retry) and the detect-time lookup (match / miss / org isolation).
/// Column headers and format are supplied directly — the service no longer downloads the
/// source file itself, so no file-storage fixture is required here.
/// </summary>
public class SchemaFingerprintServiceTests
{
    private static readonly string[] LayoutAHeaders = { "po_number", "supplier_code", "sku", "qty", "price" };

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SchemaFingerprintService NewService(ProcuLinkDbContext db) =>
        new(db, NullLogger<SchemaFingerprintService>.Instance);

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId,
        string key, string? supplierName = null)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = supplierName ?? "Generic Supplier" });

        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-1",
            Currency = "EUR",
            Status = OrderStatusConstants.PendingReview,
            SourceFileKey = key,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    [Fact]
    public void Model_DeclaresUniqueIndex_OnOrganisationIdAndColumnNameHash()
    {
        // The concurrent-insert race-recovery path in RecordParseSuccessAsync only works if the
        // database actually rejects a duplicate (OrganisationId, ColumnNameHash) with a 23505.
        // That guarantee lives in the EF model as a UNIQUE index — without it, two concurrent
        // ParseOrderJob workers for different orders with the same layout both insert, silently
        // duplicating fingerprints and undercounting ParseSuccessCount. InMemory does not enforce
        // unique indexes, so we assert the model declares it rather than relying on a runtime throw.
        using var db = NewDb();

        var entity = db.Model.FindEntityType(typeof(SchemaFingerprint));
        entity.Should().NotBeNull();

        var hasUniqueIndex = entity!.GetIndexes().Any(ix =>
            ix.IsUnique &&
            ix.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(SchemaFingerprint.OrganisationId), nameof(SchemaFingerprint.ColumnNameHash) }));

        hasUniqueIndex.Should().BeTrue(
            "a unique index on (OrganisationId, ColumnNameHash) is what makes the race-recovery path reachable");
    }

    // ── Phase 1: fingerprint → supplier binding ────────────────────────────────

    private static async Task<Guid> SeedOrderForSupplierAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string key)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId))
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = $"Supplier {supplierId:N}".Substring(0, 14) });
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-1", Currency = "EUR", Status = OrderStatusConstants.PendingReview,
            SourceFileKey = key, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    [Fact]
    public async Task RecordParseSuccess_BindsTheOrdersSupplier_SingleSupplierIsNotShared()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        var orderId = await SeedOrderForSupplierAsync(db, orgId, supplierId, "k/1.csv");

        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        var match = await svc.LookupAsync(orgId, LayoutAHeaders, CancellationToken.None);
        match.Should().NotBeNull();
        match!.SupplierIds.Should().ContainSingle().Which.Should().Be(supplierId);
        match.IsBoundTo(supplierId).Should().BeTrue();
        match.IsSharedLayout.Should().BeFalse();
    }

    [Fact]
    public async Task RecordParseSuccess_SameLayoutTwoSuppliers_FlagsSharedLayoutCollision()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var orgId = Guid.NewGuid();
        var supplierA = Guid.NewGuid(); var supplierB = Guid.NewGuid();

        var orderA = await SeedOrderForSupplierAsync(db, orgId, supplierA, "k/a.csv");
        await svc.RecordParseSuccessAsync(orgId, orderA, LayoutAHeaders, "csv", CancellationToken.None);
        var orderB = await SeedOrderForSupplierAsync(db, orgId, supplierB, "k/b.csv");
        await svc.RecordParseSuccessAsync(orgId, orderB, LayoutAHeaders, "csv", CancellationToken.None);

        var match = await svc.LookupAsync(orgId, LayoutAHeaders, CancellationToken.None);
        match.Should().NotBeNull();
        match!.SeenCount.Should().Be(2);
        match.SupplierIds.Should().BeEquivalentTo(new[] { supplierA, supplierB });
        match.IsSharedLayout.Should().BeTrue();   // a shared layout — auto-apply must disambiguate
    }

    [Fact]
    public async Task RecordParseSuccess_SameSupplierTwice_BindsOnce_NotShared()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var o1 = await SeedOrderForSupplierAsync(db, orgId, supplierId, "k/1.csv");
        await svc.RecordParseSuccessAsync(orgId, o1, LayoutAHeaders, "csv", CancellationToken.None);
        var o2 = await SeedOrderForSupplierAsync(db, orgId, supplierId, "k/2.csv");
        await svc.RecordParseSuccessAsync(orgId, o2, LayoutAHeaders, "csv", CancellationToken.None);

        var match = await svc.LookupAsync(orgId, LayoutAHeaders, CancellationToken.None);
        match!.SeenCount.Should().Be(2);
        match.SupplierIds.Should().ContainSingle().Which.Should().Be(supplierId);  // deduped
        match.IsSharedLayout.Should().BeFalse();
    }

    private static async Task<Guid> SeedUnroutedOrderAsync(ProcuLinkDbContext db, Guid orgId, string key)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = null,
            PoNumber = "PO-U1", Currency = "EUR", Status = OrderStatusConstants.Unrouted,
            SourceFileKey = key, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// The layout→supplier binding must learn from an operator correction. An unrouted order
    /// fingerprints with no supplier (there is none to bind). The operator then assigns one via
    /// POST /orders/{id}/assign-supplier, which re-enqueues the parse job — and the recorder
    /// re-enters for the same order. If that re-entry only short-circuits, SupplierIdsCsv can
    /// never accumulate anything but suppliers already known at ingest, i.e. exactly the orders
    /// that never needed routing help. The correction must be learned; the layout must NOT be
    /// re-counted (the same document re-parsed is not a new sighting).
    /// </summary>
    [Fact]
    public async Task RecordParseSuccess_AfterOperatorAssignsSupplier_LearnsBinding_WithoutRecounting()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = await SeedUnroutedOrderAsync(db, orgId, "k/unrouted.csv");

        // 1. First parse — unrouted, so there is no supplier to bind.
        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);
        (await svc.LookupAsync(orgId, LayoutAHeaders, CancellationToken.None))!
            .SupplierIds.Should().BeEmpty("an unrouted order has no supplier to bind");

        // 2. The operator assigns a supplier (what assign-supplier writes) …
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Corrected Supplier" });
        var order = await db.PurchaseOrders.FirstAsync(o => o.Id == orderId);
        order.SupplierId = supplierId;
        order.Status = OrderStatusConstants.Parsing;
        await db.SaveChangesAsync();

        // 3. … and the endpoint re-enqueues the parse, so the recorder re-enters for this order.
        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        var match = await svc.LookupAsync(orgId, LayoutAHeaders, CancellationToken.None);
        match!.SupplierIds.Should().ContainSingle(
            "the human correction is the only training signal this heuristic ever gets")
            .Which.Should().Be(supplierId);
        match.IsBoundTo(supplierId).Should().BeTrue();
        match.IsSharedLayout.Should().BeFalse();
        match.SeenCount.Should().Be(1, "re-parsing the same document is not a new sighting of the layout");
        match.SampleSupplierName.Should().Be("Corrected Supplier",
            "a layout first seen unrouted has no sample name until the correction supplies one");
    }

    [Fact]
    public async Task RecordParseSuccess_ReParsedWhileStillUnrouted_ChangesNothing()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var orgId = Guid.NewGuid();
        var orderId = await SeedUnroutedOrderAsync(db, orgId, "k/unrouted.csv");

        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);
        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        var match = await svc.LookupAsync(orgId, LayoutAHeaders, CancellationToken.None);
        match!.SeenCount.Should().Be(1, "a retry for the same order must not double-count");
        match.SupplierIds.Should().BeEmpty("there is still no supplier to learn");
    }

    [Fact]
    public async Task RecordParseSuccess_CreatesFingerprint_OnFirstParse()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, orgId, "uploads/a.csv", supplierName: "Acme Supplies");

        await NewService(db).RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        var fp = await db.SchemaFingerprints.SingleAsync();
        fp.OrganisationId.Should().Be(orgId);
        fp.ParseSuccessCount.Should().Be(1);
        fp.DetectedFormat.Should().Be("csv");
        fp.SampleSupplierName.Should().Be("Acme Supplies");
        fp.ColumnNameHash.Should().NotBeNullOrEmpty();

        var order = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        order.SchemaFingerprintHash.Should().Be(fp.ColumnNameHash, "the guard hash is persisted on the order");
    }

    [Fact]
    public async Task RecordParseSuccess_UsesCallerProvidedHeaders_NotFileStorage()
    {
        // Pass headers that only the caller could know — proves the service uses
        // the caller-supplied list instead of re-reading the file.
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, orgId, "uploads/a.csv");
        var customHeaders = new[] { "ref_num", "part_id", "units" };

        await NewService(db).RecordParseSuccessAsync(orgId, orderId, customHeaders, "csv", CancellationToken.None);

        var fp = await db.SchemaFingerprints.SingleAsync();
        fp.ColumnNameHash.Should().Be(SchemaFingerprintHasher.ComputeColumnNameHash(customHeaders));
    }

    [Fact]
    public async Task RecordParseSuccess_IncrementsCount_ForSameLayout()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();

        var order1 = await SeedOrderAsync(db, orgId, "uploads/a.csv");
        var order2 = await SeedOrderAsync(db, orgId, "uploads/b.csv");

        var svc = NewService(db);
        await svc.RecordParseSuccessAsync(orgId, order1, LayoutAHeaders, "csv", CancellationToken.None);
        await svc.RecordParseSuccessAsync(orgId, order2, LayoutAHeaders, "csv", CancellationToken.None);

        var fingerprints = await db.SchemaFingerprints.ToListAsync();
        fingerprints.Should().ContainSingle("the same layout must collapse to one row");
        fingerprints[0].ParseSuccessCount.Should().Be(2);
    }

    [Fact]
    public async Task RecordParseSuccess_IsIdempotent_AcrossRetriesForSameOrder()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, orgId, "uploads/a.csv");

        var svc = NewService(db);
        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);
        // Simulate a Hangfire retry re-running the whole job for the same order.
        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        var fp = await db.SchemaFingerprints.SingleAsync();
        fp.ParseSuccessCount.Should().Be(1, "re-running the job for the same order must not double-count");
    }

    [Fact]
    public async Task RecordParseSuccess_DoesNothing_WhenNullColumnHeaders()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, orgId, "uploads/a.pdf");

        await NewService(db).RecordParseSuccessAsync(orgId, orderId, null, "pdf", CancellationToken.None);

        (await db.SchemaFingerprints.AnyAsync()).Should().BeFalse();
        var order = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        order.SchemaFingerprintHash.Should().BeNull();
    }

    [Fact]
    public async Task Lookup_ReturnsMatchWithSeenCount_AfterRecording()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var orderId = await SeedOrderAsync(db, orgId, "uploads/a.csv", supplierName: "Acme Supplies");

        var svc = NewService(db);
        await svc.RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        // Lookup with the headers in a DIFFERENT order — must still match (order-independent hash).
        var shuffled = new[] { "qty", "price", "po_number", "sku", "supplier_code" };
        var match = await svc.LookupAsync(orgId, shuffled, CancellationToken.None);

        match.Should().NotBeNull();
        match!.SeenCount.Should().Be(1);
        match.SampleSupplierName.Should().Be("Acme Supplies");
        match.DetectedFormat.Should().Be("csv");
    }

    [Fact]
    public async Task Lookup_ReturnsNull_ForUnseenLayout()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();

        var match = await NewService(db).LookupAsync(orgId, new[] { "never", "seen", "before" }, CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task Lookup_IsOrgScoped()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await using var db = NewDb();
        var orderA = await SeedOrderAsync(db, orgA, "uploads/a.csv");

        var svc = NewService(db);
        await svc.RecordParseSuccessAsync(orgA, orderA, LayoutAHeaders, "csv", CancellationToken.None);

        // Org B has never parsed this layout, even though Org A has.
        var match = await svc.LookupAsync(orgB, LayoutAHeaders, CancellationToken.None);

        match.Should().BeNull("fingerprints must never leak across organisations");
    }

    /// <summary>
    /// Two concurrent Hangfire workers for *different* orders with the same column layout
    /// both read existing=null, both call Add, and the loser hits the unique index
    /// (Postgres 23505). The loser must recover and still stamp its order's hash.
    /// </summary>
    [Fact]
    public async Task RecordParseSuccess_SetsOrderHash_WhenConcurrentWorkerInsertedFingerprint()
    {
        var orgId  = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        await using var db = new RaceSimulatingDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options,
            dbName);

        var orderId = await SeedOrderAsync(db, orgId, "uploads/race.csv", "Acme Supplies");

        await new SchemaFingerprintService(
                db, NullLogger<SchemaFingerprintService>.Instance)
            .RecordParseSuccessAsync(orgId, orderId, LayoutAHeaders, "csv", CancellationToken.None);

        // The order hash must be stamped even though the initial SaveChangesAsync threw.
        var order = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        order.SchemaFingerprintHash.Should().NotBeNull(
            "the service must recover from a concurrent-insert DbUpdateException and still set the order hash");

        // The rival-inserted fingerprint must have been incremented (not duplicated).
        var fp = await db.SchemaFingerprints.SingleAsync();
        fp.ParseSuccessCount.Should().Be(2,
            "recovery path increments the rival's row instead of inserting a duplicate");
    }

    /// <summary>
    /// Simulates the concurrent-Hangfire-worker race in RecordParseSuccessAsync.
    /// On the first SaveChangesAsync that would insert a SchemaFingerprint, inserts an
    /// equivalent row via a rival in-memory context (Worker A wins) then throws
    /// DbUpdateException (Worker B hits the unique index). Subsequent saves proceed normally.
    /// </summary>
    private sealed class RaceSimulatingDbContext : ProcuLinkDbContext
    {
        private readonly string _dbName;
        private bool _raced;

        public RaceSimulatingDbContext(DbContextOptions<ProcuLinkDbContext> opts, string dbName)
            : base(opts) => _dbName = dbName;

        public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            if (!_raced)
            {
                var entry = ChangeTracker.Entries<SchemaFingerprint>()
                    .FirstOrDefault(e => e.State == EntityState.Added);

                if (entry is not null)
                {
                    _raced = true;

                    // Worker A wins: insert the same fingerprint via a separate context so
                    // it exists in the shared in-memory store before we throw.
                    await using var rival = new ProcuLinkDbContext(
                        new DbContextOptionsBuilder<ProcuLinkDbContext>()
                            .UseInMemoryDatabase(_dbName)
                            .Options);
                    rival.SchemaFingerprints.Add(new SchemaFingerprint
                    {
                        Id                 = Guid.NewGuid(),
                        OrganisationId     = entry.Entity.OrganisationId,
                        ColumnNameHash     = entry.Entity.ColumnNameHash,
                        DetectedFormat     = entry.Entity.DetectedFormat,
                        SampleSupplierName = entry.Entity.SampleSupplierName,
                        ParseSuccessCount  = 1,
                        LastSeenAt         = entry.Entity.LastSeenAt,
                        CreatedAt          = entry.Entity.CreatedAt,
                    });
                    await rival.SaveChangesAsync(ct);

                    // Worker B hits the unique index.
                    throw new DbUpdateException(
                        "Simulated: duplicate key value violates unique constraint IX_schema_fingerprints_org_id_column_name_hash",
                        new InvalidOperationException("23505"));
                }
            }

            return await base.SaveChangesAsync(ct);
        }
    }
}
