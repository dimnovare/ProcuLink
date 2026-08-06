using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// CONCURRENCY GUARD (verified high finding) — proves on REAL Postgres that two retries for the
/// SAME order racing each other do NOT double-dispatch the PO.
///
/// <para>Before the fix, <c>RetryDeliveryAsync</c> read the order status and the attempt count in
/// SEPARATE queries and then decided — so two concurrent retries could each pass the
/// status / max-attempt check and BOTH dispatch (a double 'delivered', or two clobbering terminal
/// statuses). The fix atomically CLAIMS the order with a single guarded <c>ExecuteUpdateAsync</c>
/// (a deliverable status → <c>delivering</c>) inside one transaction, reading the attempt count in
/// the same transaction: the worker that loses the row-lock race finds the order no longer in a
/// from-failed claimable state and returns without dispatching.</para>
///
/// <para>The atomic claim is untranslatable on EF InMemory (which also cannot model row-lock
/// contention), so this real-Postgres test is the one that exercises the race. Each "worker" gets
/// its own DbContext — a DbContext is not thread-safe — exactly like two parallel Hangfire
/// <c>RetryDeliveryJob</c> activations. Pooling is disabled so the two contexts hold genuinely
/// distinct physical connections that can contend on the row lock. Docker-gated; skips where
/// Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class DeliveryConcurrentRetryPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int MaxAttempts = 3;

    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_deliveryrace");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    [DockerRequiredFact]
    public async Task TwoConcurrentRetries_ForSameFailedOrder_DispatchExactlyOnce()
    {
        var encryption = CreateEncryption();
        var ids = await SeedFailedOrderAsync(encryption);

        // A SHARED dispatcher counts every real dispatch across both workers. A short blocking
        // hold widens the dispatch window so the two RetryDeliveryAsync claim transactions overlap —
        // the loser must be excluded by the atomic claim, not merely by finishing too late to race.
        var dispatcher = new CountingGateDispatcher(
            new DeliveryResult(true, null, 202), holdFor: TimeSpan.FromMilliseconds(400));

        async Task<DeliveryResult> RunRetry()
        {
            await using var db = NewContext();
            var service = CreateService(db, dispatcher, encryption);
            return await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);
        }

        var results = await Task.WhenAll(Task.Run(RunRetry), Task.Run(RunRetry));

        // EXACTLY ONE real dispatch — the heart of the guard (no double-sent PO).
        Assert.Equal(1, dispatcher.Calls);

        // Exactly one of the two callers actually delivered; the other was excluded cleanly.
        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = NewContext();

        // Exactly one terminal SUCCESS attempt row — never two 'delivered' for one order.
        var successAttempts = await verify.DeliveryAttempts
            .CountAsync(a => a.OrderId == ids.OrderId && a.Status == "success");
        Assert.Equal(1, successAttempts);

        var status = await verify.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatusConstants.Delivered, status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    /// <summary>Seeds org + supplier + delivery config + a 'delivery_failed' order with an artifact (parents first — FKs enforced).</summary>
    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderId)> SeedFailedOrderAsync(DeliveryEncryptionService encryption)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id            = orgId,
            ClerkOrgId    = $"org_race_{orgId:N}",
            Name          = "Delivery Race Org",
            Slug          = $"race-{orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Race Supplier", CreatedAt = now });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id                   = Guid.NewGuid(),
            OrgId                = orgId,
            SupplierId           = supplierId,
            Protocol             = "http",
            AutoDeliver          = true,
            ConfigJson           = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt            = now,
            UpdatedAt            = now,
        });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-RACE-1",
            OrderDate  = DateOnly.FromDateTime(now),
            Currency   = "EUR",
            Status     = OrderStatusConstants.DeliveryFailed,
            CreatedAt  = now,
            UpdatedAt  = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id        = Guid.NewGuid(),
            OrderId   = orderId,
            OrgId     = orgId,
            Format    = "csv",
            FileKey   = "artifact.csv",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    /// <summary>
    /// Thread-safe across the two concurrent workers: counts every real dispatch and holds for a
    /// short window so the claim transactions overlap. If the claim worked, only ONE call ever lands.
    /// </summary>
    private sealed class CountingGateDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        private readonly TimeSpan _holdFor;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string Protocol => "http";

        public CountingGateDispatcher(DeliveryResult result, TimeSpan holdFor)
        {
            _result = result;
            _holdFor = holdFor;
        }

        public async Task<DeliveryResult> DispatchAsync(
            byte[] content,
            string fileName,
            string contentType,
            SupplierDeliveryConfig config,
            string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(_holdFor, ct);
            return _result;
        }
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
