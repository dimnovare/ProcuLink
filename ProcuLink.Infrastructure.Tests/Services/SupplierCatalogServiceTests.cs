using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="SupplierCatalogService"/> — the supplier product catalog (ground
/// truth for AI code suggestions). Every assertion exercises the (orgId, supplierId) scope,
/// idempotent upsert-by-code, listing/filtering, and tenant isolation.
///
/// Uses the real <see cref="ProcuLinkDbContext"/> on the EF InMemory provider (same approach
/// as <see cref="ItemMappingServiceResolveManyTests"/>).
/// </summary>
public class SupplierCatalogServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierProduct Draft(string code, string? name = null, decimal? price = null,
        string? unit = null, string? barcode = null,
        string? manufacturerPartNumber = null, string? manufacturerName = null) =>
        new()
        {
            Code = code,
            Name = name,
            Price = price,
            Unit = unit,
            Barcode = barcode,
            // Deliberately never sets ManufacturerPartNumberNormalized — a parsed feed row does
            // not carry one, and UpsertManyAsync is its single writer.
            ManufacturerPartNumber = manufacturerPartNumber,
            ManufacturerName = manufacturerName,
        };

    [Fact]
    public async Task UpsertManyAsync_InsertsNewRows_AndCounts()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var (created, updated) = await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("A-1", "Widget", 1.5m, "PCS"),
            Draft("A-2", "Gadget", 2.0m, "PCS"),
        }, CancellationToken.None);

        created.Should().Be(2);
        updated.Should().Be(0);
        (await svc.CountAsync(orgId, supplierId, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task UpsertManyAsync_IsIdempotentByCode_UpdatesInPlace()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId,
            new[] { Draft("A-1", "Widget", 1.5m) }, CancellationToken.None);

        // Re-import the SAME code with new data → update, not a second row.
        var (created, updated) = await svc.UpsertManyAsync(orgId, supplierId,
            new[] { Draft("A-1", "Widget v2", 1.75m) }, CancellationToken.None);

        created.Should().Be(0);
        updated.Should().Be(1);

        var items = await svc.ListAsync(orgId, supplierId, null, null, CancellationToken.None);
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Widget v2");
        items[0].Price.Should().Be(1.75m);
    }

    [Fact]
    public async Task UpsertManyAsync_DedupesWithinBatch_ByCode_LastWins()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var (created, updated) = await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("DUP", "first"),
            Draft(" DUP ", "second"), // same code after trim
        }, CancellationToken.None);

        created.Should().Be(1);
        updated.Should().Be(0);

        var items = await svc.ListAsync(orgId, supplierId, null, null, CancellationToken.None);
        items.Should().ContainSingle();
        items[0].Code.Should().Be("DUP");
        items[0].Name.Should().Be("second"); // last draft wins
    }

    [Fact]
    public async Task UpsertManyAsync_SkipsBlankCodes()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var (created, _) = await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("", "no code"),
            Draft("   ", "blank code"),
            Draft("REAL", "kept"),
        }, CancellationToken.None);

        created.Should().Be(1);
        (await svc.CountAsync(orgId, supplierId, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_FiltersByQuery_OnCodeNameAndBarcode_CaseInsensitive()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("RES-220", "Resistor 220 Ohm", barcode: "4001234000010"),
            Draft("CAP-100", "Capacitor 100uF", barcode: "4001234000027"),
            Draft("LED-RED", "Red LED",          barcode: "4009999000038"),
        }, CancellationToken.None);

        // by code (lowercase query)
        (await svc.ListAsync(orgId, supplierId, "res-220", null, CancellationToken.None))
            .Should().ContainSingle(p => p.Code == "RES-220");

        // by name fragment
        (await svc.ListAsync(orgId, supplierId, "capacitor", null, CancellationToken.None))
            .Should().ContainSingle(p => p.Code == "CAP-100");

        // by barcode prefix
        (await svc.ListAsync(orgId, supplierId, "4009999", null, CancellationToken.None))
            .Should().ContainSingle(p => p.Code == "LED-RED");

        // no query → all
        (await svc.ListAsync(orgId, supplierId, null, null, CancellationToken.None))
            .Should().HaveCount(3);
    }

    [Fact]
    public async Task ListAsync_ExcludesInactive_AndRespectsTake()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("A"), Draft("B"), Draft("C"),
        }, CancellationToken.None);

        // Deactivate one directly.
        var b = await db.SupplierProducts.SingleAsync(p => p.Code == "B");
        b.IsActive = false;
        await db.SaveChangesAsync();

        (await svc.ListAsync(orgId, supplierId, null, null, CancellationToken.None))
            .Select(p => p.Code).Should().BeEquivalentTo(new[] { "A", "C" });

        (await svc.ListAsync(orgId, supplierId, null, 1, CancellationToken.None))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task AllOperations_AreOrgAndSupplierScoped()
    {
        await using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var supplier1 = Guid.NewGuid();
        var supplier2 = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgA, supplier1, new[] { Draft("SHARED", "A/s1") }, CancellationToken.None);
        await svc.UpsertManyAsync(orgB, supplier1, new[] { Draft("SHARED", "B/s1") }, CancellationToken.None);
        await svc.UpsertManyAsync(orgA, supplier2, new[] { Draft("SHARED", "A/s2") }, CancellationToken.None);

        // Same code in three different scopes → three independent rows.
        db.SupplierProducts.Count().Should().Be(3);

        var aS1 = await svc.ListAsync(orgA, supplier1, null, null, CancellationToken.None);
        aS1.Should().ContainSingle();
        aS1[0].Name.Should().Be("A/s1");

        (await svc.CountAsync(orgA, supplier1, CancellationToken.None)).Should().Be(1);
        (await svc.CountAsync(orgB, supplier1, CancellationToken.None)).Should().Be(1);
    }

    // DeleteAsync has NO test here on purpose. It is set-based (ExecuteDelete) so a full
    // 200,000-row catalog clear costs no per-row memory, and the EF InMemory provider does
    // not implement ExecuteDelete — it throws. The scoping coverage that used to live here
    // (DeleteAsync_RemovesOnlyTheScopedSupplier) moved verbatim to
    // ProcuLink.Api.Tests/Integration/SupplierCatalogDeletePostgresTests.cs on real Postgres,
    // where it is joined by a cross-ORG case, a discontinued-rows case, and the two
    // assertions that pin the set-based behaviour itself.

    // ── Large-import memory bounding (BE-2) ───────────────────────────────────
    // A real distributor feed runs to the parser's 200k row cap. Measured on a synthetic
    // 200k-row CSV: each row costs ~976 B once tracked (473 B entity + 503 B EF change-tracker
    // state), so a single-SaveChanges upsert holds ~186 MB of tracked entities on top of the
    // parsed drafts — 431 MB peak working set for one import. The upsert must therefore work
    // in bounded batches and release each batch, instead of tracking the whole file at once.

    [Fact]
    public async Task UpsertManyAsync_LargeImport_SavesInMoreThanOneBatch()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var saves = 0;
        db.SavedChanges += (_, _) => saves++;

        var drafts = Enumerable.Range(0, 12_000).Select(i => Draft($"BULK-{i:D6}", $"Product {i}"));
        var (created, _) = await svc.UpsertManyAsync(orgId, supplierId, drafts, CancellationToken.None);

        created.Should().Be(12_000);
        saves.Should().BeGreaterThan(1, "a 12k-row import must commit in batches, not one giant SaveChanges");
    }

    [Fact]
    public async Task UpsertManyAsync_LargeImport_DoesNotKeepEveryRowTracked()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var drafts = Enumerable.Range(0, 12_000).Select(i => Draft($"BULK-{i:D6}", $"Product {i}"));
        await svc.UpsertManyAsync(orgId, supplierId, drafts, CancellationToken.None);

        // At most ONE batch may remain in flight — detaching only the final batch (and leaving
        // every earlier one tracked) must not pass.
        db.ChangeTracker.Entries<SupplierProduct>().Should()
            .HaveCountLessThanOrEqualTo(SupplierCatalogService.UpsertBatchSize,
                "each committed batch must be released from the change tracker");
    }

    [Fact]
    public async Task UpsertManyAsync_LargeImport_IsIdempotentAcrossBatchBoundaries()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        SupplierProduct[] Batch() =>
            Enumerable.Range(0, 12_000).Select(i => Draft($"BULK-{i:D6}", $"Product {i}")).ToArray();

        var first = await svc.UpsertManyAsync(orgId, supplierId, Batch(), CancellationToken.None);
        var second = await svc.UpsertManyAsync(orgId, supplierId, Batch(), CancellationToken.None);

        first.Created.Should().Be(12_000);
        first.Updated.Should().Be(0);

        // Re-import of the same file updates in place — no duplicate rows at a batch seam.
        second.Created.Should().Be(0);
        second.Updated.Should().Be(12_000);
        (await svc.CountAsync(orgId, supplierId, CancellationToken.None)).Should().Be(12_000);
    }

    [Fact]
    public async Task UpsertManyAsync_LargeImport_LeavesUnrelatedTrackedEntitiesIntact()
    {
        // Guard for the batching implementation: CatalogPullService holds a TRACKED
        // SupplierCatalogSource across this call and writes its sync status to the SAME
        // DbContext afterwards (CatalogPullService.PullAsync). A blanket ChangeTracker.Clear()
        // to free memory would silently detach that row and drop the status write.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var supplier = new Supplier { Id = supplierId, OrgId = orgId, Name = "Exemplar" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        supplier.Name = "Exemplar Distribution"; // pending, uncommitted change on a tracked entity

        var drafts = Enumerable.Range(0, 12_000).Select(i => Draft($"BULK-{i:D6}", $"Product {i}"));
        await svc.UpsertManyAsync(orgId, supplierId, drafts, CancellationToken.None);

        // The upsert's own SaveChanges legitimately flushes this pending edit too, so the state
        // may be Unchanged — what must never happen is Detached, which is what a blanket
        // ChangeTracker.Clear() produces and which drops the caller's write on the floor.
        db.Entry(supplier).State.Should().NotBe(EntityState.Detached,
            "the upsert must only release the rows it touched, never the whole change tracker");

        supplier.Name = "Exemplar Distribution EU"; // a second edit, made after the upsert returned
        await db.SaveChangesAsync();
        (await db.Suppliers.SingleAsync(s => s.Id == supplierId)).Name.Should().Be("Exemplar Distribution EU");
    }

    // ── Manufacturer part number (MPN) ──────────────────────────────────────────
    // The MPN is the only identifier a punchout / distributor line reliably shares with the
    // supplier's catalog, so it is a first-class matching key. Lookups compare the NORMALISED
    // key (ProductKeyNormalizer: separators stripped, upper-cased), and UpsertManyAsync is its
    // SINGLE writer — both branches derive it from the raw value in the same statement, so the
    // key can never drift away from the number an operator sees on screen.

    private static Task<SupplierProduct> RowAsync(ProcuLinkDbContext db, string code) =>
        db.SupplierProducts.AsNoTracking().SingleAsync(p => p.Code == code);

    [Fact]
    public async Task UpsertManyAsync_Insert_DerivesNormalizedManufacturerPartNumberFromTheRawValue()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: "REDACTED-ORDER-DATA",
                manufacturerName: "Zebra"),
        }, CancellationToken.None);

        var row = await RowAsync(db, "REDACTED-ITEM");
        row.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA", "the raw value is stored verbatim for humans");
        row.ManufacturerPartNumberNormalized.Should().Be("QBT2500BKBTK1",
            "the lookup key is the raw value with separators stripped and upper-cased");
        row.ManufacturerName.Should().Be("Zebra");
    }

    [Fact]
    public async Task UpsertManyAsync_Update_RewritesTheNormalizedKey_WhenAReimportChangesTheRawMpn()
    {
        // THE DRIFT GUARD. A distributor correcting an MPN in the next feed must move the lookup
        // key with it. If the update branch wrote the raw value but left the key alone, the
        // product would still be found by its OLD manufacturer number and never by its new one —
        // a silent, permanent mismatch that no screen shows.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: "REDACTED-ORDER-DATA"),
        }, CancellationToken.None);

        var (created, updated) = await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: "REDACTED-ORDER-DATA"),
        }, CancellationToken.None);

        created.Should().Be(0);
        updated.Should().Be(1);

        var row = await RowAsync(db, "REDACTED-ITEM");
        row.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        row.ManufacturerPartNumberNormalized.Should().Be("P1058930010");
        row.ManufacturerPartNumberNormalized.Should().NotBe("QBT2500BKBTK1",
            "the stale key from the previous import must not survive the re-import");
    }

    [Fact]
    public async Task UpsertManyAsync_Update_ClearsTheNormalizedKey_WhenAReimportDropsTheMpn()
    {
        // The other direction of the same drift: a feed that stops carrying the MPN must not
        // leave the old key behind, or the product keeps matching a part it no longer claims.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: "REDACTED-ORDER-DATA"),
        }, CancellationToken.None);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: null),
        }, CancellationToken.None);

        var row = await RowAsync(db, "REDACTED-ITEM");
        row.ManufacturerPartNumber.Should().BeNull();
        row.ManufacturerPartNumberNormalized.Should().BeNull();
    }

    [Fact]
    public async Task UpsertManyAsync_IsTheSingleWriterOfTheKey_IgnoringAnyCallerSuppliedValue()
    {
        // A draft that arrives with a hand-set (or stale) normalized key must not be trusted on
        // EITHER branch — the service always re-derives it, which is what makes "single writer"
        // an enforced property rather than a convention.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        var insert = Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: "REDACTED-ORDER-DATA");
        insert.ManufacturerPartNumberNormalized = "WRONG-KEY-FROM-CALLER";
        await svc.UpsertManyAsync(orgId, supplierId, new[] { insert }, CancellationToken.None);

        (await RowAsync(db, "REDACTED-ITEM")).ManufacturerPartNumberNormalized
            .Should().Be("QBT2500BKBTK1", "the insert branch derives the key, it does not copy it");

        var update = Draft("REDACTED-ITEM", "Handheld scanner", manufacturerPartNumber: "REDACTED-ORDER-DATA");
        update.ManufacturerPartNumberNormalized = "WRONG-KEY-FROM-CALLER";
        await svc.UpsertManyAsync(orgId, supplierId, new[] { update }, CancellationToken.None);

        (await RowAsync(db, "REDACTED-ITEM")).ManufacturerPartNumberNormalized
            .Should().Be("P1058930010", "the update branch derives the key too");
    }

    [Fact]
    public async Task UpsertManyAsync_BlankOrPunctuationOnlyMpn_StoresANullKey_NeverAnEmptyString()
    {
        // An empty-string key is the dangerous failure mode: every MPN-less product would carry
        // it, so the first MPN lookup that normalised to "" would match the whole catalog. The
        // assertion is written as the lookup Pass 1b actually performs (an IN over the key
        // column), so an empty-string key would show up as three bogus matches.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("NO-MPN-NULL",  "Cable tie",   manufacturerPartNumber: null),
            Draft("NO-MPN-BLANK", "Cable clip",  manufacturerPartNumber: "   "),
            Draft("NO-MPN-PUNCT", "Cable gland", manufacturerPartNumber: "---"),
            Draft("HAS-MPN",      "Scanner",     manufacturerPartNumber: "REDACTED-ORDER-DATA"),
        }, CancellationToken.None);

        foreach (var code in new[] { "NO-MPN-NULL", "NO-MPN-BLANK", "NO-MPN-PUNCT" })
        {
            var row = await RowAsync(db, code);
            row.ManufacturerPartNumberNormalized.Should().BeNull(
                $"{code} carries no comparable manufacturer part number");
        }

        // The MPN-less rows are unreachable by key lookup …
        (await db.SupplierProducts.AsNoTracking()
                .Where(p => p.OrgId == orgId && p.SupplierId == supplierId
                         && p.ManufacturerPartNumberNormalized == "")
                .ToListAsync())
            .Should().BeEmpty("an empty-string key would match every product that has no MPN");

        // … while the row that does have one is found by exactly that key.
        (await db.SupplierProducts.AsNoTracking()
                .Where(p => p.OrgId == orgId && p.SupplierId == supplierId
                         && p.ManufacturerPartNumberNormalized == "QBT2500BKBTK1")
                .ToListAsync())
            .Should().ContainSingle(p => p.Code == "HAS-MPN");
    }

    [Fact]
    public async Task ListAsync_FindsAProductBySubstringOfItsManufacturerPartNumber()
    {
        // An operator reviewing a punchout line has the MANUFACTURER's number in front of them,
        // not the supplier's code. The search needle below appears in NO other column of either
        // row, so a match can only come from the manufacturer-part predicate.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var svc = new SupplierCatalogService(db);

        await svc.UpsertManyAsync(orgId, supplierId, new[]
        {
            Draft("REDACTED-ITEM", "Handheld scanner", barcode: "4001234000010",
                manufacturerPartNumber: "REDACTED-ORDER-DATA"),
            Draft("REDACTED-ITEM",  "Label printer",    barcode: "4001234000027",
                manufacturerPartNumber: "REDACTED-ITEM"),
        }, CancellationToken.None);

        (await svc.ListAsync(orgId, supplierId, "2500-bk", null, CancellationToken.None))
            .Should().ContainSingle(p => p.Code == "REDACTED-ITEM");

        (await svc.ListAsync(orgId, supplierId, "ZD421", null, CancellationToken.None))
            .Should().ContainSingle(p => p.Code == "REDACTED-ITEM");

        // A manufacturer number no product carries still finds nothing — the predicate widened
        // the search, it did not make it match everything.
        (await svc.ListAsync(orgId, supplierId, "LS2208", null, CancellationToken.None))
            .Should().BeEmpty();
    }
}
