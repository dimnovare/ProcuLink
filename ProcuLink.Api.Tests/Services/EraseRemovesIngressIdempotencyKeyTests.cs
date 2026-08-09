using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Right-to-erasure completeness for the row NO existing test covers end to end:
/// <c>idempotency_keys</c> written by the REST ingress channel.
///
/// <para>
/// Why this exists. <see cref="DataErasureService"/> deletes idempotency keys with
/// <c>k.OrderId == orderId &amp;&amp; k.OrgId == organisationId</c>, and
/// <c>DataErasureServiceTests</c> proves that predicate — but it proves it against a row the
/// TEST hand-built with exactly those two fields already correct. That is the one shape of
/// this bug a hand-built row can never catch: if the real ingress write path ever stopped
/// populating <c>OrderId</c>/<c>OrgId</c> the way the erase predicate reads them (a
/// pre-generated claim id that the order is never created under, a tenant id from a different
/// value-space, a key bound after the org context changed), the hand-built test stays green
/// and the production key outlives the erased order as an orphan pointer to sensitive content.
/// </para>
///
/// <para>
/// So every key row here is written by <see cref="IngressController.ReceiveOrder"/> through the
/// real <see cref="IdempotencyService"/>, and the order it points at is created by the real
/// <see cref="OrderService"/> — nothing about the linkage is asserted into existence by the test.
/// Both key flavours the channel can produce are covered: the explicit
/// <c>Idempotency-Key</c> header and the payload-derived key used when the header is absent.
/// </para>
/// </summary>
public class EraseRemovesIngressIdempotencyKeyTests
{
    private const string Slug = "acme-distribution";

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// An ingress controller wired the way production wires it: the internal org UUID arrives via
    /// HttpContext.Items (what ApiKeyAuthHandler publishes) and is read back through the shared
    /// <see cref="CurrentTenantService"/>, with the REAL idempotency service over
    /// <paramref name="db"/>. Seeds the org + supplier the slug guard and supplier resolution need.
    /// </summary>
    private static (IngressController Controller, Guid OrgId, Guid SupplierId, OrderService Orders)
        BuildIngress(ProcuLinkDbContext db, string slug = Slug)
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

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim("org_id", orgId.ToString()) }, "ApiKey")),
        };
        httpContext.Items[CurrentTenantService.Items.OrganisationId] = orgId;

        var controller = new IngressController(
            db,
            new IdempotencyService(db),
            new CurrentTenantService(new PinnedHttpContextAccessor(httpContext)),
            PermissiveBilling.Service(),
            NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, orgId, supplierId, OrderListTestSupport.BuildOrderService(db));
    }

    /// <summary>
    /// One accessor per controller. The framework's <see cref="HttpContextAccessor"/> keeps its
    /// context in a STATIC AsyncLocal, so building a second tenant's controller would silently
    /// repoint the first one's tenant at the second org — the two-tenant test below would then
    /// fail on the slug guard rather than on anything it is about.
    /// </summary>
    private sealed class PinnedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static DataErasureService BuildErasure(ProcuLinkDbContext db) =>
        new(db, new Mock<IFileStorageService>().Object, NullLogger<DataErasureService>.Instance);

    private static IngressOrderRequest MakeRequest(Guid supplierId, string poNumber) => new(
        OrderNumber: poNumber,
        OrderDate:   new DateOnly(2026, 6, 6),
        Currency:    "EUR",
        Notes:       null,
        SupplierId:  supplierId.ToString(),
        Lines: new List<IngressOrderLine>
        {
            new(BuyerItemCode: "BUY-1", Description: "Widget", Quantity: 10m, Unit: "EA", UnitPrice: 4.50m),
        });

    private static Guid OrderIdOf(IActionResult result) =>
        (Guid)Assert.IsType<OkObjectResult>(result).Value!.GetType().GetProperty("Id")!
            .GetValue(Assert.IsType<OkObjectResult>(result).Value)!;

    /// <summary>
    /// Pushes one order through the real ingress path and returns the created order id. The
    /// idempotency row is a side effect of THAT call, never seeded here.
    /// </summary>
    private static async Task<Guid> PushOrderAsync(
        IngressController controller, OrderService orders, Guid supplierId,
        string poNumber, string? idempotencyKeyHeader, string slug = Slug)
    {
        if (idempotencyKeyHeader is not null)
            controller.HttpContext.Request.Headers["Idempotency-Key"] = idempotencyKeyHeader;

        var result = await controller.ReceiveOrder(
            slug, MakeRequest(supplierId, poNumber), orders, orders, CancellationToken.None);

        return OrderIdOf(result);
    }

    [Theory]
    // The two key flavours the channel produces: an explicit header, and — header absent — a key
    // derived from the payload. Both must be erasable; only the first is obvious.
    [InlineData("zapier-task-abc-123")]
    [InlineData(null)]
    public async Task EraseOrder_RemovesTheIdempotencyKeyThatIngressActuallyWrote(string? headerKey)
    {
        await using var db = NewDb();
        var (controller, orgId, supplierId, orders) = BuildIngress(db);

        var orderId = await PushOrderAsync(controller, orders, supplierId, "PO-ERASE-001", headerKey);

        // Anti-vacuity floor. If ingress ever stops writing a key row, or writes one the erase
        // predicate cannot see, this fails HERE — the test must never pass because there was
        // nothing to delete. Both fields are asserted against the real order, because both are
        // what the erase predicate matches on.
        var written = await db.IdempotencyKeys.AsNoTracking().ToListAsync();
        written.Should().ContainSingle("ingress must persist exactly one idempotency key per push");
        written[0].OrgId.Should().Be(orgId);
        written[0].OrderId.Should().Be(orderId,
            "the erase matches on OrderId, so the key ingress wrote must point at the created order");
        if (headerKey is not null) written[0].Key.Should().Be(headerKey);

        var result = await BuildErasure(db).EraseOrderAsync(orgId, orderId, CancellationToken.None);

        result.Found.Should().BeTrue();
        result.IdempotencyKeysDeleted.Should().Be(1);
        (await db.IdempotencyKeys.AsNoTracking().AnyAsync()).Should().BeFalse(
            "an erased order must leave no idempotency key pointing at it");
    }

    [Fact]
    public async Task BulkErase_RemovesIngressIdempotencyKeys_AndLeavesOtherOrgsAlone()
    {
        // The bulk path is a separate public entry point, so it gets its own proof rather than
        // inheriting the single-order one. Tenant isolation is asserted with the SAME key string
        // in both orgs — the (org_id, key) primary key means that is a legal collision in
        // production, and it is the case a predicate that dropped its OrgId term would break.
        await using var db = NewDb();
        var (controller, orgId, supplierId, orders) = BuildIngress(db);
        var (otherController, otherOrgId, otherSupplierId, otherOrders) =
            BuildIngress(db, slug: "other-tenant");

        const string sharedKey = "make-com-run-7788";

        var orderId      = await PushOrderAsync(controller, orders, supplierId, "PO-BULK-001", sharedKey);
        var otherOrderId = await PushOrderAsync(
            otherController, otherOrders, otherSupplierId, "PO-BULK-001", sharedKey, slug: "other-tenant");

        (await db.IdempotencyKeys.AsNoTracking().CountAsync()).Should().Be(2);

        var result = await BuildErasure(db).BulkEraseOrdersAsync(
            orgId, new BulkEraseFilter(Ids: new[] { orderId }), CancellationToken.None);

        result.OrdersErased.Should().Be(1);
        result.IdempotencyKeysDeleted.Should().Be(1);

        var surviving = await db.IdempotencyKeys.AsNoTracking().ToListAsync();
        surviving.Should().ContainSingle();
        surviving[0].OrgId.Should().Be(otherOrgId);
        surviving[0].OrderId.Should().Be(otherOrderId);
    }
}
