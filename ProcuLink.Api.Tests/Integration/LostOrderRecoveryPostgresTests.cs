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
/// <item><b>B3</b> — the operator <c>retry-delivery</c> endpoint (the same pre-flip, missed when B2 was
/// fixed) leaves the order CLAIMABLE, so the enqueued <c>RetryDeliveryJob</c>'s claim SUCCEEDS on its
/// FIRST run and dispatches immediately — not after the ~30-minute backoff the pre-flip forced.</item>
/// <item><b>B4</b> — a delivery attempt predating the order's CURRENT artifact (the order was
/// re-transformed after that attempt) is not evidence this artifact was dispatched, so the sweep must
/// still recover the strand. Negating on mere attempt-row existence made it permanently unrecoverable:
/// the corrected PO silently never sent.</item>
/// <item><b>B5</b> — the converse, which keeps B4 honest: an attempt made AFTER the current artifact
/// still blocks the sweep, so the double-send guard survives.</item>
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

        // Post-requeue state: NOT the claim-defeating fresh 'delivering'; a claimable idle status
        // with the attempt cap reset by SUPERSEDING the rows (they survive with their evidence —
        // option B), so the cap predicate reads 0.
        await using (var verify = NewContext())
        {
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.NotEqual(OrderStatusConstants.Delivering, status);
            Assert.Equal(OrderStatusConstants.DeliveryFailed, status);
            Assert.Equal(3, await verify.DeliveryAttempts.CountAsync(a => a.OrderId == ids.OrderId));
            Assert.Equal(0, await verify.DeliveryAttempts
                .Where(a => a.OrderId == ids.OrderId)
                .Where(DeliveryAttempt.CountsAgainstCap)
                .CountAsync());
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
            // The three superseded rows survive; the new success attempt is the only row in the
            // fresh budget, and its number ASCENDS past them (attempt 4 — ruling 1).
            var attempts = await verify.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId).ToListAsync();
            Assert.Equal(4, attempts.Count);
            var newest = attempts.Single(a => a.CapSupersededAt == null);
            Assert.Equal("success", newest.Status);
            Assert.Equal(4, newest.AttemptNumber);
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.Delivered, status);
        }
    }

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

    // ── B4: a stale attempt older than the CURRENT artifact must not blind the sweep ────────────

    [DockerRequiredFact]
    public async Task B4_StaleAttemptOlderThanCurrentArtifact_SweepStillRecovers()
    {
        var encryption = CreateEncryption();
        // The reachable chain: the order already had a delivery attempt (here a pre-dispatch failure —
        // zero bytes sent, ArtifactSha256 null), the operator then edited the mapping, which reset the
        // order to Ready, and the re-transform committed a NEW artifact + ready_to_deliver. The crash
        // landed in the transform-commit → DeliverOrderJob.Enqueue gap. Nothing deletes the old attempt
        // row on re-transform, so the strand carries it.
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.ReadyToDeliver, agedMinutes: 45);

        // Attempt against the OLD artifact, strictly BEFORE the re-transform.
        await AddAttemptAsync(ids.OrgId, ids.OrderId, DateTime.UtcNow.AddMinutes(-40), DeliveryAttempt.StatusFailed);

        // Re-transform: a new artifact, strictly NEWER than the stale attempt, is now the current one.
        var currentArtifactId = await AddArtifactAsync(ids.OrgId, ids.OrderId, DateTime.UtcNow.AddMinutes(-35));

        var recorder = new RecordingDispatchEnqueuer();
        await using (var db = NewContext())
        {
            var sweep = new StrandedReadyOrderDetectionService(
                db, NullLogger<StrandedReadyOrderDetectionService>.Instance, recorder);
            var acted = await sweep.RunAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
            Assert.Equal(1, acted);
        }

        // It re-drives the CURRENT artifact — not the one the stale attempt belonged to.
        var call = Assert.Single(recorder.Calls);
        Assert.Equal((ids.OrderId, ids.OrgId, currentArtifactId), call);
    }

    // ── B5: an attempt ON the current artifact still blocks (double-send guard) ─────────────────

    [DockerRequiredFact]
    public async Task B5_AttemptOnCurrentArtifact_SweepSkips_NoDoubleSend()
    {
        var encryption = CreateEncryption();
        // Artifact seeded at -45m; the attempt at -40m is strictly NEWER, so a dispatch for THIS
        // payload already ran. Re-driving would double-send it.
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.ReadyToDeliver, agedMinutes: 45);
        await AddAttemptAsync(ids.OrgId, ids.OrderId, DateTime.UtcNow.AddMinutes(-40), DeliveryAttempt.StatusFailed);

        var recorder = new RecordingDispatchEnqueuer();
        await using (var db = NewContext())
        {
            var sweep = new StrandedReadyOrderDetectionService(
                db, NullLogger<StrandedReadyOrderDetectionService>.Instance, recorder);
            var acted = await sweep.RunAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
            Assert.Equal(0, acted);
        }

        Assert.Empty(recorder.Calls);
    }

    // ── B6: no attempt-writing path may leave the order in ready_to_deliver ────────────────────

    [DockerRequiredFact]
    public async Task B6_NoAttemptWritingPath_LeavesTheOrderInReadyToDeliver()
    {
        // THE INVARIANT THE MISSING CAP RESTS ON. StrandedReadyOrderDetectionService deliberately has
        // NO attempt cap, because the sweep can drive at most ONE dispatch per artifact: the first
        // dispatch writes a current-artifact attempt row (which its discriminator then blocks on) AND
        // leaves the order out of ready_to_deliver (which its status filter then blocks on) — excluded
        // twice over. That "AND" is what this test pins.
        //
        // It holds by TWO routes, and neither is enforced by the type system or the schema:
        //   • PRE-CLAIM paths flip the status themselves (missing config; no dispatcher; bad
        //     credentials) — they run while the order really is still ready_to_deliver.
        //   • POST-CLAIM paths are covered by the atomic claim (DeliveryService.cs:206-210), which
        //     already moved the order to 'delivering' before any attempt row exists (download failure;
        //     OpenDispatchAttemptAsync; PersistAttemptAsync). OpenDispatchAttemptAsync writes an
        //     attempt row and NO order status at all — the claim ALONE covers it. So do NOT restate
        //     this as "every attempt-writing path writes a terminal status": that is false.
        // Nor is it about the source ORDER of the status write and the row Add within a path — those
        // are both tracked and land in ONE SaveChanges, so they commit atomically and swapping the two
        // statements has no observable effect. The live hazard is removing or weakening the CLAIM, or
        // adding a pre-claim path that never moves the order.
        //
        // Either way the symptom is identical: an order sits in ready_to_deliver holding a
        // current-artifact row, the discriminator blocks it, the status filter no longer saves it, and
        // the sweep silently skips a never-sent PO — the same silent lost order this class exists to
        // prevent, re-entered through the back door. Then the sweep WOULD need a cap. This test asserts
        // the outcome for every path, so it catches that regardless of which route was meant to cover it.
        var encryption = CreateEncryption();

        // Every path that persists a DeliveryAttempt, each on its own order seeded ready_to_deliver.
        var cases = new (string Path, Func<Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)>> Seed,
                         Func<ProcuLinkDbContext, DeliveryService> Build)[]
        {
            // Pre-claim: the order is still ready_to_deliver when these run, so they must flip it themselves.
            ("missing config (FailMissingConfigAsync)",
                () => SeedDeliverableOrderAsync(encryption, OrderStatusConstants.ReadyToDeliver, 45, withConfig: false),
                db => BuildService(db, new CountingDispatcher(), encryption)),

            ("no dispatcher registered for protocol",
                () => SeedDeliverableOrderAsync(encryption, OrderStatusConstants.ReadyToDeliver, 45, protocol: "sftp"),
                db => BuildService(db, new CountingDispatcher(), encryption)), // CountingDispatcher is http-only

            ("undecryptable credentials",
                () => SeedDeliverableOrderAsync(encryption, OrderStatusConstants.ReadyToDeliver, 45,
                        rawCredentials: "not-a-valid-ciphertext"),
                db => BuildService(db, new CountingDispatcher(), encryption)),

            // Post-claim (already 'delivering'), but it must still never land back on ready_to_deliver.
            ("artifact download failed (R2 blip)",
                () => SeedDeliverableOrderAsync(encryption, OrderStatusConstants.ReadyToDeliver, 45),
                db => BuildService(db, new CountingDispatcher(), encryption, new ThrowingFileStorage())),

            // The normal terminal persist — the success side of PersistAttemptAsync.
            ("successful dispatch (PersistAttemptAsync)",
                () => SeedDeliverableOrderAsync(encryption, OrderStatusConstants.ReadyToDeliver, 45),
                db => BuildService(db, new CountingDispatcher(), encryption)),
        };

        foreach (var (path, seed, build) in cases)
        {
            var ids = await seed();

            await using (var db = NewContext())
                await build(db).DispatchArtifactAsync(
                    ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);

            await using var verify = NewContext();
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            var wroteAttempt = await verify.DeliveryAttempts.AsNoTracking()
                .AnyAsync(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId);

            // Guard the guard: a path that silently stopped writing an attempt row would make the
            // status assertion below pass vacuously.
            Assert.True(wroteAttempt, $"path '{path}' was expected to persist a DeliveryAttempt row but did not — " +
                                       "this test no longer exercises what it claims to.");
            Assert.True(status != OrderStatusConstants.ReadyToDeliver,
                $"path '{path}' left the order in ready_to_deliver while holding an attempt row against the " +
                "current artifact. StrandedReadyOrderDetectionService's discriminator now blocks that order and " +
                "its status filter no longer excludes it, so the sweep will silently skip a never-sent PO. " +
                "Either restore whichever route was covering this path — the path's own terminal-status write " +
                "if it runs pre-claim, or the atomic claim at DeliveryService.cs:206-210 if it runs post-claim " +
                "— or give the sweep a real attempt cap.");
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
                EncryptedCredentials = rawCredentials ?? encryption.Encrypt("{\"type\":\"none\"}"),
                CreatedAt = aged, UpdatedAt = aged,
            });
        await db.SaveChangesAsync();

        return (orgId, supplierId, orderId, artifactId);
    }
}
