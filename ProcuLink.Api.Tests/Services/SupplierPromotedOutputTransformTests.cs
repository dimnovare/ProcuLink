using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// Launch batch 4A — the supplier-level promoted output mapping (<see cref="PoMappingConfig.Output"/>,
/// written by "Save mappings for this supplier") must be CONSUMED at transform time, with the
/// effective priority: per-order override → supplier-promoted output → fixed transformer.
///
/// The CRITICAL regressions guarded here:
/// <list type="bullet">
///   <item>GOLDEN — a supplier WITHOUT a usable promoted output mapping (no row, inbound-only
///        config, or empty output config) produces output BYTE-IDENTICAL to the fixed
///        transformer.</item>
///   <item>The per-order override always outranks the promoted supplier mapping.</item>
///   <item>A malformed supplier ConfigJson falls through to the fixed transformer — never a
///        throw.</item>
///   <item>The provenance ConfigDigest distinguishes the three sources (override JSON /
///        "supplier:{json}" / "fixed:{format}").</item>
/// </list>
/// </summary>
public class SupplierPromotedOutputTransformTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Builds OrderService wired with the REAL <see cref="PoMappingService"/> over the same db
    /// (so the supplier-promoted read path is exercised end-to-end), the real CSV/JSON/XML
    /// transformers, and a byte-capturing storage mock.
    /// </summary>
    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db)
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
            new PoMappingService(db), // REAL — the supplier-promoted read path under test
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedResolvedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Promoted Supplier", CreatedAt = DateTime.UtcNow });

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-4A-1",
            BuyerName  = "Batch4A Buyer",
            OrderDate  = new DateOnly(2026, 6, 10),
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

    /// <summary>A usable promoted output mapping (one header + one line rule).</summary>
    private static OutputMappingConfig PromotedOutput() => new()
    {
        Header = { ["po"]   = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
        Lines  = { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
    };

    // ── (a) GOLDEN — no usable promoted output → byte-identical to the fixed transformer ──────

    [Fact]
    public async Task Transform_SupplierWithNoMappingRow_IsByteIdenticalToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, _, orderId) = await SeedResolvedOrderAsync(db);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        var (svc, captured) = Build(db); // real PoMappingService; no SupplierPoMapping row exists

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, captured()); // byte-for-byte (CSV is timestamp-free)
    }

    [Fact]
    public async Task Transform_SupplierMappingWithInboundRulesOnly_NoOutput_IsByteIdenticalToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // The common pre-4A shape: a promoted INBOUND mapping (Header/Lines), no Output side.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "PO Number" } },
            Lines  = { ["Quantity"] = new FieldMappingEntry { ExternalField = "Qty" } },
            Output = null,
        }, CancellationToken.None);

        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);
        var (svc, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, captured()); // inbound-only supplier mappings never divert the output
    }

    [Fact]
    public async Task Transform_SupplierMappingWithEmptyOutputConfig_IsByteIdenticalToFixedTransformer()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // An Output object with ZERO rules is not usable — must not divert the transform.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig(),
        }, CancellationToken.None);

        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);
        var (svc, captured) = Build(db);

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, captured());
    }

    // ── (b) promoted output + NO per-order override → the promoted mapping drives the output ──

    [Fact]
    public async Task Transform_PromotedOutput_NoPerOrderOverride_DrivesNativeCsvOutput()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var fixedBytes = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var csv = Encoding.UTF8.GetString(captured()!);
        Assert.StartsWith("OrderRef,ItemCode", csv);   // promoted output paths, not the fixed columns
        Assert.Contains("PO-4A-1,SUP-1", csv);
        Assert.NotEqual(fixedBytes, captured());       // genuinely different from the fixed transformer
    }

    [Fact]
    public async Task Transform_PromotedOutput_StructuredFormat_AppliesEffectiveEntityToFixedTransform()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // A promoted rule that targets the recognised canonical PoNumber with a fixed value —
        // for XML this must resolve an effective entity and reuse the EXISTING fixed transform.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", FixedValue = "PROMOTED-PO" } },
            },
        }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Xml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var xml = Encoding.UTF8.GetString(captured()!);
        Assert.Contains("<PoNumber>PROMOTED-PO</PoNumber>", xml);
        Assert.DoesNotContain("PO-4A-1", xml);
    }

    // ── TRUST: a configured output mapping that THROWS must fail loudly, never deliver default ──

    private sealed class ThrowingXmlTransformService : ITransformService
    {
        public bool CanTransform(OutputFormat format) => format == OutputFormat.Xml;
        public Task<TransformResult> TransformAsync(
            PurchaseOrderEntity order, OutputFormat format, CancellationToken ct,
            CxmlCredentialConfig? cxmlCredentials = null) =>
            throw new InvalidOperationException("simulated transformer failure");
    }

    [Fact]
    public async Task Transform_PromotedOutput_TransformerThrows_FailsLoudly_NoSilentDefaultDelivery()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // A promoted XML output mapping routes through the (throwing) XML transformer.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PoNumber", FixedValue = "X" } },
            },
        }, CancellationToken.None);

        byte[]? captured = null;
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((stream, _, _, _) =>
            { using var ms = new MemoryStream(); stream.Position = 0; stream.CopyTo(ms); captured = ms.ToArray(); })
            .ReturnsAsync("artifact-key");

        var svc = new OrderService(
            db, fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new ThrowingXmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Xml, CancellationToken.None);

        Assert.False(result.IsSuccess);                           // failed loudly — no success-on-default
        Assert.Contains("could not be applied", result.Error ?? ""); // honest reason
        Assert.Null(captured);                                    // nothing uploaded → NOT delivered
        var order = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal("ready", order.Status);                      // reverted to retryable, not stranded
    }

    // ── (c) both present → the per-order override wins ────────────────────────────────────────

    [Fact]
    public async Task Transform_PerOrderOverrideAndPromotedOutput_PerOrderOverrideWins()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // Supplier-promoted output (would emit "OrderRef,ItemCode").
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        // Per-order override with DIFFERENT output paths — must outrank the promoted mapping.
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"]   = new OutputFieldRule { OutputPath = "PerOrderRef",  CanonicalField = "PoNumber" } },
                Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PerOrderCode", CanonicalField = "SupplierItemCode" } },
            },
        }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var csv = Encoding.UTF8.GetString(captured()!);
        Assert.StartsWith("PerOrderRef,PerOrderCode", csv); // override layout
        Assert.DoesNotContain("OrderRef,ItemCode", csv);    // promoted layout NOT used
    }

    // ── (d) malformed supplier ConfigJson → falls through to fixed, never throws ──────────────

    [Fact]
    public async Task Transform_MalformedSupplierConfigJson_FallsThroughToFixedTransformer_NoThrow()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var expected = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        // Seed a corrupt mapping row directly — PoMappingService.GetAsync will throw JsonException.
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            ConfigJson = "{ this is not valid json",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);   // never a throw / failure
        Assert.Equal(expected, captured());            // byte-identical fixed output

        // And the provenance records the FIXED marker, not a supplier digest.
        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), artifact.ConfigDigest);
    }

    // ── (e) ConfigDigest distinguishes supplier: / fixed: / override sources ──────────────────

    [Fact]
    public async Task Transform_PromotedOutput_ConfigDigestUsesSupplierPrefixedDescriptor()
    {
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        var promoted = PromotedOutput();
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = promoted }, CancellationToken.None);

        var (svc, _) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error);

        // Expected descriptor: "supplier:" + camelCase serialization of the promoted Output config
        // (matching how PoMappingService persists ConfigJson).
        var outputJson = JsonSerializer.Serialize(promoted, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var expectedDigest = ProvenanceHash.TrySha256HexUtf8($"supplier:{outputJson}");

        var artifact = await db.OutboundArtifacts.SingleAsync(a => a.OrderId == orderId);
        Assert.Equal(expectedDigest, artifact.ConfigDigest);
        Assert.NotEqual(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), artifact.ConfigDigest);
    }

    [Fact]
    public async Task Transform_ConfigDigest_DistinguishesAllThreeSources()
    {
        // One order transformed three times: fixed → supplier-promoted → per-order override.
        // Each step's NEW artifact (id returned by TransformAsync) must carry a distinct digest.
        await using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);
        var (svc, _) = Build(db);

        async Task<string?> DigestOfAsync(Guid artifactId) =>
            (await db.OutboundArtifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId)).ConfigDigest;

        // 1. FIXED (no supplier mapping yet, no override).
        var fixedResult = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(fixedResult.IsSuccess, fixedResult.Error);
        var fixedDigest = await DigestOfAsync(fixedResult.Value!.ArtifactId);

        // 2. SUPPLIER-PROMOTED (promote an output mapping, transform again). The
        //    idempotency guard only lets a 'ready' order start a transform, so return
        //    the order to 'ready' first (as a re-resolve would in the real flow).
        await ResetToReadyAsync(db, orderId);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);
        var supplierResult = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(supplierResult.IsSuccess, supplierResult.Error);
        var supplierDigest = await DigestOfAsync(supplierResult.Value!.ArtifactId);

        // 3. PER-ORDER OVERRIDE (store one, transform again — outranks the promoted mapping).
        await ResetToReadyAsync(db, orderId);
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "Ref", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);
        var overrideResult = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
        Assert.True(overrideResult.IsSuccess, overrideResult.Error);
        var overrideDigest = await DigestOfAsync(overrideResult.Value!.ArtifactId);

        Assert.Equal(ProvenanceHash.TrySha256HexUtf8("fixed:csv"), fixedDigest);
        Assert.NotNull(supplierDigest);
        Assert.NotNull(overrideDigest);
        Assert.NotEqual(fixedDigest,    supplierDigest);
        Assert.NotEqual(fixedDigest,    overrideDigest);
        Assert.NotEqual(supplierDigest, overrideDigest);
    }

    /// <summary>Returns a transformed order to 'ready' so the next transform can claim it.</summary>
    private static async Task ResetToReadyAsync(ProcuLinkDbContext db, Guid orderId)
    {
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status = "ready";
        await db.SaveChangesAsync();
    }

    // ── Phase B: a structured OutputNode tree delivers ARBITRARY structure end-to-end ─────────────

    [Fact]
    public async Task Transform_OutputTreeOverride_DeliversArbitraryNestedStructure_EndToEnd()
    {
        // The capability that is IMPOSSIBLE with the flat builder: a nested JSON with a renamed root
        // key and an "items" array of objects — designed as an OutputNode tree, persisted on the order
        // override (round-tripped through the override JSON), and DELIVERED by the live transform path.
        await using var db = NewDb();
        var (orgId, orderId) = (Guid.Empty, Guid.Empty);
        (orgId, _, orderId) = await SeedResolvedOrderAsync(db);

        var tree = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("orderNumber",
                    new OutputFieldRule { OutputPath = "orderNumber", CanonicalField = "PoNumber" }),
                OutputNode.Arr("items",
                    OutputNode.Obj("item",
                        OutputNode.FieldOf("code",
                            new OutputFieldRule { OutputPath = "code", CanonicalField = "SupplierItemCode" })))),
        };
        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderId, new OrderMappingOverride { OutputTree = tree }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(captured()!));
        Assert.Equal("PO-4A-1", doc.RootElement.GetProperty("orderNumber").GetString());  // renamed root key
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());                                            // repeating array
        Assert.Equal("SUP-1", items[0].GetProperty("code").GetString());                   // nested object
        Assert.Equal("SUP-2", items[1].GetProperty("code").GetString());
    }
}
