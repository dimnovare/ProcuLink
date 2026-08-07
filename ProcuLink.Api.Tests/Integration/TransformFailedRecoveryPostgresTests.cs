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
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The RECOVERY door for a failed transform: POST /api/orders/{id}/transform on REAL Postgres —
/// the endpoint's atomic status claim uses <c>ExecuteUpdateAsync</c>, which EF InMemory cannot
/// translate, so this is the only place the claim's real predicate can be proven.
///
/// <para><b>Why this matters.</b> A terminal transform failure now parks the order in
/// <c>transform_failed</c> so it is VISIBLE (ops health, exceptions) instead of reverting to
/// <c>ready</c>, where it was indistinguishable from "never transformed". That is only safe if the
/// order can get back OUT: nothing else resets the status for the user — the MV-1 "a mapping edit
/// invalidates the artifact" reset in <c>OrderMappingOverrideService</c> fires only for post-artifact
/// states, and a failed transform produced no artifact. So if this endpoint's claim stayed keyed on
/// <c>ready</c> alone, a user who fixed their template would get 409 forever and the order would be
/// PERMANENTLY stuck — strictly worse than the silent strand the status exists to expose.</para>
///
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class TransformFailedRecoveryPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_tfrecover");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    /// <summary>
    /// Controller wiring (mirrors the existing OrdersController Postgres harness). The IOrderService
    /// is mocked to serve the pre-flight GetByIdAsync from the REAL row — the endpoint under test is
    /// the atomic claim + enqueue, not the transform itself (which runs later, in the Hangfire job).
    /// </summary>
    private static (OrdersController Ctrl, Mock<IBackgroundJobClient> Jobs) BuildController(
        ProcuLinkDbContext db, Guid orgId, PurchaseOrderEntity order)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderService>();
        orders
            .Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var billing = new Mock<IBillingService>();
        billing
            .Setup(b => b.CheckOrderLimitAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LimitCheckResult(Allowed: true, PilotExpired: false, Plan: "operations", Limit: 1000));

        var jobs = new Mock<IBackgroundJobClient>();

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            jobs.Object,
            db,
            NullLogger<OrdersController>.Instance,
            billing.Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());

        return (ctrl, jobs);
    }

    private async Task<(Guid OrgId, Guid OrderId, PurchaseOrderEntity Order)> SeedOrderAsync(string status)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_tfr_{orgId:N}", Name = "Recovery Org",
            Slug = $"tfr-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = now });
        await db.SaveChangesAsync();

        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-TFR-1", BuyerName = "Acme Buyer",
            OrderDate = new DateOnly(2026, 6, 1), Currency = "EUR",
            Status = status, CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (orgId, orderId, order);
    }

    [DockerRequiredFact]
    public async Task Transform_OnATransformFailedOrder_IsAccepted_AndReClaimsIt()
    {
        var (orgId, orderId, order) = await SeedOrderAsync(OrderStatusConstants.TransformFailed);

        await using var db = NewContext();
        var (ctrl, jobs) = BuildController(db, orgId, order);

        var response = await ctrl.Transform(orderId, new TransformRequest("csv"), CancellationToken.None);

        // 202, NOT 409: the user fixed the template and asked for a re-transform.
        Assert.IsType<AcceptedResult>(response);

        // The claim actually moved the row and a job was enqueued to do the work.
        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatusConstants.Transforming, status);
        jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Once);
    }

    [DockerRequiredFact]
    public async Task Transform_OnAnUnclaimableStatus_StillConflicts()
    {
        // The claim widened by exactly ONE status. A genuinely non-transformable order (already
        // delivered — re-transforming would mint a second artifact for a shipped PO) must still 409.
        var (orgId, orderId, order) = await SeedOrderAsync(OrderStatusConstants.Delivered);

        await using var db = NewContext();
        var (ctrl, jobs) = BuildController(db, orgId, order);

        var response = await ctrl.Transform(orderId, new TransformRequest("csv"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response);

        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatusConstants.Delivered, status); // untouched
        jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Never);
    }
}
