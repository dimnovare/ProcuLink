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

        await service.UpsertAsync(orgId, supplierId, "B-1", "SUP-A", MappingSource.Manual, confidence: null, CancellationToken.None);
        await service.UpsertAsync(orgId, supplierId, "b-1", "SUP-B", MappingSource.Manual, confidence: null, CancellationToken.None);

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

    // ── 4. The batch resolver is keyed by the CALLER's exact code ────────────────
    //
    // Case-insensitivity belongs in the MATCH (which stored row does this code find?), never in the
    // RESULT KEYING (which answer does this line get?). Folding the keys collapses two genuinely
    // different lines onto one answer, and OrderIngestionService.BuildLineEntitiesAsync writes that
    // one answer onto both lines — the WRONG ITEM is ordered, silently, with a confident mapping.

    /// <summary>
    /// Seeds two case-variant rows and returns a service over them. Deliberately models the
    /// production hazard: an org that treats <c>B-1</c> and <c>b-1</c> as different products.
    /// </summary>
    private static async Task<(ItemMappingService Service, Guid OrgId, Guid SupplierId)> TwinRowsAsync(
        ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        db.ItemMappings.AddRange(
            new ItemMapping
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                BuyerItemCode = "B-1", SupplierItemCode = "SUP-UPPER",
                Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
            },
            new ItemMapping
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                BuyerItemCode = "b-1", SupplierItemCode = "SUP-LOWER",
                Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();
        return (new ItemMappingService(db), orgId, supplierId);
    }

    [Fact]
    public async Task ResolveMany_WithTwoCaseVariantsOnOneOrder_AnswersEachLineSeparately()
    {
        await using var db = NewDb();
        var (service, orgId, supplierId) = await TwinRowsAsync(db);

        var many = await service.ResolveManyAsync(
            orgId, supplierId, new[] { "B-1", "b-1" }, CancellationToken.None);

        many.Should().HaveCount(2,
            "both codes were requested; collapsing them to one key makes the dictionary lie to the "
            + "caller about how many distinct answers it holds");
        many["B-1"].Should().Be("SUP-UPPER");
        many["b-1"].Should().Be("SUP-LOWER",
            "a line carrying 'b-1' must be answered from the 'b-1' row — answering it with the 'B-1' "
            + "row's supplier code orders the WRONG ITEM");
    }

    [Fact]
    public async Task ResolveMany_AgreesWithResolveAsync_ForEveryCodeOnAMixedCaseOrder()
    {
        // The difference assertion (R6): batch and single are compared to EACH OTHER, so a case rule
        // that drifts in only one of them fails here even if both are individually "reasonable".
        await using var db = NewDb();
        var (service, orgId, supplierId) = await TwinRowsAsync(db);

        var codes = new[] { "B-1", "b-1", "B-1" };
        var many  = await service.ResolveManyAsync(orgId, supplierId, codes, CancellationToken.None);

        // The sweep below is only evidence if it actually swept. A batch that answered fewer codes
        // than were asked leaves the loop comparing batch to single on a shorter set — or on none at
        // all — and the run reports green having found no disagreement it ever looked for.
        many.Should().HaveCount(2,
            "both case spellings on this order must reach the comparison; a batch holding fewer "
            + "answers means the parity check silently stopped covering one of them");

        foreach (var code in codes.Distinct(StringComparer.Ordinal))
        {
            var single = await service.ResolveAsync(orgId, supplierId, code, CancellationToken.None);
            many[code].Should().Be(single,
                "the batch and single resolvers must return the same supplier code for '{0}'", code);
        }
    }

    [Fact]
    public async Task ResolveMany_StillFoldsCase_WhenOnlyOneSpellingIsStored()
    {
        // The other half: exact-keying must NOT undo WP-14's actual fix. One stored row, an ERP that
        // changed its export casing — that still has to resolve.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = "WIDGET-1", SupplierItemCode = "SUP-9",
            Source = "manual", Confidence = 1f,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var many = await new ItemMappingService(db).ResolveManyAsync(
            orgId, supplierId, new[] { "widget-1", "WIDGET-1" }, CancellationToken.None);

        many["widget-1"].Should().Be("SUP-9", "one stored spelling still answers every case variant");
        many["WIDGET-1"].Should().Be("SUP-9");
    }

    // ── 5. The snapshot resolver's tie-break must match the live one ─────────────

    [Fact]
    public async Task ResolveFromSnapshot_PicksTheSameTwinAsTheLiveResolver()
    {
        // A replay renders through ResolveFromSnapshot. "Last snapshot row wins" and the live
        // resolver's "exact-case wins" disagree whenever the snapshot lists the twins in the order
        // below — so a replay of a delivered order silently substitutes a different supplier code.
        await using var db = NewDb();
        var (service, orgId, supplierId) = await TwinRowsAsync(db);

        var snapshot = new[]
        {
            new EffectiveRevisionItemMapping("B-1", "SUP-UPPER"),
            new EffectiveRevisionItemMapping("b-1", "SUP-LOWER"),
        };
        var lines = new[] { new ParsedOrderLine(1, "B-1", null, 1m, null, 1m) };

        var fromSnapshot = OrderIngestionService.ResolveFromSnapshot(snapshot, lines);
        var live         = await service.ResolveManyAsync(
            orgId, supplierId, new[] { "B-1" }, CancellationToken.None);

        fromSnapshot["B-1"].Should().Be(live["B-1"],
            "a replay must reproduce what the live path did, including which of two case-variant "
            + "rows won");
    }

    [Fact]
    public async Task ResolveFromSnapshot_KeysByTheExactRequestedCode_LikeTheLiveResolver()
    {
        await using var db = NewDb();
        var (service, orgId, supplierId) = await TwinRowsAsync(db);

        var snapshot = new[]
        {
            new EffectiveRevisionItemMapping("B-1", "SUP-UPPER"),
            new EffectiveRevisionItemMapping("b-1", "SUP-LOWER"),
        };
        var lines = new[]
        {
            new ParsedOrderLine(1, "B-1", null, 1m, null, 1m),
            new ParsedOrderLine(2, "b-1", null, 1m, null, 1m),
        };

        var fromSnapshot = OrderIngestionService.ResolveFromSnapshot(snapshot, lines);
        var live         = await service.ResolveManyAsync(
            orgId, supplierId, new[] { "B-1", "b-1" }, CancellationToken.None);

        fromSnapshot.Should().HaveCount(live.Count);
        fromSnapshot["B-1"].Should().Be(live["B-1"]);
        fromSnapshot["b-1"].Should().Be(live["b-1"]);
    }

    // ── 6. EVERY write path obeys the shared rule (the twin population is closed) ─

    [Fact]
    public async Task CreateAsync_WhenACaseVariantAlreadyExists_UpdatesIt_NeverInsertsATwin()
    {
        // UpsertAsync's own comment says the twin "must never exist". CreateAsync is a blind insert
        // reached from POST /mappings and the bulk CSV import, so it creates exactly that row.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var service    = new ItemMappingService(db);

        await service.CreateAsync(orgId, supplierId, "B-1", "SUP-A", MappingSource.Manual, confidence: null, CancellationToken.None);
        await service.CreateAsync(orgId, supplierId, "b-1", "SUP-B", MappingSource.Manual, confidence: null, CancellationToken.None);

        var rows = await db.ItemMappings
            .Where(m => m.OrgId == orgId && m.SupplierId == supplierId)
            .ToListAsync();

        rows.Should().HaveCount(1, "the create path must apply the same case rule as UpsertAsync");
        rows[0].SupplierItemCode.Should().Be("SUP-B");
    }

    [Fact]
    public async Task UpdateByIdAsync_RenamingOntoAnotherRowsCaseVariant_RefusesInsteadOfCreatingATwin()
    {
        // Renaming mapping X's buyer code onto a spelling row Y already owns cannot be silently
        // allowed: the unique index is case-SENSITIVE, so Postgres accepts it and the resolver then
        // holds two candidates. Merging the rows would DELETE one — a founder decision, not this
        // method's. Refusing is the only option that neither corrupts nor destroys.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var service    = new ItemMappingService(db);

        var keep   = await service.CreateAsync(orgId, supplierId, "B-1", "SUP-A", MappingSource.Manual, confidence: null, CancellationToken.None);
        var rename = await service.CreateAsync(orgId, supplierId, "C-9", "SUP-C", MappingSource.Manual, confidence: null, CancellationToken.None);

        var result = await service.UpdateByIdAsync(
            orgId, rename.Id, "b-1", "SUP-D", MappingSource.Manual, CancellationToken.None);

        result.Should().BeNull("the rename collides with an existing case-variant row");

        var rows = await db.ItemMappings
            .Where(m => m.OrgId == orgId && m.SupplierId == supplierId)
            .OrderBy(m => m.BuyerItemCode)
            .ToListAsync();

        rows.Should().HaveCount(2, "no twin was created");
        rows.Select(r => r.BuyerItemCode).Should().BeEquivalentTo(new[] { "B-1", "C-9" },
            "the refused rename left both rows exactly as they were");
        rows.Single(r => r.Id == keep.Id).SupplierItemCode.Should().Be("SUP-A");
        rows.Single(r => r.Id == rename.Id).SupplierItemCode.Should().Be("SUP-C");
    }

    // ── 7. Existing twins are REPORTED, never silently repaired ─────────────────

    [Fact]
    public async Task FindCaseVariantTwins_ReportsTheGroup_WithEverySpelling()
    {
        await using var db = NewDb();
        var (_, orgId, supplierId) = await TwinRowsAsync(db);

        var twins = await new ItemMappingService(db).FindCaseVariantTwinsAsync(orgId, CancellationToken.None);

        var group = twins.Should().ContainSingle().Subject;
        group.SupplierId.Should().Be(supplierId);
        group.FoldedCode.Should().Be("b-1");
        group.RowCount.Should().Be(2);
        group.Spellings.Should().BeEquivalentTo(new[] { "B-1", "b-1" });
    }

    [Fact]
    public async Task FindCaseVariantTwins_DoesNotTouchTheRows()
    {
        // Detection must not become repair by accident: merging or deleting a twin changes a
        // customer's item codes and is a founder decision, not a report's side effect.
        await using var db = NewDb();
        var (_, orgId, _) = await TwinRowsAsync(db);

        await new ItemMappingService(db).FindCaseVariantTwinsAsync(orgId, CancellationToken.None);

        var rows = await db.ItemMappings.Where(m => m.OrgId == orgId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.SupplierItemCode).Should().BeEquivalentTo(new[] { "SUP-UPPER", "SUP-LOWER" });
    }

    [Fact]
    public async Task FindCaseVariantTwins_IsEmpty_ForAHealthyOrg_AndIsOrgScoped()
    {
        await using var db = NewDb();
        var (_, orgId, _) = await TwinRowsAsync(db);

        // Non-vacuity in both directions: a different org sees nothing, and an org whose codes
        // genuinely differ sees nothing either.
        (await new ItemMappingService(db).FindCaseVariantTwinsAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().BeEmpty("the report is org-scoped like every other read here");

        var healthyOrg  = Guid.NewGuid();
        var healthySupp = Guid.NewGuid();
        var now         = DateTime.UtcNow;
        db.ItemMappings.AddRange(
            new ItemMapping
            {
                Id = Guid.NewGuid(), OrgId = healthyOrg, SupplierId = healthySupp,
                BuyerItemCode = "A-1", SupplierItemCode = "SUP-A",
                Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
            },
            new ItemMapping
            {
                Id = Guid.NewGuid(), OrgId = healthyOrg, SupplierId = healthySupp,
                BuyerItemCode = "A-2", SupplierItemCode = "SUP-B",
                Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();

        (await new ItemMappingService(db).FindCaseVariantTwinsAsync(healthyOrg, CancellationToken.None))
            .Should().BeEmpty("two genuinely different codes are not twins");

        // …and the unhealthy org still reports, so the two assertions above are not passing for the
        // trivial reason that the query returns nothing at all.
        (await new ItemMappingService(db).FindCaseVariantTwinsAsync(orgId, CancellationToken.None))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateByIdAsync_RenamingOntoItsOwnCaseVariant_IsAllowed()
    {
        // The guard must only fire for a DIFFERENT row. Re-casing a mapping's own code is a normal
        // edit and must keep working.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var service    = new ItemMappingService(db);

        var mapping = await service.CreateAsync(orgId, supplierId, "B-1", "SUP-A", MappingSource.Manual, confidence: null, CancellationToken.None);

        var result = await service.UpdateByIdAsync(
            orgId, mapping.Id, "b-1", "SUP-B", MappingSource.Manual, CancellationToken.None);

        result.Should().NotBeNull();
        result!.BuyerItemCode.Should().Be("b-1");
        result.SupplierItemCode.Should().Be("SUP-B");
    }
}
