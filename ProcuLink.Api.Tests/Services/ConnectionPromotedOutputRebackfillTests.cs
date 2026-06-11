using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Launch-batch-7 review fix (finding 1): the V1 backfill used to write
/// <c>output_mapping_json = null</c> while copying the FULL <c>SupplierPoMapping.ConfigJson</c>
/// (which contains the batch-4A promoted Output section) into <c>input_mapping_json</c> — so a
/// flag-ON pinned order would silently revert to the fixed transformer.
///
/// Guards:
/// <list type="bullet">
///   <item>FORWARD: a fresh backfill now snapshots the promoted Output section into
///       <c>output_mapping_json</c> in the revision-consumable shape (camelCase
///       <see cref="OutputMappingConfig"/>), while a config without a usable Output still
///       snapshots null (fixed transformer, today's behaviour).</item>
///   <item>RE-BACKFILL: <see cref="ConnectionBackfillService.RebackfillPromotedOutputAsync"/>
///       repairs legacy null-output rows created by the backfill — fill-null-only, idempotent,
///       scoped to "system:backfill" rows; rows without a usable promoted Output (none / empty /
///       malformed / no po-mapping) and user-authored revisions stay untouched.</item>
///   <item>CONSUMABLE-SHAPE PROOF: after the re-backfill, a PINNED order rendered through the
///       revision path produces BYTE-IDENTICAL output to an identical UNPINNED order rendered
///       through the live batch-4A supplier-promoted path, for the same supplier config.</item>
/// </list>
/// </summary>
public class ConnectionPromotedOutputRebackfillTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions CamelCaseInsensitive = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>A supplier PoMappingConfig carrying a USABLE batch-4A promoted Output section.</summary>
    private static PoMappingConfig PromotedConfig() => new()
    {
        Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        Output = new OutputMappingConfig
        {
            Header = { ["po"]   = new OutputFieldRule { OutputPath = "PromotedRef",  CanonicalField = "PoNumber" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PromotedCode", CanonicalField = "SupplierItemCode" } },
        },
    };

    private static async Task<(Guid orgId, Guid supplierId)> SeedSupplierAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Promoted OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    /// <summary>
    /// Seeds a LEGACY-shaped backfilled rev-1: published, CreatedBy "system:backfill",
    /// output_mapping_json NULL — exactly what the pre-fix backfill produced.
    /// </summary>
    private static async Task<SupplierConnectionRevision> SeedLegacyBackfilledRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string createdBy = ConnectionBackfillService.BackfillActor)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id            = Guid.NewGuid(),
            ConnectionId  = connectionId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            VersionNo     = 1,
            Status        = "published",
            EffectiveFrom = now,
            PublishedAt   = now,
            CreatedAt     = now,
            CreatedBy     = createdBy,
            PublishedBy   = createdBy,
            OutputMappingJson = null, // the legacy bug shape
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);
        await db.SaveChangesAsync();
        return revision;
    }

    // ── FORWARD: fresh backfills snapshot the promoted Output ──────────────────

    [Fact]
    public async Task Backfill_SupplierConfigWithPromotedOutput_SnapshotsConsumableOutputJson()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        // Persist through the REAL PoMappingService so ConfigJson is stored exactly as production stores it.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);

        var created = await new ConnectionBackfillService(db).BackfillAllAsync(CancellationToken.None);
        Assert.Equal(1, created);

        var rev = await db.SupplierConnectionRevisions.SingleAsync();
        Assert.NotNull(rev.OutputMappingJson);

        // The snapshot must be the EXACT consumable shape: the camelCase OutputMappingConfig the
        // revision transform path (OrderTransformService.RevisionOutputSerializerOptions /
        // ReplayService.DeserializeOutputConfig) reads back.
        var roundTripped = JsonSerializer.Deserialize<OutputMappingConfig>(rev.OutputMappingJson!, CamelCaseInsensitive);
        Assert.NotNull(roundTripped);
        Assert.Equal("PromotedRef",  roundTripped!.Header["po"].OutputPath);
        Assert.Equal("PromotedCode", roundTripped.Lines["code"].OutputPath);
        Assert.Equal(JsonSerializer.Serialize(PromotedConfig().Output, CamelCase), rev.OutputMappingJson);

        // The input snapshot is still the FULL ConfigJson, byte-identical (prime directive intact).
        var stored = await db.SupplierPoMappings.SingleAsync();
        Assert.Equal(stored.ConfigJson, rev.InputMappingJson);
    }

    [Fact]
    public async Task Backfill_SupplierConfigWithoutOutput_StillSnapshotsNull()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        }, CancellationToken.None);

        await new ConnectionBackfillService(db).BackfillAllAsync(CancellationToken.None);

        var rev = await db.SupplierConnectionRevisions.SingleAsync();
        Assert.Null(rev.OutputMappingJson); // no promoted Output → fixed transformer, today's behaviour
    }

    // ── RE-BACKFILL: legacy null-output rows are repaired ──────────────────────

    [Fact]
    public async Task Rebackfill_FillsNullOutput_FromPromotedSupplierConfig()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);

        var updated = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(1, updated);
        var rev = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id);
        Assert.Equal(JsonSerializer.Serialize(PromotedConfig().Output, CamelCase), rev.OutputMappingJson);
        Assert.Equal("published", rev.Status); // lifecycle untouched — content repair only
    }

    [Fact]
    public async Task Rebackfill_IsIdempotent_SecondRunIsNoOp()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);
        var svc = new ConnectionBackfillService(db);

        var first  = await svc.RebackfillPromotedOutputAsync(CancellationToken.None);
        var jsonAfterFirst = (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson;
        var second = await svc.RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // re-run repairs nothing
        Assert.Equal(jsonAfterFirst,
            (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson);
    }

    [Fact]
    public async Task Rebackfill_NeverOverwritesNonNullSnapshot()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);

        // The revision already carries a (different) output snapshot — must stay byte-identical.
        const string existing = "{\"header\":{\"po\":{\"outputPath\":\"PinnedRef\",\"canonicalField\":\"PoNumber\"}},\"lines\":{}}";
        var tracked = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id);
        tracked.OutputMappingJson = existing;
        await db.SaveChangesAsync();

        var updated = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Equal(existing,
            (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson);
    }

    [Fact]
    public async Task Rebackfill_RowsWithoutUsablePromotedOutput_AreLeftUntouched()
    {
        var db = NewDb();
        var svc = new ConnectionBackfillService(db);

        // (a) config with NO Output section
        var (orgA, supplierA) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgA, supplierA, new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        }, CancellationToken.None);
        var revA = await SeedLegacyBackfilledRevisionAsync(db, orgA, supplierA);

        // (b) MALFORMED ConfigJson (legacy/free-form mapping JSON) — must skip, never throw
        var (orgB, supplierB) = await SeedSupplierAsync(db);
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id = Guid.NewGuid(), OrgId = orgB, SupplierId = supplierB,
            ConfigJson = "{ not valid json", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var revB = await SeedLegacyBackfilledRevisionAsync(db, orgB, supplierB);

        // (c) NO po-mapping row at all
        var (orgC, supplierC) = await SeedSupplierAsync(db);
        var revC = await SeedLegacyBackfilledRevisionAsync(db, orgC, supplierC);

        var updated = await svc.RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revA.Id)).OutputMappingJson);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revB.Id)).OutputMappingJson);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revC.Id)).OutputMappingJson);
    }

    [Fact]
    public async Task Rebackfill_UserAuthoredRevisionWithNullOutput_IsNeverTouched()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        // A USER published this revision with a null output mapping — that means "fixed
        // transformer" by choice and must never be rewritten from the live table.
        var userRev = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId, createdBy: "user:someone");

        var updated = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == userRev.Id)).OutputMappingJson);
    }

    // ── CONSUMABLE-SHAPE PROOF: revision path ≡ batch-4A promoted path ─────────

    /// <summary>OrderService wired exactly like <see cref="RevisionAuthorityTransformTests"/>: real PoMappingService (4A path), real transformers, byte-capturing storage, revision-authority resolver.</summary>
    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db, bool flagEnabled)
    {
        byte[]? captured = null;

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((stream, _, _, _) =>
            {
                using var ms = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(ms);
                captured = ms.ToArray();
            })
            .ReturnsAsync("artifact-key");

        var resolver = new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = flagEnabled ? "true" : "false",
            })
            .Build());

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db), // REAL — drives the live 4A promoted path for the unpinned order
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: resolver);

        return (svc, () => captured);
    }

    /// <summary>Seeds a fully-resolved 2-line order; identical content per call so renders are comparable.</summary>
    private static async Task<Guid> SeedResolvedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, Guid? connectionRevisionId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            ConnectionRevisionId = connectionRevisionId,
            PoNumber   = "PO-PARITY-1",
            BuyerName  = "Parity Buyer",
            OrderDate  = new DateOnly(2026, 6, 11),
            Currency   = "EUR",
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "B-2", SupplierItemCode = "SUP-2", Description = "Gadget",
                    Quantity = 2m, Unit = "EA", UnitPrice = 5.5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    [Fact]
    public async Task Rebackfill_PinnedRevisionPath_RendersByteIdenticalToLivePromotedPath()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);

        // Identical orders: one PINNED to the legacy revision, one UNPINNED (live 4A path).
        var pinnedOrderId   = await SeedResolvedOrderAsync(db, orgId, supplierId, legacy.Id);
        var unpinnedOrderId = await SeedResolvedOrderAsync(db, orgId, supplierId, null);

        var repaired = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);
        Assert.Equal(1, repaired);

        var (svc, captured) = Build(db, flagEnabled: true);

        var pinnedResult = await svc.TransformAsync(orgId, pinnedOrderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(pinnedResult.IsSuccess, pinnedResult.Error);
        var pinnedBytes = captured()!;

        var unpinnedResult = await svc.TransformAsync(orgId, unpinnedOrderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(unpinnedResult.IsSuccess, unpinnedResult.Error);
        var promotedBytes = captured()!;

        // The promoted layout actually drove the pinned render (not a coincidental fixed fallback)…
        var pinnedCsv = Encoding.UTF8.GetString(pinnedBytes);
        Assert.StartsWith("PromotedRef,PromotedCode", pinnedCsv);
        Assert.Contains("PO-PARITY-1,SUP-1", pinnedCsv);

        // …and the revision path renders BYTE-IDENTICAL output to the batch-4A promoted path
        // for the same supplier config (CSV is timestamp-free) — the consumable-shape proof.
        Assert.Equal(promotedBytes, pinnedBytes);

        // Provenance: the pinned artifact records the revision-prefixed digest over the
        // re-backfilled snapshot; the unpinned one records the supplier-prefixed digest.
        var revJson = (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson;
        var pinnedArtifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == pinnedOrderId);
        Assert.Equal(
            ProvenanceHash.TrySha256HexUtf8($"revision:{legacy.Id}:{revJson}"),
            pinnedArtifact.ConfigDigest);
        var unpinnedArtifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == unpinnedOrderId);
        Assert.Equal(
            ProvenanceHash.TrySha256HexUtf8($"supplier:{revJson}"),
            unpinnedArtifact.ConfigDigest); // same output-config JSON, supplier-prefixed — shapes provably identical
    }
}
