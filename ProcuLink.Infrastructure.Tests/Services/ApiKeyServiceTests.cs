using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class ApiKeyServiceTests
{
    private static ProcuLinkDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApiKeyTestDbContext(opts);
    }

    [Fact]
    public async Task CreateAsync_ReturnsRawKeyAndEntityWithPrefix()
    {
        var db  = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "c_test", Name = "Test", Slug = "test-0001"
        });
        await db.SaveChangesAsync();

        var (entity, rawKey) = await svc.CreateAsync(orgId, "Zapier prod", null, default);

        rawKey.Should().StartWith("plk_");
        entity.KeyPrefix.Should().Be(rawKey[..8]);
        entity.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAsync_SetsIsActiveFalse()
    {
        var db  = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = "c2", Name = "T2", Slug = "t2-0002"
        });
        await db.SaveChangesAsync();

        var (entity, _) = await svc.CreateAsync(orgId, "temp", null, default);
        var ok = await svc.RevokeAsync(orgId, entity.Id, default);

        ok.Should().BeTrue();
        var loaded = await db.TenantApiKeys.FindAsync(entity.Id);
        loaded!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_WrongOrg_ReturnsFalse()
    {
        var db = MakeDb();
        var svc = new ApiKeyService(db);
        var orgId1 = Guid.NewGuid();
        var orgId2 = Guid.NewGuid();
        db.Organisations.Add(new Organisation { Id = orgId1, ClerkOrgId = "a1", Name = "A1", Slug = "a1-aaaa" });
        db.Organisations.Add(new Organisation { Id = orgId2, ClerkOrgId = "b2", Name = "B2", Slug = "b2-bbbb" });
        await db.SaveChangesAsync();

        var (entity, _) = await svc.CreateAsync(orgId1, "key", null, default);
        var ok = await svc.RevokeAsync(orgId2, entity.Id, default);

        ok.Should().BeFalse();
        var loaded = await db.TenantApiKeys.FindAsync(entity.Id);
        loaded!.IsActive.Should().BeTrue();
    }

    /// <summary>
    /// Minimal in-memory DbContext that only materialises the entities needed
    /// for ApiKeyService tests. All other entities (including those with JsonDocument
    /// properties unsupported by the InMemory provider) are ignored.
    /// </summary>
    private sealed class ApiKeyTestDbContext : ProcuLinkDbContext
    {
        public ApiKeyTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Ignore entities that use JsonDocument (unsupported by InMemory provider).
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
            modelBuilder.Ignore<AiUsageMonthly>();
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

            // Configure Organisation minimally (no JsonDocument navigations).
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
            });

            // Configure TenantApiKey with its FK to Organisation.
            modelBuilder.Entity<TenantApiKey>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.KeyHash).IsUnique();
                b.HasOne(k => k.Organisation)
                 .WithMany(o => o.ApiKeys)
                 .HasForeignKey(k => k.OrganisationId);
            });

            // IntegrationSubscription — not needed for ApiKeyService tests, but
            // referenced by Organisation.IntegrationSubscriptions navigation.
            modelBuilder.Entity<IntegrationSubscription>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasOne(s => s.Organisation)
                 .WithMany(o => o.IntegrationSubscriptions)
                 .HasForeignKey(s => s.OrganisationId);
            });
        }
    }
}
