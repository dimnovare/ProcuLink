using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// D-1 (HIGH) — proves the first-deliver/redeliver/requeue path can NOT double-send a PO on REAL
/// Postgres, where <c>DispatchArtifactAsync</c>'s atomic <c>ExecuteUpdateAsync</c> ready_to_deliver /
/// delivery_failed / delivery_unconfirmed / stale-delivering → delivering claim actually runs (the EF
/// InMemory provider can't translate it, so its sibling predicate is separate code these tests cannot
/// cover). The claim mirrors <c>RetryDeliveryAsync</c>'s verbatim:
/// <list type="bullet">
/// <item>two parallel <c>DispatchArtifactAsync</c> for the SAME order → exactly ONE dispatch, ONE
/// success attempt row (a double-clicked Redeliver / Redeliver racing an ops Requeue);</item>
/// <item>a direct dispatch racing the <c>RetryDeliveryAsync</c> path → exactly ONE dispatch;</item>
/// <item>a redeliver on an already-<c>delivered</c> order → NO new dispatch, NO new attempt row;</item>
/// <item>a redeliver on a PARKED order → claimed and dispatched (a status missing from the claim
/// fails silently: 0 rows matched reads as a benign no-op success).</item>
/// </list>
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class DeliveryConcurrencyPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_deliv");

        // Pooling=false so each concurrent context opens its OWN physical connection — the claim race
        // is only real when two workers hold two connections (a pooled single connection would
        // serialise them and hide the bug the atomic claim must defend against).
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

    // ── A counting dispatcher shared across the two concurrent services. Thread-safe; records every
    //    dispatch so we can assert "exactly one" survived the claim. ──
    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Protocol => "http";

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null,
            bool isTestFire = false)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new DeliveryResult(true, null, 200));
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

    /// <summary>Seeds org + supplier + delivery config + a ready_to_deliver order with one artifact.</summary>
    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedDeliverableOrderAsync(
        DeliveryEncryptionService encryption, string status = OrderStatusConstants.ReadyToDeliver)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_deliv_{orgId:N}", Name = "Delivery Org",
            Slug = $"deliv-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Deliv Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-DELIV-CONC-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 1),
            Currency = "EUR", Status = status, CreatedAt = now, UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = now,
        });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt(
                "{\"type\":\"none\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId, artifactId);
    }

    [DockerRequiredFact]
    public async Task TwoParallelDispatches_SameOrder_DispatchExactlyOnce()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption);
        var dispatcher = new CountingDispatcher();

        // Two concurrent activations (each with its OWN context, like two Hangfire workers) call the
        // SAME DispatchArtifactAsync the DeliverOrderJob first-deliver/redeliver path calls.
        async Task<DeliveryResult> RunOne()
        {
            await using var db = NewContext();
            var svc = BuildService(db, dispatcher, encryption);
            return await svc.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => RunOne()));

        // Exactly ONE worker dispatched to the supplier.
        Assert.Equal(1, dispatcher.Calls);

        await using (var db = NewContext())
        {
            // Exactly ONE success attempt row — no double-delivery audit trail.
            var attempts = await db.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId).ToListAsync();
            var success = Assert.Single(attempts);
            Assert.Equal("success", success.Status);

            var status = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.Delivered, status);
        }

        // Both calls report success (one delivered, one benign "already in progress" no-op).
        Assert.All(results, r => Assert.True(r.Success));
    }

    [DockerRequiredFact]
    public async Task DirectDispatch_RacingRetryDelivery_DispatchExactlyOnce()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption);
        var dispatcher = new CountingDispatcher();

        async Task<DeliveryResult> RunDispatch()
        {
            await using var db = NewContext();
            var svc = BuildService(db, dispatcher, encryption);
            return await svc.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
        }

        async Task<DeliveryResult> RunRetry()
        {
            await using var db = NewContext();
            var svc = BuildService(db, dispatcher, encryption);
            return await svc.RetryDeliveryAsync(ids.OrgId, ids.OrderId, maxAttempts: 3, CancellationToken.None);
        }

        // A DeliverOrderJob (direct dispatch) racing a RetryDeliveryJob for the same order. The atomic
        // delivering-claim in BOTH paths is keyed on the same row, so only one wins the claim.
        var dispatchTask = RunDispatch();
        var retryTask = RunRetry();
        await Task.WhenAll(dispatchTask, retryTask);

        Assert.Equal(1, dispatcher.Calls);

        await using var verify = NewContext();
        var attempts = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId).ToListAsync();
        // One success attempt; the loser made no attempt (benign "already in progress").
        var success = Assert.Single(attempts);
        Assert.Equal("success", success.Status);

        var status = await verify.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatusConstants.Delivered, status);
    }

    [DockerRequiredFact]
    public async Task Redeliver_OnAlreadyDeliveredOrder_IsNoOp()
    {
        var encryption = CreateEncryption();
        // Order is already terminal-delivered (with the original successful attempt on record).
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.Delivered);
        var now = DateTime.UtcNow;
        await using (var seed = NewContext())
        {
            seed.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                Channel = "http", Destination = "https://supplier.example/orders",
                Status = "success", AttemptNumber = 1, AttemptedAt = now, ResponseCode = 200,
                TransportAcceptedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
            // A redeliver on a delivered order must NOT re-dispatch — benign no-op, not a throw.
            Assert.True(result.Success);
        }

        // No new dispatch, no new attempt row: still the single original success attempt.
        Assert.Equal(0, dispatcher.Calls);
        await using var verify = NewContext();
        var attempts = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId).ToListAsync();
        Assert.Single(attempts);
        var status = await verify.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatusConstants.Delivered, status);
    }

    /// <summary>
    /// The operator's "Send again" on a parked (delivery_unconfirmed) order must reach the supplier
    /// on REAL Postgres. The relational claim is the predicate that runs in production, and the
    /// InMemory sibling of this test cannot cover it — the two predicates are separate code that
    /// must agree, and a status missing from this one fails SILENTLY: the ExecuteUpdateAsync matches
    /// 0 rows and returns the benign no-op success, so the job logs success having sent nothing.
    /// </summary>
    [DockerRequiredFact]
    public async Task Redeliver_FromParkedOrder_IsClaimedAndDispatched()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.DeliveryUnconfirmed);

        // The exact post-park state: the attempt row was finalised TERMINAL ('unconfirmed'), which
        // is what lets this re-send open a fresh attempt rather than re-adopt an in-flight row and
        // immediately re-park.
        await using (var seed = NewContext())
        {
            seed.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                Channel = "http", Destination = "https://supplier.example/orders",
                Status = DeliveryAttempt.StatusUnconfirmed, AttemptNumber = 1,
                AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
                IdempotencyKey = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId),
                ErrorMessage = "Delivery unconfirmed. We may have sent this order, but lost the "
                             + "connection before the supplier confirmed it.",
            });
            await seed.SaveChangesAsync();
        }

        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success);
            Assert.Equal(DeliveryOutcome.Dispatched, result.Outcome);
        }

        Assert.Equal(1, dispatcher.Calls);

        await using var verify = NewContext();
        var status = await verify.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatusConstants.Delivered, status);

        var attempts = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId)
            .OrderBy(a => a.AttemptNumber).ToListAsync();
        Assert.Equal(2, attempts.Count);
        // The parked row records an outcome we never observed — a later send never rewrites it.
        Assert.Equal(DeliveryAttempt.StatusUnconfirmed, attempts[0].Status);
        Assert.Equal("success", attempts[1].Status);
    }
}
