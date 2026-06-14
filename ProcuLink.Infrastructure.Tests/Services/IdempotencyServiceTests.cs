using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Tests the Gap 1 idempotency contract: two uploads with the same key + same
/// org return the same OrderId, the same key + different org returns different
/// OrderIds, and an expired (&gt;24 h) key is treated as new.
/// </summary>
public class IdempotencyServiceTests
{
    private const string Key = "client-key-abc";
    private static readonly DateTimeOffset T0 = new(2026, 5, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameKeyAndOrg_TwoUploads_ReturnSameOrderId()
    {
        await using var db = CreateDb();
        var clock = new MutableClock(T0);
        var service = new IdempotencyService(db, () => clock.Now, TimeSpan.FromHours(24));

        var orgId = Guid.NewGuid();
        var firstOrderId = Guid.NewGuid();

        // First upload: no existing binding, then bind to the new order.
        (await service.TryGetExistingOrderIdAsync(Key, orgId)).Should().BeNull();
        await service.BindAsync(Key, orgId, firstOrderId);

        // Second upload one minute later replays the same key for the same org.
        clock.Now = T0.AddMinutes(1);
        var replayedId = await service.TryGetExistingOrderIdAsync(Key, orgId);

        replayedId.Should().Be(firstOrderId,
            "the second upload with the same key and same org must reuse the first order");
    }

    [Fact]
    public async Task SameKey_DifferentOrgs_ReturnDifferentOrderIds()
    {
        await using var db = CreateDb();
        var clock = new MutableClock(T0);
        var service = new IdempotencyService(db, () => clock.Now, TimeSpan.FromHours(24));

        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var orderForA = Guid.NewGuid();
        var orderForB = Guid.NewGuid();

        await service.BindAsync(Key, orgA, orderForA);
        await service.BindAsync(Key, orgB, orderForB);

        var lookupA = await service.TryGetExistingOrderIdAsync(Key, orgA);
        var lookupB = await service.TryGetExistingOrderIdAsync(Key, orgB);

        lookupA.Should().Be(orderForA);
        lookupB.Should().Be(orderForB);
        lookupA!.Value.Should().NotBe(lookupB!.Value);
    }

    [Fact]
    public async Task ExpiredKey_IsTreatedAsNew()
    {
        await using var db = CreateDb();
        var clock = new MutableClock(T0);
        var service = new IdempotencyService(db, () => clock.Now, TimeSpan.FromHours(24));

        var orgId = Guid.NewGuid();
        var staleOrderId = Guid.NewGuid();

        await service.BindAsync(Key, orgId, staleOrderId);

        // Jump forward past the 24-hour window — the binding should now look
        // expired to the caller (caller decides to create a fresh order).
        clock.Now = T0.AddHours(24).AddSeconds(1);

        var lookup = await service.TryGetExistingOrderIdAsync(Key, orgId);
        lookup.Should().BeNull(
            "an idempotency-key row older than the 24-hour window must be treated as new");

        // After binding to a fresh order id, lookups again return the new id.
        var freshOrderId = Guid.NewGuid();
        await service.BindAsync(Key, orgId, freshOrderId);

        var afterRebind = await service.TryGetExistingOrderIdAsync(Key, orgId);
        afterRebind.Should().Be(freshOrderId);
    }

    [Fact]
    public async Task BlankKey_IsRejected()
    {
        await using var db = CreateDb();
        var service = new IdempotencyService(db, () => T0, TimeSpan.FromHours(24));

        var orgId = Guid.NewGuid();

        (await service.TryGetExistingOrderIdAsync("", orgId)).Should().BeNull();
        (await service.TryGetExistingOrderIdAsync("   ", orgId)).Should().BeNull();

        var act = async () => await service.BindAsync("", orgId, Guid.NewGuid());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new IdempotencyTestDbContext(options);
    }

    /// <summary>
    /// Mutable wrapper so a test can advance the clock between bind and lookup.
    /// </summary>
    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset start) { Now = start; }
        public DateTimeOffset Now { get; set; }
    }

    /// <summary>
    /// Minimal in-memory DbContext that only materialises IdempotencyKey — all
    /// other entities (with their FK requirements) are ignored so the test
    /// doesn't have to fabricate orgs, suppliers, audit events, etc.
    /// </summary>
    private sealed class IdempotencyTestDbContext : ProcuLinkDbContext
    {
        public IdempotencyTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
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

            // IdempotencyKey: keep the composite key, drop nothing else.
            modelBuilder.Entity<IdempotencyKey>(b =>
            {
                b.HasKey(x => new { x.OrgId, x.Key });
            });
        }
    }
}
