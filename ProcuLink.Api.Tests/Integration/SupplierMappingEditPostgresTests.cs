using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The SUPPLIER-level twin of <see cref="HeldOrderMappingEditPostgresTests"/>, asserted the same way:
/// on the BYTES a dispatcher was handed, never on an artifact id, a status alone, or a row count.
///
/// <para><b>The failure this pins.</b> Five endpoints persist a supplier's <c>PoMappingConfig</c> —
/// <c>PUT /api/suppliers/{id}/po-mapping</c>, apply-template, the two DELETEs, and
/// <c>POST /api/orders/{id}/mapping-override/promote</c> — and none of them writes
/// <c>PurchaseOrders</c>. The config's <c>Output</c>/<c>OutputTree</c> half is CONSUMED at transform
/// time (<c>OrderTransformService.TryReadSupplierPromotedOutputAsync</c>) for an order carrying no
/// per-order output seam. So for that supplier's already-transformed orders the edit was accepted and
/// the artifact built BEFORE it was what Redeliver / RetryDelivery / the ops requeue / the stranded
/// sweep went on to ship — the same shape as the per-order bug, at supplier scale.
/// <c>OrderMappingOverrideService</c>'s reset cannot help, because that service never sees these writes.</para>
///
/// <para><b>And the half that was already correct, pinned so a future fix cannot break it.</b>
/// <c>OrderTransformService</c> is explicit that the two config sources are mutually exclusive: a
/// PINNED order consults only its revision snapshot and never this table. A blanket reset would churn
/// every pinned order of the supplier into a re-transform that produces byte-identical output. The
/// pinned order in this test must come out untouched AND still ship its own document.</para>
///
/// <para><b>Why real Postgres.</b> The reset is a set-based query over a status set joined to a
/// per-order pin lookup and a <c>canonical_json</c> read, committed in its own <c>SaveChanges</c>
/// ahead of the mapping write. EF InMemory resolves all three through one change tracker and cannot
/// show that the candidate query really excludes what it claims to.</para>
///
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SupplierMappingEditPostgresTests : IAsyncLifetime
{
    /// <summary>The unpinned order's document, built BEFORE the supplier mapping was corrected.</summary>
    private const string PreEditDocument = "po_number,supplier_item_code\r\nPO-SUP-1,WRONG-SKU-0001\r\n";

    /// <summary>What a re-transform under the corrected supplier mapping produces.</summary>
    private const string CorrectedDocument = "po_number,supplier_item_code\r\nPO-SUP-1,RIGHT-SKU-0002\r\n";

    /// <summary>The PINNED order's document. Its revision governs it, so it must still be shippable.</summary>
    private const string PinnedDocument = "po_number,supplier_item_code\r\nPO-SUP-2,PINNED-SKU-0003\r\n";

    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_supedit_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
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
    public async Task SupplierMappingEdit_NeverShipsThePreEditDocument_AndLeavesThePinnedOrderAlone()
    {
        var encryption = CreateEncryption();
        var storage    = new KeyedFileStorage();
        var dispatcher = new CapturingDispatcher();

        var orgId      = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId, encryption);

        // Both orders belong to the SAME supplier, both sit post-transform holding a shippable
        // artifact, and neither carries a per-order output seam. The ONLY difference is the pin.
        var (unpinnedId, preEditKey) =
            await SeedTransformedOrderAsync(orgId, supplierId, "PO-SUP-1", pin: null);
        var revisionId = await SeedPublishedRevisionAsync(orgId, supplierId);
        var (pinnedId, pinnedKey) =
            await SeedTransformedOrderAsync(orgId, supplierId, "PO-SUP-2", pin: revisionId);

        storage.Seed(preEditKey, PreEditDocument);
        storage.Seed(pinnedKey, PinnedDocument);

        // 1 ── The operator corrects the supplier's OUTPUT mapping. An ordinary click path: the
        //      endpoint carries no status gate and knows nothing about in-flight orders.
        await using (var db = NewContext())
        {
            await BuildPoMappings(db).UpsertAsync(orgId, supplierId, CorrectedSupplierConfig(), CancellationToken.None);
        }

        // 2 ── Drive every path that ships a STORED artifact at the unpinned order — Redeliver, the
        //      retry ladder, and a direct dispatch of the pre-edit artifact by id. If ANY of them
        //      sends, the bytes are captured below.
        var preEditArtifactId = await ArtifactIdForKeyAsync(preEditKey);
        await using (var db = NewContext())
        {
            var svc = BuildDelivery(db, storage, dispatcher, encryption);
            await svc.RetryDeliveryAsync(orgId, unpinnedId, maxAttempts: 3, CancellationToken.None);
            await svc.DispatchArtifactAsync(
                orgId, unpinnedId, preEditArtifactId, requireAutoDeliver: false, CancellationToken.None);
        }

        // ── THE ASSERTION. On the bytes: a status check alone would still read green if the right
        //    row pointed at the wrong document.
        Assert.True(
            dispatcher.CapturedText != PreEditDocument,
            "A correction to the SUPPLIER's output mapping must invalidate the artifacts built before "
          + "it. The supplier was handed the PRE-EDIT document instead, and the correction was never "
          + $"sent. Dispatched bytes:\n{dispatcher.CapturedText}");

        // 3 ── Only now the statuses, so that a regression fails on the BYTES above rather than being
        //      pre-empted here. A status assertion placed earlier short-circuits the whole point of
        //      this test: it would report "expected ready, was ready_to_deliver" and never establish
        //      that the wrong document actually went out.
        await using (var verify = NewContext())
        {
            var unpinned = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == unpinnedId);
            Assert.Equal(OrderStatusConstants.Ready, unpinned.Status);

            var pinned = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == pinnedId);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, pinned.Status);
        }

        // 4 ── ...and the correction is not merely withheld: it is what the order now sends.
        var correctedArtifactId = await AppendReTransformedArtifactAsync(orgId, unpinnedId, storage);
        await using (var db = NewContext())
        {
            var svc = BuildDelivery(db, storage, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                orgId, unpinnedId, correctedArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success, result.ErrorMessage);
        }
        Assert.Equal(CorrectedDocument, dispatcher.CapturedText);

        // 5 ── The pinned order was not collateral damage: it still ships its own document, unchanged.
        //      Asserted on bytes too — "status still ready_to_deliver" would not catch a reset that
        //      also purged or re-pointed its artifact.
        var pinnedArtifactId = await ArtifactIdForKeyAsync(pinnedKey);
        await using (var db = NewContext())
        {
            var svc = BuildDelivery(db, storage, dispatcher, encryption);
            var result = await svc.DispatchArtifactAsync(
                orgId, pinnedId, pinnedArtifactId, requireAutoDeliver: false, CancellationToken.None);
            Assert.True(result.Success, result.ErrorMessage);
        }
        Assert.Equal(PinnedDocument, dispatcher.CapturedText);
    }

    /// <summary>
    /// The two saves that must reset NOTHING. Both are far more common than an output-mapping change,
    /// and resetting for either would move a supplier's whole in-flight book for no byte difference —
    /// the disproportionate-blast-radius failure this design exists to avoid.
    /// </summary>
    [DockerRequiredFact]
    public async Task AnInboundOnlySave_AndAnOrderWithItsOwnOutputMapping_AreBothLeftAlone()
    {
        var encryption = CreateEncryption();
        var orgId      = await SeedOrgAsync();
        var supplierId = await SeedSupplierAsync(orgId, encryption);

        var (plainId, _)     = await SeedTransformedOrderAsync(orgId, supplierId, "PO-SUP-3", pin: null);
        var (overriddenId, _) = await SeedTransformedOrderAsync(
            orgId, supplierId, "PO-SUP-4", pin: null, perOrderOutput: true);

        // Establish a baseline output half, then confirm the reset it causes is real (so the
        // no-reset assertions below cannot pass merely because nothing ever resets).
        await using (var db = NewContext())
            await BuildPoMappings(db).UpsertAsync(orgId, supplierId, CorrectedSupplierConfig(), CancellationToken.None);

        await using (var verify = NewContext())
        {
            var plain = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == plainId);
            Assert.Equal(OrderStatusConstants.Ready, plain.Status);

            // The order carrying its OWN output mapping outranks the supplier layer for every format,
            // so the supplier mapping is never read for it — and it must not be reset.
            var overridden = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == overriddenId);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, overridden.Status);
        }

        // Put the plain order back post-transform, then save a mapping whose INBOUND half changed and
        // whose output half did not. Header/Lines drive PoMappingEngine at PARSE time and the
        // transform never re-parses, so this cannot change any already-parsed order's bytes.
        await using (var db = NewContext())
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == plainId);
            order.Status = OrderStatusConstants.ReadyToDeliver;
            await db.SaveChangesAsync();
        }

        var inboundOnly = CorrectedSupplierConfig();
        inboundOnly.Header["BuyerName"] = new FieldMappingEntry { ExternalField = "customer_name" };

        await using (var db = NewContext())
            await BuildPoMappings(db).UpsertAsync(orgId, supplierId, inboundOnly, CancellationToken.None);

        await using (var verify = NewContext())
        {
            var plain = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == plainId);
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, plain.Status);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The interface exactly as the API composition root builds it — decorator over the real service.</summary>
    private static IPoMappingService BuildPoMappings(ProcuLinkDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // ON, matching production on both Railway services. With the flag OFF a pinned order
                // reads the LIVE table and MUST be reset, which is why the resolver is asked rather
                // than the pin column being read directly.
                [EffectiveConnectionConfigResolver.FlagKey] = "true",
            })
            .Build();

        return new ArtifactInvalidatingPoMappingService(
            new PoMappingService(db),
            db,
            new EffectiveConnectionConfigResolver(db, config),
            NullLogger<ArtifactInvalidatingPoMappingService>.Instance);
    }

    /// <summary>A supplier config whose OUTPUT half differs from the seeded one.</summary>
    private static PoMappingConfig CorrectedSupplierConfig() => new()
    {
        Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        Output = new OutputMappingConfig
        {
            Lines = { ["code"] = new OutputFieldRule { OutputPath = "RIGHT-SKU-0002", CanonicalField = "SupplierItemCode" } },
        },
    };

    private async Task<Guid> ArtifactIdForKeyAsync(string fileKey)
    {
        await using var db = NewContext();
        return await db.OutboundArtifacts.AsNoTracking()
            .Where(a => a.FileKey == fileKey).Select(a => a.Id).SingleAsync();
    }

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
    /// Real per-key byte semantics. The suite's common <c>FakeFileStorage</c> returns the SAME constant
    /// for every key and therefore cannot tell a stale artifact from a fresh one — the exact
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

    private static DeliveryService BuildDelivery(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService encryption) =>
        new(
            db,
            storage,
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance,
            retryEnqueuer: null);

    private async Task<Guid> SeedOrgAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_se_{orgId:N}", Name = "Supplier-Edit Org",
            Slug = $"se-{orgId:N}", Plan = "operations", AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private async Task<Guid> SeedSupplierAsync(Guid orgId, DeliveryEncryptionService encryption)
    {
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewContext();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Protocol = "http", AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    /// <summary>
    /// A published revision an order can pin to. Seeded connection-first with a null
    /// <c>ActiveRevisionId</c>, then the revision, then the pointer — the SupplierConnection ↔
    /// SupplierConnectionRevision FKs are circular and Postgres enforces them.
    /// </summary>
    private async Task<Guid> SeedPublishedRevisionAsync(Guid orgId, Guid supplierId)
    {
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        await using var db = NewContext();
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId,
            Name = "Connection", ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", PublishedAt = now, CreatedAt = now,
            OutputFormat = "csv",
        });
        await db.SaveChangesAsync();

        var connection = await db.SupplierConnections.SingleAsync(c => c.Id == connectionId);
        connection.ActiveRevisionId = revisionId;
        await db.SaveChangesAsync();

        return revisionId;
    }

    /// <summary>An order already transformed once: post-transform status + one stored artifact.</summary>
    private async Task<(Guid OrderId, string ArtifactKey)> SeedTransformedOrderAsync(
        Guid orgId, Guid supplierId, string poNumber, Guid? pin, bool perOrderOutput = false)
    {
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now        = DateTime.UtcNow;
        var artifactKey = $"{orgId}/{orderId}/artifacts/{artifactId}.csv";

        await using var db = NewContext();
        var order = new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            ConnectionRevisionId = pin,
            PoNumber = poNumber, BuyerName = "Buyer", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = OrderStatusConstants.ReadyToDeliver,
            CreatedAt = now, UpdatedAt = now,
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        if (perOrderOutput)
        {
            // Written through the real per-order writer so the canonical_json shape is the one
            // production stores and OrderMappingOverrideReader.Read really finds it.
            await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
            {
                Output = new OutputMappingConfig
                {
                    Lines = { ["code"] = new OutputFieldRule { OutputPath = "PerOrder", CanonicalField = "SupplierItemCode" } },
                },
            }, CancellationToken.None);

            // That write resets the order to 'ready' (the per-order path this packet's twin fixed);
            // put it back post-transform so the supplier-level save has a real candidate to skip.
            var written = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            written.Status = OrderStatusConstants.ReadyToDeliver;
            await db.SaveChangesAsync();
        }

        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = artifactKey, CreatedAt = now,
            ConnectionRevisionId = pin,
        });
        await db.SaveChangesAsync();

        return (orderId, artifactKey);
    }
}
