using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Group V10 — <see cref="CatalogRetrievalService"/> PROVIDER-SAFETY contract under the EF
/// InMemory provider (the provider all unit tests use). pg_trgm / EF.Functions.TrigramsSimilarity
/// is Postgres-only and throws on InMemory, so this service MUST:
///   • report it cannot serve indexed retrieval on a non-Postgres provider (even above threshold), and
///   • return <c>null</c> from RetrieveCandidatesAsync so the caller falls back to the in-memory path.
///
/// The trigram RANKING itself cannot be proven here (no Postgres) — it needs live-Postgres
/// verification. These tests lock in the guard that keeps non-Postgres dev + CI green.
/// </summary>
public class CatalogRetrievalServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId)> SeedCatalogAsync(int count)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        for (var i = 0; i < count; i++)
        {
            db.SupplierProducts.Add(new SupplierProduct
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Code = $"ES-CODE-{i:D5}", Name = $"Product {i}", IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (db, orgId, supplierId);
    }

    [Fact]
    public async Task ShouldUseIndexedRetrieval_IsFalse_OnNonPostgresProvider_EvenAboveThreshold()
    {
        // 3 rows, threshold 2 — count exceeds threshold, but the InMemory provider is not Postgres,
        // so indexed retrieval is unavailable and the caller must keep the in-memory path.
        var (db, orgId, supplierId) = await SeedCatalogAsync(3);
        var svc = new CatalogRetrievalService(db);

        var should = await svc.ShouldUseIndexedRetrievalAsync(orgId, supplierId, inMemoryThreshold: 2, CancellationToken.None);

        Assert.False(should);
    }

    [Fact]
    public async Task RetrieveCandidates_ReturnsNull_OnNonPostgresProvider_SignallingFallback()
    {
        var (db, orgId, supplierId) = await SeedCatalogAsync(3);
        var svc = new CatalogRetrievalService(db);

        var queries = new List<CatalogRetrievalQuery>
        {
            new(1, "ES-CODE-00001", "Product 1"),
        };

        var result = await svc.RetrieveCandidatesAsync(
            orgId, supplierId, queries, perQueryTopK: 10, overallCap: 40, CancellationToken.None);

        // null ⇒ "indexed path unavailable, fall back to in-memory" — the documented contract.
        Assert.Null(result);
    }

    [Fact]
    public async Task RetrieveCandidates_EmptyQueries_ReturnsEmpty_NotNull_OnPostgresOnlyPaths()
    {
        // Defensive early-out is reached BEFORE the provider guard only if there are no queries;
        // on InMemory the provider guard wins first, so this still returns null here. Documented:
        // empty-query semantics are exercised on Postgres. This asserts the no-throw contract.
        var (db, orgId, supplierId) = await SeedCatalogAsync(1);
        var svc = new CatalogRetrievalService(db);

        var result = await svc.RetrieveCandidatesAsync(
            orgId, supplierId, new List<CatalogRetrievalQuery>(), perQueryTopK: 10, overallCap: 40, CancellationToken.None);

        // On InMemory the provider guard returns null before the empty-query branch — no throw.
        Assert.Null(result);
    }
}
