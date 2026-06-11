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
/// Launch batch 7 — the pinned revision's OUTPUT snapshot must govern the transform when the
/// <c>Connections:RevisionAuthority</c> flag is ON, with the effective priority:
/// per-order override → revision-pinned output (pinned) / supplier-promoted output (unpinned) →
/// fixed transformer.
///
/// The CRITICAL regressions guarded here:
/// <list type="bullet">
///   <item>GOLDEN — flag OFF (or unpinned with flag ON) produces output BYTE-IDENTICAL to the
///        pre-batch-7 transform, even when a revision with a divergent output snapshot exists.</item>
///   <item>The per-order override always outranks the revision snapshot.</item>
///   <item>A PINNED order never consults the live supplier-promoted mapping (reproducibility) —
///        a null revision output snapshot falls to the FIXED transformer, not to batch 4A.</item>
///   <item>The revision's snapshotted OutputFormat outranks the requested format.</item>
///   <item>A malformed snapshot falls to the fixed transformer — never a throw.</item>
///   <item>The provenance ConfigDigest is the revision-prefixed descriptor
///        ("revision:{id}:{json}") when the snapshot drove the output.</item>
/// </list>
/// </summary>
public class RevisionAuthorityTransformTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IEffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool enabled) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = enabled ? "true" : "false",
            })
            .Build());

    /// <summary>OrderService wired with the REAL PoMappingService (4A path), real transformers, a byte-capturing storage mock, and the revision-authority resolver.</summary>
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

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db), // REAL — the supplier-promoted (4A) read path stays live for unpinned orders
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: Resolver(db, flagEnabled));

        return (svc, () => captured);
    }

    /// <summary>A resolved 2-line order, optionally pinned to <paramref name="connectionRevisionId"/>.</summary>
    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedResolvedOrderAsync(
        ProcuLinkDbContext db, Guid? connectionRevisionId = null)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Authority Supplier", CreatedAt = DateTime.UtcNow });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            ConnectionRevisionId = connectionRevisionId,
            PoNumber   = "PO-B7-1",
            BuyerName  = "Batch7 Buyer",
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
        return (orgId, supplierId, orderId);
    }

    /// <summary>Seeds a published connection revision for the supplier and returns it (orders pin its id).</summary>
    private static async Task<SupplierConnectionRevision> SeedRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string? outputMappingJson, string? outputFormat = null)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id           = Guid.NewGuid(),
            ConnectionId = connectionId,
            OrgId        = orgId,
            SupplierId   = supplierId,
            VersionNo    = 1,
            Status       = "published",
            CreatedAt    = now,
            PublishedAt  = now,
            OutputMappingJson = outputMappingJson,
            OutputFormat      = outputFormat,
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);
        await db.SaveChangesAsync();
        return revision;
    }

    /// <summary>Pins an already-seeded order to a revision (mirrors the ingest-time stamp).</summary>
    private static async Task PinOrderAsync(ProcuLinkDbContext db, Guid orderId, Guid revisionId)
    {
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revisionId;
        await db.SaveChangesAsync();
    }

    /// <summary>The fixed transformer's exact bytes for this order — the byte-identical golden.</summary>
    private static async Task<byte[]> FixedTransformBytesAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId, OutputFormat format)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .FirstAsync(o => o.Id == orderId && o.OrgId == orgId);

        ITransformService fixedSvc = format switch
        {
            OutputFormat.Csv => new CsvTransformService(),
            OutputFormat.Xml => new XmlTransformService(),
            _                => new JsonTransformService(),
        };

        var result = await fixedSvc.TransformAsync(order, format, CancellationToken.None);
        result.Content.Position = 0;
        using var ms = new MemoryStream();
        await result.Content.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>A usable revision output snapshot (one header + one line rule), serialized as the revision stores it.</summary>
    private static OutputMappingConfig RevisionOutput() => new()
    {
        Header = { ["po"]   = new OutputFieldRule { OutputPath = "RevRef",  CanonicalField = "PoNumber" } },
        Lines  = { ["code"] = new OutputFieldRule { OutputPath = "RevCode", CanonicalField = "SupplierItemCode" } },
    };

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string RevisionOutputJson() => JsonSerializer.Serialize(RevisionOutput(), CamelCase);

    // ── GOLDEN — flag OFF / unpinned: byte-identical even when a divergent revision exists ──

    [Fact]
    public async Task FlagOff_PinnedOrderWithDivergentRevisionOutput_ByteIdenticalToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var rev = await SeedRevisionAsync(db, orgId, supplierId, RevisionOutputJson(), "json");
        await PinOrderAsync(db, orderId, rev.Id);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db, flagEnabled: false);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("csv", result.Value!.Format); // the revision's "json" format must NOT apply flag-off
        Assert.Equal(expected, captured());        // byte-for-byte (CSV is timestamp-free)

        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), artifact.ConfigDigest);
    }

    [Fact]
    public async Task FlagOn_UnpinnedOrder_ByteIdenticalToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db); // NOT pinned
        await SeedRevisionAsync(db, orgId, supplierId, RevisionOutputJson(), "json");
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("csv", result.Value!.Format);
        Assert.Equal(expected, captured());
    }

    // ── Flag ON + pinned: the revision snapshot governs while live tables differ ──

    [Fact]
    public async Task FlagOn_Pinned_RevisionOutputDrivesNativeCsv_AndDigestIsRevisionPrefixed()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var outputJson = RevisionOutputJson();
        var rev = await SeedRevisionAsync(db, orgId, supplierId, outputJson);
        await PinOrderAsync(db, orderId, rev.Id);
        var fixedBytes = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var csv = Encoding.UTF8.GetString(captured()!);
        Assert.StartsWith("RevRef,RevCode", csv);   // the revision's snapshotted output layout
        Assert.Contains("PO-B7-1,SUP-1", csv);
        Assert.NotEqual(fixedBytes, captured());

        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal(rev.Id, artifact.ConnectionRevisionId);
        Assert.Equal(
            ProvenanceHash.TrySha256HexUtf8($"revision:{rev.Id}:{outputJson}"),
            artifact.ConfigDigest);
        Assert.NotEqual(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), artifact.ConfigDigest);
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionDrives_EvenWhenLiveSupplierPromotedMappingDiffers()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var rev = await SeedRevisionAsync(db, orgId, supplierId, RevisionOutputJson());
        await PinOrderAsync(db, orderId, rev.Id);

        // A LIVE supplier-promoted output mapping edited AFTER publish — must be ignored for a pinned order.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "LiveEditedRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var csv = Encoding.UTF8.GetString(captured()!);
        Assert.StartsWith("RevRef,RevCode", csv);     // revision snapshot, reproducible
        Assert.DoesNotContain("LiveEditedRef", csv);  // live edit ignored
    }

    [Fact]
    public async Task FlagOn_Pinned_PerOrderOverrideStillBeatsRevision()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var rev = await SeedRevisionAsync(db, orgId, supplierId, RevisionOutputJson());
        await PinOrderAsync(db, orderId, rev.Id);

        // Per-order override — the HIGHEST-priority seam, unchanged by batch 7.
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"]   = new OutputFieldRule { OutputPath = "PerOrderRef",  CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PerOrderCode", CanonicalField = "SupplierItemCode" } },
            },
        }, CancellationToken.None);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var csv = Encoding.UTF8.GetString(captured()!);
        Assert.StartsWith("PerOrderRef,PerOrderCode", csv); // override layout wins
        Assert.DoesNotContain("RevRef", csv);               // revision layout NOT used
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionOutputFormatOverridesRequestedFormat()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        // Revision pins the supplier contract to JSON; no output mapping → fixed JSON transformer.
        var rev = await SeedRevisionAsync(db, orgId, supplierId, outputMappingJson: null, outputFormat: "json");
        await PinOrderAsync(db, orderId, rev.Id);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("json", result.Value!.Format);  // the revision's format governs
        // The fixed JSON transformer's document (not byte-compared — it embeds generatedAt).
        var json = Encoding.UTF8.GetString(captured()!);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PO-B7-1", doc.RootElement.GetProperty("poNumber").GetString());

        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal("json", artifact.Format);
        Assert.Equal(ProvenanceHash.TrySha256HexUtf8("fixed:json"), artifact.ConfigDigest);
    }

    // ── Granular fallbacks — null / malformed snapshot, orphan pin ──

    [Fact]
    public async Task FlagOn_Pinned_NullRevisionOutput_FixedDrives_LiveSupplierPromotedNotConsulted()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        // Backfilled-rev-1 shape: NO output mapping snapshotted.
        var rev = await SeedRevisionAsync(db, orgId, supplierId, outputMappingJson: null);
        await PinOrderAsync(db, orderId, rev.Id);

        // A live supplier-promoted mapping exists — but a PINNED order must NOT consult it
        // (mutable table; consulting it would break reproducibility).
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PromotedRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);
        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, captured()); // fixed transformer, byte-identical
        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), artifact.ConfigDigest);
    }

    [Fact]
    public async Task FlagOn_Pinned_MalformedRevisionOutput_FallsToFixed_NoThrow()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var rev = await SeedRevisionAsync(db, orgId, supplierId, outputMappingJson: "{ not valid json");
        await PinOrderAsync(db, orderId, rev.Id);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error); // never a throw / failure
        Assert.Equal(expected, captured());
        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), artifact.ConfigDigest);
    }

    [Fact]
    public async Task FlagOn_OrphanPin_FallsBackToLivePath_NoThrow()
    {
        await using var db = NewDb();
        var (orgId, _, orderId) = await SeedResolvedOrderAsync(db, connectionRevisionId: Guid.NewGuid()); // dangling pin
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db, flagEnabled: true);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, captured()); // live path = fixed transformer here
    }
}
