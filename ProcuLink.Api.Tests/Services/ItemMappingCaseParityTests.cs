using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;
using FluentAssertions;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-14 — the learned-mapping resolver and the supplier catalog must agree on CASE.
///
/// <para>The defect: a learned <c>ItemMapping</c> was matched with an ordinal <c>==</c>
/// (case-SENSITIVE), while <c>OrderServiceShared.BuildCatalogLookupAsync</c> keys its dictionary
/// with <see cref="StringComparer.OrdinalIgnoreCase"/>. The SAME code therefore resolved through
/// the catalog and not through the learned mapping — a buyer whose ERP exports <c>b-1</c> one week
/// and <c>B-1</c> the next silently lost every mapping their operators had taught the system, and
/// the lines dropped to review with no explanation.</para>
///
/// <para><b>These tests assert the DIFFERENCE, not the sameness (R6).</b> Each case-variant is
/// driven through THREE paths that must agree — the live resolver, the pinned-revision snapshot
/// resolver, and the catalog lookup — and the assertion compares their outcomes to each other. If
/// any ONE path changes its case rule alone, the comparison fails. A test that merely asserted
/// "resolves case-insensitively" three times would pass while the three drifted apart.</para>
///
/// <para>This class runs on the EF InMemory provider, where <c>.ToLower()</c> executes in C#. It
/// therefore proves the CONTRACT but says nothing about Npgsql translation or database collation —
/// <c>ItemMappingCaseParityPostgresTests</c> proves that half against a real postgres:16.</para>
/// </summary>
public class ItemMappingCaseParityTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private const string Stored = "WIDGET-1";
    private const string Target = "SUP-9";

    /// <summary>
    /// Every spelling an ERP export realistically produces for one stored code. Whatever the case
    /// rule is, all three paths must apply it identically to each of these.
    /// </summary>
    public static TheoryData<string> CaseVariants() => new()
    {
        "WIDGET-1",   // exact
        "widget-1",   // all lower
        "Widget-1",   // title
        "wIdGeT-1",   // mixed
        " WIDGET-1 ", // padded (trimming is already the documented contract)
    };

    // ── 1. The difference assertion: three paths, one answer ─────────────────────

    [Theory]
    [MemberData(nameof(CaseVariants))]
    public async Task LiveResolver_SnapshotResolver_AndCatalog_AgreeOnEveryCaseVariant(string queried)
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = Stored, SupplierItemCode = Target,
            Source = "manual", Confidence = 1f,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SupplierProducts.Add(new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = Stored, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new ItemMappingService(db);

        // Path A — the live learned-mapping resolver (single + batch must also agree with each other).
        var liveSingle = await service.ResolveAsync(orgId, supplierId, queried, CancellationToken.None);
        var liveMany   = await service.ResolveManyAsync(
            orgId, supplierId, new[] { queried }, CancellationToken.None);
        var liveManyHit = liveMany.TryGetValue(queried.Trim(), out var v) && v is not null;

        // Path B — the pinned-revision snapshot resolver (must mirror the live one exactly).
        var snapshot = OrderIngestionService.ResolveFromSnapshot(
            new[] { new EffectiveRevisionItemMapping(Stored, Target) },
            new[] { new ParsedOrderLine(1, queried, null, 1m, null, 1m) });
        var snapshotHit = snapshot.TryGetValue(queried.Trim(), out var sv) && sv is not null;

        // Path C — the supplier catalog lookup.
        var catalog    = await OrderServiceShared.BuildCatalogLookupAsync(db, orgId, supplierId, CancellationToken.None);
        var catalogHit = catalog.ContainsKey(queried.Trim());

        var liveSingleHit = liveSingle is not null;

        liveSingleHit.Should().Be(catalogHit,
            "the learned-mapping resolver and the catalog must apply the SAME case rule to '{0}' — "
            + "one resolving while the other does not is the defect", queried);
        liveManyHit.Should().Be(catalogHit,
            "the batch resolver must agree with the catalog on '{0}'", queried);
        snapshotHit.Should().Be(catalogHit,
            "the pinned-revision snapshot resolver must agree with the catalog on '{0}' — a replay "
            + "that resolves differently from the live path is not a replay", queried);
    }

    // ── 2. Non-vacuity anchors ───────────────────────────────────────────────────

    [Fact]
    public async Task TheAgreementIsNotVacuous_ExactCaseResolvesEverywhere()
    {
        // Without this, all three paths returning "no" for everything would satisfy the theory above.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = Stored, SupplierItemCode = Target,
            Source = "manual", Confidence = 1f,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SupplierProducts.Add(new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = Stored, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        (await new ItemMappingService(db).ResolveAsync(orgId, supplierId, Stored, CancellationToken.None))
            .Should().Be(Target);

        (await OrderServiceShared.BuildCatalogLookupAsync(db, orgId, supplierId, CancellationToken.None))
            .Should().ContainKey(Stored);
    }

    [Fact]
    public async Task AGenuinelyDifferentCode_ResolvesNowhere()
    {
        // The other half of non-vacuity: case-insensitive must not mean "matches anything".
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = Stored, SupplierItemCode = Target,
            Source = "manual", Confidence = 1f,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Separator-stripping is explicitly NOT the rule for item codes (unlike manufacturer part
        // numbers): "WIDGET1" is a different SKU namespace entry from "WIDGET-1".
        (await new ItemMappingService(db).ResolveAsync(orgId, supplierId, "WIDGET1", CancellationToken.None))
            .Should().BeNull("case folding must not become separator folding");
        (await new ItemMappingService(db).ResolveAsync(orgId, supplierId, "WIDGET-2", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Resolution_IsStillOrgAndSupplierScoped()
    {
        // Case-insensitivity must not widen the tenancy boundary by accident.
        await using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgA, SupplierId = supplierId,
            BuyerItemCode = Stored, SupplierItemCode = Target,
            Source = "manual", Confidence = 1f,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new ItemMappingService(db);
        (await service.ResolveAsync(orgB, supplierId, "widget-1", CancellationToken.None)).Should().BeNull();
        (await service.ResolveAsync(orgA, Guid.NewGuid(), "widget-1", CancellationToken.None)).Should().BeNull();
    }

    // ── 3. The WRITE side must use the same rule as the READ side ────────────────

    [Fact]
    public async Task Upsert_WithDifferentCasing_UpdatesTheExistingRow_NeverInsertsASecond()
    {
        // The trap this closes: fixing resolution alone. If UpsertAsync keeps matching ordinally,
        // an operator correcting "b-1" while "B-1" exists writes a SECOND row — after which the
        // now case-insensitive resolver has two candidates and picks non-deterministically. The
        // read fix without the write fix converts a missed mapping into an unstable one.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var service    = new ItemMappingService(db);

        await service.UpsertAsync(orgId, supplierId, "B-1", "SUP-A", MappingSource.Manual, CancellationToken.None);
        await service.UpsertAsync(orgId, supplierId, "b-1", "SUP-B", MappingSource.Manual, CancellationToken.None);

        var rows = await db.ItemMappings
            .Where(m => m.OrgId == orgId && m.SupplierId == supplierId)
            .ToListAsync();

        rows.Should().HaveCount(1, "correcting a mapping must update it, not create a case-variant twin");
        rows[0].SupplierItemCode.Should().Be("SUP-B", "the newer correction wins");
        rows[0].BuyerItemCode.Should().Be("B-1",
            "the stored spelling stays as first written — rewriting it would churn the unique index "
            + "for no gain");

        (await service.ResolveAsync(orgId, supplierId, "B-1", CancellationToken.None)).Should().Be("SUP-B");
        (await service.ResolveAsync(orgId, supplierId, "b-1", CancellationToken.None)).Should().Be("SUP-B");
    }

    [Fact]
    public async Task Resolution_IsDeterministic_WhenLegacyCaseVariantRowsAlreadyExist()
    {
        // Rows written BEFORE the upsert fix can already be case-variant twins. Resolution must
        // still be repeatable and must prefer the exact-case row, never "whichever the DB returns".
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        db.ItemMappings.AddRange(
            new ItemMapping
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                BuyerItemCode = "b-1", SupplierItemCode = "SUP-LOWER",
                Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
            },
            new ItemMapping
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                BuyerItemCode = "B-1", SupplierItemCode = "SUP-UPPER",
                Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();

        var service = new ItemMappingService(db);

        (await service.ResolveAsync(orgId, supplierId, "B-1", CancellationToken.None))
            .Should().Be("SUP-UPPER", "an exact-case row must win over a case-variant one");
        (await service.ResolveAsync(orgId, supplierId, "b-1", CancellationToken.None))
            .Should().Be("SUP-LOWER");

        // Repeatable: the same query must not alternate between the twins.
        for (var i = 0; i < 5; i++)
            (await service.ResolveAsync(orgId, supplierId, "B-1", CancellationToken.None))
                .Should().Be("SUP-UPPER");

        var many = await service.ResolveManyAsync(
            orgId, supplierId, new[] { "B-1" }, CancellationToken.None);
        many["B-1"].Should().Be("SUP-UPPER", "the batch resolver must pick the same row as the single one");
    }
}
