using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Auth;

/// <summary>
/// Verifies the UNIFIED tenant-resolution path for the API-key auth scheme:
///
///  • <see cref="ApiKeyAuthHandler"/> authenticates a <c>plk_</c> key, then publishes
///    the resolved INTERNAL organisation UUID into <c>HttpContext.Items</c> under the
///    SAME key the JWT path uses — not just the org_id claim.
///  • <see cref="CurrentTenantService"/> (the single resolver shared with JWT
///    controllers) reads that value, so <see cref="IngressController"/> resolves the
///    correct internal org WITHOUT parsing its own claim.
///  • Ingress still works AND stays idempotent when driven through this unified path.
///
/// This is the regression gate for the "two value-spaces" fix: before the change the
/// API-key path used a private org_id claim that bypassed CurrentTenantService.
/// </summary>
public class ApiKeyAuthHandlerTenantUnificationTests
{
    private const string Slug       = "acme-distribution";
    private const string HashSecret = "test-api-key-hash-secret-please-rotate";
    private const string RawKey     = "plk_abcdEXAMPLEKEY1234567890";

    /// <summary>
    /// Options rather than a context, because the handler now takes options and opens its own
    /// short-lived context for the key lookup: that lookup happens during authentication, before any
    /// organisation is known, so it must not consume the request-scoped context that
    /// TenantResolutionMiddleware later arms.
    /// </summary>
    private static DbContextOptions<ProcuLinkDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    /// <summary>Seeds an org + an active API key whose hash matches <see cref="RawKey"/>.</summary>
    private static (Guid OrgId, Guid SupplierId) Seed(ProcuLinkDbContext db, string slug = Slug)
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
        db.TenantApiKeys.Add(new TenantApiKey
        {
            Id             = Guid.NewGuid(),
            OrganisationId = orgId,
            Label          = "Zapier",
            KeyHash        = ApiKeyHasher.ComputeHash(RawKey, HashSecret),
            KeyPrefix      = RawKey[..8],
            IsActive       = true,
            CreatedAt      = DateTime.UtcNow,
        });
        db.SaveChanges();

