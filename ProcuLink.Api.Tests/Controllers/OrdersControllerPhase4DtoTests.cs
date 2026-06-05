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
/// Verifies the Phase 4 enrichment fields (totals, payment terms, document type,
/// extracted supplier name, and per-line amount/tax/delivery-date) flow through
/// <c>OrdersController.MapToDto</c> onto the <see cref="OrderDto"/> read path, and
/// that they are null for orders that never captured enrichment (CSV/XLSX path) —
/// offer⇔works: the UI must render nothing extra when the data is absent.
/// </summary>
public class OrdersControllerPhase4DtoTests
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
            new Mock<ISupplierAcceptanceService>().Object);
    }

    [Fact]
    public async Task Get_OrderWithPhase4Enrichment_MapsAllFields()
    {
        var orgId      = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var deliveryDate = new DateOnly(2026, 7, 1);

        await using var db = NewDb();

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            // Resolved supplier (navigation) — distinct from the extracted document name.
            Supplier      = new Supplier { Id = supplierId, OrgId = orgId, Name = "Resolved Supplier Ltd" },
            PoNumber      = "PO-ENRICHED",
            OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency      = "EUR",
            Status        = "pending_review",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            // Phase 4 header enrichment.
            SupplierName  = "AS PRINTED GmbH",     // extracted (document) name
            SubTotal      = 100.00m,
            TaxTotal      = 21.00m,
            GrandTotal    = 121.00m,
            PaymentTerms  = "Net 30",
            DocumentType  = "invoice",
            OutboundArtifacts = new List<OutboundArtifact>(),
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id               = Guid.NewGuid(),
                    LineNumber       = 1,
                    BuyerItemCode    = "BUY-1",
                    SupplierItemCode = "SUP-1",
                    Description      = "Widget",
                    Quantity         = 4m,
                    Unit             = "PCS",
                    UnitPrice        = 25m,
                    Confidence       = 1f,
                    NeedsReview      = false,
                    // Phase 4 per-line enrichment.
                    LineAmount       = 100.00m,
                    TaxRate          = 21.0m,
                    DeliveryDate     = deliveryDate,
                },
            },
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc.Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

        var controller = BuildController(db, orgId, ordersSvc.Object);
        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);

        // Header enrichment maps.
        Assert.Equal(100.00m, dto.SubTotal);
        Assert.Equal(21.00m,  dto.TaxTotal);
        Assert.Equal(121.00m, dto.GrandTotal);
        Assert.Equal("Net 30", dto.PaymentTerms);
        Assert.Equal("invoice", dto.DocumentType);

        // Resolved supplier name (Supplier.Name) and extracted document supplier name are distinct.
        Assert.Equal("Resolved Supplier Ltd", dto.SupplierName);
        Assert.Equal("AS PRINTED GmbH",       dto.DocumentSupplierName);

        // Per-line enrichment maps, delivery date serialized as yyyy-MM-dd.
        var line = Assert.Single(dto.Lines);
        Assert.Equal(100.00m, line.LineAmount);
        Assert.Equal(21.0m,   line.TaxRate);
        Assert.Equal("2026-07-01", line.DeliveryDate);
    }

    [Fact]
    public async Task Get_OrderWithoutEnrichment_Phase4FieldsAreNull()
    {
        var orgId      = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await using var db = NewDb();

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            Supplier      = new Supplier { Id = supplierId, OrgId = orgId, Name = "CSV Supplier" },
            PoNumber      = "PO-CSV",
            OrderDate     = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency      = "EUR",
            Status        = "ready",
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
            OutboundArtifacts = new List<OutboundArtifact>(),
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id            = Guid.NewGuid(),
                    LineNumber    = 1,
                    BuyerItemCode = "BUY-1",
                    Quantity      = 2m,
                    UnitPrice     = 10m,
                    Confidence    = 1f,
                    NeedsReview   = false,
                    // No Phase 4 fields set → all null.
                },
            },
        };

        var ordersSvc = new Mock<IOrderService>();
        ordersSvc.Setup(s => s.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(entity));

        var controller = BuildController(db, orgId, ordersSvc.Object);
        var result = await controller.Get(orderId, CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OrderDto>(ok.Value);

        Assert.Null(dto.SubTotal);
        Assert.Null(dto.TaxTotal);
        Assert.Null(dto.GrandTotal);
        Assert.Null(dto.PaymentTerms);
        Assert.Null(dto.DocumentType);
        Assert.Null(dto.DocumentSupplierName);

        var line = Assert.Single(dto.Lines);
        Assert.Null(line.LineAmount);
        Assert.Null(line.TaxRate);
        Assert.Null(line.DeliveryDate);
    }
}
