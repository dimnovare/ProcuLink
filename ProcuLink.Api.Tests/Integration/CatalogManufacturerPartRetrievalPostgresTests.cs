using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Manufacturer-part retrieval on REAL Postgres. <see cref="CatalogRetrievalService"/> is the
/// LARGE-catalog path (it only engages above the in-memory threshold) and it is Postgres-only by
/// construction: <c>RetrieveCandidatesAsync</c> returns null at its <c>IsNpgsql()</c> guard on any
/// other provider. An EF InMemory test of it would therefore pass without executing one line of
/// the code it claims to cover, so the manufacturer-part pass can only be proven here.
///
/// What this pins:
///   • the migration really created <c>manufacturer_part_number_normalized</c> and its index —
///     a forgotten column or an EF-Ignored property vanishes here, not in production;
///   • Pass 1b retrieves a product whose supplier CODE matches nothing on the line, purely on the
///     manufacturer part number — the punchout case, where the buyer's code is the buying
///     network's internal id;
///   • the normalised key really does bridge separator/case differences THROUGH SQL (the compare
///     happens in Postgres, not in memory);
///   • retrieval stays scoped to (org, supplier).
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class CatalogManufacturerPartRetrievalPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    // The supplier's own code shares no substring with the manufacturer part number, so a pass
    // can never be an accident of the trigram similarity pass that runs after this one.
    private const string SupplierCode = "REDACTED-ITEM";
    private const string ManufacturerPart = "REDACTED-ORDER-DATA";

    // The punchout line: the buyer's item code is the buying network's internal id.
    private const string PunchoutBuyerCode = "29954596";

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_mpn");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    /// <summary>
    /// Seeds one org+supplier and its catalog through the production writer
    /// (<see cref="SupplierCatalogService.UpsertManyAsync"/>), so the normalised key under test is
    /// the one production would actually store — never a value the test hand-computed.
    /// </summary>
    private async Task<(Guid OrgId, Guid SupplierId)> SeedAsync(
        params (string Code, string? Name, string? Mpn)[] products)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id = orgId, Name = "Markit", Slug = $"markit-{orgId:N}",
                // ClerkOrgId defaults to "" and carries a UNIQUE index, so two orgs seeded in one
                // test (the cross-tenant case) collide on it. Only Postgres enforces this — EF
                // InMemory ignores unique indexes entirely, which is why it passed locally.
                ClerkOrgId = $"org_{orgId:N}",
                CreatedAt = DateTime.UtcNow,
            });
            db.Suppliers.Add(new Supplier
            {
                Id = supplierId, OrgId = orgId, Name = "REDACTED-NAME", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            await new SupplierCatalogService(db).UpsertManyAsync(
                orgId, supplierId,
                products.Select(p => new SupplierProduct
                {
                    Code = p.Code, Name = p.Name, ManufacturerPartNumber = p.Mpn, ManufacturerName = "REDACTED-PARTY",
                }),
                CancellationToken.None);
        }

        return (orgId, supplierId);
    }

    [DockerRequiredFact]
    public async Task Normalized_key_column_and_index_really_exist_and_round_trip()
    {
        var (orgId, supplierId) = await SeedAsync((SupplierCode, "QuickScan REDACTED-ORDER-DATA kit", ManufacturerPart));

        await using var db = new ProcuLinkDbContext(_options!);

        var stored = await db.SupplierProducts.AsNoTracking()
            .SingleAsync(p => p.OrgId == orgId && p.SupplierId == supplierId);

        Assert.Equal(ManufacturerPart, stored.ManufacturerPartNumber);
        Assert.Equal("QBT2500BKBTK1", stored.ManufacturerPartNumberNormalized);
        Assert.Equal("REDACTED-PARTY", stored.ManufacturerName);

        // The index the fallback lookup depends on — named explicitly in the model config, so a
        // migration that dropped it would leave the query a sequential scan on every large catalog.
        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexname FROM pg_indexes WHERE tablename = 'supplier_products'")
            .ToListAsync();

        Assert.Contains("IX_supplier_products_org_id_supplier_id_mpn_normalized", indexes);
    }

    [DockerRequiredFact]
    public async Task Retrieves_by_manufacturer_part_number_when_the_buyer_code_matches_nothing()
    {
        var (orgId, supplierId) = await SeedAsync(
            (SupplierCode, "QuickScan REDACTED-ORDER-DATA kit", ManufacturerPart),
            ("REDACTED-ITEM", "QuickScan QBT2400 kit", "QBT2400-BK-BTK1"));

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new CatalogRetrievalService(db);

        // No description: the trigram pass has nothing to rank on, so ONLY the manufacturer-part
        // pass can produce this result. Without it the query returns an empty set.
        var query = new CatalogRetrievalQuery(
            LineNumber: 1,
            BuyerItemCode: PunchoutBuyerCode,
            Description: null,
            ManufacturerPartNumber: ManufacturerPart);

        var candidates = await svc.RetrieveCandidatesAsync(
            orgId, supplierId, new[] { query }, perQueryTopK: 20, overallCap: 40, CancellationToken.None);

        // A null here would mean the indexed path bailed out — the test must not pass vacuously.
        Assert.NotNull(candidates);
        Assert.Contains(candidates!, p => p.Code == SupplierCode);
        Assert.DoesNotContain(candidates!, p => p.Code == "REDACTED-ITEM");

        // The retrieved row carries the manufacturer fields through the projection, which is what
        // lets the grounded candidate tell the model "this catalog product IS part X".
        var hit = candidates!.Single(p => p.Code == SupplierCode);
        Assert.Equal(ManufacturerPart, hit.ManufacturerPartNumber);
        Assert.Equal("REDACTED-PARTY", hit.ManufacturerName);
    }

    [DockerRequiredFact]
    public async Task Matches_across_separator_and_case_differences_in_sql()
    {
        // The catalog spells it without separators and in lower case; the order line uses hyphens
        // and upper case. The comparison happens in Postgres, on the stored normalised key.
        var (orgId, supplierId) = await SeedAsync((SupplierCode, "QuickScan REDACTED-ORDER-DATA kit", "qbt2500 bk btk1"));

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new CatalogRetrievalService(db);

        var candidates = await svc.RetrieveCandidatesAsync(
            orgId, supplierId,
            new[] { new CatalogRetrievalQuery(1, PunchoutBuyerCode, null, ManufacturerPart) },
            perQueryTopK: 20, overallCap: 40, CancellationToken.None);

        Assert.NotNull(candidates);
        Assert.Contains(candidates!, p => p.Code == SupplierCode);

        // Both sides really did differ before normalisation — otherwise this proves nothing.
        Assert.NotEqual("qbt2500 bk btk1", ManufacturerPart);
        Assert.Equal(
            ProductKeyNormalizer.Normalize("qbt2500 bk btk1"),
            ProductKeyNormalizer.Normalize(ManufacturerPart));
    }

    [DockerRequiredFact]
    public async Task Retrieval_never_crosses_org_or_supplier()
    {
        var (orgId, supplierId) = await SeedAsync(("REDACTED-ITEM", "Something else", null));
        var (otherOrgId, otherSupplierId) = await SeedAsync((SupplierCode, "QuickScan REDACTED-ORDER-DATA kit", ManufacturerPart));

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new CatalogRetrievalService(db);

        var query = new CatalogRetrievalQuery(1, PunchoutBuyerCode, null, ManufacturerPart);

        var mine = await svc.RetrieveCandidatesAsync(
            orgId, supplierId, new[] { query }, 20, 40, CancellationToken.None);

        Assert.NotNull(mine);
        Assert.DoesNotContain(mine!, p => p.Code == SupplierCode);

        // Positive control: the very same query DOES find it for the org that owns it, so the
        // empty result above is real scoping and not a query that simply never matches.
        var theirs = await svc.RetrieveCandidatesAsync(
            otherOrgId, otherSupplierId, new[] { query }, 20, 40, CancellationToken.None);

        Assert.NotNull(theirs);
        Assert.Contains(theirs!, p => p.Code == SupplierCode);
    }
}
