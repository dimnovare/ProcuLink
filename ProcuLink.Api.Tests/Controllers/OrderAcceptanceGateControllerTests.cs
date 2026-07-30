using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-17 — the operator's two endpoints, over the REAL gate (no mock): a mocked gate would only
/// prove the controller can map a record to a DTO, which is not a claim worth a test file.
/// </summary>
public sealed class OrderAcceptanceGateControllerTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FixedTenant : ICurrentTenantService
    {
        public FixedTenant(Guid orgId, string user) { OrganisationId = orgId; ClerkUserId = user; }
        public Guid OrganisationId { get; }
        public string ClerkUserId { get; }
    }

    private static OrderAcceptanceGateController Build(ProcuLinkDbContext db, Guid orgId, string user = "user_2opsLead") =>
        new(new AcceptanceGate(db, new SupplierAcceptanceService(db)), new FixedTenant(orgId, user));

    [Fact]
    public async Task Get_reportsTheBlock_inPlainLanguage()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, currency: "USD");

        var response = await Build(db, orgId).Get(orderId, CancellationToken.None);

        var dto = Assert.IsType<OrderAcceptanceGateController.AcceptanceGateDto>(
            Assert.IsType<OkObjectResult>(response).Value);
        Assert.True(dto.Blocked);
        Assert.False(dto.Overridden);
        Assert.Contains("currency must be EUR", Assert.Single(dto.Blockers).Message);
        Assert.Contains("Acme GmbH", dto.Reason!);
    }

    [Fact]
    public async Task Get_forAnUnknownOrder_is404()
    {
        await using var db = NewDb();
        var (orgId, _) = await SeedAsync(db, currency: "USD");

        Assert.IsType<NotFoundResult>(await Build(db, orgId).Get(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Override_recordsTheCallersIdentity_andClearsTheBlock()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, currency: "USD");

        var response = await Build(db, orgId).Override(
            orderId,
            new OrderAcceptanceGateController.AcceptanceOverrideRequest("Supplier confirmed USD, ticket TCK-4412."),
            CancellationToken.None);

        var dto = Assert.IsType<OrderAcceptanceGateController.AcceptanceGateDto>(
            Assert.IsType<OkObjectResult>(response).Value);
        Assert.False(dto.Blocked);
        Assert.True(dto.Overridden);
        // The identity comes from the authenticated caller, never from the request body — an
        // override whose "who" the caller can type is not an audit trail.
        Assert.Equal("user_2opsLead", dto.OverriddenBy);

        var ev = await db.AuditEvents.SingleAsync(a => a.Action == AcceptanceGateAudit.OverriddenAction);
        Assert.Equal("user_2opsLead", ev.Payload!.RootElement.GetProperty(AcceptanceGateAudit.ActorKey).GetString());
    }

    [Fact]
    public async Task Override_withNoReason_is400_andRecordsNothing()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, currency: "USD");

        var response = await Build(db, orgId).Override(
            orderId, new OrderAcceptanceGateController.AcceptanceOverrideRequest(null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Override_onAnOrderNothingBlocks_is409()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedAsync(db, currency: "EUR");

        var response = await Build(db, orgId).Override(
            orderId, new OrderAcceptanceGateController.AcceptanceOverrideRequest("Pre-approving this."), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Override_forAnotherOrgsOrder_is404_andRecordsNothing()
    {
        await using var db = NewDb();
        var (_, orderId) = await SeedAsync(db, currency: "USD");

        var response = await Build(db, Guid.NewGuid(), "user_intruder").Override(
            orderId, new OrderAcceptanceGateController.AcceptanceOverrideRequest("Let me through."), CancellationToken.None);

        Assert.IsType<NotFoundResult>(response);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    private static async Task<(Guid OrgId, Guid OrderId)> SeedAsync(ProcuLinkDbContext db, string currency)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Org", Slug = $"org-{orgId:N}", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-1", BuyerName = "Buyer Ltd", Currency = currency,
            OrderDate = new DateOnly(2026, 7, 30), Status = "ready", CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 1m, Unit = "EA", UnitPrice = 5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "active", CreatedAt = now,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "equals",
                    ExpectedValue = "EUR", Severity = "error", BlockOnFail = false,
                },
            },
        });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }
}
