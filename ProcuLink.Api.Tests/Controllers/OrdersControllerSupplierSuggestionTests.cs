using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The READ half of supplier auto-detect: ranked candidates on the order an operator is looking at.
/// <para>The decision-recording half lives in
/// <c>Integration/SupplierSuggestionAssignDecisionPostgresTests</c> and not here, because
/// <c>assign-supplier</c>'s atomic <c>unrouted → parsing</c> claim is an <c>ExecuteUpdateAsync</c>
/// that the InMemory provider cannot translate at all — an InMemory test of it could only ever
/// assert on an exception.</para>
/// </summary>
public class OrdersControllerSupplierSuggestionTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId, IOrderService? ordersSvc = null)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return new OrdersController(
            ordersSvc ?? new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());
    }

    private static Supplier Supplier(ProcuLinkDbContext db, Guid orgId, string name)
    {
        var s = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = name, CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(s);
        return s;
    }

    private static PurchaseOrderEntity Order(ProcuLinkDbContext db, Guid orgId, string status = OrderStatusConstants.Unrouted)
    {
        var o = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = null,
            PoNumber = "PO-UNROUTED", Currency = "EUR", Status = status,
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.PurchaseOrders.Add(o);
        return o;
    }

    private static OrderSupplierSuggestion Suggestion(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, Guid supplierId, int rank, double score,
        string? decision = null)
    {
        var row = new OrderSupplierSuggestion
        {
            Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId, SupplierId = supplierId,
            Rank = rank, Score = score, Decision = decision,
            SignalsJson = """[{"Signal":"supplier_name","Contribution":0.2,"Detail":"the supplier name on the document matches theirs"}]""",
            ModelVersion = "rules-v1", CreatedAt = DateTime.UtcNow,
        };
        db.OrderSupplierSuggestions.Add(row);
        return row;
    }

    // ── Read path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_unroutedOrder_carriesRankedSuggestionsWithReasonAndSignals()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var order  = Order(db, orgId);
        var best   = Supplier(db, orgId, "Best Match Ltd");
        var second = Supplier(db, orgId, "Second Match Ltd");
        Suggestion(db, orgId, order.Id, best.Id, rank: 1, score: 0.72);
        Suggestion(db, orgId, order.Id, second.Id, rank: 2, score: 0.31);
        await db.SaveChangesAsync();

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await BuildController(db, orgId, orders.Object).Get(order.Id, default);

        var dto = Assert.IsType<OrderDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(dto.SupplierSuggestions);
        Assert.Equal(2, dto.SupplierSuggestions!.Count);

        var top = dto.SupplierSuggestions[0];
        Assert.Equal(best.Id, top.SupplierId);
        Assert.Equal("Best Match Ltd", top.SupplierName);
        Assert.Equal(1, top.Rank);
        Assert.Equal(0.72, top.Score, 3);
        Assert.NotEmpty(top.Signals);
        Assert.Equal("supplier_name", top.Signals[0].Signal);
    }

    [Fact]
    public async Task Get_unroutedOrder_omitsSuggestionsAlreadyDecided()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var order = Order(db, orgId);
        var stale = Supplier(db, orgId, "Superseded Guess");
        Suggestion(db, orgId, order.Id, stale.Id, 1, 0.6, OrderSupplierSuggestionDecision.Superseded);
        await db.SaveChangesAsync();

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await BuildController(db, orgId, orders.Object).Get(order.Id, default);

        var dto = Assert.IsType<OrderDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(dto.SupplierSuggestions is null || dto.SupplierSuggestions.Count == 0);
    }

    [Fact]
    public async Task Get_unroutedOrder_omitsASupplierThatHasSinceBeenDeleted()
    {
        // assign-supplier refuses a soft-deleted supplier with a 400, so leaving it in the banner
        // would offer the operator a candidate they cannot accept.
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var order = Order(db, orgId);
        var gone  = Supplier(db, orgId, "Removed Ltd");
        gone.DeletedAt = DateTime.UtcNow;
        var live  = Supplier(db, orgId, "Still Here Ltd");
        Suggestion(db, orgId, order.Id, gone.Id, 1, 0.80);
        Suggestion(db, orgId, order.Id, live.Id, 2, 0.40);
        await db.SaveChangesAsync();

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await BuildController(db, orgId, orders.Object).Get(order.Id, default);

        var dto = Assert.IsType<OrderDto>(Assert.IsType<OkObjectResult>(result).Value);
        var only = Assert.Single(dto.SupplierSuggestions!);
        Assert.Equal(live.Id, only.SupplierId);
    }

    [Fact]
    public async Task Get_routedOrder_carriesNoSuggestions()
    {
        // A routed order has nothing to resolve, so the banner must not appear on it — and we do
        // not pay for the lookup either.
        var orgId = Guid.NewGuid();
        await using var db = NewDb();
        var supplier = Supplier(db, orgId, "Already Routed Ltd");
        var order = Order(db, orgId, OrderStatusConstants.PendingReview);
        order.SupplierId = supplier.Id;
        order.Supplier = supplier;
        Suggestion(db, orgId, order.Id, supplier.Id, 1, 0.9);   // leftover row from when it was unrouted
        await db.SaveChangesAsync();

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var result = await BuildController(db, orgId, orders.Object).Get(order.Id, default);

        var dto = Assert.IsType<OrderDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(dto.SupplierSuggestions is null || dto.SupplierSuggestions.Count == 0);
    }
}
