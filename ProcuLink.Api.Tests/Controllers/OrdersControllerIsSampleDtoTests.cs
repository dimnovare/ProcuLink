using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-27: <c>PurchaseOrderEntity.IsSample</c> must reach the order read model.
///
/// <para>
/// The flag has existed on the row since migration <c>20260528150709_AddIsSampleFlags</c> and is
/// exposed on <see cref="PassportDto"/>, but it was NOT on <see cref="OrderDto"/> — the payload the
/// review screen actually reads. So the frontend drove its "practice order" framing off a
/// <c>?sample=1</c> query parameter instead, and a practice order opened from a bookmark, the
/// back button, or an inbox row rendered as a real one. These tests pin the field so that
/// regression cannot return quietly.
/// </para>
/// </summary>
public class OrdersControllerIsSampleDtoTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId, IOrderService ordersSvc)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return new OrdersController(
            ordersSvc,
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

    private static PurchaseOrderEntity Order(Guid orgId, Guid orderId, Guid supplierId, bool isSample) => new()
    {
        Id                = orderId,
        OrgId             = orgId,
        SupplierId        = supplierId,
        Supplier          = new Supplier { Id = supplierId, OrgId = orgId, Name = "ProcuLink Sample Supplier" },
        PoNumber          = "DEMO-2026-001",
        OrderDate         = DateOnly.FromDateTime(DateTime.UtcNow),
        Currency          = "EUR",
        Status            = "pending_review",
        CreatedAt         = DateTime.UtcNow,
        UpdatedAt         = DateTime.UtcNow,
        IsSample          = isSample,
        OutboundArtifacts = new List<OutboundArtifact>(),
        Lines             = new List<PurchaseOrderLineEntity>(),
    };

    private static async Task<OrderDto> GetDtoAsync(bool isSample)
    {
        var orgId      = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await using var db = NewDb();

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc.Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(Order(orgId, orderId, supplierId, isSample)));

        var controller = BuildController(db, orgId, ordersSvc.Object);
        var ok = Assert.IsType<OkObjectResult>(await controller.Get(orderId, CancellationToken.None));
        return Assert.IsType<OrderDto>(ok.Value);
    }

    [Fact]
    public async Task Get_SampleOrder_ReportsIsSampleTrue()
    {
        var dto = await GetDtoAsync(isSample: true);
        Assert.True(dto.IsSample);
    }

    [Fact]
    public async Task Get_RealOrder_ReportsIsSampleFalse()
    {
        // The negative half matters as much as the positive one: a hardcoded `true` would make the
        // practice framing render on every real order in the workspace.
        var dto = await GetDtoAsync(isSample: false);
        Assert.False(dto.IsSample);
    }
}
