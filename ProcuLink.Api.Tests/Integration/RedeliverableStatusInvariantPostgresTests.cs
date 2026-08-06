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
/// The structural invariant behind a whole class of SILENT delivery strands.
///
/// <para><b>The drift class.</b> "Send again" (<c>OrdersController.Redeliver</c>) admits exactly the
/// statuses in <see cref="OrderStatusMachine.RedeliverableFrom"/> — that one named set decides 202-vs-400.
/// Everything the resulting <c>DeliverOrderJob</c> then traverses carries its OWN hand-written status
/// list: <c>DeliveryService.DispatchArtifactAsync</c>'s atomic claim (twice — a relational
/// <c>ExecuteUpdateAsync</c> predicate and its InMemory emulation) and <c>HoldForBillingAsync</c>'s
/// holdable set. Nothing makes those lists agree with the set that let the operator press the button.</para>
///
/// <para><b>Why the failure is silent.</b> A status the controller admits but the claim does not claim
/// matches 0 rows, which <c>DispatchArtifactAsync</c> reads as a benign "someone else claimed it" and
/// reports as <c>DeliveryResult(true, null)</c>. The API returned 202, the job logs SUCCESS, no attempt
/// row is written, no exception is raised — and the PO is never sent. The billing twin is worse: a status
/// the controller admits but <c>HoldForBillingAsync</c> refuses holds nothing, sends nothing, audits
/// nothing, and is never rescued by <c>ReleaseBillingHeldOrdersAsync</c>, because the order never becomes
/// <c>delivery_held</c>. This has bitten twice already (52c6431, 392b5a4), on the same branch.</para>
///
/// <para><b>The invariant.</b> If an operator can be offered "Send again" from a status, then every
/// downstream gate that "Send again" traverses must accept that status. Stated behaviourally, per status
/// in <see cref="OrderStatusMachine.RedeliverableFrom"/>:
/// <list type="number">
/// <item>the delivery claim CLAIMS it — proven by a real dispatch + a <c>DeliveryAttempt</c> row, never
/// by the returned <c>Success</c> flag, which is <c>true</c> for the silent-strand case too;</item>
/// <item><c>HoldForBillingAsync</c> HOLDS it — the branch a lapsed org's "Send again" takes instead.</item>
/// </list></para>
///
/// <para><b>Why behavioural, not set-vs-set.</b> Asserting <c>RedeliverableFrom ⊆ SomeNamedSet</c> only
/// works once every gate derives from that named set; until then it would pin a declaration against a
/// parallel declaration and let the real literals drift underneath. This form calls the production code,
/// so it holds against today's four hand-written literals AND against the canonical-predicate refactor
/// designed in <c>docs/superpowers/specs/2026-07-16-delivery-claim-canonical-predicate-design.md</c>.
/// It also enumerates the canonical set, so a status ADDED to <c>RedeliverableFrom</c> enters this test
/// automatically — which is precisely the commit where the drift is introduced.</para>
///
/// <para><b>Real Postgres is mandatory.</b> The claim is an <c>ExecuteUpdateAsync</c> predicate; the EF
/// InMemory provider cannot translate it and does not exercise it at all, so an InMemory version of this
/// test would assert the emulation and prove nothing about production. Docker-gated; skips where Docker
/// is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class RedeliverableStatusInvariantPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_redeliv");

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
        public string Protocol => "http";

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
    /// Seeds org + supplier + delivery config + an order in <paramref name="status"/> with one artifact —
    /// the state a "Send again" press acts on.
    /// </summary>
    private async Task<(Guid OrgId, Guid OrderId, Guid ArtifactId)> SeedOrderInStatusAsync(
        DeliveryEncryptionService encryption, string status)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_redeliv_{orgId:N}", Name = "Redeliver Org",
            Slug = $"redeliv-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Redeliver Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = $"PO-REDELIV-{status}", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
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
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return (orgId, orderId, artifactId);
    }

    /// <summary>
    /// Every status "Send again" admits must be CLAIMABLE by the delivery claim.
    ///
    /// <para>Dispatch evidence is the dispatcher call plus the <c>DeliveryAttempt</c> row, deliberately
    /// NOT <c>result.Success</c>: the lost-claim branch returns <c>DeliveryResult(true, null)</c>, so a
    /// Success-based assertion would pass on the exact defect this test exists to catch.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task EveryRedeliverableStatus_IsClaimedAndDispatched()
    {
        var encryption = CreateEncryption();
        var strands = new List<string>();

        foreach (var status in OrderStatusMachine.RedeliverableFrom)
        {
            var ids = await SeedOrderInStatusAsync(encryption, status);
            var dispatcher = new CountingDispatcher();

            await using (var db = NewContext())
            {
                var svc = BuildService(db, dispatcher, encryption);
                await svc.DispatchArtifactAsync(
                    ids.OrgId, ids.OrderId, ids.ArtifactId, requireAutoDeliver: false, CancellationToken.None);
            }

            await using var verify = NewContext();
            // Count SUCCESS rows, not every attempt row: a seed that grows a prior failed attempt
            // (the realistic state for delivery_failed) must not read as a claim failure here.
            var succeeded = await verify.DeliveryAttempts.AsNoTracking()
                .Where(a => a.OrderId == ids.OrderId && a.OrgId == ids.OrgId && a.Status == "success")
                .CountAsync();
            var finalStatus = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId && o.OrgId == ids.OrgId)
                .Select(o => o.Status).SingleAsync();

            // The order must also LAND: a dispatch that never writes the terminal status leaves the
            // order stuck in 'delivering' — a strand one step later in the same method.
            if (dispatcher.Calls != 1 || succeeded != 1 || finalStatus != OrderStatusConstants.Delivered)
                strands.Add(
                    $"{status}: dispatches={dispatcher.Calls} (expected 1), successAttemptRows={succeeded} " +
                    $"(expected 1), finalStatus={finalStatus} (expected delivered)");
        }

        Assert.True(strands.Count == 0,
            "Every status in OrderStatusMachine.RedeliverableFrom must be claimable by DispatchArtifactAsync's " +
            "atomic claim. A status the Redeliver endpoint accepts (202) but the claim cannot claim matches 0 " +
            "rows, which the claim reports as a benign DeliveryResult(true, null) — so the job logs SUCCESS, no " +
            "attempt row is written, and the PO is silently never sent. Add the status to BOTH claim paths in " +
            "DeliveryService.DispatchArtifactAsync (the relational ExecuteUpdateAsync predicate and its InMemory " +
            "emulation), or remove it from RedeliverableFrom so the endpoint honestly returns 400. Silently " +
            "stranded: " + string.Join(" | ", strands));
    }

    /// <summary>
    /// Every status "Send again" admits must be HOLDABLE by the billing gate.
    ///
    /// <para><c>DeliverOrderJob</c> checks <c>CanProcessOrdersAsync</c> before dispatching and routes a
    /// lapsed org's order to <c>HoldForBillingAsync</c>. Redeliver deliberately does NOT pre-flip the
    /// status before enqueueing (the B2 lost-order fix), so the hold sees the same status the endpoint
    /// admitted. A status it refuses holds nothing, audits nothing, and never reaches
    /// <c>ReleaseBillingHeldOrdersAsync</c>'s <c>delivery_held</c> sweep, so reactivation never rescues it.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task EveryRedeliverableStatus_IsHeldByTheBillingGate()
    {
        var encryption = CreateEncryption();
        var unheld = new List<string>();

        foreach (var status in OrderStatusMachine.RedeliverableFrom)
        {
            var ids = await SeedOrderInStatusAsync(encryption, status);

            // A hold must PAUSE the send, never perform it: a held order that still reaches a
            // dispatcher would meter the €0.50 overage the billing gate exists to prevent.
            var dispatcher = new CountingDispatcher();
            bool held;
            await using (var db = NewContext())
            {
                var svc = BuildService(db, dispatcher, encryption);
                held = await svc.HoldForBillingAsync(ids.OrgId, ids.OrderId, CancellationToken.None);
            }

            await using var verify = NewContext();
            var finalStatus = await verify.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ids.OrderId && o.OrgId == ids.OrgId)
                .Select(o => o.Status).SingleAsync();
            var audited = await verify.AuditEvents.AsNoTracking()
                .Where(a => a.OrgId == ids.OrgId && a.EntityId == ids.OrderId && a.Action == "DeliveryHeldForBilling")
                .CountAsync();

            if (!held || finalStatus != OrderStatusConstants.DeliveryHeld || audited != 1 || dispatcher.Calls != 0)
                unheld.Add(
                    $"{status}: held={held}, finalStatus={finalStatus} (expected delivery_held), " +
                    $"auditRows={audited} (expected 1), dispatches={dispatcher.Calls} (expected 0)");
        }

        Assert.True(unheld.Count == 0,
            "Every status in OrderStatusMachine.RedeliverableFrom must be holdable by " +
            "DeliveryService.HoldForBillingAsync. A lapsed org's 'Send again' from a status the hold refuses " +
            "holds nothing, sends nothing and audits nothing — and because the order never becomes " +
            "'delivery_held', ReleaseBillingHeldOrdersAsync never re-drives it on reactivation, so it is " +
            "stranded permanently and invisibly. Add the status to HoldForBillingAsync's holdable set, or " +
            "remove it from RedeliverableFrom. Not held: " + string.Join(" | ", unheld));
    }
}
