using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Detection;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// The DB-backed half of supplier auto-detect: which signals fire against real rows, and the
/// tenancy + exclusion rules around them. The cross-supplier catalog probe additionally has a
/// real-Postgres test (<c>SupplierSuggestionCatalogOverlapPostgresTests</c>) — this provider
/// would happily pass a query whose index path is wrong.
/// </summary>
public class SupplierSuggestionServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierSuggestionService NewService(ProcuLinkDbContext db) =>
        new(db, new SchemaFingerprintService(db, NullLogger<SchemaFingerprintService>.Instance),
            NullLogger<SupplierSuggestionService>.Instance);

    private static readonly Guid OrgId = Guid.NewGuid();

    private static Supplier SeedSupplier(
        ProcuLinkDbContext db, string name, Guid? orgId = null,
        string? vat = null, string? regNr = null, string? ediCode = null, string? domain = null,
        bool deleted = false, bool isSample = false)
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            OrgId = orgId ?? OrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            IsSample = isSample,
            VatNumber = vat,
            RegistrationNumber = regNr,
            EdiCode = ediCode,
            PrimaryDomain = domain,
        };
        db.Suppliers.Add(supplier);
        return supplier;
    }

    private static SupplierSuggestionInput Input(
        IReadOnlyList<string>? headers = null,
        string? documentSupplierName = null,
        IReadOnlyList<SupplierSuggestionParty>? parties = null,
        IReadOnlyList<string>? lineCodes = null,
        string? senderDomain = null,
        Guid? orderId = null) =>
        new(OrgId, orderId ?? Guid.NewGuid(), headers, documentSupplierName, parties, lineCodes, senderDomain);

    // ── Nothing to suggest ────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_returnsNothing_whenTheOrgHasNoSuppliers()
    {
        await using var db = NewDb();
        var result = await NewService(db).SuggestAsync(Input(documentSupplierName: "Acme"), default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SuggestAsync_returnsNothing_whenNoSignalFires()
    {
        await using var db = NewDb();
        SeedSupplier(db, "Acme GmbH");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(documentSupplierName: "Totally Unrelated Oy"), default);

        Assert.Empty(result);
    }

    // ── Name ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_matchesSupplierName_acrossLegalFormAndCase()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(documentSupplierName: "ACME  gmbh"), default);

        var only = Assert.Single(result);
        Assert.Equal(acme.Id, only.SupplierId);
        Assert.Contains(only.Signals, s => s.Signal == SupplierSignalKind.Name);
    }

    [Fact]
    public async Task SuggestAsync_matchesTheSupplierRoleParty_whenTheHeaderNameIsAbsent()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(parties: new[] { new SupplierSuggestionParty("supplier", Name: "Acme GmbH") }), default);

        Assert.Equal(acme.Id, Assert.Single(result).SupplierId);
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_matchesVatNumber_fromAnyPartyRole()
    {
        // A VAT number is a near-unique key, so the role it appears under does not change what it
        // identifies — which matters because the counterparty is the 'buyer' party on an inbound
        // customer PO and the 'supplier' party on an outbound one.
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH", vat: "EE101234567");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(parties: new[] { new SupplierSuggestionParty("buyer", Name: "Something Else", Vat: "EE 101 234 567") }),
            default);

        var only = Assert.Single(result);
        Assert.Equal(acme.Id, only.SupplierId);
        Assert.Contains(only.Signals, s => s.Signal == SupplierSignalKind.Identity);
    }

    [Fact]
    public async Task SuggestAsync_matchesRegistrationNumberAndEdiCode()
    {
        await using var db = NewDb();
        var byReg = SeedSupplier(db, "Reg Match", regNr: "12345678");
        var byEdi = SeedSupplier(db, "Edi Match", ediCode: "1111111111116");
        await db.SaveChangesAsync();

        var reg = await NewService(db).SuggestAsync(
            Input(parties: new[] { new SupplierSuggestionParty("supplier", RegNr: "1234-5678") }), default);
        var edi = await NewService(db).SuggestAsync(
            Input(parties: new[] { new SupplierSuggestionParty("supplier", EdiCode: "1111111111116") }), default);

        Assert.Equal(byReg.Id, Assert.Single(reg).SupplierId);
        Assert.Equal(byEdi.Id, Assert.Single(edi).SupplierId);
    }

    [Fact]
    public async Task SuggestAsync_ranksAnIdentityMatchAboveANameMatch()
    {
        await using var db = NewDb();
        SeedSupplier(db, "Acme GmbH");                                  // name twin
        var real = SeedSupplier(db, "Acme Trading OU", vat: "EE101234567");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(documentSupplierName: "Acme GmbH",
                  parties: new[] { new SupplierSuggestionParty("supplier", Vat: "EE101234567") }),
            default);

        Assert.Equal(real.Id, result[0].SupplierId);
        Assert.Equal(1, result[0].Rank);
    }

    // ── Layout fingerprint ────────────────────────────────────────────────────

    private static readonly string[] Headers = { "PO Number", "Item", "Qty", "Price" };

    private static void SeedFingerprint(ProcuLinkDbContext db, IEnumerable<Guid> supplierIds, Guid? orgId = null)
    {
        db.SchemaFingerprints.Add(new SchemaFingerprint
        {
            Id = Guid.NewGuid(),
            OrganisationId = orgId ?? OrgId,
            ColumnNameHash = SchemaFingerprintHasher.ComputeColumnNameHash(Headers)!,
            DetectedFormat = "csv",
            SupplierIdsCsv = string.Join(',', supplierIds),
            ParseSuccessCount = 3,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task SuggestAsync_suggestsTheSoleSupplierBoundToTheLayout()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        SeedFingerprint(db, new[] { acme.Id });
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(headers: Headers), default);

        var only = Assert.Single(result);
        Assert.Equal(acme.Id, only.SupplierId);
        Assert.Equal(
            SupplierSuggestionScoring.LayoutWeight,
            only.Signals.Single(s => s.Signal == SupplierSignalKind.Layout).Contribution, 3);
    }

    [Fact]
    public async Task SuggestAsync_sharedLayout_suggestsEveryBoundSupplier_withIdenticalScores()
    {
        // Founder ruling D4: suggest ALL bound suppliers, ranked. The point of "equally" is that a
        // layout collision can never silently pick one of them — so the scores must come back the
        // SAME, not merely close.
        await using var db = NewDb();
        var a = SeedSupplier(db, "Alpha Supplies");
        var z = SeedSupplier(db, "Zeta Supplies");
        SeedFingerprint(db, new[] { a.Id, z.Id });
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(headers: Headers), default);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { a.Id, z.Id }.OrderBy(x => x), result.Select(r => r.SupplierId).OrderBy(x => x));
        Assert.Equal(result[0].Score, result[1].Score, 6);
    }

    [Fact]
    public async Task SuggestAsync_sharedLayout_contributesLessPerSupplierThanASoleBinding()
    {
        // Two suppliers behind one layout is weaker evidence for each of them than one supplier
        // behind it. Equal between themselves, smaller than the unambiguous case.
        await using var db = NewDb();
        var a = SeedSupplier(db, "Alpha Supplies");
        var z = SeedSupplier(db, "Zeta Supplies");
        SeedFingerprint(db, new[] { a.Id, z.Id });
        await db.SaveChangesAsync();

        var shared = await NewService(db).SuggestAsync(Input(headers: Headers), default);

        Assert.All(shared, s => Assert.True(
            s.Signals.Single(x => x.Signal == SupplierSignalKind.Layout).Contribution
                < SupplierSuggestionScoring.LayoutWeight,
            "a shared layout must contribute less per supplier than a sole binding"));
    }

    [Fact]
    public async Task SuggestAsync_sharedLayout_cannotBreakATieItIsTheOnlySignalFor()
    {
        await using var db = NewDb();
        var a = SeedSupplier(db, "Alpha Supplies");
        var z = SeedSupplier(db, "Zeta Supplies");
        SeedFingerprint(db, new[] { a.Id, z.Id });
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(headers: Headers), default);

        Assert.Equal(1, result.Select(r => r.Score).Distinct().Count());
    }

    [Fact]
    public async Task SuggestAsync_ignoresALayoutBindingBelongingToAnotherOrg()
    {
        await using var db = NewDb();
        var mine = SeedSupplier(db, "Acme GmbH");
        SeedFingerprint(db, new[] { mine.Id }, orgId: Guid.NewGuid());   // same layout, someone else's org
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(headers: Headers), default);

        Assert.Empty(result);
    }

    // ── Catalog overlap ───────────────────────────────────────────────────────

    private static void SeedCatalog(ProcuLinkDbContext db, Guid supplierId, params string[] codes)
    {
        foreach (var code in codes)
            db.SupplierProducts.Add(new SupplierProduct
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                SupplierId = supplierId,
                Code = code,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
    }

    [Fact]
    public async Task SuggestAsync_scoresCatalogOverlap_asAFractionOfTheDocumentsCodes()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        SeedCatalog(db, acme.Id, "AAA", "BBB");          // 2 of the 4 codes on the document
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(lineCodes: new[] { "AAA", "BBB", "CCC", "DDD" }), default);

        var overlap = Assert.Single(result).Signals.Single(s => s.Signal == SupplierSignalKind.CatalogOverlap);
        Assert.Equal(SupplierSuggestionScoring.CatalogOverlapWeight * 0.5, overlap.Contribution, 3);
        Assert.Contains("2 of 4", overlap.Detail);
    }

    [Fact]
    public async Task SuggestAsync_catalogOverlap_prefersTheSupplierCarryingMoreOfTheCodes()
    {
        await using var db = NewDb();
        var most = SeedSupplier(db, "Carries Most");
        var few  = SeedSupplier(db, "Carries Few");
        SeedCatalog(db, most.Id, "AAA", "BBB", "CCC");
        SeedCatalog(db, few.Id, "AAA");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(lineCodes: new[] { "AAA", "BBB", "CCC", "DDD" }), default);

        Assert.Equal(most.Id, result[0].SupplierId);
    }

    [Fact]
    public async Task SuggestAsync_catalogOverlap_countsEachDistinctCodeOnce()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        SeedCatalog(db, acme.Id, "AAA");
        await db.SaveChangesAsync();

        // The same code on five lines is one piece of evidence, not five.
        var result = await NewService(db).SuggestAsync(
            Input(lineCodes: new[] { "AAA", "AAA", "AAA", "AAA", "ZZZ" }), default);

        var overlap = Assert.Single(result).Signals.Single(s => s.Signal == SupplierSignalKind.CatalogOverlap);
        Assert.Equal(SupplierSuggestionScoring.CatalogOverlapWeight * 0.5, overlap.Contribution, 3);
    }

    [Fact]
    public async Task SuggestAsync_catalogOverlap_appliesTheSameCaseRuleAsTheCatalogLookup()
    {
        // WP-14 folded case when RESOLVING a code against SupplierProduct.Code
        // (OrderServiceShared.BuildCatalogLookupAsync). This probe reads the SAME column and must
        // not disagree: if it stays ordinal, a supplier whose ERP exports lower-case scores ZERO on
        // auto-detect while resolving perfectly once routed. The order parks `unrouted` looking, to
        // the operator, exactly like "we do not recognise this supplier" — a silent failure with no
        // error anywhere, unlike a wrong code, which is at least visible on the document.
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        SeedCatalog(db, acme.Id, "AAA", "BBB");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(lineCodes: new[] { "aaa", "bbb", "ccc", "ddd" }), default);

        var overlap = Assert.Single(result).Signals.Single(s => s.Signal == SupplierSignalKind.CatalogOverlap);
        Assert.Equal(SupplierSuggestionScoring.CatalogOverlapWeight * 0.5, overlap.Contribution, 3);
        Assert.Contains("2 of 4", overlap.Detail);
    }

    [Fact]
    public async Task SuggestAsync_catalogOverlap_countsTwoCaseVariantsOfOneCodeOnce()
    {
        // The dedupe of the DOCUMENT's codes must fold case too, or "AAA" and "aaa" on one order
        // inflate the denominator and depress every supplier's score.
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        SeedCatalog(db, acme.Id, "AAA");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(lineCodes: new[] { "AAA", "aaa", "ZZZ" }), default);

        var overlap = Assert.Single(result).Signals.Single(s => s.Signal == SupplierSignalKind.CatalogOverlap);
        Assert.Equal(SupplierSuggestionScoring.CatalogOverlapWeight * 0.5, overlap.Contribution, 3);
        Assert.Contains("1 of 2", overlap.Detail);
    }

    // ── Sender domain ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_matchesTheSuppliersPrimaryDomain()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH", domain: "acme.example");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(senderDomain: "ACME.com"), default);

        var only = Assert.Single(result);
        Assert.Equal(acme.Id, only.SupplierId);
        Assert.Contains(only.Signals, s => s.Signal == SupplierSignalKind.SenderDomain);
    }

    [Fact]
    public async Task SuggestAsync_learnsFromWhereEarlierOrdersFromTheSameDomainWereRouted()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        for (var i = 0; i < 3; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = OrgId, SupplierId = acme.Id,
                PoNumber = $"PO-{i}", Status = "delivered", Currency = "EUR",
                InboundSenderDomain = "acme.example", InboundSenderDomainCapturedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(senderDomain: "acme.example"), default);

        var only = Assert.Single(result);
        Assert.Equal(acme.Id, only.SupplierId);
        var history = only.Signals.Single(s => s.Signal == SupplierSignalKind.SenderDomainHistory);
        Assert.Contains("3", history.Detail);
    }

    [Fact]
    public async Task SuggestAsync_domainHistory_excludesTheOrderBeingScored()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = OrgId, SupplierId = acme.Id,
            PoNumber = "PO-SELF", Status = "unrouted", Currency = "EUR",
            InboundSenderDomain = "acme.example", InboundSenderDomainCapturedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(senderDomain: "acme.example", orderId: orderId), default);

        // Its own row is the only history there is — an order must not be evidence for itself.
        Assert.Empty(result);
    }

    // ── Exclusions + tenancy ──────────────────────────────────────────────────

    [Fact]
    public async Task SuggestAsync_neverSuggestsASoftDeletedOrSampleSupplier()
    {
        await using var db = NewDb();
        SeedSupplier(db, "Acme GmbH", deleted: true);
        SeedSupplier(db, "Acme GmbH", isSample: true);
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(documentSupplierName: "Acme GmbH"), default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SuggestAsync_neverCrossesTenants_evenOnAnExactVatMatch()
    {
        await using var db = NewDb();
        SeedSupplier(db, "Acme GmbH", orgId: Guid.NewGuid(), vat: "EE101234567", domain: "acme.example");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            Input(documentSupplierName: "Acme GmbH", senderDomain: "acme.example",
                  parties: new[] { new SupplierSuggestionParty("supplier", Vat: "EE101234567") }),
            default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SuggestAsync_returnsAtMostThree()
    {
        await using var db = NewDb();
        for (var i = 0; i < 5; i++)
            SeedSupplier(db, $"Acme {i}", domain: "acme.example");
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(Input(senderDomain: "acme.example"), default);

        Assert.Equal(SupplierSuggestionScoring.MaxSuggestions, result.Count);
    }

    // ── RecordAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_stagesUndecidedRows_butLeavesTheSaveToTheCaller()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        await db.SaveChangesAsync();
        var orderId = Guid.NewGuid();

        var suggestion = new SupplierSuggestion(acme.Id, "Acme GmbH", 1, 0.42, "because",
            new[] { new SupplierSignalContribution(SupplierSignalKind.Name, 0.42, "name matches") });

        await NewService(db).RecordAsync(OrgId, orderId, new[] { suggestion }, default);

        // Nothing is persisted until the caller's transaction says so — the parse path needs these
        // rows to commit with the order's status flip or not at all.
        Assert.Empty(await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync());

        await db.SaveChangesAsync();

        var row = Assert.Single(await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync());
        Assert.Equal(orderId, row.OrderId);
        Assert.Equal(acme.Id, row.SupplierId);
        Assert.Equal(1, row.Rank);
        Assert.Equal(0.42, row.Score, 3);
        Assert.Null(row.Decision);
        Assert.Null(row.DecidedAt);
        Assert.Equal(SupplierSuggestionScoring.ModelVersion, row.ModelVersion);
        Assert.Contains("name matches", row.SignalsJson);
    }

    [Fact]
    public async Task RecordAsync_supersedesTheEarlierUndecidedSet_ratherThanDeletingIt()
    {
        await using var db = NewDb();
        var first  = SeedSupplier(db, "First Guess");
        var second = SeedSupplier(db, "Second Guess");
        await db.SaveChangesAsync();
        var orderId = Guid.NewGuid();
        var service = NewService(db);

        await service.RecordAsync(OrgId, orderId, new[]
        {
            new SupplierSuggestion(first.Id, "First Guess", 1, 0.40, "r", Array.Empty<SupplierSignalContribution>()),
        }, default);
        await db.SaveChangesAsync();

        await service.RecordAsync(OrgId, orderId, new[]
        {
            new SupplierSuggestion(second.Id, "Second Guess", 1, 0.70, "r", Array.Empty<SupplierSignalContribution>()),
        }, default);
        await db.SaveChangesAsync();

        var rows = await db.OrderSupplierSuggestions.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);

        var old = rows.Single(r => r.SupplierId == first.Id);
        Assert.Equal(OrderSupplierSuggestionDecision.Superseded, old.Decision);
        Assert.NotNull(old.DecidedAt);

        var live = rows.Single(r => r.SupplierId == second.Id);
        Assert.Null(live.Decision);
    }

    [Fact]
    public async Task RecordAsync_neverTouchesAnAlreadyDecidedRow()
    {
        await using var db = NewDb();
        var chosen = SeedSupplier(db, "Chosen");
        await db.SaveChangesAsync();
        var orderId = Guid.NewGuid();

        db.OrderSupplierSuggestions.Add(new OrderSupplierSuggestion
        {
            Id = Guid.NewGuid(), OrgId = OrgId, OrderId = orderId, SupplierId = chosen.Id,
            Rank = 1, Score = 0.9, Decision = OrderSupplierSuggestionDecision.Accepted,
            DecidedBy = "user_abc", DecidedAt = DateTime.UtcNow.AddMinutes(-5), CreatedAt = DateTime.UtcNow.AddMinutes(-6),
        });
        await db.SaveChangesAsync();

        await NewService(db).RecordAsync(OrgId, orderId, new[]
        {
            new SupplierSuggestion(chosen.Id, "Chosen", 1, 0.5, "r", Array.Empty<SupplierSignalContribution>()),
        }, default);
        await db.SaveChangesAsync();

        var accepted = await db.OrderSupplierSuggestions.AsNoTracking()
            .SingleAsync(r => r.Decision == OrderSupplierSuggestionDecision.Accepted);
        Assert.Equal("user_abc", accepted.DecidedBy);
    }

    [Fact]
    public async Task RecordAsync_supersedesOnlyWithinItsOwnOrder()
    {
        await using var db = NewDb();
        var acme = SeedSupplier(db, "Acme GmbH");
        await db.SaveChangesAsync();
        var otherOrderId = Guid.NewGuid();
        var service = NewService(db);

        await service.RecordAsync(OrgId, otherOrderId, new[]
        {
            new SupplierSuggestion(acme.Id, "Acme GmbH", 1, 0.40, "r", Array.Empty<SupplierSignalContribution>()),
        }, default);
        await db.SaveChangesAsync();

        await service.RecordAsync(OrgId, Guid.NewGuid(), new[]
        {
            new SupplierSuggestion(acme.Id, "Acme GmbH", 1, 0.40, "r", Array.Empty<SupplierSignalContribution>()),
        }, default);
        await db.SaveChangesAsync();

        var untouched = await db.OrderSupplierSuggestions.AsNoTracking()
            .SingleAsync(r => r.OrderId == otherOrderId);
        Assert.Null(untouched.Decision);
    }
}
