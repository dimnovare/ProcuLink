using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class DataErasureServiceTests
{
    private static ProcuLinkDbContext CreateDb() =>
        new ErasureTestDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingFileStorage : IFileStorageService
    {
        public List<string> Deleted { get; } = new();
        public Task DeleteAsync(string key, CancellationToken ct) { Deleted.Add(key); return Task.CompletedTask; }
        public Task<string> UploadAsync(Stream c, string k, string t, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetSignedDownloadUrlAsync(string k, TimeSpan e, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string k, CancellationToken ct) => throw new NotSupportedException();
    }

    // Seeds a full order graph incl. an order confirmation + line (sensitive PO data
    // with its own R2 source) so the erase is exercised end to end. Returns the keys.
    // PoNumber/status/createdAt default sensibly but are overridable for bulk-filter tests.
    private static void SeedOrderGraph(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, string prefix,
        string poNumber = "PO-0001", string status = "delivered", DateTime? createdAt = null)
    {
        var lineId = Guid.NewGuid();
        var confId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity { Id = orderId, OrgId = orgId, PoNumber = poNumber, Status = status, CreatedAt = createdAt ?? DateTime.UtcNow, SourceFileKey = $"{prefix}/source.csv" });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity { Id = lineId, OrderId = orderId });
        db.OutboundArtifacts.Add(new OutboundArtifact { Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId, FileKey = $"{prefix}/out.xml" });
        db.DeliveryAttempts.Add(new DeliveryAttempt { Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId });
        db.OrderExceptions.Add(new OrderException { Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId });
        db.OrderValidationResults.Add(new OrderValidationResult { Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId });
        db.PoPassportEvents.Add(new PoPassportEvent { Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId });
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), OrgId = orgId, EntityType = "order", EntityId = orderId });
        db.OrderConfirmations.Add(new OrderConfirmationEntity { Id = confId, OrgId = orgId, PurchaseOrderId = orderId, SourceFileKey = $"{prefix}/conf.pdf" });
        db.OrderConfirmationLines.Add(new OrderConfirmationLineEntity { Id = Guid.NewGuid(), OrgId = orgId, OrderConfirmationId = confId, PurchaseOrderLineId = lineId });
    }

    [Fact]
    public async Task EraseOrderAsync_DeletesFullOrderGraphAndR2_OrgScoped_LeavesOtherOrgUntouched()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();

        var org1 = Guid.NewGuid(); var order1 = Guid.NewGuid();
        var org2 = Guid.NewGuid(); var order2 = Guid.NewGuid();
        SeedOrderGraph(db, org1, order1, "org1/order1");
        SeedOrderGraph(db, org2, order2, "org2/order2");
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        var result = await service.EraseOrderAsync(org1, order1, default);

        result.Found.Should().BeTrue();
        result.R2ObjectsDeleted.Should().Be(3);              // source + artifact + confirmation
        result.LinesDeleted.Should().Be(1);
        result.ArtifactsDeleted.Should().Be(1);
        result.DeliveryAttemptsDeleted.Should().Be(1);
        result.ExceptionsDeleted.Should().Be(1);
        result.ValidationResultsDeleted.Should().Be(1);
        result.PassportEventsDeleted.Should().Be(1);
        result.AuditEventsDeleted.Should().Be(1);
        result.ConfirmationsDeleted.Should().Be(1);
        result.ConfirmationLinesDeleted.Should().Be(1);

        storage.Deleted.Should().BeEquivalentTo(new[]
        {
            "org1/order1/source.csv", "org1/order1/out.xml", "org1/order1/conf.pdf",
        });

        // Order 1's entire graph (incl. confirmations) is gone.
        (await db.PurchaseOrders.AnyAsync(o => o.Id == order1)).Should().BeFalse();
        (await db.PurchaseOrderLines.AnyAsync(l => l.OrderId == order1)).Should().BeFalse();
        (await db.OutboundArtifacts.AnyAsync(a => a.OrderId == order1)).Should().BeFalse();
        (await db.DeliveryAttempts.AnyAsync(a => a.OrderId == order1)).Should().BeFalse();
        (await db.OrderExceptions.AnyAsync(e => e.OrderId == order1)).Should().BeFalse();
        (await db.OrderValidationResults.AnyAsync(v => v.OrderId == order1)).Should().BeFalse();
        (await db.PoPassportEvents.AnyAsync(p => p.OrderId == order1)).Should().BeFalse();
        (await db.AuditEvents.AnyAsync(e => e.EntityId == order1)).Should().BeFalse();
        (await db.OrderConfirmations.AnyAsync(c => c.PurchaseOrderId == order1)).Should().BeFalse();
        (await db.OrderConfirmationLines.AnyAsync(cl => cl.OrgId == org1)).Should().BeFalse();

        // Tenant isolation: org 2's graph is completely untouched.
        (await db.PurchaseOrders.AnyAsync(o => o.Id == order2)).Should().BeTrue();
        (await db.OrderConfirmations.AnyAsync(c => c.PurchaseOrderId == order2)).Should().BeTrue();
        (await db.AuditEvents.AnyAsync(e => e.EntityId == order2)).Should().BeTrue();
        storage.Deleted.Should().NotContain(k => k.StartsWith("org2/"));
    }

    [Fact]
    public async Task EraseOrderAsync_WrongOrg_IsNoOp_DoesNotDeleteAcrossTenant()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();
        var org1 = Guid.NewGuid(); var order1 = Guid.NewGuid();
        SeedOrderGraph(db, org1, order1, "org1/order1");
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        var result = await service.EraseOrderAsync(Guid.NewGuid(), order1, default);

        result.Found.Should().BeFalse();
        storage.Deleted.Should().BeEmpty();
        (await db.PurchaseOrders.AnyAsync(o => o.Id == order1)).Should().BeTrue();
        (await db.OrderConfirmations.AnyAsync(c => c.PurchaseOrderId == order1)).Should().BeTrue();
    }

    [Fact]
    public async Task EraseOrderAsync_UnknownOrder_IsIdempotentNoOp()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();
        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);

        var result = await service.EraseOrderAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        result.Found.Should().BeFalse();
        storage.Deleted.Should().BeEmpty();
    }

    // ── bulk erase ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkEraseOrdersAsync_PoNumberPrefix_ErasesOnlyMatching_OrgScoped()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();

        var org1 = Guid.NewGuid(); var org2 = Guid.NewGuid();
        var t1 = Guid.NewGuid(); var t2 = Guid.NewGuid();
        var keep = Guid.NewGuid(); var otherOrg = Guid.NewGuid();
        SeedOrderGraph(db, org1, t1,   "org1/t1",   poNumber: "TEST-001");
        SeedOrderGraph(db, org1, t2,   "org1/t2",   poNumber: "TEST-002");
        SeedOrderGraph(db, org1, keep, "org1/keep", poNumber: "KEEP-001");
        SeedOrderGraph(db, org2, otherOrg, "org2/t", poNumber: "TEST-999"); // matches prefix but WRONG org
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        var result = await service.BulkEraseOrdersAsync(org1, new BulkEraseFilter(PoNumberPrefix: "TEST-"), default);

        result.OrdersErased.Should().Be(2, "only org1's two TEST- orders match");
        (await db.PurchaseOrders.AnyAsync(o => o.Id == t1)).Should().BeFalse();
        (await db.PurchaseOrders.AnyAsync(o => o.Id == t2)).Should().BeFalse();
        (await db.PurchaseOrders.AnyAsync(o => o.Id == keep)).Should().BeTrue("KEEP- does not match the prefix");
        (await db.PurchaseOrders.AnyAsync(o => o.Id == otherOrg)).Should().BeTrue("org2 is never touched");
        storage.Deleted.Should().NotContain(k => k.StartsWith("org2/"));
    }

    [Fact]
    public async Task BulkEraseOrdersAsync_OrgGuard_IdsFromAnotherOrgAreNeverErased()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();

        var org1 = Guid.NewGuid(); var org2 = Guid.NewGuid();
        var org2Order = Guid.NewGuid();
        SeedOrderGraph(db, org2, org2Order, "org2/o", poNumber: "PO-1");
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        // Caller is org1 but tries to target org2's order id explicitly.
        var result = await service.BulkEraseOrdersAsync(org1, new BulkEraseFilter(Ids: new[] { org2Order }), default);

        result.OrdersErased.Should().Be(0);
        (await db.PurchaseOrders.AnyAsync(o => o.Id == org2Order)).Should().BeTrue("the org guard prevents cross-tenant erase");
        storage.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkEraseOrdersAsync_EmptyFilter_MatchesNothing_NeverWipesOrg()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();
        var org1 = Guid.NewGuid(); var o1 = Guid.NewGuid();
        SeedOrderGraph(db, org1, o1, "org1/o1", poNumber: "PO-1");
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        var result = await service.BulkEraseOrdersAsync(org1, new BulkEraseFilter(), default);

        result.OrdersErased.Should().Be(0, "an empty filter must NEVER mass-wipe the org");
        (await db.PurchaseOrders.AnyAsync(o => o.Id == o1)).Should().BeTrue();
    }

    [Fact]
    public async Task BulkEraseOrdersAsync_BlankPrefix_MatchesNothing()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();
        var org1 = Guid.NewGuid(); var o1 = Guid.NewGuid();
        SeedOrderGraph(db, org1, o1, "org1/o1", poNumber: "PO-1");
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        // An empty/whitespace prefix must NOT StartsWith-match every order.
        var result = await service.BulkEraseOrdersAsync(org1, new BulkEraseFilter(PoNumberPrefix: "   "), default);

        result.OrdersErased.Should().Be(0);
        (await db.PurchaseOrders.AnyAsync(o => o.Id == o1)).Should().BeTrue();
    }

    [Fact]
    public async Task BulkEraseOrdersAsync_StatusFilter_ErasesMatching_AndSumsChildCounts()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();
        var org1 = Guid.NewGuid();
        var d1 = Guid.NewGuid(); var d2 = Guid.NewGuid(); var pending = Guid.NewGuid();
        SeedOrderGraph(db, org1, d1, "org1/d1", poNumber: "PO-1", status: "delivered");
        SeedOrderGraph(db, org1, d2, "org1/d2", poNumber: "PO-2", status: "delivered");
        SeedOrderGraph(db, org1, pending, "org1/p", poNumber: "PO-3", status: "pending_parse");
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        var result = await service.BulkEraseOrdersAsync(org1, new BulkEraseFilter(Status: "delivered"), default);

        result.OrdersErased.Should().Be(2);
        result.LinesDeleted.Should().Be(2, "each erased order graph has one line — counts are summed");
        result.R2ObjectsDeleted.Should().Be(6, "3 blobs per order × 2 orders");
        result.ConfirmationsDeleted.Should().Be(2);
        (await db.PurchaseOrders.AnyAsync(o => o.Id == pending)).Should().BeTrue("the pending order does not match the status filter");
    }

    [Fact]
    public async Task BulkEraseOrdersAsync_OlderThan_ErasesOnlyOlderOrders()
    {
        await using var db = CreateDb();
        var storage = new RecordingFileStorage();
        var org1 = Guid.NewGuid();
        var old1 = Guid.NewGuid(); var recent = Guid.NewGuid();
        SeedOrderGraph(db, org1, old1,   "org1/old", poNumber: "PO-1", createdAt: DateTime.UtcNow.AddDays(-40));
        SeedOrderGraph(db, org1, recent, "org1/new", poNumber: "PO-2", createdAt: DateTime.UtcNow.AddDays(-1));
        await db.SaveChangesAsync();

        var service = new DataErasureService(db, storage, NullLogger<DataErasureService>.Instance);
        var result = await service.BulkEraseOrdersAsync(
            org1, new BulkEraseFilter(OlderThan: DateTime.UtcNow.AddDays(-7)), default);

        result.OrdersErased.Should().Be(1);
        (await db.PurchaseOrders.AnyAsync(o => o.Id == old1)).Should().BeFalse();
        (await db.PurchaseOrders.AnyAsync(o => o.Id == recent)).Should().BeTrue();
    }

    private sealed class ErasureTestDbContext : ProcuLinkDbContext
    {
        public ErasureTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Ignore everything the erase service does NOT touch.
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
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

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });
            modelBuilder.Entity<PurchaseOrderLineEntity>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Order); });
            modelBuilder.Entity<OutboundArtifact>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Order); b.Ignore(x => x.Organisation); });
            modelBuilder.Entity<DeliveryAttempt>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Order); b.Ignore(x => x.Organisation); });
            modelBuilder.Entity<OrderException>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Organisation); });
            modelBuilder.Entity<OrderValidationResult>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Organisation); });
            modelBuilder.Entity<PoPassportEvent>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Organisation); });
            modelBuilder.Entity<AuditEvent>(b => { b.HasKey(x => x.Id); b.Ignore(x => x.Organisation); b.Ignore(x => x.Payload); });
            modelBuilder.Entity<OrderConfirmationEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.PurchaseOrder);
                b.Ignore(x => x.Lines);
            });
            modelBuilder.Entity<OrderConfirmationLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.OrderConfirmation);
                b.Ignore(x => x.PurchaseOrderLine);
            });
        }
    }
}
