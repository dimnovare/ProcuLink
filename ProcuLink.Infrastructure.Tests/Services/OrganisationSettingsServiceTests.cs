using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Organisation;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class OrganisationSettingsServiceTests
{
    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrgSettingsTestDbContext(options);
    }

    [Fact]
    public async Task GetAsync_NewOrg_ReturnsOutbound()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        var service = new OrganisationSettingsService(db);

        var result = await service.GetAsync(orgId, default);

        result.Direction.Should().Be(OrderDirection.Outbound);
    }

    [Fact]
    public async Task GetAsync_ReturnsOrgSlug()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, slug: "nordic-distribution-ab12");
        var service = new OrganisationSettingsService(db);

        var result = await service.GetAsync(orgId, default);

        result.Slug.Should().Be("nordic-distribution-ab12");
    }

    [Fact]
    public async Task UpdateDirectionAsync_Inbound_Persists()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db);
        var service = new OrganisationSettingsService(db);

        var updated = await service.UpdateDirectionAsync(
            orgId, new UpdateOrderDirectionRequest(OrderDirection.Inbound), default);

        updated.Direction.Should().Be(OrderDirection.Inbound);

        var fetched = await service.GetAsync(orgId, default);
        fetched.Direction.Should().Be(OrderDirection.Inbound);
    }

    [Fact]
    public async Task UpdateDirectionAsync_IsOrgScoped()
    {
        await using var db = CreateDb();
        var orgA = await SeedOrgAsync(db);
        var orgB = await SeedOrgAsync(db);
        var service = new OrganisationSettingsService(db);

        await service.UpdateDirectionAsync(
            orgA, new UpdateOrderDirectionRequest(OrderDirection.Inbound), default);

        (await service.GetAsync(orgA, default)).Direction.Should().Be(OrderDirection.Inbound);
        (await service.GetAsync(orgB, default)).Direction.Should().Be(OrderDirection.Outbound);
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string? slug = null)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Nordic Distribution",
            Slug = slug ?? $"nordic-distribution-{orgId:N}"[..24],
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private sealed class OrgSettingsTestDbContext : ProcuLinkDbContext
    {
        public OrgSettingsTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
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

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.Suppliers);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });
        }
    }
}
