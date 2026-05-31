using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class SupplierAcceptanceServiceTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AcceptanceRuleInput RequiredSupplierCode() =>
        new("line", "supplierItemCode", "required", null, "error", true);

    [Fact]
    public async Task CreateVersion_FirstVersion_IsVersion1Draft()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var p = await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { RequiredSupplierCode() }, "redacted@example.invalid", CancellationToken.None);

        Assert.Equal(1, p.VersionNo);
        Assert.Equal("draft", p.Status);
        Assert.Single(p.Rules);
    }

    [Fact]
    public async Task CreateVersion_SecondVersion_IncrementsVersionNo()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        await svc.CreateVersionAsync(orgId, supplierId, null, "xml", new[] { RequiredSupplierCode() }, null, CancellationToken.None);
        var p2 = await svc.CreateVersionAsync(orgId, supplierId, null, "xml", new[] { RequiredSupplierCode() }, null, CancellationToken.None);
        Assert.Equal(2, p2.VersionNo);
    }

    [Fact]
    public async Task ActivateVersion_ArchivesPreviousActive()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();
        await svc.CreateVersionAsync(orgId, supplierId, null, "xml", new[] { RequiredSupplierCode() }, null, CancellationToken.None);
        await svc.CreateVersionAsync(orgId, supplierId, null, "xml", new[] { RequiredSupplierCode() }, null, CancellationToken.None);

        await svc.ActivateVersionAsync(orgId, supplierId, 1, CancellationToken.None);
        await svc.ActivateVersionAsync(orgId, supplierId, 2, CancellationToken.None);

        var versions = await svc.ListVersionsAsync(orgId, supplierId, CancellationToken.None);
        Assert.Equal("active",   versions.First(v => v.VersionNo == 2).Status);
        Assert.Equal("archived", versions.First(v => v.VersionNo == 1).Status);
    }

    [Fact]
    public async Task ActivateVersion_NotFound_ReturnsFalse()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var ok = await svc.ActivateVersionAsync(Guid.NewGuid(), Guid.NewGuid(), 99, CancellationToken.None);
        Assert.False(ok);
    }

    [Fact]
    public async Task ValidateOrder_RequiredSupplierItemCode_FailsForUnresolvedLine()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid(); var orderId = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-1",
            Status = "pending_review", Currency = "EUR",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new() { Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1, BuyerItemCode = "B1",
                        SupplierItemCode = null, Quantity = 1, UnitPrice = 1, NeedsReview = true }
            }
        });
        await db.SaveChangesAsync();

        var profile = await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { RequiredSupplierCode() }, null, CancellationToken.None);
        await svc.ActivateVersionAsync(orgId, supplierId, profile.VersionNo, CancellationToken.None);

        var results = await svc.ValidateOrderAsync(orgId, orderId, CancellationToken.None);

        var fail = Assert.Single(results);
        Assert.Equal("fail", fail.Status);
        Assert.Equal(1, fail.LineNumber);
    }

    [Fact]
    public async Task ValidateOrder_NoActiveProfile_ReturnsEmpty()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid(); var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-2",
            Status = "ready", Currency = "EUR", OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var results = await svc.ValidateOrderAsync(orgId, orderId, CancellationToken.None);
        Assert.Empty(results);
    }
}
