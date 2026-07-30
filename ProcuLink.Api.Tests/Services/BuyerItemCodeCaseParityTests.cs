using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-14 part 3 — the two adjacent code resolvers must agree about CASE.
///
/// <para>A line's code is resolved by two sibling mechanisms: the LEARNED item mappings
/// (<see cref="ItemMappingService"/>, and its snapshot twin
/// <c>OrderIngestionService.ResolveFromSnapshot</c>) and the SUPPLIER CATALOG
/// (<c>OrderServiceShared.BuildCatalogLookupAsync</c>, plus the manufacturer-part matcher).
/// The catalog folds case — its lookup is keyed <see cref="StringComparer.OrdinalIgnoreCase"/> and
/// its exact pass compares <see cref="StringComparison.OrdinalIgnoreCase"/>. The learned mappings
/// did not: they compared the buyer item code with <c>==</c>, so <c>"b-1"</c> and <c>"B-1"</c> were
/// different codes.</para>
///
/// <para>The visible defect: a buyer whose ERP exports the same SKU in a different case on a later
/// PO loses every learned mapping and every line drops to manual review — while the very same
/// difference in case resolves fine on the catalog path. Whether a code resolves must not depend on
/// which of two adjacent resolvers happened to see it.</para>
/// </summary>
public class BuyerItemCodeCaseParityTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ItemMapping Mapping(Guid orgId, Guid supplierId, string buyer, string supplier) => new()
    {
        Id               = Guid.NewGuid(),
        OrgId            = orgId,
        SupplierId       = supplierId,
        BuyerItemCode    = buyer,
        SupplierItemCode = supplier,
        Confidence       = 1.0f,
        Source           = "manual",
        CreatedAt        = DateTime.UtcNow,
        UpdatedAt        = DateTime.UtcNow,
    };

    private static SupplierProduct Product(Guid orgId, Guid supplierId, string code) => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = orgId,
        SupplierId = supplierId,
        Code       = code,
        IsActive   = true,
    };

    private static ParsedOrderLine Line(string buyerItemCode) =>
        new(LineNumber: 1, BuyerItemCode: buyerItemCode, Description: "Item",
            Quantity: 1m, Unit: "EA", UnitPrice: 10m);

    // ── THE parity assertion ────────────────────────────────────────────────────

    /// <summary>
    /// AC: "a case-differing buyer code resolves identically through the item-mapping path and the
    /// catalog path." One order, one code typed in a different case than it was learned/catalogued.
    /// Both resolvers must reach their row — or the product silently loses a mapping depending on
    /// which resolver ran.
    /// </summary>
    [Fact]
    public async Task CaseDifferingCode_ResolvesThroughBothTheItemMappingPathAndTheCatalogPath()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(Mapping(orgId, supplierId, "B-1", "SUP-1"));   // learned as upper case
        db.SupplierProducts.Add(Product(orgId, supplierId, "SUP-1"));      // catalogued as upper case
        await db.SaveChangesAsync();

        // The later PO prints both codes in lower case.
        var viaItemMapping = await new ItemMappingService(db)
            .ResolveAsync(orgId, supplierId, "b-1", CancellationToken.None);

        var catalog = await OrderServiceShared.BuildCatalogLookupAsync(db, orgId, supplierId, CancellationToken.None);
        var viaCatalog = catalog.TryGetValue("sup-1", out var product) ? product.Code : null;

        Assert.Equal("SUP-1", viaCatalog);       // catalog already folds case
        Assert.Equal("SUP-1", viaItemMapping);   // …and the learned mapping must agree
    }

    // ── Learned-mapping resolvers ───────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_CaseDifferingBuyerCode_Resolves()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        db.ItemMappings.Add(Mapping(orgId, supplierId, "Bolt-M8", "SUP-M8"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        Assert.Equal("SUP-M8", await svc.ResolveAsync(orgId, supplierId, "BOLT-M8", CancellationToken.None));
        Assert.Equal("SUP-M8", await svc.ResolveAsync(orgId, supplierId, "bolt-m8", CancellationToken.None));
        Assert.Equal("SUP-M8", await svc.ResolveAsync(orgId, supplierId, "  bolt-M8  ", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveManyAsync_CaseDifferingBuyerCode_Resolves_AndKeepsRequestedKeys()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        db.ItemMappings.AddRange(
            Mapping(orgId, supplierId, "B-1", "SUP-1"),
            Mapping(orgId, supplierId, "B-2", "SUP-2"));
        await db.SaveChangesAsync();

        var result = await new ItemMappingService(db).ResolveManyAsync(
            orgId, supplierId, new[] { "b-1", "B-2" }, CancellationToken.None);

        // Keys stay the codes the CALLER asked for — the ingestion loop looks lines up by their own
        // printed code, so re-keying to the stored casing would break every consumer.
        Assert.Equal("SUP-1", result["b-1"]);
        Assert.Equal("SUP-2", result["B-2"]);
    }

    [Fact]
    public async Task ResolveAsync_ExactCaseMatchWins_OverACaseDifferingRow()
    {
        // An org that deliberately keeps two case variants still gets the exact row for an exact
        // request — folding case ADDS resolutions, it never re-points an existing one.
        await using var db = NewDb();
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        db.ItemMappings.AddRange(
            Mapping(orgId, supplierId, "abc", "SUP-LOWER"),
            Mapping(orgId, supplierId, "ABC", "SUP-UPPER"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        Assert.Equal("SUP-LOWER", await svc.ResolveAsync(orgId, supplierId, "abc", CancellationToken.None));
        Assert.Equal("SUP-UPPER", await svc.ResolveAsync(orgId, supplierId, "ABC", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveManyAsync_ExactCaseMatchWins_OverACaseDifferingRow()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        db.ItemMappings.AddRange(
            Mapping(orgId, supplierId, "abc", "SUP-LOWER"),
            Mapping(orgId, supplierId, "ABC", "SUP-UPPER"));
        await db.SaveChangesAsync();

        var result = await new ItemMappingService(db).ResolveManyAsync(
            orgId, supplierId, new[] { "abc", "ABC", "AbC" }, CancellationToken.None);

        Assert.Equal("SUP-LOWER", result["abc"]);
        Assert.Equal("SUP-UPPER", result["ABC"]);
        Assert.NotNull(result["AbC"]);   // no exact row — resolves through a case variant, deterministically
    }

    [Fact]
    public async Task CaseFolding_StaysScopedToOrgAndSupplier()
    {
        // Folding case must not widen the tenancy predicate by even one row.
        await using var db = NewDb();
        var orgA = Guid.NewGuid(); var orgB = Guid.NewGuid();
        var supplier1 = Guid.NewGuid(); var supplier2 = Guid.NewGuid();

        db.ItemMappings.AddRange(
            Mapping(orgB, supplier1, "B-1", "OTHER-ORG"),
            Mapping(orgA, supplier2, "B-1", "OTHER-SUPPLIER"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        Assert.Null(await svc.ResolveAsync(orgA, supplier1, "b-1", CancellationToken.None));
        var many = await svc.ResolveManyAsync(orgA, supplier1, new[] { "b-1" }, CancellationToken.None);
        Assert.Null(many["b-1"]);
    }

    // ── The snapshot twin must not diverge ──────────────────────────────────────

    /// <summary>
    /// <c>ResolveFromSnapshot</c> exists to mirror <c>ResolveManyAsync</c> exactly, so a pinned
    /// connection revision reproduces what the live table would have done. If only the live resolver
    /// folded case, a replay would resolve FEWER codes than the original run.
    /// </summary>
    [Fact]
    public void ResolveFromSnapshot_CaseDifferingBuyerCode_Resolves_MirroringTheLiveResolver()
    {
        var snapshot = new[]
        {
            new EffectiveRevisionItemMapping("B-1", "REV-1"),
            new EffectiveRevisionItemMapping("b-2", "REV-2"),
        };

        var map = OrderIngestionService.ResolveFromSnapshot(snapshot, new[] { Line("  b-1  "), Line("B-2") });

        Assert.Equal(2, map.Count);            // still total over the trimmed, non-blank input set
        Assert.Equal("REV-1", map["b-1"]);
        Assert.Equal("REV-2", map["B-2"]);
    }

    [Fact]
    public void ResolveFromSnapshot_ExactCaseMatchWins_AndUnmappedStaysNull()
    {
        var snapshot = new[]
        {
            new EffectiveRevisionItemMapping("abc", "REV-LOWER"),
            new EffectiveRevisionItemMapping("ABC", "REV-UPPER"),
        };

        var map = OrderIngestionService.ResolveFromSnapshot(
            snapshot, new[] { Line("abc"), Line("ABC"), Line("zzz") });

        Assert.Equal("REV-LOWER", map["abc"]);
        Assert.Equal("REV-UPPER", map["ABC"]);
        Assert.Null(map["zzz"]);               // present (total) but unresolved → review downstream
    }
}
