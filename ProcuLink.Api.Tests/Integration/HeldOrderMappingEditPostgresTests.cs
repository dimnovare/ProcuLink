using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The wrong document reaching the supplier — proven on REAL Postgres, and asserted on the BYTES
/// the dispatcher was handed rather than on an artifact id, a status, or a row count. Those all
/// answer "did the plumbing move?"; only the bytes answer the question the operator cares about,
/// which is "did the correction I typed reach the counterparty?"
///
/// <para><b>The failure this pins.</b> An org lapses to <c>past_due</c> while a transformed order is
/// waiting to go out, so <c>HoldForBillingAsync</c> parks it in <c>delivery_held</c> holding its
/// already-built artifact. The operator notices a wrong supplier item code and edits the order's
/// mapping. <c>OrderMappingOverrideService.UpsertAsync</c> only resets an order to <c>ready</c>
/// (forcing a re-transform before the next send) when the status is one that holds a stale
/// artifact — and <c>delivery_held</c> was missing from that list. Billing is then restored,
/// <c>ReleaseBillingHeldOrdersAsync</c> writes <c>ready_to_deliver</c> and enqueues a retry, and
/// <c>RetryDeliveryAsync</c> resolves the STORED artifact and dispatches it. The pre-edit document
/// goes to the supplier and nothing anywhere reports that the correction was dropped.</para>
///
/// <para><b>Why real Postgres.</b> The chain crosses three writers and two atomic claims —
/// <c>UpsertAsync</c>'s tracked <c>SaveChanges</c>, the release's guarded <c>ExecuteUpdateAsync</c>
/// (whose <c>Status == delivery_held</c> predicate is the thing the fix makes miss), and the retry
/// claim. EF InMemory emulates all three through the change tracker, so it cannot show that the
/// release's predicate really stops matching.</para>
///
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class HeldOrderMappingEditPostgresTests : IAsyncLifetime
{
    /// <summary>The document built BEFORE the operator's correction. It must never be dispatched.</summary>
    private const string PreEditDocument = "po_number,supplier_item_code\r\nPO-HELD-1,WRONG-SKU-0001\r\n";

    /// <summary>The document a re-transform produces from the corrected mapping.</summary>
    private const string CorrectedDocument = "po_number,supplier_item_code\r\nPO-HELD-1,RIGHT-SKU-0002\r\n";

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_heldedit_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
            // Npgsql's default connect timeout is 15s. A CI agent (or a dev box) running several
            // container-backed classes at once can take longer than that to get a fresh postgres:16
            // accepting connections, and the migrate below is the first thing to feel it — it fails
            // as a TimeoutException that reads like a product fault. Generous, not load-bearing.
            Timeout = 60,
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

    [DockerRequiredFact]
    public async Task MappingEditOnAHeldOrder_NeverShipsThePreEditDocument_AndTheCorrectedOneIsWhatGoesOut()
    {
        var encryption = CreateEncryption();
        var storage    = new KeyedFileStorage();
        var dispatcher = new CapturingDispatcher();

        var orgId = await SeedOrgAsync();
        var (orderId, supplierId, preEditKey) = await SeedTransformedOrderAsync(orgId, encryption);
        storage.Seed(preEditKey, PreEditDocument);

        // 1 ── The org lapses mid-pipeline. DeliverOrderJob's billing gate parks the transformed
        //      order rather than delivering it. The artifact is intact and still dispatchable.
        await using (var db = NewContext())
        {
            var svc = BuildService(db, storage, dispatcher, encryption, enqueuer: null);
            Assert.True(await svc.HoldForBillingAsync(orgId, orderId, CancellationToken.None));
        }

        await using (var verify = NewContext())
        {
            var held = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatusConstants.DeliveryHeld, held.Status);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, held.HeldFromStatus);
        }

        // 2 ── The operator spots the wrong supplier item code and corrects the mapping while the
        //      order sits held. This is an ordinary click path: PUT /api/orders/{id}/mapping-override
        //      carries no status gate.
        await using (var db = NewContext())
        {
            var overrides = new OrderMappingOverrideService(db);
            Assert.True(await overrides.UpsertAsync(orgId, orderId, CorrectedMapping(), CancellationToken.None));
        }

        // 3 ── Billing is restored and the reactivation sweep runs.
        var enqueuer = new RecordingRetryEnqueuer();
        await using (var db = NewContext())
        {
            var svc = BuildService(db, storage, dispatcher, encryption, enqueuer);
            await svc.ReleaseBillingHeldOrdersAsync(orgId, CancellationToken.None);
        }

        // 4 ── Drive every retry the release enqueued, plus one unconditional retry, so the assertion
        //      below cannot pass merely because nothing happened to be queued. If ANY of these
        //      dispatches, the bytes are captured.
        foreach (var (queuedOrderId, queuedOrgId) in enqueuer.Calls.Append((orderId, orgId)))
        {
            await using var db = NewContext();
            var svc = BuildService(db, storage, dispatcher, encryption, enqueuer: null);
            await svc.RetryDeliveryAsync(queuedOrgId, queuedOrderId, maxAttempts: 3, CancellationToken.None);
        }

        // ── THE ASSERTION. On the bytes, deliberately: an artifact id or an attempt count would
        //    still read green if the right row pointed at the wrong document.
        Assert.True(
            dispatcher.CapturedText != PreEditDocument,
            "A mapping correction made while the order was billing-held must invalidate the artifact "
          + "built before it. The supplier was handed the PRE-EDIT document instead, and the "
          + $"correction was never sent. Dispatched bytes:\n{dispatcher.CapturedText}");

        Assert.Null(dispatcher.CapturedContent);

        // 5 ── ...and the correction is not merely withheld, it is what the order now sends. The
        //      order is back at 'ready', so the next send re-transforms; a re-transform appends a
        //      new deliverable artifact, which is what the operator's Send then ships.
        await using (var verify = NewContext())
        {
            var reset = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatusConstants.Ready, reset.Status);
        }

        var correctedArtifactId = await AppendReTransformedArtifactAsync(orgId, orderId, storage);

        await using (var db = NewContext())
        {
            var svc = BuildService(db, storage, dispatcher, encryption, enqueuer: null);
            var result = await svc.DispatchArtifactAsync(
                orgId, orderId, correctedArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success, result.ErrorMessage);
        }

        Assert.Equal(CorrectedDocument, dispatcher.CapturedText);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A source→canonical remap of the supplier item code — the correction an operator makes when a
    /// supplier rejects, or would reject, the code the parser produced.
    /// </summary>
    private static OrderMappingOverride CorrectedMapping() => new()
    {
        SourceMap = new Dictionary<string, SourceFieldRule>
        {
            ["SupplierItemCode"] = new() { FixedValue = "RIGHT-SKU-0002" },
        },
    };

    /// <summary>Stands in for the transform: a NEW deliverable artifact carrying the corrected bytes.</summary>
    private async Task<Guid> AppendReTransformedArtifactAsync(Guid orgId, Guid orderId, KeyedFileStorage storage)
    {
        var artifactId = Guid.NewGuid();
        var key = $"{orgId}/{orderId}/artifacts/{artifactId}.csv";
        storage.Seed(key, CorrectedDocument);

        await using var db = NewContext();
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = key, CreatedAt = DateTime.UtcNow,
        });

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status = OrderStatusConstants.ReadyToDeliver;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return artifactId;
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

    /// <summary>Captures the bytes actually handed to a supplier channel.</summary>
    private sealed class CapturingDispatcher : IDeliveryDispatcher
    {
        public string Protocol => "http";
        public byte[]? CapturedContent { get; private set; }
        public string? CapturedText =>
            CapturedContent is null ? null : Encoding.UTF8.GetString(CapturedContent);

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct,
            string? idempotencyKey = null)
        {
            CapturedContent = content;
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Real per-key byte semantics. The common <c>FakeFileStorage</c> in this suite returns the SAME
    /// constant for every key, which cannot tell a stale artifact from a fresh one — the exact
    /// distinction this test is about.
    /// </summary>
    private sealed class KeyedFileStorage : IFileStorageService
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public void Seed(string key, string content) => _objects[key] = Encoding.UTF8.GetBytes(content);

        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _objects[key] = ms.ToArray();
            return Task.FromResult(key);
        }

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            _objects.TryGetValue(key, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                : throw new FileNotFoundException($"No object stored under key '{key}'.");

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _objects.Remove(key);
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
        ProcuLinkDbContext db,
        IFileStorageService storage,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption,
        IRetryDeliveryEnqueuer? enqueuer) =>
        new(
            db,
            storage,
            encryption,
            new[] { dispatcher },
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
            Id = orgId, ClerkOrgId = $"org_he_{orgId:N}", Name = "Held-Edit Org",
            Slug = $"he-{orgId:N}", Plan = "operations", AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    /// <summary>An order that has already been transformed once: status ready_to_deliver + one artifact.</summary>
    private async Task<(Guid OrderId, Guid SupplierId, string ArtifactKey)> SeedTransformedOrderAsync(
        Guid orgId, DeliveryEncryptionService encryption)
    {
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;
        var artifactKey = $"{orgId}/{orderId}/artifacts/{artifactId}.csv";

        await using var db = NewContext();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

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
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-HELD-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = OrderStatusConstants.ReadyToDeliver,
            CreatedAt = now, UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = artifactKey, CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orderId, supplierId, artifactKey);
    }
}
