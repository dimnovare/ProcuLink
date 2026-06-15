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

        Assert.NotNull(results);
        // The supplier rule fails for the unresolved line. (Mandatory invariants also run and pass
        // for this order — qty/price/currency/identifier are all valid — so filter to the rule row.)
        var fail = Assert.Single(results, r => r.Code == "supplierItemCode.required");
        Assert.Equal("fail", fail.Status);
        Assert.Equal(1, fail.LineNumber);
        Assert.All(results.Where(r => InvariantValidator.IsInvariantCode(r.Code)), r => Assert.Equal("pass", r.Status));
    }

    [Fact]
    public async Task ValidateOrder_NoActiveProfile_RunsInvariantsOnly_NeverVacuouslyEmpty()
    {
        // Trust regression guard: an order with NO acceptance profile must NOT yield an empty result
        // set (which the UI rendered as a vacuous green "Passed — meets all acceptance rules").
        // Mandatory invariants ALWAYS run; there are simply no supplier-rule rows.
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
        Assert.NotNull(results);
        Assert.NotEmpty(results);                                                  // invariants ran
        Assert.All(results!, r => Assert.True(InvariantValidator.IsInvariantCode(r.Code))); // only invariants, no supplier rules
    }

    [Fact]
    public async Task ValidateOrder_OrderNotFound_ReturnsNull()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var results = await svc.ValidateOrderAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Null(results);
    }

    [Fact]
    public async Task GetLatestAsync_NoDraftOrActive_ReturnsNull()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var result = await svc.GetLatestAsync(orgId, supplierId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestAsync_OnlyDraftExists_ReturnsDraft()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var draft = await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { RequiredSupplierCode() }, null, CancellationToken.None);

        var result = await svc.GetLatestAsync(orgId, supplierId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(draft.Id, result.Id);
        Assert.Equal("draft", result.Status);
    }

    [Fact]
    public async Task GetLatestAsync_ActiveExists_ReturnsActive()
    {
        var db = MakeDb();
        var svc = new SupplierAcceptanceService(db);
        var orgId = Guid.NewGuid(); var supplierId = Guid.NewGuid();

        var v1 = await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { RequiredSupplierCode() }, null, CancellationToken.None);
        await svc.ActivateVersionAsync(orgId, supplierId, v1.VersionNo, CancellationToken.None);

        // Create a second draft on top of the active version
        await svc.CreateVersionAsync(orgId, supplierId, null, "xml",
            new[] { RequiredSupplierCode() }, null, CancellationToken.None);

        var result = await svc.GetLatestAsync(orgId, supplierId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("active", result.Status);
        Assert.Equal(v1.VersionNo, result.VersionNo);
    }
}
