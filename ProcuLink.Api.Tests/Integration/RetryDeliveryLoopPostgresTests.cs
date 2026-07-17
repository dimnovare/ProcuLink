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
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// UNBOUNDED RETRY LOOP guard — proves on REAL Postgres that <see cref="RetryDeliveryJob"/> stops
/// rescheduling itself when a retry can never help, while STILL rescheduling when it can.
///
/// <para>The loop: a non-dispatch failure writes no <c>DeliveryAttempt</c> row and carries no
/// response code, so it is indistinguishable (to the job) from a transient 5xx —
/// <c>IsSupplierRejection</c> is false, <c>CountDeliveryAttemptsAsync</c> returns the SAME number
/// forever so the cap never trips, and <c>BackoffFor</c> returns the same delay. The job reschedules
/// every ~30 min against an order it can never move. <see cref="DeliveryOutcome.NotRetryable"/> is
/// what breaks the cycle.</para>
///
/// <para>The mirror-image hazard is over-correcting: a <see cref="DeliveryOutcome.ClaimLost"/> result
/// ALSO writes no attempt row, yet must keep rescheduling — it is the crash-recovery net. Both
/// directions are pinned here.</para>
///
/// <para>Real Postgres is required: the atomic <c>delivering</c>-claim is an untranslatable
/// <c>ExecuteUpdateAsync</c> on EF InMemory, whose retry path emulates the claim through the change
/// tracker with NO status gate — it cannot produce a lost claim at all. Docker-gated.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class RetryDeliveryLoopPostgresTests : IAsyncLifetime
{
    private static readonly DeliveryReliabilityOptions Options =
        new() { MaxAttempts = 3, BackoffMinutes = new[] { 30, 60, 120 } };

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_retryloop_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
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

    /// <summary>
    /// The claim matches 0 rows because ANOTHER worker holds a FRESH <c>delivering</c> claim
    /// mid-send. No dispatch — but the backoff MUST still be scheduled, because that reschedule is
    /// what recovers the order if the holder turns out to be dead (see
    /// <c>CrashedHolderRecoveryCompositionPostgresTests</c>). Marked ClaimLost, not NotRetryable.
    /// (The status passes the advisory pre-check, so only the atomic claim rejects it.)
    /// </summary>
    [DockerRequiredFact]
    public async Task ClaimLostToAnotherWorker_NoDispatch_ButStillReschedules()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.Delivering, updatedAt: DateTime.UtcNow);
        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));

        var (result, jobs) = await RunJobAsync(ids, dispatcher);

        result.Outcome.Should().Be(DeliveryOutcome.ClaimLost);
        result.Success.Should().BeFalse();
        dispatcher.Calls.Should().Be(0, "the other worker's send is in flight — this one must not double-dispatch");
        jobs.Captured.Should().HaveCount(1,
            "if that holder is dead, this backoff is the only thing that ever gets the order sent");
        await AssertNoAttemptRowsAsync(ids.OrderId);
    }

    /// <summary>
    /// A billing-held order fails the retryable-status pre-check. Nothing a backoff retry does can
    /// release it — the reactivation re-drive owns that — so the queue must stop, not poll it every
    /// 30 min for as long as the org stays lapsed.
    /// </summary>
    [DockerRequiredFact]
    public async Task HeldOrder_NotRetryableStatus_JobDoesNotReschedule()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.DeliveryHeld, updatedAt: DateTime.UtcNow);
        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));

        var (result, jobs) = await RunJobAsync(ids, dispatcher);

        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        dispatcher.Calls.Should().Be(0);
        jobs.Captured.Should().BeEmpty();
        await AssertNoAttemptRowsAsync(ids.OrderId);
    }

    /// <summary>A dead-lettered order is terminal: no dispatch, no reschedule.</summary>
    [DockerRequiredFact]
    public async Task DeadLetteredOrder_JobDoesNotReschedule()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.DeliveryDeadLetter, updatedAt: DateTime.UtcNow);
        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));

        var (result, jobs) = await RunJobAsync(ids, dispatcher);

        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        dispatcher.Calls.Should().Be(0);
        jobs.Captured.Should().BeEmpty();
    }

    /// <summary>
    /// An erased order (GDPR erase / bulk-erase between the schedule and the run) has no rows left
    /// to count, so the frozen count is 0 — the cap could never trip and the job would outlive the
    /// order forever.
    /// </summary>
    [DockerRequiredFact]
    public async Task ErasedOrder_JobDoesNotReschedule()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.DeliveryFailed, updatedAt: DateTime.UtcNow);

        await using (var erase = NewContext())
        {
            await erase.OutboundArtifacts.Where(a => a.OrderId == ids.OrderId).ExecuteDeleteAsync();
            await erase.PurchaseOrders.Where(o => o.Id == ids.OrderId).ExecuteDeleteAsync();
        }

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202));
        var (result, jobs) = await RunJobAsync(ids, dispatcher);

        result.Outcome.Should().Be(DeliveryOutcome.NotRetryable);
        dispatcher.Calls.Should().Be(0);
        jobs.Captured.Should().BeEmpty();
    }

    /// <summary>
    /// THE OTHER HALF of the fix: a real transient failure DID reach the wire and DID write an
    /// attempt row, so the backoff queue must still run. Without this the "stop looping" change
    /// would silently abandon deliverable orders — a far worse bug than the loop.
    /// </summary>
    [DockerRequiredFact]
    public async Task TransientDispatchFailure_AttemptRowWritten_JobStillReschedules()
    {
        var ids = await SeedOrderAsync(OrderStatusConstants.DeliveryFailed, updatedAt: DateTime.UtcNow);
        var dispatcher = new CountingDispatcher(new DeliveryResult(false, "HTTP 503", 503));

        var before = DateTime.UtcNow;
        var (result, jobs) = await RunJobAsync(ids, dispatcher);

        result.Outcome.Should().Be(DeliveryOutcome.Dispatched, "the payload reached the dispatcher");
        dispatcher.Calls.Should().Be(1);
        jobs.Captured.Should().HaveCount(1, "a 5xx is transient — the retry queue owns it");
        ((ScheduledState)jobs.Captured.Single().State).EnqueueAt
            .Should().BeCloseTo(before.AddMinutes(30), TimeSpan.FromMinutes(1));

        await using var verify = NewContext();
        var attempts = await verify.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId);
        attempts.Should().Be(1, "the attempt count MUST advance, or the cap can never trip");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(DeliveryResult Result, CapturingJobClient Jobs)> RunJobAsync(
        (Guid OrgId, Guid SupplierId, Guid OrderId) ids, IDeliveryDispatcher dispatcher)
    {
        var jobs = new CapturingJobClient();

        await using var db = NewContext();
        var service = new RecordingDeliveryService(CreateService(db, dispatcher));
        var job = new RetryDeliveryJob(service, jobs, NullLogger<RetryDeliveryJob>.Instance, Options);

        await job.ExecuteAsync(ids.OrderId, ids.OrgId, default);

        return (service.LastResult!, jobs);
    }

    private async Task AssertNoAttemptRowsAsync(Guid orderId)
    {
        await using var verify = NewContext();
        var attempts = await verify.DeliveryAttempts.CountAsync(a => a.OrderId == orderId);
        attempts.Should().Be(0, "nothing was dispatched, so the retry counter is frozen");
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    /// <summary>Seeds org + supplier + delivery config + an order (at the given status) with an artifact.</summary>
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
            ClerkOrgId    = $"org_retryloop_{orgId:N}",
            Name          = "Retry Loop Org",
            Slug          = $"retryloop-{orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Loop Supplier", CreatedAt = now });
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
            PoNumber   = "PO-LOOP-1",
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

    /// <summary>Passes through to the real service while exposing the result the job saw.</summary>
    private sealed class RecordingDeliveryService : IDeliveryService
    {
        private readonly IDeliveryService _inner;
        public DeliveryResult? LastResult { get; private set; }

        public RecordingDeliveryService(IDeliveryService inner) => _inner = inner;

        public async Task<DeliveryResult> RetryDeliveryAsync(Guid orgId, Guid orderId, int maxAttempts, CancellationToken ct)
            => LastResult = await _inner.RetryDeliveryAsync(orgId, orderId, maxAttempts, ct);

        public Task<DeliveryResult> DispatchArtifactAsync(Guid orgId, Guid orderId, Guid artifactId, bool requireAutoDeliver, CancellationToken ct)
            => _inner.DispatchArtifactAsync(orgId, orderId, artifactId, requireAutoDeliver, ct);

        public Task<DeliveryTestResult> TestFireAsync(Guid orgId, Guid supplierId, CancellationToken ct)
            => _inner.TestFireAsync(orgId, supplierId, ct);

        public Task<int> CountDeliveryAttemptsAsync(Guid orgId, Guid orderId, CancellationToken ct)
            => _inner.CountDeliveryAttemptsAsync(orgId, orderId, ct);

        public Task<bool> HoldForBillingAsync(Guid orgId, Guid orderId, CancellationToken ct)
            => _inner.HoldForBillingAsync(orgId, orderId, ct);

        public Task<int> ReleaseBillingHeldOrdersAsync(Guid orgId, CancellationToken ct)
            => _inner.ReleaseBillingHeldOrdersAsync(orgId, ct);
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
