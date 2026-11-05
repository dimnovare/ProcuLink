using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>POST /api/orders/{id}/send-anyway releases a held invoice with a reason, and nothing else.</summary>
public class OrdersSendAnywayTests
{
    private readonly Guid _org = Guid.NewGuid();
    private readonly ProcuLinkDbContext _db = new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private async Task<PurchaseOrderEntity> SeedAsync(string documentType, string status, string exceptionState = "open")
    {
        var order = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = _org, SupplierId = Guid.NewGuid(), PoNumber = "INV-1", Currency = "EUR",
            DocumentType = documentType, Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.PurchaseOrders.Add(order);
        _db.OrderExceptions.Add(new OrderException
        {
            Id = Guid.NewGuid(), OrgId = _org, OrderId = order.Id, Stage = "Validate", Code = "invoice_check",
            State = exceptionState, Message = "Possible duplicate.", CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return order;
    }

    private OrdersController Build(PurchaseOrderEntity order)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(_org);
        tenant.SetupGet(t => t.ClerkUserId).Returns("user_admin");
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(_org, order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));
        return new OrdersController(
            orders.Object, tenant.Object, new Mock<IBackgroundJobClient>().Object, _db,
            NullLogger<OrdersController>.Instance, new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object, new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object, new Mock<IOrderMappingOverrideService>().Object,
            new Mock<IPromoteMappingService>().Object, new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());
    }

    /// <summary>"ignored": a held invoice whose check rows were all ignored on the exceptions page.</summary>
    [Theory]
    [InlineData("open")]
    [InlineData("ignored")]
    public async Task HeldInvoice_WithReason_IsReleasedClosedAsIgnoredAndAudited(string exceptionState)
    {
        var invoice = await SeedAsync("invoice", OrderStatusConstants.PendingReview, exceptionState);
        var other   = await SeedAsync("invoice", OrderStatusConstants.PendingReview);

        var result = await Build(invoice).SendAnyway(invoice.Id, new(" Same bill, re-sent by the vendor. "), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OrderStatusConstants.Ready, invoice.Status);
        Assert.Single(_db.OrderExceptions, e => e.OrderId == invoice.Id && e.State == "ignored");
        Assert.Single(_db.OrderExceptions, e => e.OrderId == other.Id && e.State == "open");
        var audit = Assert.Single(_db.AuditEvents, a => a.Action == OrdersController.InvoiceSentAnywayAction);
        using var payload = System.Text.Json.JsonDocument.Parse(audit.Payload!.RootElement.GetRawText());
        Assert.Equal("user_admin", payload.RootElement.GetProperty("by").GetString());
        Assert.Equal("Same bill, re-sent by the vendor.", payload.RootElement.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("order", OrderStatusConstants.PendingReview, "open")]
    [InlineData("invoice", OrderStatusConstants.Ready, "open")]
    public async Task NotAHeldInvoice_IsRefused(string documentType, string status, string exceptionState)
    {
        var order = await SeedAsync(documentType, status, exceptionState);

        var result = await Build(order).SendAnyway(order.Id, new("It is fine to send."), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(status, order.Status);
    }
}
