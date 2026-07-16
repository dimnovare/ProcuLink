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
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// A3 (P2) — crash-after-ACK double delivery. On REAL Postgres, reproduce the post-crash state: an
/// order stuck in <c>delivering</c> (its <c>UpdatedAt</c> aged past the reclaim window, exactly as
/// <c>StuckDeliveryDetectionService</c> would re-drive it) with a committed but never-finalised
/// <c>dispatching</c> attempt row — the marker written just BEFORE the supplier ACK that the crash
/// lost. The stuck re-drive must RE-ADOPT that in-flight row and finalise it — never manufacture a
/// SECOND terminal attempt row.
///
/// <para>READ THIS BEFORE TRUSTING THE NAME: the re-drive DOES send to the supplier again — this
/// test asserts that it does (<c>dispatcher.Calls == 1</c> on the re-drive). What it pins is that
/// the second WIRE SEND carries the SAME deterministic idempotency key and collapses back into the
/// one in-flight row, so it can never become a second terminal attempt or a second billable
/// delivery record. It does NOT establish that the SUPPLIER receives the PO only once: nothing here
/// can distinguish "crashed before the send" from "supplier already ACKed", so delivery is
/// AT-LEAST-ONCE and duplicate-order suppression rests on the supplier honouring the idempotency key
/// (http), an overwriting upload (sftp/ftps), or the receiving MTA (email). The erp_* channels have
/// no such mitigation. A green test here is NOT duplicate-PO safety.</para>
///
/// <para>The stale-<c>delivering</c> reclaim is an atomic guarded <c>ExecuteUpdateAsync</c> that is
/// untranslatable on EF InMemory, so this real-Postgres test is the one that exercises the actual
/// crash-recovery claim path. Docker-gated; skips where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class DeliveryCrashRecoveryPostgresTests : IAsyncLifetime
{
    private const int MaxAttempts = 3;

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_crashrec_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    [DockerRequiredFact]
    public async Task StuckDeliveringWithInFlightAttempt_ReDrive_ReAdoptsRow_NoSecondAttemptRow()
    {
        var encryption = CreateEncryption();
        var ids = await SeedStuckDeliveringOrderAsync(encryption);

        var dispatcher = new CapturingKeyDispatcher(new DeliveryResult(true, null, 202));

        await using (var db = NewContext())
        {
            var service = CreateService(db, dispatcher, encryption);
            var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);
            Assert.True(result.Success);
        }

        // The re-send carried the SAME deterministic idempotency key the crashed attempt used.
        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(ids.ExpectedKey, dispatcher.LastIdempotencyKey);

        await using var verify = NewContext();

        // Exactly ONE attempt row — the in-flight 'dispatching' row was re-adopted + finalised, not
        // duplicated: a stuck re-drive can never produce a second 'delivered' for the same PO.
        var attempts = await verify.DeliveryAttempts
            .Where(a => a.OrderId == ids.OrderId)
            .ToListAsync();
        Assert.Single(attempts);
        Assert.Equal(DeliveryAttempt.StatusSuccess, attempts[0].Status);
        Assert.Equal(1, attempts[0].AttemptNumber);
        Assert.Equal(ids.ExpectedKey, attempts[0].IdempotencyKey);

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

    /// <summary>
    /// Seeds the exact post-crash state: a stale 'delivering' order + artifact + a committed
    /// 'dispatching' attempt row carrying the deterministic idempotency key.
    /// </summary>
    private async Task<(Guid OrgId, Guid OrderId, string ExpectedKey)> SeedStuckDeliveringOrderAsync(
        DeliveryEncryptionService encryption)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // Mirrors DeliveryService.BuildIdempotencyKey (internal to Infrastructure).
        var expectedKey = $"plk-dlv-{orderId:N}-{artifactId:N}";

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_crash_{orgId:N}",
            Name = "Crash Recovery Org",
            Slug = $"crash-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Crash Supplier", CreatedAt = now });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-CRASH-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            // Stuck 'delivering', aged well past the 2-minute reclaim window → the sweep re-drives it.
            Status = OrderStatusConstants.Delivering,
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-30),
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = orgId,
            Format = "csv",
            FileKey = "artifact.csv",
            CreatedAt = now,
        });
        // The in-flight marker: committed just before the supplier ACK that the crash lost.
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            OrgId = orgId,
            Channel = "http",
            Destination = "https://supplier.example/orders",
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 1,
            AttemptedAt = now.AddMinutes(-30),
            IdempotencyKey = expectedKey,
        });
        await db.SaveChangesAsync();

        return (orgId, orderId, expectedKey);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private sealed class CapturingKeyDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastIdempotencyKey { get; private set; }
        public string Protocol => "http";

        public CapturingKeyDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null)
        {
            Interlocked.Increment(ref _calls);
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(_result);
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
