using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Conformance;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-21 (a1) — THE DETERMINISTIC PROOF that revision authority actually holds: a PINNED order
/// does not re-route when the supplier's live delivery endpoint is edited underneath it.
///
/// <para><b>Why this test exists.</b> <c>Connections__RevisionAuthority = true</c> on both Railway
/// services (API <c>ProcuLink</c> and Worker <c>aware-amazement</c>), so every reproducibility
/// claim ProcuLink makes — "this order was sent under the contract it was ingested with" — rests
/// on this behaviour being real. Until this test, nothing proved it end to end: the existing
/// <see cref="DeliveryConfigRepublishPostgresTests"/> proves the opposite direction (a FUTURE
/// order picks up the edit) and the InMemory
/// <c>RevisionAuthorityDeliveryTests.FlagOn_Pinned_DispatchesViaRevisionChannel_NotTheLiveEditedConfig</c>
/// never runs the archive path. Production's actual sequence — publish v1, pin an order, edit the
/// live config, REPUBLISH (which ARCHIVES v1 and moves the active pointer to v2), then dispatch
/// the already-pinned order — was covered by nothing.</para>
///
/// <para><b>Why real Postgres.</b> Republish archives v1 through the published-immutability
/// trigger, which EF InMemory cannot run. An archived revision that stopped resolving would send
/// every in-flight pinned order to the NEW endpoint silently — the exact 2026-06-13 incident in
/// reverse — and only a real database can prove it does not.</para>
///
/// <para><b>Rule R6 — this asserts the DIFFERENCE, not the sameness.</b> Both routes are resolved
/// in the same run against the same database, and the difference is asserted FIRST, before either
/// endpoint is named. That ordering is the whole point: an <c>Assert.NotEqual</c> placed after two
/// <c>Assert.Equal</c>s against distinct constants can never fail — both operands are already
/// pinned by the time it runs — so it would document R6 while proving nothing. Asserted here, in
/// order:
/// <list type="number">
///   <item>the two destinations DIFFER (fails if either route moves onto the other);</item>
///   <item>the two CREDENTIAL payloads differ, and each is its own route's — the contract is more
///     than a URL;</item>
///   <item>only then, which endpoint is which.</item>
/// </list>
/// Break either side alone — a status filter added to
/// <see cref="EffectiveConnectionConfigResolver"/> so archived pins stop resolving, or a republish
/// that fails to carry the edit forward — and the pair collapses to one endpoint, so assertion (1)
/// fails before anything else is checked. The companion test below then removes any doubt that the
/// flag is what produces the difference: with revision authority OFF the two routes are
/// IDENTICAL.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class PinnedOrderDoesNotRerouteAfterConfigEditPostgresTests : IAsyncLifetime
{
    private const string OldUrl     = "https://supplier.test/v1-endpoint";
    private const string NewUrl     = "https://supplier.test/v2-endpoint";
    private const string OldUrlJson = $$"""{"url":"{{OldUrl}}","method":"POST"}""";
    private const string NewUrlJson = $$"""{"url":"{{NewUrl}}","method":"POST"}""";

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_revauth_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
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

    // ── (a1) THE PROOF ────────────────────────────────────────────────────────

    /// <summary>
    /// Revision authority ON — the production configuration. One live delivery edit; two orders;
    /// two different destinations.
    /// </summary>
    [DockerRequiredFact]
    public async Task RevisionAuthorityOn_PinnedOrderKeepsItsOldEndpoint_WhileAnUnpinnedOrderTakesTheEdit()
    {
        var (destinations, versions) = await RunTheEditAndDispatchBothOrdersAsync(revisionAuthority: true);

        // Republish really happened on the real schema: v1 archived (immutably intact), v2 active.
        Assert.Equal("archived",  versions.V1Status);
        Assert.Equal("published", versions.V2Status);
        Assert.Equal(2,           versions.ActiveVersionNo);
        Assert.Contains(OldUrl, versions.V1DeliveryConfigJson);
        Assert.Contains(NewUrl, versions.V2DeliveryConfigJson);

        // ── R6 FIRST: the DIFFERENCE, before either endpoint is named ─────────
        // These two lines are the load-bearing ones. Break either route alone and the pair
        // collapses onto one endpoint / one credential set, and execution stops HERE — not at a
        // later Assert.Equal against a constant, which is what a difference assertion placed after
        // the equals would degrade into.
        Assert.NotEqual(destinations.Pinned, destinations.Unpinned);
        Assert.NotEqual(destinations.PinnedCredentials, destinations.UnpinnedCredentials);

        // The pinned order was ingested under v1 and STAYS on v1's endpoint, even though v1 is now
        // archived and the active pointer has moved. This is the whole reproducibility claim.
        Assert.Equal(OldUrl, destinations.Pinned);

        // …and on v1's CREDENTIALS. "Processed under the contract it was ingested with" is not a
        // URL claim: the revision snapshots the encrypted credential payload verbatim, and the
        // dispatcher must receive THAT decrypted, not the ones the operator saved with the edit.
        // A resolver that took the endpoint from the pin but the secret from the live row would
        // authenticate as the wrong party against the right host — and pass every URL-only test.
        Assert.Equal("""{"type":"v1"}""", destinations.PinnedCredentials);

        // The unpinned order takes the operator's edit — the live-resolution path still moves,
        // endpoint and credentials together.
        Assert.Equal(NewUrl, destinations.Unpinned);
        Assert.Equal("""{"type":"v2"}""", destinations.UnpinnedCredentials);

        // The persisted attempt row records the endpoint the send actually used. (It is derived
        // from the same resolved config the dispatcher was handed — DeliveryService builds both
        // from one object — so this is NOT independent corroboration of the routing. What it does
        // prove is that the audit trail is written from the RESOLVED config and never re-reads the
        // live supplier row, which is what an operator reading delivery history depends on.)
        Assert.Equal(OldUrl, destinations.PinnedAttemptDestination);
        Assert.Equal(NewUrl, destinations.UnpinnedAttemptDestination);
    }

    /// <summary>
    /// The anti-vacuity companion. Identical scenario with the flag OFF: republish is a no-op, the
    /// pinned order falls back to the live config, and BOTH orders land on the edited endpoint.
    /// The difference proved above therefore exists *because of* revision authority and nothing
    /// else — this test fails if the proof above would still pass with the subsystem disabled.
    /// </summary>
    [DockerRequiredFact]
    public async Task RevisionAuthorityOff_BothOrdersRouteToTheSameEditedEndpoint_SoTheDifferenceIsTheFlag()
    {
        var (destinations, _) = await RunTheEditAndDispatchBothOrdersAsync(revisionAuthority: false);

        // Sameness first, for the same reason the sibling asserts difference first: after two
        // Assert.Equals against one constant, an Assert.Equal between them is already decided.
        Assert.Equal(destinations.Pinned, destinations.Unpinned);
        Assert.Equal(destinations.PinnedCredentials, destinations.UnpinnedCredentials);

        Assert.Equal(NewUrl, destinations.Pinned);   // the pin is ignored — pre-batch-7 behaviour
        Assert.Equal(NewUrl, destinations.Unpinned);
        Assert.Equal("""{"type":"v2"}""", destinations.PinnedCredentials);
    }

    // ── the shared scenario ───────────────────────────────────────────────────

    private sealed record Destinations(
        string Pinned, string Unpinned,
        string? PinnedCredentials, string? UnpinnedCredentials,
        string? PinnedAttemptDestination, string? UnpinnedAttemptDestination);

    private sealed record Versions(
        string V1Status, string V2Status, int ActiveVersionNo,
        string V1DeliveryConfigJson, string V2DeliveryConfigJson);

    /// <summary>
    /// Publish v1 (OLD endpoint) → pin an order to it → edit the live delivery config to the NEW
    /// endpoint → republish → dispatch BOTH the pinned order and an unpinned one through the real
    /// <see cref="DeliveryService"/>, capturing where each actually went.
    /// </summary>
    private async Task<(Destinations, Versions)> RunTheEditAndDispatchBothOrdersAsync(bool revisionAuthority)
    {
        var encryption = CreateEncryption();
        var seed       = await SeedGovernedSupplierAsync(encryption);

        // An order ingested TODAY, pinned to the published v1 contract (order + artifact, exactly
        // as OrderIngestionService/TransformOrderJob stamp them).
        var pinned   = await SeedOrderAsync(seed, pinTo: seed.V1Id, poNumber: "PO-PINNED");
        // An order with no pin — the live-resolution path.
        var unpinned = await SeedOrderAsync(seed, pinTo: null,      poNumber: "PO-UNPINNED");

        // ── the operator edits the supplier's live delivery endpoint ──────────
        await SetLiveDeliveryConfigAsync(seed, NewUrlJson, encryption);

        // …and the edit is routed into the versioned connection (what SuppliersController does on
        // PUT /delivery-config). Flag OFF → NotGoverned no-op, which is the point of the companion
        // test: the live row alone then governs everything.
        await using (var db = NewContext())
        {
            await MakeConnectionSvc(db, revisionAuthority)
                .RepublishLiveDeliveryAsync(seed.OrgId, seed.SupplierId, "wp21-test", default);
        }

        // ── dispatch both orders through the REAL delivery path ───────────────
        var pinnedDispatcher   = new CapturingDispatcher();
        var unpinnedDispatcher = new CapturingDispatcher();

        await using (var db = NewContext())
        {
            var result = await BuildDeliveryService(db, pinnedDispatcher, encryption, revisionAuthority)
                .DispatchArtifactAsync(seed.OrgId, pinned.OrderId, pinned.ArtifactId, requireAutoDeliver: true, default);
            Assert.True(result.Success, $"pinned dispatch failed: {result.ErrorMessage}");
        }
        await using (var db = NewContext())
        {
            var result = await BuildDeliveryService(db, unpinnedDispatcher, encryption, revisionAuthority)
                .DispatchArtifactAsync(seed.OrgId, unpinned.OrderId, unpinned.ArtifactId, requireAutoDeliver: true, default);
            Assert.True(result.Success, $"unpinned dispatch failed: {result.ErrorMessage}");
        }

        Assert.Equal(1, pinnedDispatcher.Calls);
        Assert.Equal(1, unpinnedDispatcher.Calls);

        await using var verify = NewContext();

        var pinnedAttempt = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == pinned.OrderId).OrderByDescending(a => a.AttemptNumber)
            .Select(a => a.Destination).FirstOrDefaultAsync();
        var unpinnedAttempt = await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == unpinned.OrderId).OrderByDescending(a => a.AttemptNumber)
            .Select(a => a.Destination).FirstOrDefaultAsync();

        var v1 = await verify.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == seed.V1Id);
        var v2 = await verify.SupplierConnectionRevisions.AsNoTracking()
            .Where(r => r.ConnectionId == seed.ConnectionId).OrderByDescending(r => r.VersionNo).FirstAsync();

        return (
            new Destinations(
                UrlOf(pinnedDispatcher.LastConfigJson),
                UrlOf(unpinnedDispatcher.LastConfigJson),
                pinnedDispatcher.LastCredentials,
                unpinnedDispatcher.LastCredentials,
                pinnedAttempt,
                unpinnedAttempt),
            new Versions(
                v1.Status, v2.Status, v2.VersionNo,
                v1.DeliveryConfigJson ?? "", v2.DeliveryConfigJson ?? ""));
    }

    /// <summary>Pulls the endpoint out of whatever config JSON the dispatcher was actually handed.</summary>
    private static string UrlOf(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return "(no config)";
        using var doc = System.Text.Json.JsonDocument.Parse(configJson);
        return doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() ?? "(no url)" : "(no url)";
    }

    // ── seeding ───────────────────────────────────────────────────────────────

    private sealed record Seed(Guid OrgId, Guid SupplierId, Guid ConnectionId, Guid V1Id);
    private sealed record SeededOrder(Guid OrderId, Guid ArtifactId);

    private ProcuLinkDbContext NewContext() => new(_options!);

    private async Task<Seed> SeedGovernedSupplierAsync(DeliveryEncryptionService encryption)
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var v1Id         = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        // Both the published revision's CredentialsRef and the live config's EncryptedCredentials
        // represent the SAME credential — in production CredentialsRef is a verbatim byte-copy of
        // the live row at publish time, so this is encrypted ONCE and assigned to both below. A
        // second Encrypt call would use a fresh random nonce and diverge byte-for-byte even though
        // the plaintext and scope are identical.
        var v1CredentialsCiphertext = encryption.Encrypt(
            """{"type":"v1"}""",
            CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId));

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_wp21_{orgId:N}", Name = "WP-21 Org",
            Slug = $"wp21-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Pinned Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId,
            Name = "Pinned Supplier", ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Published v1 — the contract the pinned order is ingested under. Credentials are copied
        // VERBATIM into the snapshot, exactly as the backfill/republish path does.
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = v1Id, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", PublishedAt = now, EffectiveFrom = now,
            CreatedAt = now, CatalogMode = "live",
            OutputFormat        = "csv",
            DeliveryProtocol    = "http",
            DeliveryConfigJson  = OldUrlJson,
            DeliveryAutoDeliver = true,
            CredentialsRef      = v1CredentialsCiphertext,
        });
        await db.SaveChangesAsync();

        var conn = await db.SupplierConnections.SingleAsync(c => c.Id == connectionId);
        conn.ActiveRevisionId = v1Id;
        await db.SaveChangesAsync();

        // The live row starts in sync with v1 — the operator has not edited anything yet.
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", AutoDeliver = true, ConfigJson = OldUrlJson,
            EncryptedCredentials = v1CredentialsCiphertext,
            OutputFormat = "csv", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return new Seed(orgId, supplierId, connectionId, v1Id);
    }

    private async Task<SeededOrder> SeedOrderAsync(Seed seed, Guid? pinTo, string poNumber)
    {
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = seed.OrgId, SupplierId = seed.SupplierId,
            ConnectionRevisionId = pinTo,
            PoNumber = poNumber, BuyerName = "WP-21 Buyer",
            OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
            Status = OrderStatusConstants.ReadyToDeliver, CreatedAt = now, UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = seed.OrgId,
            Format = "csv", FileKey = $"{poNumber}.csv", CreatedAt = now,
            ConnectionRevisionId = pinTo,
        });
        await db.SaveChangesAsync();
        return new SeededOrder(orderId, artifactId);
    }

    private async Task SetLiveDeliveryConfigAsync(Seed seed, string configJson, DeliveryEncryptionService encryption)
    {
        await using var db = NewContext();
        var live = await db.SupplierDeliveryConfigs
            .SingleAsync(x => x.OrgId == seed.OrgId && x.SupplierId == seed.SupplierId);
        live.ConfigJson           = configJson;
        live.EncryptedCredentials = encryption.Encrypt(
            """{"type":"v2"}""",
            CredentialScope.ForSupplier(seed.OrgId, CredentialPurpose.SupplierDeliveryCredentials, seed.SupplierId));
        live.UpdatedAt            = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ── wiring ────────────────────────────────────────────────────────────────

    private static IConfiguration FlagConfig(bool on) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = on ? "true" : "false",
            })
            .Build();

    private static DeliveryEncryptionService CreateEncryption() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build());

    private static SupplierConnectionService MakeConnectionSvc(ProcuLinkDbContext db, bool flagOn) =>
        new(db,
            new ReplayService(db, Array.Empty<ITransformService>()),
            new ConformanceService(),
            FlagConfig(flagOn));

    private static DeliveryService BuildDeliveryService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption, bool flagOn) =>
        new(
            db,
            new StubFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            reliability: null,
            effectiveConfig: new EffectiveConnectionConfigResolver(db, FlagConfig(flagOn)));

    // ── doubles ───────────────────────────────────────────────────────────────

    private sealed class CapturingDispatcher : IDeliveryDispatcher
    {
        public int Calls { get; private set; }
        public string? LastConfigJson { get; private set; }
        public string? LastCredentials { get; private set; }
        public string Protocol => "http";

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct,
            string? idempotencyKey = null)
        {
            Calls++;
            LastConfigJson  = config.ConfigJson;
            LastCredentials = decryptedCredentials;
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
}
