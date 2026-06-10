using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Validation + pass-through coverage for the header fields added to
/// <c>POST /api/orders/{id}/resolve</c> (<see cref="ResolveRequest"/>): invalid currency
/// and unparseable order date return 400 before the service is touched; a valid request
/// forwards the parsed <see cref="ResolveHeaderFields"/> to <see cref="IOrderService.ResolveAsync"/>.
/// </summary>
public class OrdersControllerResolveValidationTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrgId) Build()
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders       = new Mock<IOrderService>();
        var jobs         = new Mock<Hangfire.IBackgroundJobClient>();
        var billing      = new Mock<IBillingService>();
        var idempotency  = new Mock<IIdempotencyService>();
        var exceptions   = new Mock<IOrderExceptionService>();
        var acceptance   = new Mock<ISupplierAcceptanceService>();

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            jobs.Object,
            NewDb(),
            NullLogger<OrdersController>.Instance,
            billing.Object,
            idempotency.Object,
            exceptions.Object,
            acceptance.Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        return (ctrl, orders, orgId);
    }

    private static ResolveRequest WithLine(Action<ResolveRequest>? configure = null)
    {
        var req = new ResolveRequest
        {
            LineResolutions = new() { new Contracts.LineResolution { LineNumber = 1, SupplierItemCode = "SUP-1" } },
        };
        configure?.Invoke(req);
        return req;
    }

    [Fact]
    public async Task Resolve_InvalidCurrency_Returns400_AndNeverCallsService()
    {
        var (ctrl, orders, _) = Build();

        var result = await ctrl.Resolve(
            Guid.NewGuid(),
            WithLine(r => r.Currency = "EU"), // 2 letters → invalid
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_NonAlphaCurrency_Returns400()
    {
        var (ctrl, _, _) = Build();

        var result = await ctrl.Resolve(
            Guid.NewGuid(),
            WithLine(r => r.Currency = "EU1"), // 3 chars but not all letters
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Resolve_UnparseableOrderDate_Returns400_AndNeverCallsService()
    {
        var (ctrl, orders, _) = Build();

        var result = await ctrl.Resolve(
            Guid.NewGuid(),
            WithLine(r => r.OrderDate = "not-a-date"),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_ValidHeader_ForwardsParsedFieldsToService()
    {
        var (ctrl, orders, orgId) = Build();
        var id = Guid.NewGuid();

        ResolveHeaderFields? captured = null;
        orders
            .Setup(o => o.ResolveAsync(
                orgId, id, It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()))
            .Callback<Guid, Guid, IReadOnlyList<Core.Services.LineResolution>, bool, CancellationToken, ResolveHeaderFields?>(
                (_, _, _, _, _, h) => captured = h)
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = id, OrgId = orgId, PoNumber = "PO-1", Currency = "GBP",
                OrderDate = new DateOnly(2026, 7, 1), Status = "ready",
            }));

        var result = await ctrl.Resolve(
            id,
            WithLine(r =>
            {
                r.OrderDate = "2026-07-01";
                r.BuyerName = "  Padded Buyer  "; // trimmed by the controller
                r.Currency  = "gbp";              // upper-cased by the controller
            }),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.Equal(new DateOnly(2026, 7, 1), captured!.OrderDate);
        Assert.Equal("Padded Buyer", captured.BuyerName);
        Assert.Equal("GBP", captured.Currency);
    }

    [Fact]
    public async Task Resolve_NoHeaderFields_ForwardsAllNullHeader()
    {
        var (ctrl, orders, orgId) = Build();
        var id = Guid.NewGuid();

        ResolveHeaderFields? captured = null;
        orders
            .Setup(o => o.ResolveAsync(
                orgId, id, It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()))
            .Callback<Guid, Guid, IReadOnlyList<Core.Services.LineResolution>, bool, CancellationToken, ResolveHeaderFields?>(
                (_, _, _, _, _, h) => captured = h)
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = id, OrgId = orgId, PoNumber = "PO-1", Currency = "EUR",
                OrderDate = new DateOnly(2026, 1, 1), Status = "ready",
            }));

        var result = await ctrl.Resolve(id, WithLine(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.False(captured!.HasAnyChange);
    }

    // ── PO number + supplier name are forwarded (trimmed) to the service ────────────

    [Fact]
    public async Task Resolve_PoNumberAndSupplierName_ForwardedTrimmed()
    {
        var (ctrl, orders, orgId) = Build();
        var id = Guid.NewGuid();

        ResolveHeaderFields? captured = null;
        orders
            .Setup(o => o.ResolveAsync(
                orgId, id, It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()))
            .Callback<Guid, Guid, IReadOnlyList<Core.Services.LineResolution>, bool, CancellationToken, ResolveHeaderFields?>(
                (_, _, _, _, _, h) => captured = h)
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = id, OrgId = orgId, PoNumber = "PO-NEW", Currency = "EUR",
                OrderDate = new DateOnly(2026, 1, 1), Status = "ready",
            }));

        var result = await ctrl.Resolve(
            id,
            WithLine(r =>
            {
                r.PoNumber     = "  PO-NEW  ";
                r.SupplierName = "  Acme Display  ";
            }),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.Equal("PO-NEW", captured!.PoNumber);
        Assert.Equal("Acme Display", captured.SupplierName);
        Assert.True(captured.HasAnyChange);
    }

    // ── A header-only PO-number edit (no line resolutions) is accepted ─────────────

    [Fact]
    public async Task Resolve_PoNumberOnly_NoLines_IsAccepted()
    {
        var (ctrl, orders, orgId) = Build();
        var id = Guid.NewGuid();

        orders
            .Setup(o => o.ResolveAsync(
                orgId, id, It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = id, OrgId = orgId, PoNumber = "PO-EDIT", Currency = "EUR",
                OrderDate = new DateOnly(2026, 1, 1), Status = "ready",
            }));

        var req = new ResolveRequest { PoNumber = "PO-EDIT" }; // no line resolutions

        var result = await ctrl.Resolve(id, req, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            orgId, id, It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Once);
    }

    // ── Over-long PO number / supplier name → 400 before the service is touched ─────

    [Fact]
    public async Task Resolve_OverlongPoNumber_Returns400_AndNeverCallsService()
    {
        var (ctrl, orders, _) = Build();

        var result = await ctrl.Resolve(
            Guid.NewGuid(),
            WithLine(r => r.PoNumber = new string('X', 257)), // > 256
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_OverlongSupplierName_Returns400()
    {
        var (ctrl, _, _) = Build();

        var result = await ctrl.Resolve(
            Guid.NewGuid(),
            WithLine(r => r.SupplierName = new string('Y', 300)),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
