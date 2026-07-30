using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The release-vs-webhook race, closed (#36 secondary scope; the window was documented in
/// <c>ReleaseBillingHeldOrdersAsync</c>'s "KNOWN WINDOW" note when PR #40 shipped around it).
///
/// <para><b>The race.</b> A held row can be moved by another writer at any moment. The old release
/// read the held rows TRACKED and wrote them back with <c>SaveChanges</c> and no concurrency token,
/// so anything landing in the milliseconds between its SELECT and its save was overwritten
/// BACKWARDS: a just-settled order flipped to <c>ready_to_deliver</c> and re-driven (a duplicate
/// send of a PO already confirmed), or a just-settled park "restored" to
/// <c>delivery_unconfirmed</c> with a reopened nag over an order whose outcome was already known.</para>
///
/// <para><b>The producer has changed; the race has not. (WP-09.)</b> This test — and the class and
/// method names, which are historical — were written when the concurrent claimant was the inbound
/// supplier-status callback, which could report a held order terminal via
/// <c>ExecuteUpdateAsync</c>. That subsystem is retired, and with it the ONLY writer of
/// <c>delivery_held → delivered</c>; the edge is gone from both transition maps. What survives is
/// the race SHAPE, and it still has real producers: a concurrent (or Hangfire-retried) invocation
/// of <c>ReleaseBillingHeldOrdersAsync</c> itself, and an operator "mark rejected"
/// (<c>OrderResolutionService.MarkRejectedAsync</c> carries no from-status guard, so it moves a held
/// row too). The guarded atomic claim is what makes both safe, and that is what is under test.</para>
///
/// <para><b>Known imprecision, deliberately not changed blind.</b> The interceptor writes
/// <c>delivered</c> as its sentinel for "some other claimant got here first". Since WP-09 nothing in
/// production writes <c>delivered</c> from <c>delivery_held</c>, so a producible target
/// (<c>rejected_by_supplier</c>) would be strictly more honest. The mechanism under test is
/// unaffected — the assertion is that the release yields 0 rows and SKIPS, not that the row reached
/// any particular status — and swapping it needs a real-Postgres run to verify, which the host's
/// wedged Docker daemon could not provide. Filed rather than guessed.</para>
///
/// <para><b>The fix under test.</b> Release claims each row atomically —
/// <c>ExecuteUpdateAsync</c> guarded on <c>Status == delivery_held</c>, in one transaction with
/// that row's audit event — so a row another claimant moved first yields 0 rows and is skipped:
/// released count, audit trail and re-drive list all exclude it. The claim-shape is the same
/// "guarded atomic status flip" the canonical delivery-claim predicate work standardises; the
/// target set here is {delivery_held}, which never composes a staleness window, so it stays a
/// plain guarded update rather than a <c>DeliveryClaim</c> composition.</para>
///
/// <para><b>Determinism.</b> A <see cref="DbCommandInterceptor"/> on the release's own
/// <c>DbContext</c> fires the competing write immediately AFTER the held-list SELECT returns and
/// BEFORE the release writes anything — the exact interleaving the window describes, every run.
/// Real Postgres is mandatory: the race is between two connections and an
/// <c>ExecuteUpdateAsync</c>, neither of which InMemory executes.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class BillingReleaseWebhookRacePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_relrace_{Guid.NewGuid():N}")
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
    /// A re-drive-origin hold (<c>HeldFromStatus: ready_to_deliver</c>) whose order ANOTHER claimant
    /// settles between the release's read and its write. The release must lose that race — never flip
    /// the settled order back to <c>ready_to_deliver</c> and re-send it — and must still release the
    /// org's OTHER held order normally. (Written for the retired supplier callback; see the class doc
    /// for the claimants that produce this race today.)
    /// </summary>
    [DockerRequiredFact]
    public async Task Release_LosesTheRace_ToAWebhookDeliveredReDriveHold()
    {
        var encryption = CreateEncryption();
        var orgId  = await SeedOrgAsync();
        var raced  = await SeedHeldOrderAsync(orgId, heldFrom: OrderStatusConstants.ReadyToDeliver);
        var normal = await SeedHeldOrderAsync(orgId, heldFrom: OrderStatusConstants.ReadyToDeliver);

        var (released, enqueuer, interceptor) = await RunReleaseWithMidwayWebhookAsync(orgId, raced, encryption);

        interceptor.Fired.Should().BeTrue("the webhook write must actually have interleaved, or this test proves nothing");

        await using var verify = NewContext();
        var racedRow = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == raced && o.OrgId == orgId);
        racedRow.Status.Should().Be(OrderStatusConstants.Delivered,
            "the supplier's terminal report landed between the release's read and its write — " +
            "the release must lose that race, never overwrite it backwards into a re-send");

        var normalRow = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == normal && o.OrgId == orgId);
        normalRow.Status.Should().Be(OrderStatusConstants.ReadyToDeliver, "the un-raced hold releases normally");
        normalRow.HeldFromStatus.Should().BeNull();

        released.Should().Be(1, "only the un-raced order actually left the hold");
        enqueuer.Calls.Should().BeEquivalentTo(new[] { (normal, orgId) },
            "a just-delivered order must never be re-driven");
        (await verify.AuditEvents.CountAsync(e => e.EntityId == raced && e.Action == "DeliveryHoldReleased"))
            .Should().Be(0, "no release may be recorded for an order the release did not move");
        (await verify.AuditEvents.CountAsync(e => e.EntityId == normal && e.Action == "DeliveryHoldReleased"))
            .Should().Be(1);
    }

    /// <summary>
    /// The park-origin sibling (<c>HeldFromStatus: delivery_unconfirmed</c>): another claimant answers
    /// the exact question the park was waiting on while the order sits held. The
    /// release must not "restore" the park over the answer — that would reopen the SLA nag and ask
    /// an operator to re-decide an outcome that is already settled. Pinned separately because
    /// the park restore is a DIFFERENT guarded write than the re-drive release, and the two
    /// drifting apart is this change's whole subject.
    /// </summary>
    [DockerRequiredFact]
    public async Task Release_LosesTheRace_ToAWebhookDeliveredHeldPark()
    {
        var encryption = CreateEncryption();
        var orgId = await SeedOrgAsync();
        var raced = await SeedHeldOrderAsync(orgId, heldFrom: OrderStatusConstants.DeliveryUnconfirmed);

        var (released, enqueuer, interceptor) = await RunReleaseWithMidwayWebhookAsync(orgId, raced, encryption);

        interceptor.Fired.Should().BeTrue("the webhook write must actually have interleaved, or this test proves nothing");

        await using var verify = NewContext();
        var racedRow = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == raced && o.OrgId == orgId);
        racedRow.Status.Should().Be(OrderStatusConstants.Delivered,
            "the supplier answered the park's question — the release must not restore the park over it");
        racedRow.DeliveryDueAt.Should().BeNull(
            "reopening the SLA nag over a delivered order would page an operator about a decision nobody owes");

        released.Should().Be(0);
        enqueuer.Calls.Should().BeEmpty();
        (await verify.AuditEvents.CountAsync(e => e.EntityId == raced && e.Action == "DeliveryHoldReleased"))
            .Should().Be(0);
    }

    // ── The interleave ──────────────────────────────────────────────────────────────────────────

    private async Task<(int Released, RecordingRetryEnqueuer Enqueuer, WebhookAfterHeldReadInterceptor Interceptor)>
        RunReleaseWithMidwayWebhookAsync(Guid orgId, Guid racedOrderId, DeliveryEncryptionService encryption)
    {
        var interceptor = new WebhookAfterHeldReadInterceptor(async () =>
        {
            // A concurrent terminal claim, in the shape any ExecuteUpdateAsync claimant lands it: an
            // atomic guarded ExecuteUpdate on its OWN connection, committed before the release
            // gets to write anything.
            await using var db = NewContext();
            var claimed = await db.PurchaseOrders
                .Where(o => o.Id == racedOrderId && o.OrgId == orgId
                         && o.Status == OrderStatusConstants.DeliveryHeld)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Delivered)
                    .SetProperty(o => o.DeliveryDueAt, (DateTime?)null)
                    .SetProperty(o => o.SlaBreached, false)
                    .SetProperty(o => o.UpdatedAt, DateTime.UtcNow));
            if (claimed != 1)
                throw new InvalidOperationException(
                    $"Race setup failed: the webhook claim affected {claimed} rows instead of 1.");
        });

        var racingOptions = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_pg!.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;

        var enqueuer = new RecordingRetryEnqueuer();
        int released;
        await using (var db = new ProcuLinkDbContext(racingOptions))
        {
            var svc = BuildService(db, encryption, enqueuer);
            released = await svc.ReleaseBillingHeldOrdersAsync(orgId, CancellationToken.None);
        }

        return (released, enqueuer, interceptor);
    }

    /// <summary>
    /// Fires <paramref name="race"/> once, immediately after the first SELECT that filters on
    /// <c>delivery_held</c> (the release's held-list read) — i.e. exactly inside the window the
    /// old KNOWN-WINDOW comment described. The status literal is a <c>const</c>, so EF inlines it
    /// into the SQL text.
    /// </summary>
    private sealed class WebhookAfterHeldReadInterceptor(Func<Task> race) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            var text = command.CommandText.TrimStart();
            if (text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && text.Contains(OrderStatusConstants.DeliveryHeld, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                await race();
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingRetryEnqueuer : IRetryDeliveryEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();
        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDispatcher : IDeliveryDispatcher
    {
        public string Protocol => "http";
        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null)
            => Task.FromResult(new DeliveryResult(true, null, 200));
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

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static DeliveryService BuildService(
        ProcuLinkDbContext db, DeliveryEncryptionService encryption, IRetryDeliveryEnqueuer enqueuer) =>
        new(
            db,
            new InMemoryFileStorage(),
            encryption,
            new IDeliveryDispatcher[] { new NoOpDispatcher() },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            retryEnqueuer: enqueuer);

    private async Task<Guid> SeedOrgAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_relrace_{orgId:N}", Name = "Release Race Org",
            Slug = $"relrace-{orgId:N}", Plan = "operations", AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private async Task<Guid> SeedHeldOrderAsync(Guid orgId, string heldFrom)
    {
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewContext();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = null,
            PoNumber = $"PO-RACE-{heldFrom}", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = OrderStatusConstants.DeliveryHeld,
            HeldFromStatus = heldFrom,
            // Exactly HoldForBillingAsync's row shape: the hold pauses the SLA window.
            DeliveryDueAt = null, SlaBreached = false,
            CreatedAt = now.AddHours(-1), UpdatedAt = now.AddMinutes(-5),
        });
        await db.SaveChangesAsync();
        return orderId;
    }
}
