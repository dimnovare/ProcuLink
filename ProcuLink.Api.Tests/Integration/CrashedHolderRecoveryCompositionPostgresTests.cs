using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Claim behaviour of <c>RetryDeliveryJob</c> against a live holder, on real Postgres. A lost claim
/// means the holder owns the send, so the retry must never double-dispatch and must end its chain
/// once the holder's send lands.
///
/// <para>Real Postgres only: the claim's staleness gate is an untranslatable ExecuteUpdate, and the
/// InMemory retry path has no status gate at all, so it cannot express a lost claim.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class CrashedHolderRecoveryCompositionPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly DeliveryReliabilityOptions Options =
        new() { MaxAttempts = 3, BackoffMinutes = new[] { 30, 60, 120 } };

    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_crashcompose");

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_databaseConnectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    /// <summary>
    /// The other half of the contract: while the holder is ALIVE (fresh 'delivering'), the retry must
    /// never DISPATCH — that would double-send the PO. It does still schedule a backoff, which is
    /// harmless when the holder is alive (the later run finds the order delivered and stops) and
    /// essential when it is not.
    /// </summary>
    [DockerRequiredFact]
    public async Task LiveHolder_RetryNeverDoubleDispatches()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.Delivering, DateTime.UtcNow);
        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var jobs = new CapturingJobClient();

        await using (var db = NewContext())
        {
            var job = new RetryDeliveryJob(
                CreateService(db, dispatcher), jobs, NullLogger<RetryDeliveryJob>.Instance, Options);
            await job.ExecuteAsync(ids.OrderId, ids.OrgId, default);
        }

        dispatcher.Calls.Should().Be(0, "the live holder's send is in flight — never double-dispatch");
    }

    /// <summary>
    /// The backoff scheduled against a LIVE holder must not become a loop: once that holder finishes,
    /// the order is 'delivered' and the next run stops on the benign already-delivered path.
    /// </summary>
    [DockerRequiredFact]
    public async Task LiveHolder_ThatCompletes_EndsTheRetryChain()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.Delivered, DateTime.UtcNow);
        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var jobs = new CapturingJobClient();

        await using (var db = NewContext())
        {
            var job = new RetryDeliveryJob(
                CreateService(db, dispatcher), jobs, NullLogger<RetryDeliveryJob>.Instance, Options);
            await job.ExecuteAsync(ids.OrderId, ids.OrgId, default);
        }

        dispatcher.Calls.Should().Be(0, "already delivered — never re-send");
        jobs.Captured.Should().BeEmpty("the chain must terminate once the holder's send landed");
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

    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderId)> SeedOrderAsync(string status, DateTime updatedAt)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id            = orgId,
            ClerkOrgId    = $"org_crash_{orgId:N}",
            Name          = "Crash Recovery Org",
            Slug          = $"crash-{orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Crash Supplier", CreatedAt = now });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id                   = Guid.NewGuid(),
            OrgId                = orgId,
            SupplierId           = supplierId,
            Protocol             = "http",
            AutoDeliver          = true,
            ConfigJson           = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = CreateEncryption().Encrypt(
                "{\"type\":\"none\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt            = now,
            UpdatedAt            = now,
        });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-CRASH-1",
            OrderDate  = DateOnly.FromDateTime(now),
            Currency   = "EUR",
            Status     = status,
            CreatedAt  = now,
            UpdatedAt  = updatedAt,
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

    private static DeliveryService CreateService(ProcuLinkDbContext db, IDeliveryDispatcher dispatcher) =>
        new(
            db,
            new FakeFileStorage(),
            CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            Options);

    private sealed class CapturingJobClient : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> Captured { get; } = new();

        public string Create(Job job, IState state)
        {
            Captured.Add((job, state));
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string Protocol => "http";

        public CountingDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
        {
            Interlocked.Increment(ref _calls);
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
