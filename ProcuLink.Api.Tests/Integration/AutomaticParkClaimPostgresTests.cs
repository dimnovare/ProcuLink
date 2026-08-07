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
/// Queue item 5 (2026-07-23) — the "~50% park race": an AUTOMATIC delivery activation must never
/// claim a <c>delivery_unconfirmed</c> park.
///
/// <para><b>The race (each step verified in code).</b> A <c>DeliverOrderJob</c> activation dies
/// mid-send (Worker deploy/OOM). Its in-flight job is REFETCHED by Hangfire ~30 min later
/// (Hangfire.PostgreSql's default non-sliding <c>InvisibilityTimeout</c> — Worker
/// <c>Program.cs</c> configures a bare <c>UseNpgsqlConnection</c>, and <c>Attempts = 0</c> does not
/// cover process death). Meanwhile <c>StuckDeliveryDetectionService</c> re-drove the order and the
/// retry re-adopted the crashed send's <c>dispatching</c> row and PARKED it
/// (<c>ParkUnconfirmedAsync</c> — order <c>delivery_unconfirmed</c>, row finalised terminal
/// <c>'unconfirmed'</c>). The refetched activation then re-ran
/// <c>DispatchArtifactAsync</c>, whose claim admitted <c>delivery_unconfirmed</c> unconditionally;
/// <c>OpenDispatchAttemptAsync</c> found no <c>dispatching</c> row to re-adopt (the parked row is
/// terminal), so it opened a FRESH attempt, skipped the re-adopt park guard, and SENT — an
/// automatic send of a parked PO, the exact duplicate the park exists to prevent.</para>
///
/// <para><b>The fix under test.</b> The claim admits <c>delivery_unconfirmed</c> only when
/// <c>requireAutoDeliver == false</c>, which is true exactly when a human pressed a button
/// (<c>DeliverOrderJob.EnqueueRedeliver</c>: <c>OrdersController.Redeliver</c> — the park's "Send
/// again" — and <c>OpsController.RequeueDelivery</c>). Every automatic activation
/// (<c>DeliverOrderJob.Enqueue</c>: <c>TransformOrderJob</c>, the stranded-ready sweep, and any
/// Hangfire refetch of those) passes <c>true</c> and is refused. The retry queue never needed this
/// gate: <c>RetryDeliveryAsync</c> refuses the status outright and its claim never admitted it.</para>
///
/// <para><b>Why real Postgres.</b> The enforcement is the relational <c>ExecuteUpdateAsync</c>
/// predicate — the advisory status reads before it can be stale (the sweep can park the order
/// between the refetched job's read and its claim), so the predicate is the only gate that holds
/// under the race. EF InMemory never executes it; the InMemory emulation branch is pinned
/// separately by <c>DeliveryServiceUnconfirmedParkTests.AutomaticActivation_FromParkedOrder_IsRefused_AndSendsNothing</c>.</para>
///
/// <para><b>The seeded park shape covers the restored park too.</b> A billing-hold release restores
/// a held park to exactly this state (PR #40: status <c>delivery_unconfirmed</c>, live
/// <c>DeliveryDueAt</c>, terminal <c>'unconfirmed'</c> attempt row still carrying its
/// <c>IdempotencyKey</c>), indistinguishable by construction from a fresh park — so these tests
/// protect both.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class AutomaticParkClaimPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_parkclaim");

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_databaseConnectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Protocol => "email";
        // Explicit, not the interface default: the park arises on Unsafe channels, so the refetched
        // automatic activation that races it runs against an Unsafe dispatcher too.
        public ResendSafety ResendSafety => ResendSafety.Unsafe;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct,
            string? idempotencyKey = null)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
    }

    private sealed class StubFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
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
            new StubFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    /// <summary>
    /// Seeds the exact post-park state the refetched activation finds: order
    /// <c>delivery_unconfirmed</c> with a LIVE SLA nag (<c>ParkUnconfirmedAsync</c> deliberately
    /// leaves <c>DeliveryDueAt</c> running), and its attempt row finalised terminal
    /// <c>'unconfirmed'</c> still carrying the dispatch-marker <c>IdempotencyKey</c>. The delivery
    /// config has <c>AutoDeliver = true</c> — the canonical refetch victim is the post-transform
    /// auto-delivery, and <c>DispatchArtifactAsync</c>'s AutoDeliver-off no-op runs BEFORE the
    /// claim, so seeding it false would mask a missing refusal.
    /// </summary>
    private async Task<(Guid OrgId, Guid OrderId, Guid ArtifactId, DateTime DueAt)> SeedParkedOrderAsync(
        DeliveryEncryptionService encryption)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;
        var parkedAt   = now.AddMinutes(-10);
        var dueAt      = parkedAt.AddHours(2);

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_parkclaim_{orgId:N}", Name = "Park Claim Org",
            Slug = $"parkclaim-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Park Claim Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-PARK-CLAIM", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = OrderStatusConstants.DeliveryUnconfirmed,
            DeliveryDueAt = dueAt, SlaBreached = false,
            CreatedAt = now.AddHours(-1), UpdatedAt = parkedAt,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = now.AddHours(-1),
        });
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "email", AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt(
                "{\"type\":\"none\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt = now, UpdatedAt = now,
        });
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId,
            Channel = "email", Destination = "orders@supplier.example",
            Status = DeliveryAttempt.StatusUnconfirmed, AttemptNumber = 1,
            AttemptedAt = parkedAt,
            IdempotencyKey = DeliveryService.BuildIdempotencyKey(orderId, artifactId),
            ErrorMessage = DeliveryService.BuildUnconfirmedMessage("email"),
        });
        await db.SaveChangesAsync();

        return (orgId, orderId, artifactId, dueAt);
    }

    /// <summary>
    /// The refetched automatic activation (<c>requireAutoDeliver: true</c>) against a parked order:
    /// nothing is sent, the order does not move, no fresh attempt row is written, and the SLA nag
    /// the park deliberately leaves running is untouched (proving the claim's
    /// <c>ExecuteUpdateAsync</c> matched 0 rows — it would otherwise stamp a new
    /// <c>DeliveryDueAt</c>).
    /// </summary>
    [DockerRequiredFact]
    public async Task AutomaticActivation_AgainstAParkedOrder_DoesNotSendAndDoesNotMoveIt()
    {
        var encryption = CreateEncryption();
        var ids = await SeedParkedOrderAsync(encryption);
        var dispatcher = new CountingDispatcher();

        DeliveryResult result;
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: true, CancellationToken.None);
        }

        Assert.Equal(0, dispatcher.Calls);
        // The benign no-op DeliverOrderJob stops on: Success=true means no failure log and no
        // retry seeded; ClaimLost records that nothing was dispatched. (In DeliverOrderJob only —
        // RetryDeliveryJob's reschedule-on-ClaimLost contract applies to RetryDeliveryAsync's
        // claim, which this path never returns through.)
        Assert.True(result.Success);
        Assert.Equal(DeliveryOutcome.ClaimLost, result.Outcome);

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Id == ids.OrderId && o.OrgId == ids.OrgId);
        Assert.Equal(OrderStatusConstants.DeliveryUnconfirmed, order.Status);
        Assert.NotNull(order.DeliveryDueAt);
        Assert.Equal(ids.DueAt, order.DeliveryDueAt!.Value, TimeSpan.FromSeconds(1));

        var attempts = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId)
            .ToListAsync();
        var attempt = Assert.Single(attempts);
        Assert.Equal(DeliveryAttempt.StatusUnconfirmed, attempt.Status);
    }

    /// <summary>
    /// The contrast pair, against the IDENTICAL parked state: the operator's "Send again"
    /// (<c>requireAutoDeliver: false</c>, <c>DeliverOrderJob.EnqueueRedeliver</c>) must still claim
    /// the park and actually dispatch — the park exists so a HUMAN can re-send.
    /// <c>RedeliverableStatusInvariantPostgresTests</c> pins this for every redeliverable status;
    /// repeated here against the full parked shape (terminal <c>'unconfirmed'</c> row present) so
    /// the pair cannot drift apart.
    /// </summary>
    [DockerRequiredFact]
    public async Task OperatorRedeliver_AgainstTheSameParkedOrder_ClaimsAndSends()
    {
        var encryption = CreateEncryption();
        var ids = await SeedParkedOrderAsync(encryption);
        var dispatcher = new CountingDispatcher();

        DeliveryResult result;
        await using (var db = NewContext())
        {
            var svc = BuildService(db, dispatcher, encryption);
            result = await svc.DispatchArtifactAsync(
                ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
        }

        Assert.Equal(1, dispatcher.Calls);
        Assert.True(result.Success);
        Assert.Equal(DeliveryOutcome.Dispatched, result.Outcome);

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Id == ids.OrderId && o.OrgId == ids.OrgId);
        Assert.Equal(OrderStatusConstants.Delivered, order.Status);

        var attempts = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync();
        Assert.Equal(2, attempts.Count);
        // The parked row records an outcome nobody observed — a later send never rewrites it.
        Assert.Equal(DeliveryAttempt.StatusUnconfirmed, attempts[0].Status);
        Assert.Equal("success", attempts[1].Status);
    }
}
