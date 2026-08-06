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
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// COMPOSED crash-recovery path, on real Postgres. <c>RetryDeliveryJob</c>'s "a lost claim means the
/// holder owns the send, so stay quiet" rule is only safe if something else recovers the order when
/// that holder DIES. Today that is <see cref="StuckDeliveryDetectionService"/>. Each half is tested
/// on its own; NOTHING asserted the two composed — so the retry queue's silence rests on an
/// unasserted premise living in another file (the exact shape of the four-list drift class: correct
/// behaviour resting on a neighbour's predicate that nobody pins).
///
/// <para>These tests pin the WHOLE path: live holder → retry stays silent → holder crashes → sweep
/// → order actually DELIVERED. If someone later narrows the sweep's status match, raises its
/// threshold, or changes its re-drive handshake, the silence becomes permanent and one of these
/// goes red instead of a PO silently never being sent.</para>
///
/// <para>Real Postgres only: the claim's staleness gate is an untranslatable ExecuteUpdate, and the
/// InMemory retry path has no status gate at all, so it cannot express a lost claim.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class CrashedHolderRecoveryCompositionPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly DeliveryReliabilityOptions Options =
        new() { MaxAttempts = 3, BackoffMinutes = new[] { 30, 60, 120 } };

    /// <summary>Longer than the service's 2-min DeliveringReclaimWindow, so the row reads as a dead holder.</summary>
    private static readonly TimeSpan CrashedFor = TimeSpan.FromMinutes(30);

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
    /// THE GATE. A holder claimed the order and died mid-send (row left 'delivering' with a stale
    /// UpdatedAt and an orphaned 'dispatching' attempt). The composed recovery must ultimately SEND
    /// the PO.
    ///
    /// <para>Note the counter-intuitive shape this test forced into the open: the sweep's re-drive
    /// does NOT deliver. The sweep bumps UpdatedAt to now before enqueuing, so the re-driven retry
    /// meets a still-fresh 'delivering' row and loses the claim. Recovery only completes on the
    /// SCHEDULED backoff, once the row has aged past the reclaim window. That is why ClaimLost must
    /// keep rescheduling: the sweep alone cannot recover a crashed holder's order.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task CrashedHolder_SweepRedriveThenBackoff_ActuallyDeliversTheOrder()
    {
        var ids = await SeedCrashedHolderAsync();
        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));

        // ── 1. The sweep finds the dead holder and re-drives it through the retry seam ──
        var enqueuer = new RecordingEnqueuer();
        await using (var sweepDb = NewContext())
        {
            var sweep = new StuckDeliveryDetectionService(
                sweepDb, NullLogger<StuckDeliveryDetectionService>.Instance, enqueuer);
            var acted = await sweep.RunAsync(TimeSpan.FromMinutes(10), default);
            acted.Should().Be(1, "a 30-min-stale 'delivering' row is exactly what this sweep is for");
        }

        enqueuer.Enqueued.Should().ContainSingle("the sweep must hand the order to the retry queue");

        // ── 2. The re-driven retry runs and BOUNCES: the sweep just freshened UpdatedAt, so the
        //       claim rejects it. It must schedule the backoff rather than give up. ──
        var jobs = new CapturingJobClient();
        var (orderId, orgId) = enqueuer.Enqueued.Single();
        await using (var jobDb = NewContext())
        {
            var job = new RetryDeliveryJob(
                CreateService(jobDb, dispatcher), jobs, NullLogger<RetryDeliveryJob>.Instance, Options);
            await job.ExecuteAsync(orderId, orgId, default);
        }

        dispatcher.Calls.Should().Be(0, "the sweep's own UpdatedAt bump makes the row unclaimable right now");
        jobs.Captured.Should().HaveCount(1,
            "THE NET: without this backoff the crashed holder's order is never sent again");

        // ── 3. Time passes to the scheduled backoff; the row is now stale and claimable. ──
        await AgeOrderAsync(ids.OrderId, TimeSpan.FromMinutes(30));

        await using (var jobDb = NewContext())
        {
            var job = new RetryDeliveryJob(
                CreateService(jobDb, dispatcher), jobs, NullLogger<RetryDeliveryJob>.Instance, Options);
            await job.ExecuteAsync(orderId, orgId, default);
        }

        // THE ASSERTION THAT MATTERS: the PO actually went out.
        dispatcher.Calls.Should().Be(1, "the crashed holder's order must ultimately be SENT, not abandoned");

        await using var verify = NewContext();
        var status = await verify.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
        status.Should().Be(OrderStatusConstants.Delivered);
    }

    /// <summary>Simulates the wall-clock wait to the scheduled backoff by ageing the row's UpdatedAt.</summary>
    private async Task AgeOrderAsync(Guid orderId, TimeSpan by)
    {
        await using var db = NewContext();
        await db.PurchaseOrders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, o => o.UpdatedAt - by));
    }

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

    /// <summary>
    /// A holder that claimed the order and then died mid-send: 'delivering', UpdatedAt stale, plus the
    /// orphaned 'dispatching' attempt row its claim wrote (which must NOT burn a retry-budget slot).
    /// </summary>
    private async Task<(Guid OrgId, Guid SupplierId, Guid OrderId)> SeedCrashedHolderAsync()
    {
        var crashedAt = DateTime.UtcNow - CrashedFor;
        var ids = await SeedOrderAsync(OrderStatusConstants.Delivering, crashedAt);

        await using var db = NewContext();
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id            = Guid.NewGuid(),
            OrderId       = ids.OrderId,
            OrgId         = ids.OrgId,
            Channel       = "http",
            Destination   = "https://supplier.example/orders",
            Status        = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 1,
            AttemptedAt   = crashedAt,
        });
        await db.SaveChangesAsync();

        return ids;
    }

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
            EncryptedCredentials = CreateEncryption().Encrypt("{\"type\":\"none\"}"),
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

    private sealed class RecordingEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Enqueued { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Enqueued.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

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
            CancellationToken ct, string? idempotencyKey = null)
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
