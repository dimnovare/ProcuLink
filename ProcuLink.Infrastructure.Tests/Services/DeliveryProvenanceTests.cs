using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Launch batch 3 — delivery provenance. Every DeliveryAttempt must carry the connection
/// revision + config digest that produced the dispatched artifact (copied from the artifact,
/// falling back to the order's pin) and the SHA-256 of the payload bytes ACTUALLY dispatched
/// (null on pre-dispatch failures). Legacy artifacts with null provenance stay null — additive.
/// </summary>
public class DeliveryProvenanceTests
{
    private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes("order,line\r\n1,ok\r\n");

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProvenanceTestDbContext(options);
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private sealed record Seeded(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId, Guid? RevisionId, string? ConfigDigest);

    private static async Task<Seeded> SeedAsync(
        ProcuLinkDbContext db,
        Guid? orderPin,
        Guid? artifactRevision,
        string? configDigest,
        string? artifactSha)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-PROV",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.ReadyToDeliver,
            ConnectionRevisionId = orderPin,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = orgId,
            Format = "csv",
            FileKey = "artifact.csv",
            CreatedAt = now,
            ConnectionRevisionId = artifactRevision,
            ConfigDigest = configDigest,
            ArtifactSha256 = artifactSha,
        });

        await db.SaveChangesAsync();
        return new Seeded(orgId, supplierId, orderId, artifactId, artifactRevision, configDigest);
    }

    [Fact]
    public async Task Dispatch_Success_CopiesProvenance_AndHashesDispatchedPayload()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var revisionId = Guid.NewGuid();
        var digest = ProvenanceHash.TrySha256HexUtf8("{\"some\":\"config\"}");
        var ids = await SeedAsync(db, orderPin: revisionId, artifactRevision: revisionId,
            configDigest: digest, artifactSha: ProvenanceHash.TrySha256Hex(PayloadBytes));
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.ConnectionRevisionId.Should().Be(revisionId);            // copied from the artifact
        attempt.ConfigDigest.Should().Be(digest);                         // same config that built the artifact
        attempt.ArtifactSha256.Should().Be(ProvenanceHash.TrySha256Hex(PayloadBytes)); // dispatched bytes hashed
        // Corruption check: dispatched-payload hash matches the artifact's stored hash.
        var artifact = await db.OutboundArtifacts.SingleAsync();
        attempt.ArtifactSha256.Should().Be(artifact.ArtifactSha256);
    }

    [Fact]
    public async Task Dispatch_MissingConfig_CopiesProvenance_ButNoPayloadHash()
    {
        await using var db = CreateDb();
        var revisionId = Guid.NewGuid();
        var ids = await SeedAsync(db, orderPin: revisionId, artifactRevision: revisionId,
            configDigest: "digest-abc", artifactSha: "sha-abc");
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)));

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeFalse();
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Channel.Should().Be("missing_config");
        attempt.ConnectionRevisionId.Should().Be(revisionId);
        attempt.ConfigDigest.Should().Be("digest-abc");
        attempt.ArtifactSha256.Should().BeNull(); // honestly: no payload was ever downloaded/dispatched
    }

    [Fact]
    public async Task Dispatch_LegacyArtifactWithoutProvenance_FallsBackToOrderPin()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orderPin = Guid.NewGuid();
        // Legacy artifact: all provenance columns null; the order still carries its ingest pin.
        var ids = await SeedAsync(db, orderPin: orderPin, artifactRevision: null,
            configDigest: null, artifactSha: null);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.ConnectionRevisionId.Should().Be(orderPin); // artifact null → order pin fallback
        attempt.ConfigDigest.Should().BeNull();             // legacy artifact had none — stays null
        attempt.ArtifactSha256.Should().Be(ProvenanceHash.TrySha256Hex(PayloadBytes));
    }

    [Fact]
    public async Task Dispatch_FullyLegacyRows_AllProvenanceNullExceptPayloadHash()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        // Pre-V1 order (no pin) + legacy artifact (no provenance): nothing to copy — columns stay
        // null and the dispatch is otherwise unaffected (additive guarantee).
        var ids = await SeedAsync(db, orderPin: null, artifactRevision: null, configDigest: null, artifactSha: null);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeTrue(); // legacy rows deliver exactly as before
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.ConnectionRevisionId.Should().BeNull();
        attempt.ConfigDigest.Should().BeNull();
        attempt.ArtifactSha256.Should().Be(ProvenanceHash.TrySha256Hex(PayloadBytes));
    }

    // ── dispatch markers: the webhook guard's evidence ───────────────────────

    /// <summary>
    /// The supplier status callback (<c>WebhookIngressController</c>) accepts a terminal report only
    /// for an order it can PROVE a send was begun for, and its proof is a per-row marker:
    /// <c>IdempotencyKey != null OR ArtifactSha256 != null</c>. That proof is only sound while EVERY
    /// pre-dispatch failure leaves BOTH null — a row alone means nothing, because all four gates
    /// below write an order-linked terminal row with zero bytes sent.
    ///
    /// <para>If any of these starts stamping either marker, the webhook guard silently reopens: a
    /// never-sent order becomes markable 'delivered', which also DISABLES its safety net (the
    /// stranded-ready sweep matches <c>ready_to_deliver</c>, the billing release matches
    /// <c>delivery_held</c>) — permanently lost, displayed as shipped, and billable. These tests are
    /// the tripwire; the guard's correctness depends on them, not on a comment.</para>
    /// </summary>
    public class PreDispatchFailuresWriteNoDispatchMarker
    {
        [Fact]
        public async Task MissingConfig_LeavesBothMarkersNull()
        {
            await using var db = CreateDb();
            var ids = await SeedAsync(db, orderPin: null, artifactRevision: null,
                configDigest: null, artifactSha: "sha-of-the-artifact-not-the-send");
            // No SupplierDeliveryConfig seeded -> FailMissingConfigAsync.
            var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)));

            var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

            result.Success.Should().BeFalse();
            var attempt = await db.DeliveryAttempts.SingleAsync();
            attempt.Channel.Should().Be("missing_config", "this is the missing-config gate's own row");
            AssertNoDispatchMarker(attempt);
        }

        [Fact]
        public async Task NoDispatcherRegistered_LeavesBothMarkersNull()
        {
            await using var db = CreateDb();
            var encryption = CreateEncryption();
            var ids = await SeedAsync(db, orderPin: null, artifactRevision: null,
                configDigest: null, artifactSha: "sha-of-the-artifact-not-the-send");
            db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
            await db.SaveChangesAsync();
            // Config protocol is "http"; the only registered dispatcher speaks "sftp" -> no dispatcher.
            var service = CreateService(db, new WrongProtocolDispatcher(), encryption);

            var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("No dispatcher registered");
            AssertNoDispatchMarker(await db.DeliveryAttempts.SingleAsync());
        }

        [Fact]
        public async Task UndecryptableCredentials_LeavesBothMarkersNull()
        {
            await using var db = CreateDb();
            var encryption = CreateEncryption();
            var ids = await SeedAsync(db, orderPin: null, artifactRevision: null,
                configDigest: null, artifactSha: "sha-of-the-artifact-not-the-send");
            var config = MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true);
            // Garbage ciphertext -> DeliveryEncryptionService.Decrypt returns null (it catches and
            // returns null rather than throwing), so the credentials gate fails before dispatch.
            config.EncryptedCredentials = "this-is-not-valid-aes-gcm-ciphertext";
            db.SupplierDeliveryConfigs.Add(config);
            await db.SaveChangesAsync();
            var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

            var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("credentials could not be decrypted");
            AssertNoDispatchMarker(await db.DeliveryAttempts.SingleAsync());
        }

        [Fact]
        public async Task ArtifactDownloadFailure_LeavesBothMarkersNull()
        {
            await using var db = CreateDb();
            var encryption = CreateEncryption();
            var ids = await SeedAsync(db, orderPin: null, artifactRevision: null,
                configDigest: null, artifactSha: "sha-of-the-artifact-not-the-send");
            db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
            await db.SaveChangesAsync();
            // An R2 blip: the payload never loads, so nothing can be sent.
            var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)),
                encryption, new ThrowingFileStorage());

            var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("Artifact download failed");
            AssertNoDispatchMarker(await db.DeliveryAttempts.SingleAsync());
        }

        /// <summary>
        /// The positive control. Without it the four tests above would still pass if the markers were
        /// never written AT ALL — which would fail closed, but would also mean the webhook guard
        /// refuses every genuine callback. A real send must stamp both.
        /// </summary>
        [Fact]
        public async Task ARealSend_StampsBothMarkers()
        {
            await using var db = CreateDb();
            var encryption = CreateEncryption();
            var ids = await SeedAsync(db, orderPin: null, artifactRevision: null,
                configDigest: null, artifactSha: null);
            db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
            await db.SaveChangesAsync();
            var service = CreateService(db, new FakeDispatcher(new DeliveryResult(true, null, 200)), encryption);

            var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

            result.Success.Should().BeTrue();
            var attempt = await db.DeliveryAttempts.SingleAsync();
            attempt.IdempotencyKey.Should().NotBeNull("OpenDispatchAttemptAsync stamps it before the wire send");
            attempt.ArtifactSha256.Should().NotBeNull("the dispatched bytes are hashed after DispatchAsync returns");
        }

        /// <summary>
        /// A send that REACHED the supplier and was refused (5xx) is still a send: the row keeps its
        /// markers, so the supplier's later callback is accepted rather than refused as never-sent.
        /// </summary>
        [Fact]
        public async Task ADispatchedButFailedSend_KeepsItsMarkers()
        {
            await using var db = CreateDb();
            var encryption = CreateEncryption();
            var ids = await SeedAsync(db, orderPin: null, artifactRevision: null,
                configDigest: null, artifactSha: null);
            db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, autoDeliver: true));
            await db.SaveChangesAsync();
            var service = CreateService(db, new FakeDispatcher(new DeliveryResult(false, "upstream exploded", 503)), encryption);

            var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

            result.Success.Should().BeFalse();
            var attempt = await db.DeliveryAttempts.SingleAsync();
            attempt.IdempotencyKey.Should().NotBeNull("the send was begun — this is not a pre-dispatch failure");
            attempt.ArtifactSha256.Should().NotBeNull("the bytes were dispatched; the supplier's 503 came back");
        }

        private static void AssertNoDispatchMarker(DeliveryAttempt attempt)
        {
            attempt.IdempotencyKey.Should().BeNull(
                "no send was begun, so OpenDispatchAttemptAsync never ran to stamp the key — the webhook "
              + "guard treats a key as proof a send started");
            attempt.ArtifactSha256.Should().BeNull(
                "nothing was dispatched, so there are honestly no dispatched bytes to hash — the webhook "
              + "guard treats this hash as proof a send executed");
        }

        private sealed class WrongProtocolDispatcher : IDeliveryDispatcher
        {
            public string Protocol => "sftp";

            public Task<DeliveryResult> DispatchAsync(
                byte[] content, string fileName, string contentType,
                SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct,
                string? idempotencyKey = null) =>
                throw new InvalidOperationException("must never be reached: the protocol does not match");
        }

        private sealed class ThrowingFileStorage : IFileStorageService
        {
            public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
                Task.FromResult(key);

            public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
                Task.FromResult($"https://files.example/{key}");

            public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
                throw new IOException("R2 signing failure");

            public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
        }
    }

    // ── scaffolding (mirrors DeliveryServiceTests) ───────────────────────────

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService? encryption = null,
        IFileStorageService? fileStorage = null) =>
        new(
            db,
            fileStorage ?? new FakeFileStorage(),
            encryption ?? CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Infrastructure.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId, Guid supplierId, DeliveryEncryptionService encryption, bool autoDeliver) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            AutoDeliver = autoDeliver,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private sealed class FakeDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public string Protocol => "http";
        public FakeDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null) =>
            Task.FromResult(_result);
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(PayloadBytes));

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ProvenanceTestDbContext : ProcuLinkDbContext
    {
        public ProvenanceTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });

            modelBuilder.Entity<OutboundArtifact>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            modelBuilder.Entity<SupplierDeliveryConfig>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
            });

            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
            });

            modelBuilder.Entity<OrderException>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
