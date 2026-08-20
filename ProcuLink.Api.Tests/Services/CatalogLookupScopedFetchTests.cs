using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// P1 perf fix — <c>OrderServiceShared.BuildCatalogLookupAsync</c> used to load the ENTIRE active
/// supplier catalog (14,713 rows for the live Jarltech supplier) on every call, and the interactive
/// mapping-preview endpoint calls it on every keystroke (300–400ms debounce). Every consumer only
/// ever PROBES the returned dictionary with keys derived from the order's lines
/// (<c>SupplierItemCode</c>, raw <c>ManufacturerPartNumber</c>, and its
/// <see cref="ProductKeyNormalizer"/> form) — nothing enumerates it — so the fetch is now scoped to
/// rows that can actually answer one of those keys.
///
/// <para><b>The core contract these tests pin:</b> for every key the order can probe, the scoped
/// lookup answers IDENTICALLY to the old full-catalog lookup. The old behaviour is kept verbatim in
/// <see cref="FullCatalogOracleAsync"/> as the equivalence oracle, so a change to the scoped query
/// that loses a match (or changes a first-wins winner) fails against the oracle, not against a
/// hand-typed expectation.</para>
///
/// <para>Runs on EF InMemory (where <c>.ToLower()</c> executes in C#).
/// <c>ItemMappingCaseParityPostgresTests</c> carries the Npgsql-translation half against a real
/// postgres:16, same split as WP-14.</para>
/// </summary>
public class CatalogLookupScopedFetchTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── The old behaviour, verbatim, as the equivalence oracle ───────────────────

    /// <summary>
    /// The pre-fix implementation of <c>BuildCatalogLookupAsync</c> — full org+supplier+IsActive
    /// fetch, ORDER BY Code, Id, then the exact same 5-key first-wins dictionary build. If the
    /// production dictionary build ever changes, this oracle must change WITH it (the scoped fetch
    /// is an optimisation of this, not a second behaviour).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, SupplierProduct>> FullCatalogOracleAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var products = await db.SupplierProducts.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.IsActive)
            .OrderBy(p => p.Code)
            .ThenBy(p => p.Id)
            .ToListAsync();

        var dict = new Dictionary<string, SupplierProduct>(ItemCodeComparison.Comparer);
        foreach (var p in products)
        {
            if (!string.IsNullOrWhiteSpace(p.Code))       dict.TryAdd(p.Code, p);
            if (!string.IsNullOrWhiteSpace(p.Barcode))    dict.TryAdd(p.Barcode!, p);
            if (!string.IsNullOrWhiteSpace(p.ManufacturerPartNumber))
            {
                dict.TryAdd(p.ManufacturerPartNumber!, p);
                var normalised = p.ManufacturerPartNumberNormalized
                                 ?? ProductKeyNormalizer.Normalize(p.ManufacturerPartNumber);
                if (normalised is not null) dict.TryAdd(normalised, p);
            }
            if (!string.IsNullOrWhiteSpace(p.ExternalId)) dict.TryAdd(p.ExternalId!, p);
        }
        return dict;
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────

    private static SupplierProduct Row(
        Guid orgId, Guid supplierId, string code,
        string? barcode = null, string? mpn = null, string? mpnNormalized = null,
        string? externalId = null, bool isActive = true, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = code, Barcode = barcode,
            ManufacturerPartNumber = mpn, ManufacturerPartNumberNormalized = mpnNormalized,
            ExternalId = externalId, IsActive = isActive,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };

    private static PurchaseOrderLineEntity Line(
        int nr, string? supplierItemCode = null, string? mpn = null)
        => new()
        {
            Id = Guid.NewGuid(), LineNumber = nr,
            BuyerItemCode = $"BUY-{nr}",
            SupplierItemCode = supplierItemCode,
            ManufacturerPartNumber = mpn,
            Quantity = 1m, UnitPrice = 1m,
        };

    /// <summary>
    /// One row per key kind the dictionary is built from, each probed through a REALISTIC line
    /// (case variants included), plus rows that must NOT be fetched.
    /// </summary>
    private static (List<SupplierProduct> catalog, List<PurchaseOrderLineEntity> lines,
                    List<SupplierProduct> expectedFetched, List<SupplierProduct> mustNotFetch)
        BuildFixture(Guid orgId, Guid supplierId)
    {
        // Should be fetched — one per key kind:
        var byCode      = Row(orgId, supplierId, "ABC-100");                                   // Code, case-variant probe
        var byBarcode   = Row(orgId, supplierId, "BC-ROW", barcode: "4006381333931");          // Barcode probed as SupplierItemCode
        var byRawMpn    = Row(orgId, supplierId, "MPN-ROW",
                              mpn: "LTQ2500-BK-BTK1", mpnNormalized: "LTQ2500BKBTK1");         // raw MPN, case-variant probe
        var byNormMpn   = Row(orgId, supplierId, "NORM-ROW",
                              mpn: "TSP23S/A", mpnNormalized: "TSP23SA");                      // reached ONLY via normalised key
        var legacyNull  = Row(orgId, supplierId, "LEGACY-ROW", mpn: "PRW-58930-010",
                              mpnNormalized: null);                                            // legacy row: normalised column never written
        var byExternal  = Row(orgId, supplierId, "EXT-ROW", externalId: "EXT-77");             // ExternalId back-compat key

        // Must NOT be fetched:
        var unrelated   = Row(orgId, supplierId, "ZZZ-1", barcode: "999", mpn: "OTHER-1",
                              mpnNormalized: "OTHER1", externalId: "EXT-99");
        var inactive    = Row(orgId, supplierId, "ABC-100-OLD", isActive: false);
        var otherOrg    = Row(Guid.NewGuid(), supplierId, "ABC-100");
        var otherSupp   = Row(orgId, Guid.NewGuid(), "ABC-100");

        var lines = new List<PurchaseOrderLineEntity>
        {
            Line(1, supplierItemCode: "abc-100"),                    // case variant of Code
            Line(2, supplierItemCode: "4006381333931"),              // barcode entered as the code
            Line(3, mpn: "ltq2500-bk-btk1"),                         // raw MPN case variant
            Line(4, mpn: "TSP23S A"),                                // separator variant → normalised key only
            Line(5, mpn: "PRW 58930 010"),                           // hits legacy row via in-memory normalise fallback
            Line(6, supplierItemCode: "EXT-77"),                     // ExternalId back-compat
            Line(7, supplierItemCode: "NOPE-404"),                   // resolves nowhere (both must MISS)
        };

        var catalog = new List<SupplierProduct>
            { byCode, byBarcode, byRawMpn, byNormMpn, legacyNull, byExternal, unrelated, inactive, otherOrg, otherSupp };
        var fetched = new List<SupplierProduct>
            { byCode, byBarcode, byRawMpn, byNormMpn, legacyNull, byExternal };
        var not     = new List<SupplierProduct> { unrelated, inactive, otherOrg, otherSupp };
        return (catalog, lines, fetched, not);
    }

    private static IEnumerable<string> ProbeKeysOf(IEnumerable<PurchaseOrderLineEntity> lines)
    {
        foreach (var l in lines)
        {
            if (!string.IsNullOrWhiteSpace(l.SupplierItemCode)) yield return l.SupplierItemCode!;
            if (!string.IsNullOrWhiteSpace(l.ManufacturerPartNumber))
            {
                yield return l.ManufacturerPartNumber!;
                var n = ProductKeyNormalizer.Normalize(l.ManufacturerPartNumber);
                if (n is not null) yield return n;
            }
        }
    }

    // ── 1. The core contract: scoped ≡ full, for every key the order can probe ──

    [Fact]
    public async Task ScopedLookup_AnswersIdenticallyToTheFullCatalog_ForEveryLineDerivedKey()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var (catalog, lines, _, _) = BuildFixture(orgId, supplierId);
        db.SupplierProducts.AddRange(catalog);
        await db.SaveChangesAsync();

        var oracle = await FullCatalogOracleAsync(db, orgId, supplierId);
        var scoped = await OrderServiceShared.BuildCatalogLookupAsync(
            db, orgId, supplierId, OrderServiceShared.CollectCatalogProbeKeys(lines), CancellationToken.None);

        foreach (var key in ProbeKeysOf(lines))
        {
            var oracleHit = oracle.TryGetValue(key, out var oracleProduct);
            var scopedHit = scoped.TryGetValue(key, out var scopedProduct);

            scopedHit.Should().Be(oracleHit,
                "the scoped fetch must resolve '{0}' exactly when the full catalog did", key);
            if (oracleHit)
                scopedProduct!.Id.Should().Be(oracleProduct!.Id,
                    "the scoped fetch must resolve '{0}' to the SAME product the full catalog did", key);
        }

        // Anti-vacuity: the fixture genuinely exercises hits of every key kind and one miss.
        // 7 = code + barcode + (raw MPN AND its normalised twin on line 3) + normalised-only
        // + legacy-fallback + external-id.
        ProbeKeysOf(lines).Count(k => oracle.ContainsKey(k)).Should().Be(7);
        oracle.ContainsKey("NOPE-404").Should().BeFalse();
    }

    // ── 2. The query is bounded: only rows a probe key can reach are fetched ────

    [Fact]
    public async Task ScopedLookup_FetchesOnlyRowsAProbeKeyCanReach_NotTheWholeCatalog()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var (catalog, lines, expectedFetched, mustNotFetch) = BuildFixture(orgId, supplierId);
        db.SupplierProducts.AddRange(catalog);
        await db.SaveChangesAsync();

        var scoped = await OrderServiceShared.BuildCatalogLookupAsync(
            db, orgId, supplierId, OrderServiceShared.CollectCatalogProbeKeys(lines), CancellationToken.None);

        // Every fetched row contributes its own Code as a key, so the dictionary's value set IS the
        // fetched row set (fixture codes are distinct). The unrelated / inactive / cross-tenant rows
        // must be absent — that absence is what proves the fetch is keyed, not the whole catalog.
        var fetchedIds = scoped.Values.Select(p => p.Id).Distinct().ToList();
        fetchedIds.Should().BeEquivalentTo(expectedFetched.Select(p => p.Id),
            "the scoped fetch must return exactly the rows a line-derived key can reach");
        fetchedIds.Should().NotContain(mustNotFetch.Select(p => p.Id));
    }

    [Fact]
    public async Task NoProbeKeys_MeansNoFetch_AnEmptyLookup()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var (catalog, _, _, _) = BuildFixture(orgId, supplierId);
        db.SupplierProducts.AddRange(catalog);
        await db.SaveChangesAsync();

        // A codeless order (all lines unresolved, no manufacturer part numbers) probes nothing, so
        // nothing may be loaded — this is the keystroke-preview fast path for a fresh upload.
        var scoped = await OrderServiceShared.BuildCatalogLookupAsync(
            db, orgId, supplierId,
            OrderServiceShared.CollectCatalogProbeKeys(new[] { Line(1) }), CancellationToken.None);

        scoped.Should().BeEmpty();
    }

    // ── 3. First-wins determinism survives the scoping ───────────────────────────

    [Fact]
    public async Task CaseTwinRows_KeepTheSameFirstWinsWinner_AsTheFullCatalog()
    {
        // The unique index on (org, supplier, code) is case-SENSITIVE, so "AB-1" and "ab-1" can
        // legally coexist while the dictionary is case-INSENSITIVE. The ORDER BY Code, Id winner
        // must be the same winner the full fetch produced — a scoped fetch that reordered rows
        // would make the same order resolve to a different product than delivery does.
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.SupplierProducts.AddRange(
            Row(orgId, supplierId, "AB-1"),
            Row(orgId, supplierId, "ab-1"));
        await db.SaveChangesAsync();

        var lines  = new[] { Line(1, supplierItemCode: "Ab-1") };
        var oracle = await FullCatalogOracleAsync(db, orgId, supplierId);
        var scoped = await OrderServiceShared.BuildCatalogLookupAsync(
            db, orgId, supplierId, OrderServiceShared.CollectCatalogProbeKeys(lines), CancellationToken.None);

        scoped["Ab-1"].Id.Should().Be(oracle["Ab-1"].Id,
            "the case-twin winner must not depend on whether the fetch was scoped");
    }

    // ── 4. The key collector matches what consumers actually probe ───────────────

    [Fact]
    public void CollectCatalogProbeKeys_CarriesCodeRawMpnAndNormalisedMpn_AndSkipsBlanks()
    {
        var keys = OrderServiceShared.CollectCatalogProbeKeys(new[]
        {
            Line(1, supplierItemCode: "ABC-100", mpn: "LTQ2500-BK-BTK1"),
            Line(2, supplierItemCode: "  "),      // whitespace code probes nothing
            Line(3, mpn: "---"),                  // all-punctuation MPN has no normalised form
        });

        keys.Should().BeEquivalentTo(new[]
        {
            "ABC-100",
            "LTQ2500-BK-BTK1",
            "LTQ2500BKBTK1",
            "---", // the raw MPN itself is still probed by ScribanOrderModel / InjectCatalogRow
        });
    }
}