        return (orgId, supplierId);
    }

    /// <summary>
    /// Builds the real handler over the given context + db, and runs the full
    /// authenticate pipeline (Initialize → AuthenticateAsync).
    /// </summary>
    private static async Task<AuthenticateResult> AuthenticateAsync(
        DbContextOptions<ProcuLinkDbContext> dbOptions, DefaultHttpContext httpContext)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyHashSecret"] = HashSecret,
            })
            .Build();

        var options = new Mock<IOptionsMonitor<ApiKeyAuthOptions>>();
        options.Setup(o => o.Get(It.IsAny<string>())).Returns(new ApiKeyAuthOptions());
        options.Setup(o => o.CurrentValue).Returns(new ApiKeyAuthOptions());

        var handler = new ApiKeyAuthHandler(
            options.Object,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            dbOptions,
            config);

        await handler.InitializeAsync(
            new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthHandler)),
            httpContext);

        return await handler.AuthenticateAsync();
    }

    private static DefaultHttpContext NewRequestContext()
    {
        // Provide a real DI scope factory so the handler's fire-and-forget LastUsedAt
        // update can resolve its own scope (it swallows failures regardless).
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Headers["X-ProcuLink-Key"] = RawKey;
        return ctx;
    }

    [Fact]
    public async Task ApiKeyAuth_PublishesInternalOrgGuid_IntoHttpContextItems()
    {
        var dbOptions = NewDbOptions();
        await using var db = new ProcuLinkDbContext(dbOptions);
        var (orgId, _) = Seed(db);

        var ctx    = NewRequestContext();
        var result = await AuthenticateAsync(dbOptions, ctx);

        Assert.True(result.Succeeded);

        // The unification guarantee: the INTERNAL org Guid is in Items under the
        // shared key — the exact value-space CurrentTenantService reads.
        Assert.True(ctx.Items.TryGetValue(CurrentTenantService.Items.OrganisationId, out var stored));
        Assert.Equal(orgId, Assert.IsType<Guid>(stored));

        // And CurrentTenantService over this context returns that same internal org.
        var tenant = new CurrentTenantService(new HttpContextAccessor { HttpContext = ctx });
        Assert.Equal(orgId, tenant.OrganisationId);
    }

    [Fact]
    public async Task IngressController_ResolvesCorrectInternalOrg_ViaUnifiedPath()
    {
        var dbOptions = NewDbOptions();
        await using var db = new ProcuLinkDbContext(dbOptions);
        var (orgId, supplierId) = Seed(db);

        // 1) Authenticate exactly as the framework would (handler populates Items).
        var ctx = NewRequestContext();
        var auth = await AuthenticateAsync(dbOptions, ctx);
        Assert.True(auth.Succeeded);
        ctx.User = auth.Principal!;

        // 2) Drive the controller off the SAME context via the shared resolver.
        var tenant     = new CurrentTenantService(new HttpContextAccessor { HttpContext = ctx });
        var idempotency = new IdempotencyService(db);
        var controller = new IngressController(db, idempotency, tenant, TestDoubles.PermissiveBilling.Service(), NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };

        // WP-22: the controller claims the Idempotency-Key FIRST and creates under the order id the
        // claim pre-generated, so the created entity's id is whatever the controller supplies — not
        // one the test picks.
        PurchaseOrderEntity Entity(Guid id) => new()
        {
            Id                = id,
            OrgId             = orgId,
            SupplierId        = supplierId,
            PoNumber          = "PO-UNIFIED-001",
            Status            = "pending_review",
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow,
            Lines             = new List<PurchaseOrderLineEntity>(),
            OutboundArtifacts = new List<OutboundArtifact>(),
        };

        // The controller MUST create with the resolved INTERNAL org id.
        Guid? observedOrgId    = null;
        Guid? claimedOrderId   = null;
        var claimedOrders = new Mock<IClaimedOrderCreator>();
        claimedOrders
            .Setup(s => s.CreateClaimedFromParsedOrderAsync(
                It.IsAny<Guid>(), supplierId, It.IsAny<Guid>(), It.IsAny<ExtractedOrder>(),
                "ingress_api", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, Guid, ExtractedOrder, string, string?, CancellationToken>(
                (o, _, id, _, _, _, _) => { observedOrgId = o; claimedOrderId = id; })
            .ReturnsAsync(() => Result<PurchaseOrderEntity>.Ok(Entity(claimedOrderId!.Value)));

        var orders = new Mock<IOrderService>();
        orders
            .Setup(s => s.GetByIdAsync(orgId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => claimedOrderId is null
                ? Result<PurchaseOrderEntity>.Fail("Order not found.")
                : Result<PurchaseOrderEntity>.Ok(Entity(claimedOrderId.Value)));

        var req = new IngressOrderRequest(
            OrderNumber: "PO-UNIFIED-001",
            OrderDate:   new DateOnly(2026, 6, 7),
            Currency:    "EUR",
            Notes:       null,
            SupplierId:  supplierId.ToString(),
            Lines: new List<IngressOrderLine>
            {
                new(BuyerItemCode: "BUY-1", Description: "Widget", Quantity: 5m, Unit: "EA", UnitPrice: 1.25m),
            });

        // Ping resolves the slug off the unified org id.
        Assert.IsType<OkObjectResult>(await controller.Ping(Slug, CancellationToken.None));

        // First POST creates the order under the correct internal org.
        Assert.IsType<OkObjectResult>(
            await controller.ReceiveOrder(Slug, req, orders.Object, claimedOrders.Object, CancellationToken.None));
        Assert.Equal(orgId, observedOrgId);

        // Idempotency still holds through the unified path: a verbatim retry
        // short-circuits and creates no second order.
        var second = await controller.ReceiveOrder(Slug, req, orders.Object, claimedOrders.Object, CancellationToken.None);
        Assert.IsType<OkObjectResult>(second);
        claimedOrders.Verify(
            s => s.CreateClaimedFromParsedOrderAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<ExtractedOrder>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var keyRows = await db.IdempotencyKeys.Where(k => k.OrgId == orgId).ToListAsync();
        Assert.Single(keyRows);
    }

    [Fact]
    public async Task ApiKeyAuth_PrincipalCarriesPerKeySubClaim_ForRateLimitPartitioning()
    {
        // M3 regression (plan 2026-06-12): the rate limiter partitions on the `sub` claim
        // (Program.cs PartitionKey). The ApiKey principal must therefore carry a stable,
        // per-key subject — prefixed so it can never collide with a Clerk user id.
        var dbOptions = NewDbOptions();
        await using var db = new ProcuLinkDbContext(dbOptions);
        Seed(db);
        var keyId = (await db.TenantApiKeys.SingleAsync()).Id;

        var ctx    = NewRequestContext();
        var result = await AuthenticateAsync(dbOptions, ctx);

        Assert.True(result.Succeeded);
        var sub = result.Principal!.FindFirst("sub")?.Value;
        Assert.Equal($"apikey:{keyId}", sub);
    }

    [Fact]
    public async Task IngressController_RejectsSlugMismatch_ViaUnifiedPath()
    {
        var dbOptions = NewDbOptions();
        await using var db = new ProcuLinkDbContext(dbOptions);
        var (orgId, _) = Seed(db, slug: "real-slug");

        var ctx = NewRequestContext();
        var auth = await AuthenticateAsync(dbOptions, ctx);
        Assert.True(auth.Succeeded);
        ctx.User = auth.Principal!;

        var tenant      = new CurrentTenantService(new HttpContextAccessor { HttpContext = ctx });
        var idempotency = new IdempotencyService(db);
        var controller  = new IngressController(db, idempotency, tenant, TestDoubles.PermissiveBilling.Service(), NullLogger<IngressController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };

        // A different slug than the caller's org must be forbidden — the slug guard
        // now compares against the unified internal org id.
        var ping = await controller.Ping("someone-elses-slug", CancellationToken.None);
        Assert.IsType<ForbidResult>(ping);
    }
}
