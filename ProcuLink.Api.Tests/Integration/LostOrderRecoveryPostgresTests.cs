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
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// B3 (a silent lost-order path) proven on REAL Postgres, where <c>DispatchArtifactAsync</c>'s
/// atomic ready_to_deliver / delivery_failed / stale-delivering → delivering claim actually runs (the
/// EF InMemory provider can't translate it).
///
/// <list type="bullet">
/// <item><b>B3</b> — the operator <c>retry-delivery</c> endpoint leaves the order CLAIMABLE, so the enqueued <c>RetryDeliveryJob</c>'s claim SUCCEEDS on its
/// FIRST run and dispatches immediately — not after the ~30-minute backoff the pre-flip forced.</item>
/// </list>
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class LostOrderRecoveryPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_lost");

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

    // ── B3: operator retry-delivery → claim SUCCEEDS immediately (no ~30-min dead window) ──────

    [DockerRequiredFact]
    public async Task B3_OperatorRetryDelivery_LeavesClaimableStatus_ClaimSucceedsImmediately_Dispatches()
    {
        var encryption = CreateEncryption();
        // A JUST-failed order (UpdatedAt = now) — the real "Retry now" scenario and the strictest case:
        // nothing here is stale, so ONLY a claimable idle status can carry the claim.
        var ids = await SeedDeliverableOrderAsync(
            encryption, status: OrderStatusConstants.DeliveryFailed, agedMinutes: 0);

        // Drive the REAL OrdersController.RetryDelivery against Postgres.
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

            var ctrl = new OrdersController(
                orders.Object, tenant.Object, jobs.Object, db,
                NullLogger<OrdersController>.Instance,
                new Mock<IBillingService>().Object,
                new Mock<IIdempotencyService>().Object,
                new Mock<IOrderExceptionService>().Object,
                new Mock<ISupplierAcceptanceService>().Object,
                new Mock<IOrderMappingOverrideService>().Object,
                new Mock<IPromoteMappingService>().Object,
                new Mock<IFileStorageService>().Object,
                new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
                Array.Empty<ITransformService>());

            var result = await ctrl.RetryDelivery(ids.OrderId, CancellationToken.None);
            Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(result);
        }

        // Post-retry state: NOT the claim-defeating fresh 'delivering' — still the claimable idle status.
        await using (var verify = NewContext())
        {
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.NotEqual(OrderStatusConstants.Delivering, status);
            Assert.Equal(OrderStatusConstants.DeliveryFailed, status);
        }

        // Simulate the enqueued RetryDeliveryJob running NOW (not after a ~30-min backoff): the claim
        // must SUCCEED and dispatch. Under the OLD pre-flip this returned Success=false / "already in
        // progress" and dispatcher.Calls would be 0 — the operator's click sent nothing for half an hour.
        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.RetryDeliveryAsync(
                ids.OrgId, ids.OrderId,
                ProcuLink.Infrastructure.Jobs.RetryDeliveryJob.MaxAttempts, CancellationToken.None);
            Assert.True(result.Success, $"retry claim must succeed immediately; got: {result.ErrorMessage}");
        }

        Assert.Equal(1, dispatcher.Calls); // claim SUCCEEDED → dispatched now (not a no-op)
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

    // ── Helpers (mirrors DeliveryConcurrencyPostgresTests) ─────────────────────────────────────

    /// <summary>Adds one delivery attempt row at an explicit <paramref name="attemptedAt"/>.</summary>
    private async Task AddAttemptAsync(Guid orgId, Guid orderId, DateTime attemptedAt, string status)
    {
        await using var db = NewContext();
        var attemptNumber = await db.DeliveryAttempts
            .CountAsync(a => a.OrderId == orderId && a.OrgId == orgId
                          && a.Status != DeliveryAttempt.StatusDispatching) + 1;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "http", Destination = "https://supplier.example/orders",
            Status = status, AttemptNumber = attemptNumber, AttemptedAt = attemptedAt,
            // The pre-dispatch failure signature: no bytes were ever sent.
            ArtifactSha256 = null,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds one artifact row at an explicit <paramref name="createdAt"/>; returns its id.</summary>
    private async Task<Guid> AddArtifactAsync(Guid orgId, Guid orderId, DateTime createdAt)
    {
        var artifactId = Guid.NewGuid();
        await using var db = NewContext();
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = $"artifact-{artifactId:N}.csv", CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return artifactId;
    }

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
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption,
        IFileStorageService? storage = null) =>
        new(
            db,
            storage ?? new CountingFileStorage(),
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

    /// <summary>Storage whose download throws — the R2-blip pre-dispatch failure path.</summary>
    private sealed class ThrowingFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("R2 blip: signing failed.");
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
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
    /// <param name="withConfig">False omits the SupplierDeliveryConfig entirely — forces the missing-config path.</param>
    /// <param name="protocol">Config protocol; set to one no dispatcher is registered for to force that path.</param>
    /// <param name="rawCredentials">Stored verbatim into EncryptedCredentials instead of a real ciphertext —
    /// use to force the undecryptable-credentials path.</param>
    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedDeliverableOrderAsync(
        DeliveryEncryptionService encryption, string status, int agedMinutes,
        bool withConfig = true, string protocol = "http", string? rawCredentials = null)
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
        if (withConfig)
            db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Protocol = protocol, AutoDeliver = true,
                ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
                EncryptedCredentials = rawCredentials ?? encryption.Encrypt(
                    "{\"type\":\"none\"}",
                    CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
                CreatedAt = aged, UpdatedAt = aged,
            });
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId, artifactId);
    }
}
