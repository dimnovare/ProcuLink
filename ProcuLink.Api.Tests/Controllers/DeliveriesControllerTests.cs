using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Tests for GET /api/orders/{id}/delivery-attempts.
/// Uses an in-memory EF context seeded with DeliveryAttempt rows.
/// </summary>
public class DeliveriesControllerTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static DeliveriesTestDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveriesTestDbContext(opts);
    }

    private static (DeliveriesController Controller, Guid OrgId, ProcuLinkDbContext Db) BuildController(
        ProcuLinkDbContext? db = null)
    {
        db ??= MakeDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new DeliveriesController(db, tenant.Object);
        return (controller, orgId, db);
    }

    // ── Test 1: returns attempts newest-first ──────────────────────────────────

    [Fact]
    public async Task GetDeliveryAttempts_SeedWithTwoAttempts_ReturnsNewestFirst()
    {
        var (controller, orgId, db) = BuildController();
        var orderId = Guid.NewGuid();

        var older = new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "http",
            Destination   = "https://supplier.example.com/orders",
            Status        = "failed",
            AttemptedAt   = new DateTime(2026, 5, 29, 10, 0, 0, DateTimeKind.Utc),
            ResponseCode  = 503,
            ErrorMessage  = "Service Unavailable",
        };

        var newer = new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 2,
            Channel       = "http",
            Destination   = "https://supplier.example.com/orders",
            Status        = "delivered",
            AttemptedAt   = new DateTime(2026, 5, 29, 10, 5, 0, DateTimeKind.Utc),
            ResponseCode  = 200,
            ErrorMessage  = null,
        };

        db.DeliveryAttempts.AddRange(older, newer);
        await db.SaveChangesAsync();

        // act
        var result = await controller.GetDeliveryAttempts(orderId, CancellationToken.None);

        // assert
        var ok      = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos    = ok.Value.Should().BeAssignableTo<IEnumerable<DeliveryAttemptDto>>().Subject.ToList();

        dtos.Should().HaveCount(2);
        // newest-first
        dtos[0].AttemptNumber.Should().Be(2);
        dtos[0].Status.Should().Be("delivered");
        dtos[0].ResponseCode.Should().Be(200);
        dtos[0].ErrorMessage.Should().BeNull();

        dtos[1].AttemptNumber.Should().Be(1);
        dtos[1].Status.Should().Be("failed");
        dtos[1].ResponseCode.Should().Be(503);
        dtos[1].ErrorMessage.Should().Be("Service Unavailable");
    }

    // ── Test 2: org-scoped — excludes rows from another org ───────────────────

    [Fact]
    public async Task GetDeliveryAttempts_CrossTenantRows_AreExcluded()
    {
        var db = MakeDb();
        var orderId    = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();

        // Row belonging to the other org for the same orderId
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = otherOrgId,
            OrderId       = orderId,
            AttemptNumber = 1,
            Channel       = "http",
            Destination   = "https://other-supplier.example.com",
            Status        = "delivered",
            AttemptedAt   = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (controller, _, _) = BuildController(db);

        // act — controller's org is different from otherOrgId
        var result = await controller.GetDeliveryAttempts(orderId, CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeAssignableTo<IEnumerable<DeliveryAttemptDto>>().Subject.ToList();

        dtos.Should().BeEmpty("cross-tenant rows must not be visible");
    }

    // ── Test 3: empty list when no attempts exist ─────────────────────────────

    [Fact]
    public async Task GetDeliveryAttempts_NoAttempts_ReturnsEmptyList()
    {
        var (controller, _, _) = BuildController();

        var result = await controller.GetDeliveryAttempts(Guid.NewGuid(), CancellationToken.None);

        var ok   = result.Should().BeOfType<OkObjectResult>().Subject;
        var dtos = ok.Value.Should().BeAssignableTo<IEnumerable<DeliveryAttemptDto>>().Subject.ToList();

        dtos.Should().BeEmpty();
    }

    // ── Test 4: DTO fields are correctly projected ────────────────────────────

    [Fact]
    public async Task GetDeliveryAttempts_SingleAttempt_FieldsProjectedCorrectly()
    {
        var (controller, orgId, db) = BuildController();
        var orderId     = Guid.NewGuid();
        var attemptedAt = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc);

        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrgId         = orgId,
            OrderId       = orderId,
            AttemptNumber = 3,
            Channel       = "sftp",
            Destination   = "sftp://supplier.example.com/inbox",
            Status        = "failed",
            AttemptedAt   = attemptedAt,
            ResponseCode  = null,
            ErrorMessage  = "Connection refused",
        });
        await db.SaveChangesAsync();

        var result = await controller.GetDeliveryAttempts(orderId, CancellationToken.None);

        var ok  = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeAssignableTo<IEnumerable<DeliveryAttemptDto>>().Subject.Single();

        dto.AttemptNumber.Should().Be(3);
        dto.Channel.Should().Be("sftp");
        dto.Destination.Should().Be("sftp://supplier.example.com/inbox");
        dto.Status.Should().Be("failed");
        dto.AttemptedAt.Should().Be(attemptedAt);
        dto.ResponseCode.Should().BeNull();
        dto.ErrorMessage.Should().Be("Connection refused");
    }

    // ── Minimal in-memory DbContext ───────────────────────────────────────────

    /// <summary>
    /// Strips all entities except DeliveryAttempt to avoid in-memory provider
    /// conflicts with jsonb columns and complex FK chains.
    /// Follows the same pattern used by SuppliersTestDbContext and MappingPreviewTestDbContext.
    /// </summary>
    private sealed class DeliveriesTestDbContext : ProcuLinkDbContext
    {
        public DeliveriesTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<SchemaFingerprint>();

            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
