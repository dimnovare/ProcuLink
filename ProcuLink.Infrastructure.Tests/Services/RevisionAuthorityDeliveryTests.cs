using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Launch batch 7 — <see cref="DeliveryService.DispatchArtifactAsync"/> must dispatch a PINNED
/// order over the CHANNEL its published revision snapshotted (protocol + non-secret config +
/// verbatim-copied encrypted credentials, decrypted by the SAME path) when the
/// <c>Connections:RevisionAuthority</c> flag is ON — so a later live delivery-config edit can
/// never silently re-route an already-pinned order. Flag off / unpinned / orphan pin / blank
/// revision channel → the live supplier delivery config, byte-identical to the pre-batch-7
/// behaviour.
/// </summary>
public class RevisionAuthorityDeliveryTests
{
    // Plain full-model context: the revision tables must be queryable (InMemory enforces no FKs).
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static IEffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool enabled) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = enabled ? "true" : "false",
            })
            .Build());

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        CapturingDispatcher dispatcher,
        DeliveryEncryptionService encryption,
        bool flagEnabled) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new IDeliveryDispatcher[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Infrastructure.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            reliability: null,
            effectiveConfig: Resolver(db, flagEnabled));

    private sealed record Seeded(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId);

    private static async Task<Seeded> SeedOrderWithArtifactAsync(
        ProcuLinkDbContext db, Guid? connectionRevisionId = null)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            ConnectionRevisionId = connectionRevisionId,
            PoNumber = "PO-DEL-7", OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
            Status = OrderStatusConstants.ReadyToDeliver, CreatedAt = now, UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = "artifact.csv", CreatedAt = now,
            ConnectionRevisionId = connectionRevisionId,
        });
        await db.SaveChangesAsync();
        return new Seeded(orgId, supplierId, orderId, artifactId);
    }

    private static async Task AddLiveConfigAsync(
        ProcuLinkDbContext db, Seeded seeded, DeliveryEncryptionService encryption,
        string url = "https://live.example/orders", bool autoDeliver = true,
        string? configJson = null)
    {
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = seeded.OrgId, SupplierId = seeded.SupplierId,
            Protocol = "http", AutoDeliver = autoDeliver,
            ConfigJson = configJson ?? $"{{\"url\":\"{url}\"}}",
            EncryptedCredentials = encryption.Encrypt(
                "{\"type\":\"live\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<SupplierConnectionRevision> SeedRevisionAsync(
        ProcuLinkDbContext db, Seeded seeded,
        string? protocol, string? configJson, string? credentialsRef, bool autoDeliver = true,
        bool pin = true)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), ConnectionId = connectionId, OrgId = seeded.OrgId,
            SupplierId = seeded.SupplierId, VersionNo = 1, Status = "published",
            CreatedAt = now, PublishedAt = now,
            DeliveryProtocol = protocol, DeliveryConfigJson = configJson,
            CredentialsRef = credentialsRef, DeliveryAutoDeliver = autoDeliver,
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = seeded.OrgId, SupplierId = seeded.SupplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);

        if (pin)
        {
            (await db.PurchaseOrders.SingleAsync(o => o.Id == seeded.OrderId)).ConnectionRevisionId = revision.Id;
            (await db.OutboundArtifacts.SingleAsync(a => a.Id == seeded.ArtifactId)).ConnectionRevisionId = revision.Id;
        }
        await db.SaveChangesAsync();
        return revision;
    }

    // ── Flag ON + pinned: the revision channel governs while the live config differs ──

    [Fact]
    public async Task FlagOn_Pinned_DispatchesViaRevisionChannel_NotTheLiveEditedConfig()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live-edited.example/orders");
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)));

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1);
        // The revision's snapshotted endpoint + credentials drove dispatch — NOT the live edit.
        dispatcher.LastConfig!.ConfigJson.Should().Contain("revision.example");
        dispatcher.LastCredentials.Should().Be("{\"type\":\"revision\"}"); // same decrypt path, verbatim payload
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.Delivered);
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.Destination.Should().Be("https://revision.example/orders"); // audit shows the real destination
    }

    // ── Credential AAD binding: the detached snapshot's Id is Guid.Empty, never a real row id ──
    //
    // Before Task 7 of the credential-AAD-binding plan deleted the legacy single-argument
    // Encrypt(string), every OTHER test in this file seeded credentialsRef with it, writing a
    // version-1 envelope carrying no associated data at all — decryptable under ANY
    // CredentialScope, including one keyed on the wrong id. That made every test above BLIND to
    // which id DeliveryService actually scopes on: DeliveryService.cs's detached-snapshot branch
    // sets Id = Guid.Empty (this config was never added to the DbContext) and SupplierId = the
    // order's real supplier — so scoping on config.Id instead of config.SupplierId would have
    // silently passed every test above while breaking every pinned-order delivery in production.
    // This test was written to close that blind spot by seeding a BOUND (version-2) envelope
    // explicitly, keyed on (org, purpose, SUPPLIER id).
    //
    // Now that the legacy overload no longer exists, every test in this file seeds through the
    // same bound scope (AddLiveConfigAsync / SeedRevisionAsync both key on seeded.SupplierId), so
    // the whole file discriminates on the correct id — a regression to config.Id would fail here
    // too, not just in this one test. This test remains: it is the one that states the invariant
    // by name, in isolation from the shared helpers.
    [Fact]
    public async Task FlagOn_Pinned_BoundCredentials_DecryptSucceeds_ScopedOnSupplierId_NotTheDetachedSnapshotsEmptyId()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live-edited.example/orders");

        var boundCredentialsRef = encryption.Encrypt(
            "{\"type\":\"revision\"}",
            CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId));
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: boundCredentialsRef);

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1);
        // The revision's snapshotted, AAD-bound credentials arrived at the dispatcher intact —
        // not the live config's, and not refused as unbindable.
        dispatcher.LastCredentials.Should().Be("{\"type\":\"revision\"}");
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionChannel_DeliversEvenWhenNoLiveConfigRowExists()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        // NO live SupplierDeliveryConfig row — pre-batch-7 this would fail with missing_config.
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)));

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1);
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task FlagOn_Pinned_RequireAutoDeliver_HonorsRevisionAutoDeliver()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        // Live config says auto-deliver TRUE, but the published contract said FALSE — the
        // revision governs: no automatic dispatch (manual send still works via requireAutoDeliver=false).
        await AddLiveConfigAsync(db, seeded, encryption, autoDeliver: true);
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)),
            autoDeliver: false);

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, requireAutoDeliver: true, default);

        result.Success.Should().BeTrue(); // no-op success, exactly like a live AutoDeliver=false
        dispatcher.Calls.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.ReadyToDeliver);
    }

    // ── Granular fallback: blank revision channel → live config ──

    [Fact]
    public async Task FlagOn_Pinned_BlankRevisionProtocol_FallsBackToLiveConfig()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live.example/orders");
        // Backfilled-rev-1 shape for a supplier whose delivery was configured AFTER backfill:
        // the revision snapshotted no channel.
        await SeedRevisionAsync(db, seeded, protocol: null, configJson: null, credentialsRef: null);

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.Calls.Should().Be(1);
        dispatcher.LastConfig!.ConfigJson.Should().Contain("live.example"); // live fallback
        dispatcher.LastCredentials.Should().Be("{\"type\":\"live\"}");
    }

    // ── Golden: flag off / unpinned / orphan pin → live config, byte-identical ──

    [Fact]
    public async Task FlagOff_Pinned_UsesLiveConfig_PreBatch7Behaviour()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live.example/orders");
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)));

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: false);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.LastConfig!.ConfigJson.Should().Contain("live.example"); // revision ignored flag-off
        dispatcher.LastCredentials.Should().Be("{\"type\":\"live\"}");
    }

    [Fact]
    public async Task FlagOn_Unpinned_UsesLiveConfig()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db); // unpinned
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live.example/orders");
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)),
            pin: false);

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.LastConfig!.ConfigJson.Should().Contain("live.example");
    }

    [Fact]
    public async Task FlagOn_OrphanPin_FallsBackToLiveConfig_NoThrow()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db, connectionRevisionId: Guid.NewGuid()); // dangling pin
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live.example/orders");

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.LastConfig!.ConfigJson.Should().Contain("live.example");
    }

    [Fact]
    public async Task FlagOn_Pinned_BlankRevisionChannel_AndNoLiveConfig_FailsHonestlyWithMissingConfig()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await SeedRevisionAsync(db, seeded, protocol: null, configJson: null, credentialsRef: null);
        // No live config either → the honest missing-config failure, exactly like today.

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        var result = await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("delivery config");
        dispatcher.Calls.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync()).Status.Should().Be(OrderStatusConstants.DeliveryFailed);
    }

    // ── SSH host-key pins live on the LIVE config, never in a revision snapshot (WP-38) ──
    //
    // A pinned revision reproduces the DOCUMENT. Which server may receive it is a fact about the
    // world NOW. Freezing the two together breaks the feature in BOTH directions — and this is the
    // path production actually uses, because revision authority is ON there.

    /// <summary>
    /// The config that GOVERNS a revision-pinned dispatch is a detached object that was never added
    /// to the DbContext. Writing a first-use fingerprint onto it would be a silent no-op: the shape
    /// of a fix with none of the effect, leaving every revision-pinned SFTP supplier permanently
    /// unverified while the logs claimed a key had been recorded.
    /// </summary>
    [Fact]
    public async Task FlagOn_Pinned_LearnedHostKey_IsRecordedOnTheLiveConfig_NotTheDetachedSnapshot()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await AddLiveConfigAsync(db, seeded, encryption, url: "https://live.example/orders");
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\"}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)));

        var dispatcher = new CapturingDispatcher(
            new DeliveryResult(true, null, 200, LearnedHostKeyFingerprint: "SHA256:learnedfromtheserver"));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        var live = await db.SupplierDeliveryConfigs.SingleAsync();
        DeliveryHostKeyConfig.Read(live.ConfigJson).Should().Equal("SHA256:learnedfromtheserver");
        live.ConfigJson.Should().Contain("live.example", "the rest of the live config must survive the write");
    }

    /// <summary>
    /// The other direction: the pin the dispatcher is handed comes from the live config, not from
    /// the revision's frozen copy. Otherwise an operator re-trusting a legitimately rebuilt server
    /// could never reach orders pinned to older revisions — they would refuse forever, with a
    /// message about a rebuild the operator had already dealt with.
    /// </summary>
    [Fact]
    public async Task FlagOn_Pinned_HostKeyPin_ComesFromTheLiveConfig_NotTheRevisionSnapshot()
    {
        await using var db = NewDb();
        var encryption = CreateEncryption();
        var seeded = await SeedOrderWithArtifactAsync(db);
        await AddLiveConfigAsync(db, seeded, encryption,
            configJson: "{\"url\":\"https://live.example/orders\",\"hostKeyFingerprints\":[\"SHA256:currentlytrusted\"]}");
        await SeedRevisionAsync(db, seeded,
            protocol: "http",
            configJson: "{\"url\":\"https://revision.example/orders\",\"hostKeyFingerprints\":[\"SHA256:frozenlongago\"]}",
            credentialsRef: encryption.Encrypt(
                "{\"type\":\"revision\"}",
                CredentialScope.ForSupplier(seeded.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seeded.SupplierId)));

        var dispatcher = new CapturingDispatcher(new DeliveryResult(true, null, 200));
        var service = CreateService(db, dispatcher, encryption, flagEnabled: true);

        await service.DispatchArtifactAsync(seeded.OrgId, seeded.OrderId, seeded.ArtifactId, true, default);

        DeliveryHostKeyConfig.Read(dispatcher.LastConfig!.ConfigJson)
            .Should().Equal("SHA256:currentlytrusted");
        dispatcher.LastConfig.ConfigJson.Should().Contain("revision.example",
            "everything EXCEPT the host-key pin must still come from the pinned revision");
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    private sealed class CapturingDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public int Calls { get; private set; }
        public SupplierDeliveryConfig? LastConfig { get; private set; }
        public string? LastCredentials { get; private set; }
        public string Protocol => "http";

        public CapturingDispatcher(DeliveryResult result) => _result = result;

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null,
            bool isTestFire = false)
        {
            Calls++;
            LastConfig = config;
            LastCredentials = decryptedCredentials;
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
