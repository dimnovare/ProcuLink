using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Support;

internal static class TestDb
{
    public static ProcuLinkDbContext Create()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SampleOrderTestDbContext(opts);
    }
}

internal static class TestSampleOrderService
{
    public static SampleOrderService Create(
        ProcuLinkDbContext db,
        out FakeParseJobEnqueuer enqueuer)
    {
        enqueuer = new FakeParseJobEnqueuer();
        return new SampleOrderService(db, enqueuer, new InMemoryFileStorage(), new NoOpAnalytics());
    }

    public static SampleOrderService Create(ProcuLinkDbContext db) =>
        Create(db, out _);
}

internal sealed class FakeParseJobEnqueuer : IParseJobEnqueuer
{
    public List<(Guid OrderId, Guid OrgId)> Enqueued { get; } = new();

    public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
    {
        Enqueued.Add((orderId, orgId));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryFileStorage : IFileStorageService
{
    public Dictionary<string, byte[]> Files { get; } = new();

    public async Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Files[key] = ms.ToArray();
        return key;
    }

    public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
        Task.FromResult($"http://test.invalid/{key}");

    public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Files.TryGetValue(key, out var bytes) ? bytes : Array.Empty<byte>()));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Files.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class NoOpAnalytics : IAnalyticsService
{
    public Task CaptureAsync(
        Guid organisationId,
        string? userId,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task SetPersonPropertiesAsync(
        string distinctId,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class SampleOrderTestDbContext : ProcuLinkDbContext
{
    public SampleOrderTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<AppUser>();
        modelBuilder.Ignore<Membership>();
        modelBuilder.Ignore<SupplierProfileEntity>();
        modelBuilder.Ignore<PurchaseOrderLineEntity>();
        modelBuilder.Ignore<OutboundArtifact>();
        modelBuilder.Ignore<DeliveryAttempt>();
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

        modelBuilder.Entity<Supplier>(b =>
        {
            b.HasKey(x => x.Id);
            b.Ignore(x => x.Organisation);
            b.Ignore(x => x.SupplierProfiles);
            b.Ignore(x => x.PurchaseOrders);
            b.Ignore(x => x.ItemMappings);
            b.Ignore(x => x.PoMappings);
            b.Ignore(x => x.DeliveryConfigs);
            b.Ignore(x => x.Products);
        });

        // Seeded by SampleOrderService (catalog + 2-of-3 mappings) — mapped standalone,
        // navigations ignored, same style as the other entities in this trimmed context.
        modelBuilder.Entity<ItemMapping>(b =>
        {
            b.HasKey(x => x.Id);
            b.Ignore(x => x.Organisation);
            b.Ignore(x => x.Supplier);
        });

        modelBuilder.Entity<SupplierProduct>(b =>
        {
            b.HasKey(x => x.Id);
            b.Ignore(x => x.Organisation);
            b.Ignore(x => x.Supplier);
        });

        modelBuilder.Entity<PurchaseOrderEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Ignore(x => x.CanonicalJson);
            b.Ignore(x => x.Organisation);
            b.Ignore(x => x.Supplier);
            b.Ignore(x => x.Lines);
            b.Ignore(x => x.OutboundArtifacts);
            b.Ignore(x => x.DeliveryAttempts);
        });
    }
}
