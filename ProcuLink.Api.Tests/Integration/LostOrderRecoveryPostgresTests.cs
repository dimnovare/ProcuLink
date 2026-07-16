using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
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
/// B1 + B2 (silent lost-order paths) proven on REAL Postgres, where <c>DispatchArtifactAsync</c>'s
/// atomic ready_to_deliver / delivery_failed / stale-delivering → delivering claim actually runs (the
/// EF InMemory provider can't translate it).
///
/// <list type="bullet">
/// <item><b>B1</b> — an order left in <c>ready_to_deliver</c> with an artifact and no delivery attempt
/// (delivery enqueue lost after the transform commit) is recovered by the stranded-ready sweep, and the
/// recovered delivery's claim SUCCEEDS and dispatches to the supplier exactly once — it is NOT stranded.</item>
/// <item><b>B2</b> — the ops requeue-delivery escalation on a dead-lettered order leaves it in a
/// CLAIMABLE status with the attempt cap reset, so the re-enqueued <c>DeliverOrderJob</c>'s claim
/// SUCCEEDS and actually dispatches (not the benign no-op the old fresh-'delivering' pre-flip produced).</item>
/// </list>
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class LostOrderRecoveryPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_lost_{Guid.NewGuid():N}")
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

    // ── B1: stranded ready_to_deliver → sweep recovers → claim SUCCEEDS + one dispatch ─────────

    [DockerRequiredFact]
    public async Task B1_StrandedReadyToDeliver_Sweep_RecoversAndDispatchesExactlyOnce()
    {
        var encryption = CreateEncryption();
        // Aged ready_to_deliver order with an artifact + auto-deliver config, NO delivery attempt:
        // the exact B1 strand (its DeliverOrderJob enqueue was lost).
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.ReadyToDeliver, agedMinutes: 45);

        // The sweep finds the strand and hands it to the dispatch enqueuer.
        var recorder = new RecordingDispatchEnqueuer();
        await using (var db = NewContext())
        {
            var sweep = new StrandedReadyOrderDetectionService(
                db, NullLogger<StrandedReadyOrderDetectionService>.Instance, recorder);
            var acted = await sweep.RunAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
            Assert.Equal(1, acted);
        }

        // It enqueued exactly this order + artifact.
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((ids.OrderId, ids.OrgId, ids.ArtifactId), call);

        // Simulate the DeliverOrderJob the adapter would enqueue: on REAL Postgres the claim must
        // SUCCEED (ready_to_deliver is claimable) and dispatch — the order is NOT stranded.
        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, CancellationToken.None);
            Assert.True(result.Success);
        }

        Assert.Equal(1, dispatcher.Calls); // claim succeeded → dispatched exactly once
        await using (var verify = NewContext())
        {
            var attempts = await verify.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId).ToListAsync();
            Assert.Equal("success", Assert.Single(attempts).Status);
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.Delivered, status);
        }
    }

    // ── B2: ops requeue on a dead-lettered order → claim SUCCEEDS + attempts reset ─────────────

    [DockerRequiredFact]
    public async Task B2_OpsRequeue_OnDeadLetteredOrder_ClaimSucceeds_AttemptsReset_Dispatches()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.DeliveryDeadLetter, agedMinutes: 5);

        // Order is at the dead-letter cap: seed 3 prior failed attempts.
        await using (var seed = NewContext())
        {
            for (var i = 1; i <= 3; i++)
                seed.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = "failed", AttemptNumber = i,
                    AttemptedAt = DateTime.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        // Drive the REAL OpsController.RequeueDelivery against Postgres.
        await using (var db = NewContext())
        {
            var orderForValidation = await db.PurchaseOrders.AsNoTracking()
                .Include(o => o.OutboundArtifacts)
                .FirstAsync(o => o.Id == ids.OrderId);

            var tenant = new Mock<ICurrentTenantService>();
            tenant.SetupGet(t => t.OrganisationId).Returns(ids.OrgId);
            var orders = new Mock<IOrderService>();
            orders.Setup(o => o.GetByIdAsync(ids.OrgId, ids.OrderId, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(orderForValidation));
            var jobs = new Mock<Hangfire.IBackgroundJobClient>();
            jobs.Setup(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
                .Returns(Guid.NewGuid().ToString());

            var ctrl = new OpsController(
                new Mock<IOpsHealthService>().Object, tenant.Object, orders.Object,
                jobs.Object, db, NullLogger<OpsController>.Instance);

            var result = await ctrl.RequeueDelivery(ids.OrderId, CancellationToken.None);
            Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(result);
        }

        // Post-requeue state: NOT the claim-defeating fresh 'delivering'; a claimable idle status with
        // the attempt cap reset.
        await using (var verify = NewContext())
        {
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.NotEqual(OrderStatusConstants.Delivering, status);
            Assert.Equal(OrderStatusConstants.DeliveryFailed, status);
            Assert.Equal(0, await verify.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId));
        }

        // Simulate the re-enqueued DeliverOrderJob: the claim must SUCCEED and dispatch (the OLD
        // fresh-'delivering' pre-flip made this a benign no-op — dispatcher.Calls would be 0).
        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success);
        }

        Assert.Equal(1, dispatcher.Calls); // claim SUCCEEDED → dispatched (not a no-op)
        await using (var verify = NewContext())
        {
            // Attempts were reset, so only the single new success attempt is on record.
            var attempts = await verify.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId).ToListAsync();
            Assert.Equal("success", Assert.Single(attempts).Status);
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.Delivered, status);
        }
    }

    // ── Helpers (mirrors DeliveryConcurrencyPostgresTests) ─────────────────────────────────────

    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Protocol => "http";

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
    }

    private sealed class RecordingDispatchEnqueuer : IDeliveryDispatchEnqueuer
    {
        public ConcurrentBag<(Guid OrderId, Guid OrgId, Guid ArtifactId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, Guid artifactId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId, artifactId));
            return Task.CompletedTask;
        }
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
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption) =>
        new(
            db,
            new CountingFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class CountingFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Seeds org + supplier + auto-deliver config + an order (aged) with one artifact.</summary>
    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedDeliverableOrderAsync(
        DeliveryEncryptionService encryption, string status, int agedMinutes)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var aged       = DateTime.UtcNow.AddMinutes(-agedMinutes);

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_lost_{orgId:N}", Name = "Lost Org",
            Slug = $"lost-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = aged,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Lost Supplier", CreatedAt = aged });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-LOST-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 1),
            Currency = "EUR", Status = status, CreatedAt = aged, UpdatedAt = aged,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = aged,
        });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = aged, UpdatedAt = aged,
        });
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId, artifactId);
    }
}
