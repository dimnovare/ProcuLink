using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Webhooks;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Option B (docs/superpowers/specs/2026-07-17-delivery-cap-without-erasing-evidence.md) proven on
/// REAL Postgres: the ops requeue resets the delivery attempt cap by stamping
/// <see cref="DeliveryAttempt.CapSupersededAt"/> — never by deleting rows — so the webhook guard's
/// premise "no dispatch marker ⇒ never dispatched" holds BY CONSTRUCTION and a late supplier
/// rejection on a requeued order is accepted instead of refused (the refused-rejection re-send P1).
///
/// <list type="bullet">
/// <item><b>C1</b> — requeue preserves every attempt row and its dispatch evidence
/// (IdempotencyKey / ArtifactSha256), stamps CapSupersededAt on the terminal rows, and the cap
/// count reads 0. The next dispatch's AttemptNumber ASCENDS (ruling 1) — attempt 4, not a lying
/// attempt 1 — and the new row itself counts against the fresh budget.</item>
/// <item><b>C2</b> — the P1 inverse: after a requeue, a late supplier <c>rejected</c> callback is
/// ACCEPTED (evidence survived), the order lands in rejected_by_supplier, and the stranded-failed
/// sweep no longer reaches it — the rejected PO is never re-sent.</item>
/// <item><b>C3</b> — condition 2: the stranded-failed sweep still re-drives a requeued order whose
/// re-enqueue was lost. Rows now SURVIVE the requeue, so without the sweep's cap subquery being
/// re-based onto the shared predicate it would read count ≥ cap and skip the order forever —
/// an operator clicks Requeue and nothing happens, silently. Green before AND after the change;
/// red exactly when the requeue stops deleting but the sweep still counts superseded rows.</item>
/// <item><b>C4</b> — the cap reset works for the retry path: an at-cap order, requeued, is
/// deliverable again through <c>RetryDeliveryAsync</c> (not instantly re-dead-lettered).</item>
/// <item><b>C7</b> — every cap site agrees on adversarial rows (superseded terminal + live
/// terminal + in-flight dispatching): they all compose <see cref="DeliveryAttempt.CountsAgainstCap"/>,
/// and this pins the observable agreement rather than eyeballing call sites.</item>
/// </list>
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class CapWithoutErasingEvidencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_cap_{Guid.NewGuid():N}")
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

    // ── C1: requeue preserves evidence, resets the cap, numbering ascends ───────────────────────

    [DockerRequiredFact]
    public async Task C1_OpsRequeue_PreservesDispatchEvidence_ResetsCap_NumberingAscends()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.ReadyToDeliver, agedMinutes: 5);

        // A REAL dispatch writes attempt 1 with both markers (IdempotencyKey + ArtifactSha256).
        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success);
        }

        // The supplier endpoint then "went bad": push the order to dead-letter at the cap with two
        // more terminal failures (pre-dispatch shape — no markers), total 3 = MaxAttempts.
        await using (var seed = NewContext())
        {
            for (var i = 2; i <= 3; i++)
                seed.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                    AttemptNumber = i, AttemptedAt = DateTime.UtcNow,
                });
            await seed.PurchaseOrders
                .Where(o => o.Id == ids.OrderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatusConstants.DeliveryDeadLetter));
            await seed.SaveChangesAsync();
        }

        await RequeueAsync(ids.OrgId, ids.OrderId);

        await using (var verify = NewContext())
        {
            var rows = await verify.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId).OrderBy(a => a.AttemptNumber).ToListAsync();

            // EVERY row survives — the requeue no longer deletes evidence.
            Assert.Equal(3, rows.Count);

            // The genuinely-dispatched row keeps its markers.
            Assert.NotNull(rows[0].IdempotencyKey);
            Assert.NotNull(rows[0].ArtifactSha256);

            // All terminal rows are excluded from the CURRENT budget…
            Assert.All(rows, r => Assert.NotNull(r.CapSupersededAt));

            // …so the cap reads 0 through the ONE shared predicate.
            var capCount = await verify.DeliveryAttempts
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId)
                .Where(DeliveryAttempt.CountsAgainstCap)
                .CountAsync();
            Assert.Equal(0, capCount);
        }

        // The re-enqueued DeliverOrderJob dispatches; the new attempt ASCENDS to 4 (ruling 1 —
        // "attempt 1" after three real attempts is a lie in supplier-visible provenance) and it
        // counts against the fresh budget.
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success);
        }

        await using (var verify = NewContext())
        {
            var newest = await verify.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId)
                .OrderByDescending(a => a.AttemptNumber).FirstAsync();
            Assert.Equal(4, newest.AttemptNumber);
            Assert.Equal(DeliveryAttempt.StatusSuccess, newest.Status);
            Assert.Null(newest.CapSupersededAt);

            var capCount = await verify.DeliveryAttempts
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId)
                .Where(DeliveryAttempt.CountsAgainstCap)
                .CountAsync();
            Assert.Equal(1, capCount);
        }
    }

    // ── C2: the P1 inverse — a late rejection after a requeue is accepted, sweep can't re-send ──

    [DockerRequiredFact]
    public async Task C2_LateSupplierRejection_AfterRequeue_IsAccepted_AndLeavesTheSweepersReach()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.ReadyToDeliver, agedMinutes: 5);

        // Genuinely dispatched (markers on row 1), then dead-lettered at the cap.
        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            Assert.True((await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None)).Success);
        }
        await using (var seed = NewContext())
        {
            for (var i = 2; i <= 3; i++)
                seed.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                    AttemptNumber = i, AttemptedAt = DateTime.UtcNow,
                });
            await seed.PurchaseOrders
                .Where(o => o.Id == ids.OrderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatusConstants.DeliveryDeadLetter));
            await seed.SaveChangesAsync();
        }

        // Operator requeues; the re-driven dispatch fails pre-dispatch (markerless row) and the
        // order rests in delivery_failed — the exact compound the KNOWN_GAP documented.
        await RequeueAsync(ids.OrgId, ids.OrderId);
        await using (var seed = NewContext())
        {
            seed.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                Channel = "missing_config", Destination = "supplier delivery config",
                Status = DeliveryAttempt.StatusFailed, AttemptNumber = 4,
                AttemptedAt = DateTime.UtcNow, ErrorMessage = "Supplier delivery config is missing.",
            });
            await seed.SaveChangesAsync();
        }

        // The late supplier rejection arrives. Evidence SURVIVED the requeue, so the callback is
        // ACCEPTED and the order leaves the sweeper's status filter.
        await using (var db = NewContext())
        {
            var verifier = new Mock<IHmacWebhookVerifier>();
            verifier.Setup(v => v.VerifyAsync(
                    "cap-slug", It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HmacVerificationResult(true, null, ids.OrgId));
            var ctrl = new WebhookIngressController(
                verifier.Object, db, new OrderExceptionService(db),
                NullLogger<WebhookIngressController>.Instance);
            SetHttpContext(ctrl, body: $"{{\"orderId\":\"{ids.OrderId}\",\"status\":\"rejected\"}}");

            var result = await ctrl.Status("cap-slug", CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        await using (var verify = NewContext())
        {
            var status = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.RejectedBySupplier, status);
        }

        // The stranded-failed sweep finds nothing to re-drive — the rejected PO cannot be re-sent.
        var enqueuer = new RecordingRetryEnqueuer();
        await using (var db = NewContext())
        {
            var sweep = new StrandedFailedDeliveryDetectionService(
                db, NullLogger<StrandedFailedDeliveryDetectionService>.Instance,
                new DeliveryReliabilityOptions(), enqueuer);
            var acted = await sweep.RunAsync(TimeSpan.FromHours(3), CancellationToken.None);
            Assert.Equal(0, acted);
        }
        Assert.Empty(enqueuer.Calls);
    }

    // ── C3: condition 2 — the sweep still re-drives a requeued order (rows survive superseded) ──

    [DockerRequiredFact]
    public async Task C3_OpsRequeue_ThenLostReEnqueue_SweepStillRedrives()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.DeliveryDeadLetter, agedMinutes: 5);

        await using (var seed = NewContext())
        {
            for (var i = 1; i <= 3; i++)
                seed.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                    AttemptNumber = i, AttemptedAt = DateTime.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        await RequeueAsync(ids.OrgId, ids.OrderId);

        // The re-enqueued DeliverOrderJob was LOST; the order ages in delivery_failed.
        await using (var age = NewContext())
        {
            await age.PurchaseOrders.Where(o => o.Id == ids.OrderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, DateTime.UtcNow.AddHours(-4)));
        }

        // The sweep must still see attempts REMAINING: the three surviving rows are superseded, so
        // the cap subquery — re-based onto the shared predicate — reads 0 < 3. If it still counted
        // raw terminal rows, it would read 3 ≥ 3 and skip this order FOREVER: an operator clicked
        // Requeue and nothing happened, silently.
        var enqueuer = new RecordingRetryEnqueuer();
        await using (var db = NewContext())
        {
            var sweep = new StrandedFailedDeliveryDetectionService(
                db, NullLogger<StrandedFailedDeliveryDetectionService>.Instance,
                new DeliveryReliabilityOptions(), enqueuer);
            var acted = await sweep.RunAsync(TimeSpan.FromHours(3), CancellationToken.None);
            Assert.Equal(1, acted);
        }
        Assert.Equal((ids.OrderId, ids.OrgId), Assert.Single(enqueuer.Calls));
    }

    // ── C4: the cap reset works for RetryDeliveryAsync — not instantly re-dead-lettered ─────────

    [DockerRequiredFact]
    public async Task C4_OpsRequeue_ResetsCap_RetryDeliveryDispatchesInsteadOfDeadLettering()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.DeliveryDeadLetter, agedMinutes: 5);

        await using (var seed = NewContext())
        {
            for (var i = 1; i <= 3; i++)
                seed.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                    AttemptNumber = i, AttemptedAt = DateTime.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        await RequeueAsync(ids.OrgId, ids.OrderId);

        // A retry against the requeued order must count the fresh budget (0), claim, and dispatch —
        // an unreset cap would dead-letter it right back ("Maximum delivery attempts reached").
        var dispatcher = new CountingDispatcher();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            var result = await svc.RetryDeliveryAsync(ids.OrgId, ids.OrderId, maxAttempts: 3, CancellationToken.None);
            Assert.True(result.Success);
            Assert.NotEqual(DeliveryOutcome.NotRetryable, result.Outcome);
        }
        Assert.Equal(1, dispatcher.Calls);
    }

    // ── C7: every cap site agrees on adversarial rows ───────────────────────────────────────────

    [DockerRequiredFact]
    public async Task C7_CapSites_AgreeOnAdversarialRows()
    {
        var encryption = CreateEncryption();
        var ids = await SeedDeliverableOrderAsync(encryption, status: OrderStatusConstants.DeliveryFailed, agedMinutes: 5);

        await using (var seed = NewContext())
        {
            // Superseded terminal (a previous budget), live terminal (current budget), and an
            // in-flight dispatching row — the three shapes the predicates must tell apart.
            seed.DeliveryAttempts.AddRange(
                new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                    AttemptNumber = 1, AttemptedAt = DateTime.UtcNow.AddHours(-2),
                    CapSupersededAt = DateTime.UtcNow.AddHours(-1),
                    IdempotencyKey = "old-budget-key", ArtifactSha256 = "aa",
                },
                new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusFailed,
                    AttemptNumber = 2, AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
                },
                new DeliveryAttempt
                {
                    Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                    Channel = "http", Destination = "d", Status = DeliveryAttempt.StatusDispatching,
                    AttemptNumber = 3, AttemptedAt = DateTime.UtcNow,
                    IdempotencyKey = "in-flight-key",
                });
            await seed.SaveChangesAsync();
        }

        await using var verify = NewContext();

        // Site 1 — the service's public count (DeliverOrderJob + RetryDeliveryJob feed off it).
        var svc = BuildService(verify, new CountingDispatcher(), encryption);
        var serviceCount = await svc.CountDeliveryAttemptsAsync(ids.OrgId, ids.OrderId, CancellationToken.None);

        // The shared predicate, composed the way every site composes it.
        var predicateCount = await verify.DeliveryAttempts
            .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId)
            .Where(DeliveryAttempt.CountsAgainstCap)
            .CountAsync();

        // Only the live terminal row counts: the superseded row left the budget, the dispatching
        // row is not yet a completed attempt.
        Assert.Equal(1, predicateCount);
        Assert.Equal(predicateCount, serviceCount);

        // Site 3 — the sweep's eligibility must agree: 1 < 3 ⇒ this delivery_failed order (aged
        // below) is a candidate. Age it first so the threshold passes.
        await verify.PurchaseOrders.Where(o => o.Id == ids.OrderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, DateTime.UtcNow.AddHours(-4)));

        // The dispatching row blocks the sweep's separate in-flight filter — that predicate is a
        // DIFFERENT question ("a send is mid-flight") and must keep blocking regardless of the cap.
        var enqueuer = new RecordingRetryEnqueuer();
        var sweep = new StrandedFailedDeliveryDetectionService(
            verify, NullLogger<StrandedFailedDeliveryDetectionService>.Instance,
            new DeliveryReliabilityOptions(), enqueuer);
        Assert.Equal(0, await sweep.RunAsync(TimeSpan.FromHours(3), CancellationToken.None));

        // Remove the in-flight row: with 1 live terminal < 3 the sweep now re-drives.
        await verify.DeliveryAttempts
            .Where(a => a.OrderId == ids.OrderId && a.Status == DeliveryAttempt.StatusDispatching)
            .ExecuteDeleteAsync();
        Assert.Equal(1, await sweep.RunAsync(TimeSpan.FromHours(3), CancellationToken.None));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drives the REAL OpsController.RequeueDelivery against Postgres.</summary>
    private async Task RequeueAsync(Guid orgId, Guid orderId)
    {
        await using var db = NewContext();
        var orderForValidation = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.OutboundArtifacts)
            .FirstAsync(o => o.Id == orderId);

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(orgId, orderId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(orderForValidation));
        var jobs = new Mock<Hangfire.IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
            .Returns(Guid.NewGuid().ToString());

        var ctrl = new OpsController(
            new Mock<IOpsHealthService>().Object, tenant.Object, orders.Object,
            jobs.Object, db, NullLogger<OpsController>.Instance);

        var result = await ctrl.RequeueDelivery(orderId, CancellationToken.None);
        Assert.IsType<AcceptedResult>(result);
    }

    private static void SetHttpContext(WebhookIngressController ctrl, string body)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-ProcuLink-Timestamp"] = "2026-07-23T00:00:00Z";
        httpContext.Request.Headers["X-ProcuLink-Nonce"]     = "nonce-cap";
        httpContext.Request.Headers["X-ProcuLink-Signature"] = "sig-placeholder";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Body          = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private sealed class RecordingRetryEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
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

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
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

    private static DeliveryService BuildService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption) =>
        new(
            db,
            new InMemoryFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

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
            Id = orgId, ClerkOrgId = $"org_cap_{orgId:N}", Name = "Cap Org",
            Slug = $"cap-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = aged,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Cap Supplier", CreatedAt = aged });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-CAP-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
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
