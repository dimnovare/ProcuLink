using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Unit tests for <see cref="OrderService.ListPagedAsync"/>:
/// pagination boundaries, org-scoping, each filter type, and default ordering.
/// Uses EF InMemory — no database required.
/// </summary>
public class OrderServiceListPagedTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[]
            {
                new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser()
            }),
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    /// <summary>Seeds N purchase orders for a given org, returning their ids newest-first.</summary>
    private static async Task<(ProcuLinkDbContext db, Guid orgId, Guid supplierId, List<Guid> orderIds)>
        SeedOrdersAsync(int count, string status = "ready")
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Seed Supplier",
            CreatedAt = DateTime.UtcNow,
        });

        var ids = new List<Guid>();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id            = id,
                OrgId         = orgId,
                SupplierId    = supplierId,
                PoNumber      = $"PO-{i + 1:D4}",
                OrderDate     = DateOnly.FromDateTime(baseTime.AddDays(i)),
                Currency      = "EUR",
                Status        = status,
                SourceFileKey = $"{orgId}/{id}/order.csv",
                // CreatedAt increases so PO-0001 is oldest, PO-000N is newest.
                CreatedAt     = baseTime.AddMinutes(i),
                UpdatedAt     = baseTime.AddMinutes(i),
            });
        }

        await db.SaveChangesAsync();

        // Reverse so index 0 = newest order id (matches DESC ordering).
        ids.Reverse();
        return (db, orgId, supplierId, ids);
    }

    // ── pagination boundary tests ──────────────────────────────────────────────

    [Fact]
    public async Task ListPagedAsync_Page1_ReturnsFirstPageNewestFirst()
    {
        var (db, orgId, _, ids) = await SeedOrdersAsync(10);
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, page: 1, pageSize: 3,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(10, total);
        Assert.Equal(3, items.Count);
        // Newest first — the first 3 ids (reversed) map to top of DESC list.
        Assert.Equal(ids[0], items[0].Id);
        Assert.Equal(ids[1], items[1].Id);
        Assert.Equal(ids[2], items[2].Id);
    }

    [Fact]
    public async Task ListPagedAsync_Page2_ReturnsCorrectSlice()
    {
        var (db, orgId, _, ids) = await SeedOrdersAsync(10);
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, page: 2, pageSize: 3,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(10, total);
        Assert.Equal(3, items.Count);
        Assert.Equal(ids[3], items[0].Id);
        Assert.Equal(ids[4], items[1].Id);
        Assert.Equal(ids[5], items[2].Id);
    }

    [Fact]
    public async Task ListPagedAsync_LastPage_ReturnsRemainder()
    {
        var (db, orgId, _, ids) = await SeedOrdersAsync(10);
        var svc = BuildService(db);

        // pageSize=3: pages 1–3 have 3 items each, page 4 has 1 item.
        var result = await svc.ListPagedAsync(orgId, page: 4, pageSize: 3,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(10, total);
        Assert.Single(items);
        Assert.Equal(ids[9], items[0].Id);
    }

    [Fact]
    public async Task ListPagedAsync_BeyondLastPage_ReturnsEmptyItemsButCorrectTotal()
    {
        var (db, orgId, _, _) = await SeedOrdersAsync(5);
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, page: 99, pageSize: 10,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(5, total);
        Assert.Empty(items);
    }

    // ── default ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListPagedAsync_DefaultOrdering_IsNewestFirst()
    {
        var (db, orgId, _, ids) = await SeedOrdersAsync(5);
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, page: 1, pageSize: 5,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, _) = result.Value;
        Assert.Equal(5, items.Count);
        // ids[0] is the newest; verify full ordering.
        for (var i = 0; i < 5; i++)
            Assert.Equal(ids[i], items[i].Id);
    }

    // ── org-scoping ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListPagedAsync_OrgANeverSeesOrgBOrders()
    {
        var db    = NewDb();
        var orgA  = Guid.NewGuid();
        var orgB  = Guid.NewGuid();
        var supA  = Guid.NewGuid();
        var supB  = Guid.NewGuid();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.AddRange(
            new Supplier { Id = supA, OrgId = orgA, Name = "Sup A", CreatedAt = baseTime },
            new Supplier { Id = supB, OrgId = orgB, Name = "Sup B", CreatedAt = baseTime });

        // 3 orders for org A, 5 for org B.
        for (var i = 0; i < 3; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgA, SupplierId = supA,
                PoNumber = $"A-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = "ready",
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        for (var i = 0; i < 5; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgB, SupplierId = supB,
                PoNumber = $"B-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = "ready",
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        await db.SaveChangesAsync();

        var svc = BuildService(db);

        // Org A query.
        var resultA = await svc.ListPagedAsync(orgA, 1, 25,
            null, null, null, null, null, CancellationToken.None);
        Assert.True(resultA.IsSuccess);
        Assert.Equal(3, resultA.Value.TotalCount);
        Assert.Equal(3, resultA.Value.Items.Count);
        // All items must have org A's supplier name (proving they came from org A).
        Assert.All(resultA.Value.Items, item => Assert.Equal("Sup A", item.SupplierName));

        // Org B query.
        var resultB = await svc.ListPagedAsync(orgB, 1, 25,
            null, null, null, null, null, CancellationToken.None);
        Assert.True(resultB.IsSuccess);
        Assert.Equal(5, resultB.Value.TotalCount);

        // Ensure no overlap between A and B result sets.
        var aIds = resultA.Value.Items.Select(i => i.Id).ToHashSet();
        var bIds = resultB.Value.Items.Select(i => i.Id).ToHashSet();
        Assert.Empty(aIds.Intersect(bIds));
    }

    // ── status filter ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListPagedAsync_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        var statuses = new[] { "ready", "ready", "delivered", "pending_review", "delivered" };
        for (var i = 0; i < statuses.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = $"PO-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = statuses[i],
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            status: "delivered", null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("delivered", i.Status));
    }

    /// <summary>
    /// The UI renders all five failure statuses as one red "Failed" pill, so filtering
    /// status=failed must return the WHOLE failure bucket — not just literal "failed".
    /// </summary>
    [Fact]
    public async Task ListPagedAsync_StatusFilterFailed_ReturnsWholeFailureBucket()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        // One order in every failure status + two non-failure statuses that must be excluded.
        var statuses = new[]
        {
            "failed",
            "transform_failed",
            "delivery_failed",
            "delivery_dead_letter",
            "rejected_by_supplier",
            "delivered",        // excluded
            "ready",            // excluded
        };
        for (var i = 0; i < statuses.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = $"PO-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = statuses[i],
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            status: "failed", null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        // All five failure statuses match; the two non-failure rows do not.
        Assert.Equal(5, total);
        Assert.Equal(5, items.Count);
        var returnedStatuses = items.Select(i => i.Status).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "failed", "transform_failed", "delivery_failed",
                "delivery_dead_letter", "rejected_by_supplier",
            },
            returnedStatuses);
    }

    /// <summary>
    /// A non-failed status filter stays an EXACT match even when failure-bucket rows exist —
    /// only the literal status is returned (the bucket expansion is failed-only).
    /// </summary>
    [Fact]
    public async Task ListPagedAsync_StatusFilterNonFailed_StaysExactMatch()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        var statuses = new[]
        {
            "delivered",
            "delivered",
            "delivery_failed",   // failure-bucket member — must NOT leak into a delivered filter
            "transform_failed",  // failure-bucket member — must NOT leak into a delivered filter
        };
        for (var i = 0; i < statuses.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = $"PO-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = statuses[i],
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            status: "delivered", null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(2, total);
        Assert.All(items, i => Assert.Equal("delivered", i.Status));
    }

    // ── supplierId filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListPagedAsync_SupplierIdFilter_ReturnsOnlyThatSupplier()
    {
        var db    = NewDb();
        var orgId = Guid.NewGuid();
        var sup1  = Guid.NewGuid();
        var sup2  = Guid.NewGuid();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.AddRange(
            new Supplier { Id = sup1, OrgId = orgId, Name = "Sup One", CreatedAt = baseTime },
            new Supplier { Id = sup2, OrgId = orgId, Name = "Sup Two", CreatedAt = baseTime });

        // 2 orders for sup1, 3 for sup2.
        for (var i = 0; i < 2; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = sup1,
                PoNumber = $"S1-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = "ready",
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        for (var i = 0; i < 3; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = sup2,
                PoNumber = $"S2-{i}", OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = "ready",
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, supplierId: sup1, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("Sup One", i.SupplierName));
    }

    // ── date range filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListPagedAsync_DateFromFilter_ExcludesOlderOrders()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        // 3 orders spread a month apart.
        var dates = new[]
        {
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        for (var i = 0; i < dates.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = $"PO-{i}", OrderDate = DateOnly.FromDateTime(dates[i]),
                Currency = "EUR", Status = "ready",
                CreatedAt = dates[i], UpdatedAt = dates[i],
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var from   = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, null, dateFrom: from, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        // Feb + Mar match; Jan does not.
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.CreatedAt >= from));
    }

    [Fact]
    public async Task ListPagedAsync_DateToFilter_ExcludesNewerOrders()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        var dates = new[]
        {
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        for (var i = 0; i < dates.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = $"PO-{i}", OrderDate = DateOnly.FromDateTime(dates[i]),
                Currency = "EUR", Status = "ready",
                CreatedAt = dates[i], UpdatedAt = dates[i],
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var to     = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, null, null, dateTo: to, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        // Jan + Feb match; Mar does not.
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.CreatedAt <= to));
    }

    [Fact]
    public async Task ListPagedAsync_DateRangeBothBounds_FiltersCorrectly()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        var dates = new[]
        {
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        for (var i = 0; i < dates.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = $"PO-{i}", OrderDate = DateOnly.FromDateTime(dates[i]),
                Currency = "EUR", Status = "ready",
                CreatedAt = dates[i], UpdatedAt = dates[i],
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var from = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, null, dateFrom: from, dateTo: to, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        // Only Feb 15 is in range.
        Assert.Equal(1, total);
        Assert.Single(items);
    }

    // ── search filter ──────────────────────────────────────────────────────────
    // NOTE: EF.Functions.ILike is a PostgreSQL-only function.  The unit tests here
    // use the EF InMemory provider, which cannot translate ILike and would throw at
    // runtime.  The four tests below verify the *data shape* (correct orders seeded,
    // BuyerName denormalised) but pass search: null so the ILike predicate is never
    // evaluated.  Case-insensitive PO/supplier/buyer-name filtering is verified in
    // integration tests that run against a real Postgres container.

    [Fact]
    public async Task ListPagedAsync_Search_NullSearch_ReturnsAllOrders()
    {
        // Verifies that omitting the search filter (null) returns the full set —
        // the baseline for all search-related integration tests.
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = baseTime });

        var poNumbers = new[] { "ALPHA-001", "BETA-002", "ALPHA-003" };
        for (var i = 0; i < poNumbers.Length; i++)
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = poNumbers[i], OrderDate = DateOnly.FromDateTime(baseTime),
                Currency = "EUR", Status = "ready",
                CreatedAt = baseTime.AddMinutes(i), UpdatedAt = baseTime,
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        // Null search — all 3 orders should be returned.
        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, search: null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(3, total);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task ListPagedAsync_Search_NullSearch_ReturnsAllSuppliers()
    {
        // Verifies org-scoped list works when two suppliers are present and search is null.
        var db    = NewDb();
        var orgId = Guid.NewGuid();
        var sup1  = Guid.NewGuid();
        var sup2  = Guid.NewGuid();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.AddRange(
            new Supplier { Id = sup1, OrgId = orgId, Name = "Northwind Trading", CreatedAt = baseTime },
            new Supplier { Id = sup2, OrgId = orgId, Name = "Contoso Ltd",       CreatedAt = baseTime });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = sup1,
            PoNumber = "PO-1", OrderDate = DateOnly.FromDateTime(baseTime),
            Currency = "EUR", Status = "ready",
            CreatedAt = baseTime, UpdatedAt = baseTime,
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = sup2,
            PoNumber = "PO-2", OrderDate = DateOnly.FromDateTime(baseTime),
            Currency = "EUR", Status = "ready",
            CreatedAt = baseTime.AddMinutes(1), UpdatedAt = baseTime,
        });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, search: null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
        // Both supplier names are surfaced correctly.
        var names = items.Select(i => i.SupplierName).ToHashSet();
        Assert.Contains("Northwind Trading", names);
        Assert.Contains("Contoso Ltd", names);
    }

    [Fact]
    public async Task ListPagedAsync_BuyerName_IsDenormalisedOnEntity()
    {
        // Verifies that BuyerName is exposed directly on the summary (not from CanonicalJson).
        // ILike search on BuyerName is covered by integration tests (InMemory cannot translate ILike).
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Generic Supplier", CreatedAt = baseTime });

        var idWithBuyer    = Guid.NewGuid();
        var idWithoutBuyer = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = idWithBuyer,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-B1",
            OrderDate  = DateOnly.FromDateTime(baseTime),
            Currency   = "EUR",
            Status     = "ready",
            BuyerName  = "REDACTED-PARTY",  // denormalised column (not CanonicalJson)
            CreatedAt  = baseTime,
            UpdatedAt  = baseTime,
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = idWithoutBuyer,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-B2",
            OrderDate  = DateOnly.FromDateTime(baseTime),
            Currency   = "EUR",
            Status     = "ready",
            BuyerName  = null,
            CreatedAt  = baseTime.AddMinutes(1),
            UpdatedAt  = baseTime,
        });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        // Use null search so InMemory EF never hits ILike.
        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, search: null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(2, total);

        // Newest first: idWithoutBuyer was inserted at baseTime+1min.
        var withBuyer    = items.FirstOrDefault(i => i.Id == idWithBuyer);
        var withoutBuyer = items.FirstOrDefault(i => i.Id == idWithoutBuyer);
        Assert.NotNull(withBuyer);
        Assert.NotNull(withoutBuyer);
        Assert.Equal("REDACTED-PARTY", withBuyer.BuyerName);
        Assert.Null(withoutBuyer.BuyerName);
    }

    [Fact]
    public async Task ListPagedAsync_Search_EmptySearch_ReturnsAllOrders()
    {
        // Verifies that whitespace/empty search behaves identically to null search.
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme Corp", CreatedAt = baseTime });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            PoNumber = "UPPER-PO-001", OrderDate = DateOnly.FromDateTime(baseTime),
            Currency = "EUR", Status = "ready",
            CreatedAt = baseTime, UpdatedAt = baseTime,
        });
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        // Empty-string search is treated as null — no ILike predicate, all rows returned.
        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, search: "   ", null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.TotalCount);
    }

    // ── combined filters + pagination correctness ──────────────────────────────

    [Fact]
    public async Task ListPagedAsync_CombinedStatusAndDateFilter_WorksTogether()
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = baseTime });

        // 4 orders: 2 "ready" in range, 1 "ready" out of range, 1 "delivered" in range.
        var t1 = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); // before range
        var t4 = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc); // in range but delivered

        foreach (var (t, s) in new[] { (t1, "ready"), (t2, "ready"), (t3, "ready"), (t4, "delivered") })
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                PoNumber = Guid.NewGuid().ToString("N")[..8],
                OrderDate = DateOnly.FromDateTime(t),
                Currency = "EUR", Status = s,
                CreatedAt = t, UpdatedAt = t,
            });

        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var from = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            status: "ready", null, null, dateFrom: from, dateTo: to, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(2, total);
        Assert.All(items, i => Assert.Equal("ready", i.Status));
        Assert.All(items, i => Assert.True(i.CreatedAt >= from && i.CreatedAt <= to));
    }

    [Fact]
    public async Task ListPagedAsync_EmptyOrg_ReturnsEmptyResult()
    {
        var db    = NewDb();
        var orgId = Guid.NewGuid();
        var svc   = BuildService(db);

        var result = await svc.ListPagedAsync(orgId, 1, 25,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ListPagedAsync_TotalCountReflectsFilterNotPageSize()
    {
        var (db, orgId, _, _) = await SeedOrdersAsync(15);
        var svc = BuildService(db);

        // Page 1 with pageSize 5 — total should still be 15, not 5.
        var result = await svc.ListPagedAsync(orgId, 1, 5,
            null, null, null, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var (items, total) = result.Value;
        Assert.Equal(15, total);
        Assert.Equal(5, items.Count);
    }
}
