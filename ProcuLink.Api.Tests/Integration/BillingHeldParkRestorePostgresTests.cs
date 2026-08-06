using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Queue item 4 (2026-07-23 open-queue handover) proven on REAL Postgres: a billing hold placed on
/// a PARKED order (<c>delivery_unconfirmed</c>) records where it came from
/// (<c>PurchaseOrderEntity.HeldFromStatus</c>, migration <c>AddPurchaseOrderHeldFromStatus</c>) and
/// the billing release RESTORES the park — it never auto re-sends it. Real Postgres matters twice
/// here: the new column must round-trip through the actual migration (EF InMemory masks a missing
/// or mis-mapped column entirely), and the release's tracked writes must persist through Npgsql the
/// way the InMemory emulation claims they do.
///
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class BillingHeldParkRestorePostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_heldpark");

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
    public async Task HeldPark_ColumnRoundTrips_AndReleaseRestoresTheParkWithoutReDrive()
    {
        var encryption = CreateEncryption();
        var orgId = await SeedOrgAsync();
        // The park: an operator's "Send again" hit the billing gate while the org was lapsed.
        // Seeded with the row shape ParkUnconfirmedAsync leaves behind (terminal 'unconfirmed'
        // attempt keeping its pre-send IdempotencyKey).
        var parkId = await SeedOrderAsync(orgId, OrderStatusConstants.DeliveryUnconfirmed, withParkedAttempt: true);
        // A genuine send-ready hold from the same org — the release must keep re-driving it.
        var genuineId = await SeedOrderAsync(orgId, OrderStatusConstants.ReadyToDeliver, withParkedAttempt: false);

        // Hold both, exactly as DeliverOrderJob's billing gate does.
        await using (var db = NewContext())
        {
            var svc = BuildService(db, encryption, enqueuer: null);
            Assert.True(await svc.HoldForBillingAsync(orgId, parkId, CancellationToken.None));
            Assert.True(await svc.HoldForBillingAsync(orgId, genuineId, CancellationToken.None));
        }

        // The origin must survive a real Postgres round trip — this is what the migration exists
        // for, and what InMemory cannot prove.
        await using (var verify = NewContext())
        {
            var park = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == parkId);
            Assert.Equal(OrderStatusConstants.DeliveryHeld, park.Status);
            Assert.Equal(OrderStatusConstants.DeliveryUnconfirmed, park.HeldFromStatus);
            Assert.Null(park.DeliveryDueAt);

            var genuine = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == genuineId);
            Assert.Equal(OrderStatusConstants.DeliveryHeld, genuine.Status);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, genuine.HeldFromStatus);
        }

        // Billing recovers → release. The park is restored for its human; only the genuine hold
        // re-drives.
        var enqueuer = new RecordingRetryEnqueuer();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, encryption, enqueuer);
            var released = await svc.ReleaseBillingHeldOrdersAsync(orgId, CancellationToken.None);
            Assert.Equal(2, released);
        }

        await using (var verify = NewContext())
        {
            var park = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == parkId);
            Assert.Equal(OrderStatusConstants.DeliveryUnconfirmed, park.Status);
            Assert.Null(park.HeldFromStatus);
            // ParkUnconfirmedAsync's contract: a park keeps nagging until a human acts — the
            // restore reopens the SLA window the hold paused.
            Assert.NotNull(park.DeliveryDueAt);
            Assert.False(park.SlaBreached);

            var genuine = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == genuineId);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, genuine.Status);
            Assert.Null(genuine.HeldFromStatus);
        }

        Assert.Equal((genuineId, orgId), Assert.Single(enqueuer.Calls));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingRetryEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDispatcher : IDeliveryDispatcher
    {
        public string Protocol => "http";
        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null)
            => Task.FromResult(new DeliveryResult(true, null, 200));
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class InMemoryFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static DeliveryService BuildService(
        ProcuLinkDbContext db, DeliveryEncryptionService encryption, IRetryDeliveryEnqueuer? enqueuer) =>
        new(
            db,
            new InMemoryFileStorage(),
            encryption,
            new IDeliveryDispatcher[] { new NoOpDispatcher() },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            retryEnqueuer: enqueuer);

    private async Task<Guid> SeedOrgAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_hp_{orgId:N}", Name = "Held-Park Org",
            Slug = $"hp-{orgId:N}", Plan = "operations", AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private async Task<Guid> SeedOrderAsync(Guid orgId, string status, bool withParkedAttempt)
    {
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = $"PO-HP-{status}", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = status, CreatedAt = now, UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = now,
        });
        if (withParkedAttempt)
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
                Channel = "email", Destination = "orders@supplier.example",
                Status = DeliveryAttempt.StatusUnconfirmed, AttemptNumber = 1,
                AttemptedAt = now, IdempotencyKey = $"park-{orderId:N}",
            });
        }
        await db.SaveChangesAsync();
        return orderId;
    }
}
