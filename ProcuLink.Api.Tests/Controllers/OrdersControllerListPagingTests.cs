using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Tokenizing;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for <c>GET /api/orders</c> honouring REST-style
/// <c>limit</c>/<c>offset</c> paging, end-to-end through the controller + a real
/// <see cref="OrderService"/> over a shared DbContext. Reproduces the live bug where the
/// endpoint silently ignored <c>limit</c>/<c>offset</c> and always returned the default
/// first 25 rows, so a user with thousands of orders could only ever see 25 of them.
/// </summary>
public class OrdersControllerListPagingTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrdersController BuildController(IOrderService orders, Guid orgId, ProcuLinkDbContext db)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            orders,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new Mock<IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());
    }

    /// <summary>Seeds <paramref name="count"/> orders for one org; returns (db, orgId, all seeded ids).</summary>
    private static async Task<(ProcuLinkDbContext db, Guid orgId, HashSet<Guid> ids)> SeedAsync(int count)
    {
        var db         = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Seed Supplier", CreatedAt = DateTime.UtcNow,
        });

        var ids = OrderListTestSupport.AddOrders(db, orgId, supplierId, count).ToHashSet();
        await db.SaveChangesAsync();
        return (db, orgId, ids);
    }

    private static PaginatedResult<PurchaseOrderSummary> Unwrap(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<PaginatedResult<PurchaseOrderSummary>>(ok.Value);
    }

    [Fact]
    public async Task List_WithLimitOffset_PagesThroughAllOrders_DistinctCoverage()
    {
        const int total = 57;
        const int limit = 20;
        var (db, orgId, seededIds) = await SeedAsync(total);
        var ctrl = BuildController(OrderListTestSupport.BuildOrderService(db), orgId, db);

        var collected = new List<Guid>();
        for (var offset = 0; offset < total; offset += limit)
        {
            var page = Unwrap(await ctrl.List(
                new OrderListQuery { Limit = limit, Offset = offset }, CancellationToken.None));

            // Total count is the whole filtered set on every page — never the page size.
            Assert.Equal(total, page.TotalCount);
            collected.AddRange(page.Items.Select(i => i.Id));
        }

        // Every seeded order appears exactly once across the pages: distinct + complete.
        Assert.Equal(total, collected.Count);
        Assert.Equal(total, collected.Distinct().Count());
        Assert.Equal(seededIds, collected.ToHashSet());
    }

    [Fact]
    public async Task List_LimitExceedingMaxPageSize_ClampedTo100()
    {
        var (db, orgId, _) = await SeedAsync(130);
        var ctrl = BuildController(OrderListTestSupport.BuildOrderService(db), orgId, db);

        var page = Unwrap(await ctrl.List(
            new OrderListQuery { Limit = 3000, Offset = 0 }, CancellationToken.None));

        Assert.Equal(130, page.TotalCount);   // count reflects all rows
        Assert.Equal(100, page.Items.Count);  // page is capped at the 100-row ceiling
    }

    [Fact]
    public async Task List_OffsetBeyondEnd_ReturnsEmptyItemsButCorrectTotal()
    {
        var (db, orgId, _) = await SeedAsync(57);
        var ctrl = BuildController(OrderListTestSupport.BuildOrderService(db), orgId, db);

        var page = Unwrap(await ctrl.List(
            new OrderListQuery { Limit = 20, Offset = 200 }, CancellationToken.None));

        Assert.Equal(57, page.TotalCount);
        Assert.Empty(page.Items);
    }
}
