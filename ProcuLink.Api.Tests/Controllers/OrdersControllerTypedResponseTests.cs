using System.Text.Json;
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
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Behaviour-preserving coverage for the typed success records that replaced the inline
/// <c>Ok(new { ... })</c> anonymous objects on three OrdersController endpoints
/// (GET /status, POST /accept-ai-suggestions, GET /dead-letter-count). Each test asserts
/// (a) the endpoint now returns the named record, and (b) that record serializes to the
/// EXACT same camelCase JSON the frontend maps by property — proving zero wire change.
/// </summary>
public class OrdersControllerTypedResponseTests
{
    // Mirrors the default ASP.NET Core MVC System.Text.Json serializer: camelCase names,
    // nulls written. The repo's API project calls AddControllers() with no JSON overrides,
    // so this is the on-the-wire shape clients actually receive.
    private static readonly JsonSerializerOptions WireOptions =
        new(JsonSerializerDefaults.Web);

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrdersController Build(
        Mock<IOrderService> ordersSvc,
        Guid orgId,
        ProcuLinkDbContext db)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            ordersSvc.Object,
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

    [Fact]
    public async Task GetStatus_ReturnsTypedRecord_SerializingToStatusOnly()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-1",
            OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency      = "EUR",
            Status        = "parsing",
            SourceFileKey = $"{orgId}/{orderId}/file.csv",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

        await using var db = NewDb();
        var ctrl = Build(ordersSvc, orgId, db);

        var result = await ctrl.GetStatus(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderStatusResponse>(ok.Value);
        Assert.Equal("parsing", dto.Status);

        var json = JsonSerializer.Serialize(dto, WireOptions);
        Assert.Equal("""{"status":"parsing"}""", json);
    }

    [Fact]
    public async Task AcceptAiSuggestions_ReturnsTypedRecord_SerializingToAcceptedOnly()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc
            .Setup(s => s.AcceptAiSuggestionsAsync(
                orgId, orderId, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Ok(3));

        await using var db = NewDb();
        var ctrl = Build(ordersSvc, orgId, db);

        var result = await ctrl.AcceptAiSuggestions(orderId, 0.85, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AcceptAiSuggestionsResponse>(ok.Value);
        Assert.Equal(3, dto.Accepted);

        var json = JsonSerializer.Serialize(dto, WireOptions);
        Assert.Equal("""{"accepted":3}""", json);
    }

    [Fact]
    public async Task GetDeadLetterCount_ReturnsTypedRecord_SerializingToCountOnly()
    {
        var orgId = Guid.NewGuid();

        await using var db = NewDb();
        // Two dead-letter orders for this org → count must be 2.
        for (var i = 0; i < 2; i++)
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id            = Guid.NewGuid(),
                OrgId         = orgId,
                SupplierId    = Guid.NewGuid(),
                PoNumber      = $"PO-DL-{i}",
                OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
                Currency      = "EUR",
                Status        = OrderStatusConstants.DeliveryDeadLetter,
                SourceFileKey = $"{orgId}/dl-{i}/file.csv",
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var ctrl = Build(new Mock<IOrderService>(), orgId, db);

        var result = await ctrl.GetDeadLetterCount(CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<DeadLetterCountResponse>(ok.Value);
        Assert.Equal(2, dto.Count);

        var json = JsonSerializer.Serialize(dto, WireOptions);
        Assert.Equal("""{"count":2}""", json);
    }
}
