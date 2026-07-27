using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Regression coverage for the inbound REST API (Zapier/Make.com) idempotency P0:
/// these integrations deliver at-least-once, so the same logical order can POST
/// to <c>/api/ingress/{slug}/orders</c> multiple times. Two POSTs carrying the
/// same Idempotency-Key must create exactly ONE order — the replay must
/// short-circuit and return the original order instead of creating (and later
/// delivering) a duplicate.
///
/// Uses the real <see cref="IdempotencyService"/> over the in-memory provider so
/// the bind/lookup path is exercised end to end; <see cref="IOrderService"/> is
/// mocked to count <c>CreateStubFromParsedOrderAsync</c> invocations.
/// </summary>
public class IngressControllerIdempotencyTests
{
    private const string Slug = "acme-distribution";

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Builds an IngressController wired through the UNIFIED tenant-resolution path:
    /// the internal org UUID is published into HttpContext.Items (exactly as
    /// ApiKeyAuthHandler does after authentication), and the controller reads it via
    /// ICurrentTenantService — not by parsing an org_id claim. Seeds a matching
    /// Organisation (by <paramref name="slug"/>) and Supplier so the slug guard and
    /// supplier resolution both pass.
    /// </summary>
    private static (IngressController Controller, Guid OrgId, Guid SupplierId) BuildController(
        ProcuLinkDbContext db,
        string slug = Slug)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id         = orgId,
            ClerkOrgId = "org_clerk_" + orgId.ToString("N"),
            Name       = "Acme Distribution",
            Slug       = slug,
            CreatedAt  = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id        = supplierId,
            OrgId     = orgId,
            Name      = "Northwind Trading",
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var idempotency = new IdempotencyService(db);

        // HttpContext.Items carries the resolved internal org UUID — the same single
        // value-space ApiKeyAuthHandler/TenantResolutionMiddleware populate. The real
        // CurrentTenantService reads it through IHttpContextAccessor.
        var httpContext = new DefaultHttpContext
        {
            User  = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim("org_id", orgId.ToString()) }, "ApiKey")),
        };
        httpContext.Items[CurrentTenantService.Items.OrganisationId] = orgId;

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var tenant   = new CurrentTenantService(accessor);

        var controller = new IngressController(
            db,
            idempotency,
            tenant,
            NullLogger<IngressController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, orgId, supplierId);
    }

    private static IngressOrderRequest MakeRequest(Guid supplierId) => new(
        OrderNumber: "PO-IDEMPOTENT-001",
        OrderDate:   new DateOnly(2026, 6, 6),
        Currency:    "EUR",
        Notes:       null,
        SupplierId:  supplierId.ToString(),
        Lines: new List<IngressOrderLine>
        {
            new(BuyerItemCode: "BUY-1", Description: "Widget", Quantity: 10m, Unit: "EA", UnitPrice: 4.50m),
            new(BuyerItemCode: "BUY-2", Description: "Gadget", Quantity: 2m,  Unit: "EA", UnitPrice: 99.00m),
        });

    [Fact]
    public async Task ReceiveOrder_SameIdempotencyKeyTwice_CreatesExactlyOneOrder()
    {
        await using var db = NewDb();
        var (controller, orgId, supplierId) = BuildController(db);

        const string idempotencyKey = "zapier-task-abc-123";
        controller.HttpContext.Request.Headers["Idempotency-Key"] = idempotencyKey;

        var createdOrderId = Guid.NewGuid();
        var createdEntity = new PurchaseOrderEntity
        {
            Id                = createdOrderId,
            OrgId             = orgId,
            SupplierId        = supplierId,
            PoNumber          = "PO-IDEMPOTENT-001",
            OrderDate         = new DateOnly(2026, 6, 6),
            Currency          = "EUR",
            Status            = "pending_review",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>
            {
                new() { Id = Guid.NewGuid(), OrderId = createdOrderId, LineNumber = 1 },
                new() { Id = Guid.NewGuid(), OrderId = createdOrderId, LineNumber = 2 },
            },
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var createCalls = 0;
        var orders = new Mock<IOrderService>();
        orders
            .Setup(s => s.CreateStubFromParsedOrderAsync(
                orgId, supplierId, It.IsAny<ExtractedOrder>(), "ingress_api", It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(() =>
            {
                createCalls++;
                return Result<PurchaseOrderEntity>.Ok(createdEntity);
            });
        // The replay path resolves the bound key, then loads the original order.
        orders
            .Setup(s => s.GetByIdAsync(orgId, createdOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(createdEntity));

        var req = MakeRequest(supplierId);

        // First delivery — creates the order and binds the key.
        var first = await controller.ReceiveOrder(Slug, req, orders.Object, CancellationToken.None);
        Assert.IsType<OkObjectResult>(first);

        // Second (at-least-once) delivery with the SAME key — must short-circuit.
        var second = await controller.ReceiveOrder(Slug, req, orders.Object, CancellationToken.None);
        Assert.IsType<OkObjectResult>(second);

        // The core assertion: exactly ONE order was created.
        Assert.Equal(1, createCalls);
        orders.Verify(
            s => s.CreateStubFromParsedOrderAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ExtractedOrder>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);

        // Exactly one idempotency-key row was persisted for this org.
        var keyRows = await db.IdempotencyKeys
            .Where(k => k.OrgId == orgId && k.Key == idempotencyKey)
            .ToListAsync();
        Assert.Single(keyRows);
        Assert.Equal(createdOrderId, keyRows[0].OrderId);

        // The replay response flags itself and returns the original order id.
        var secondOk = Assert.IsType<OkObjectResult>(second);
        var replayFlag = secondOk.Value!.GetType().GetProperty("idempotentReplay");
        Assert.NotNull(replayFlag);
        Assert.Equal(true, replayFlag!.GetValue(secondOk.Value));
    }

    [Fact]
    public async Task ReceiveOrder_NoHeaderButIdenticalBodyTwice_CreatesExactlyOneOrder()
    {
        // No Idempotency-Key header: the controller derives a stable key from the
        // payload, so a verbatim retry of the same body still deduplicates.
        await using var db = NewDb();
        var (controller, orgId, supplierId) = BuildController(db);

        var createdOrderId = Guid.NewGuid();
        var createdEntity = new PurchaseOrderEntity
        {
            Id                = createdOrderId,
            OrgId             = orgId,
            SupplierId        = supplierId,
            PoNumber          = "PO-IDEMPOTENT-001",
            OrderDate         = new DateOnly(2026, 6, 6),
            Currency          = "EUR",
            Status            = "pending_review",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        var createCalls = 0;
        var orders = new Mock<IOrderService>();
        orders
            .Setup(s => s.CreateStubFromParsedOrderAsync(
                orgId, supplierId, It.IsAny<ExtractedOrder>(), "ingress_api", It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(() =>
            {
                createCalls++;
                return Result<PurchaseOrderEntity>.Ok(createdEntity);
            });
        orders
            .Setup(s => s.GetByIdAsync(orgId, createdOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(createdEntity));

        var req = MakeRequest(supplierId);

        var first  = await controller.ReceiveOrder(Slug, req, orders.Object, CancellationToken.None);
        var second = await controller.ReceiveOrder(Slug, req, orders.Object, CancellationToken.None);

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<OkObjectResult>(second);
        Assert.Equal(1, createCalls);

        // Exactly one derived-key row persisted.
        var keyRows = await db.IdempotencyKeys.Where(k => k.OrgId == orgId).ToListAsync();
        Assert.Single(keyRows);
    }
}
