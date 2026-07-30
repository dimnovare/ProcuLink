using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
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
/// WP-12 — the structured OUTPUT TREE (<see cref="OrderMappingOverride.OutputTree"/>) must survive
/// promotion, exactly as the flat <see cref="OrderMappingOverride.Output"/> already does.
///
/// <para>The defect this class pins: the visual output designer is the product's differentiator, and
/// its output currently dies with the order. A tree designed on order A lives only in that order's
/// <c>canonical_json</c>; <c>PromoteMappingService</c> copies the flat <c>Output</c> forward but has
/// no knowledge of <c>OutputTree</c>, so the next identical file from the same supplier renders
/// through the FIXED transformer and every design decision is lost.</para>
///
/// <para>Companion to <see cref="SupplierPromotedOutputTransformTests"/>, which pins the same
/// contract for the flat output config. The precedence ladder asserted here is the one documented in
/// <c>OrderTransformService</c>: per-order tree → per-order template → per-order flat output →
/// revision-pinned output → supplier-promoted (tree, then flat) → fixed transformer.</para>
/// </summary>
public class SupplierPromotedOutputTreeTransformTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// OrderService wired with the REAL <see cref="PoMappingService"/> over the same db, so the
    /// supplier-promoted read path is exercised end-to-end, plus a byte-capturing storage mock.
    /// Mirrors <see cref="SupplierPromotedOutputTransformTests"/>.Build.
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

    /// <summary>
    /// Seeds one resolved, ready order with FIXED business content. Called twice with the same
    /// supplier to model "the same supplier sends the same file again" — the two orders differ only
    /// by row id, so a byte-identical render is a meaningful assertion rather than a tautology.
    /// </summary>
    private static async Task<Guid> SeedIdenticalOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-WP12-1",
            BuyerName  = "WP12 Buyer",
            OrderDate  = new DateOnly(2026, 7, 27),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Ready,
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

    private static async Task<(Guid OrgId, Guid SupplierId)> SeedSupplierAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "WP12 Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        return (orgId, supplierId);
    }

    /// <summary>
    /// The designed tree: a nested JSON document with a renamed root key and a repeating "items"
    /// array — structure the flat <see cref="OutputMappingConfig"/> cannot express at all, which is
    /// why losing it on promotion loses the whole differentiator.
    /// </summary>
    private static OutputNodeTemplate DesignedTree() => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("orderNumber",
                new OutputFieldRule { OutputPath = "orderNumber", CanonicalField = "PoNumber" }),
            OutputNode.FieldOf("buyer",
                new OutputFieldRule { OutputPath = "buyer", CanonicalField = "BuyerName" }),
            OutputNode.Arr("items",
                OutputNode.Obj("item",
                    OutputNode.FieldOf("code",
                        new OutputFieldRule { OutputPath = "code", CanonicalField = "SupplierItemCode" }),
                    OutputNode.FieldOf("qty",
                        new OutputFieldRule { OutputPath = "qty", CanonicalField = "Quantity" })))),
    };

    private static async Task<byte[]> TransformAndCaptureAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, OutputFormat format)
    {
        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, format, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error);
        var bytes = captured();
        Assert.NotNull(bytes);
        return bytes!;
    }

    // ── THE ACCEPTANCE CRITERION ───────────────────────────────────────────────
    // Design once on order A, promote, and the next identical file renders identically
    // with zero designer interaction. This is WP-12's entire reason to exist.

    [Fact]
    public async Task DesignTreeOnOrderA_Promote_OrderBRendersByteIdentically_WithNoDesignerInteraction()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);

        // Order A — the operator designs the supplier's exact output structure on this one.
        var orderA = await SeedIdenticalOrderAsync(db, orgId, supplierId);
        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);

        var bytesA = await TransformAndCaptureAsync(db, orgId, orderA, OutputFormat.Json);

        // "Save this layout for the supplier" — the promote control WP-13 wires up.
        var promoted = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderA, CancellationToken.None);
        Assert.NotNull(promoted);

        // Order B — the same supplier sends the same file again. NO per-order override at all.
        var orderB = await SeedIdenticalOrderAsync(db, orgId, supplierId);
        var bytesB = await TransformAndCaptureAsync(db, orgId, orderB, OutputFormat.Json);

        Assert.Equal(
            Encoding.UTF8.GetString(bytesA),
            Encoding.UTF8.GetString(bytesB)); // readable diff on failure
        Assert.Equal(bytesA, bytesB);         // byte-for-byte
    }

    [Fact]
    public async Task PromoteAsync_ReportsTheOutputTreeItSaved()
    {
        // A promote that silently drops the tree is the defect. The result must say a structured
        // layout was saved, so the operator learns what "Save mappings" actually did (WP-13 renders
        // this message verbatim).
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderA = await SeedIdenticalOrderAsync(db, orgId, supplierId);

        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);

        var result = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderA, CancellationToken.None);

        Assert.NotNull(result);
        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.OutputTree);
        Assert.Equal(OutputFormat.Json, stored.OutputTree!.Format);
        Assert.Equal("root", stored.OutputTree.Root.Name);
    }

    // ── Round-trip: the tree survives the ConfigJson column with structure intact ──────────────

    [Fact]
    public async Task PromotedTree_RoundTripsThroughConfigJson_PreservingNamespacesAndIncludeWhen()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);

        var tree = new OutputNodeTemplate
        {
            Format     = OutputFormat.Xml,
            Namespaces = new Dictionary<string, string> { ["cbc"] = "urn:oasis:names:cbc-2" },
            Root = OutputNode.Obj("Order",
                new OutputNode
                {
                    Name        = "ID",
                    NodeType    = OutputNodeType.Field,
                    Prefix      = "cbc",
                    Namespace   = "urn:oasis:names:cbc-2",
                    IncludeWhen = "order.Currency == \"EUR\"",
                    Rule        = new OutputFieldRule { OutputPath = "ID", CanonicalField = "PoNumber" },
                }),
        };

        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = tree }, CancellationToken.None);

        var reread = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);

        Assert.NotNull(reread?.OutputTree);
        var leaf = reread!.OutputTree!.Root.Children.Single();
        Assert.Equal(OutputFormat.Xml, reread.OutputTree.Format);
        Assert.Equal("urn:oasis:names:cbc-2", reread.OutputTree.Namespaces!["cbc"]);
        Assert.Equal("cbc", leaf.Prefix);
        Assert.Equal("urn:oasis:names:cbc-2", leaf.Namespace);
        Assert.Equal("order.Currency == \"EUR\"", leaf.IncludeWhen);
        Assert.Equal("PoNumber", leaf.Rule!.CanonicalField);
    }

    // ── Precedence ladder ─────────────────────────────────────────────────────
    // R6: assert the DIFFERENCE. Each case must fail if the winning layer changes alone.

    [Fact]
    public async Task PerOrderTree_OutranksPromotedTree()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() }, CancellationToken.None);

        // A DIFFERENT per-order tree — the higher-priority seam.
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            OutputTree = new OutputNodeTemplate
            {
                Format = OutputFormat.Json,
                Root = OutputNode.Obj("root",
                    OutputNode.FieldOf("perOrderRef",
                        new OutputFieldRule { OutputPath = "perOrderRef", CanonicalField = "PoNumber" })),
            },
        }, CancellationToken.None);

        var bytes = await TransformAndCaptureAsync(db, orgId, orderId, OutputFormat.Json);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        Assert.True(doc.RootElement.TryGetProperty("perOrderRef", out _));   // per-order layout
        Assert.False(doc.RootElement.TryGetProperty("orderNumber", out _));  // promoted layout NOT used
    }

    [Fact]
    public async Task PerOrderFlatOutput_OutranksPromotedTree()
    {
        // The per-order seam wins as a WHOLE, not per-mode: a per-order flat output config still
        // outranks a supplier-promoted tree. Without this, promoting a tree would silently take
        // priority over a per-order decision the operator made later.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() }, CancellationToken.None);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PerOrderRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        var csv = Encoding.UTF8.GetString(
            await TransformAndCaptureAsync(db, orgId, orderId, OutputFormat.Csv));

        Assert.StartsWith("PerOrderRef", csv);
        Assert.DoesNotContain("orderNumber", csv);
    }

    [Fact]
    public async Task PromotedTree_OutranksPromotedFlatOutput()
    {
        // Within the supplier-promoted layer the tree is the richer, later concept and wins —
        // mirroring the per-order ladder where OutputTree outranks Output.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            OutputTree = DesignedTree(),
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "FlatRef", CanonicalField = "PoNumber" } },
            },
        }, CancellationToken.None);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(
            await TransformAndCaptureAsync(db, orgId, orderId, OutputFormat.Json)));

        Assert.True(doc.RootElement.TryGetProperty("orderNumber", out _)); // tree layout
        Assert.False(doc.RootElement.TryGetProperty("FlatRef", out _));    // flat layout NOT used
    }

    // ── Safety: never throw, never silently change a supplier with no promoted tree ────────────

    [Fact]
    public async Task NoPromotedTree_IsByteIdenticalToTheFixedTransformer()
    {
        // GOLDEN. The additive property must not alter a single byte for the suppliers that have
        // never used the designer — which is all of them, today.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);

        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.Supplier)
            .FirstAsync(o => o.Id == orderId && o.OrgId == orgId);
        var fixedResult = await new CsvTransformService().TransformAsync(order, OutputFormat.Csv, CancellationToken.None);
        fixedResult.Content.Position = 0;
        using var ms = new MemoryStream();
        await fixedResult.Content.CopyToAsync(ms);

        var actual = await TransformAndCaptureAsync(db, orgId, orderId, OutputFormat.Csv);

        Assert.Equal(ms.ToArray(), actual);
    }

    [Fact]
    public async Task MalformedPromotedTree_FallsBackToTheFixedTransformer_NeverThrows()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);

        // A ConfigJson whose outputTree node is structurally wrong (a string where the tree object
        // belongs). PoMappingService.GetAsync throws JsonException; the transform must survive it.
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            ConfigJson = """{"header":{},"lines":{},"outputTree":"not-a-tree"}""",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.Supplier)
            .FirstAsync(o => o.Id == orderId && o.OrgId == orgId);
        var fixedResult = await new CsvTransformService().TransformAsync(order, OutputFormat.Csv, CancellationToken.None);
        fixedResult.Content.Position = 0;
        using var ms = new MemoryStream();
        await fixedResult.Content.CopyToAsync(ms);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error); // never a throw, never a failed order
        Assert.Equal(ms.ToArray(), captured());      // byte-identical fixed output
    }

    // ── Pinned orders (Connections:RevisionAuthority — ON in production) ───────────────────────
    // A promoted tree that only worked for UNPINNED orders would be inert on production, where the
    // flag is on. The tree rides inside the revision's InputMappingJson (a whole serialized
    // PoMappingConfig), so a pinned order reproduces the layout it was published with.

    private static IEffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool enabled) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = enabled ? "true" : "false",
            })
            .Build());

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) BuildWithAuthority(ProcuLinkDbContext db)
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
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: Resolver(db, enabled: true));

        return (svc, () => captured);
    }

    /// <summary>Publishes a revision whose InputMappingJson snapshot is the given supplier config, exactly as ConnectionBackfillService does (`InputMappingJson = poMapping?.ConfigJson`).</summary>
    private static async Task<Guid> SeedPublishedRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, PoMappingConfig snapshot)
    {
        var now          = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();

        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revisionId, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id           = revisionId,
            ConnectionId = connectionId,
            OrgId        = orgId,
            SupplierId   = supplierId,
            VersionNo    = 1,
            Status       = "published",
            CreatedAt    = now,
            PublishedAt  = now,
            // Byte-identical to what PoMappingService writes into SupplierPoMapping.ConfigJson.
            InputMappingJson = JsonSerializer.Serialize(
                snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        });
        await db.SaveChangesAsync();
        return revisionId;
    }

    [Fact]
    public async Task PinnedOrder_RendersThroughTheTreeSnapshottedOnItsRevision()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);

        var revisionId = await SeedPublishedRevisionAsync(
            db, orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() });

        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);
        var order   = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revisionId;
        await db.SaveChangesAsync();

        var (svc, captured) = BuildWithAuthority(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(captured()!));
        Assert.Equal("PO-WP12-1", doc.RootElement.GetProperty("orderNumber").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task PinnedOrder_IgnoresALiveTreePromotedAfterItWasPinned()
    {
        // Reproducibility (R6 — assert the DIFFERENCE): a pinned order renders under the config it
        // was ingested with. Its revision snapshot carries NO tree, so a tree promoted onto the LIVE
        // supplier config afterwards must not reach it — it falls to the fixed transformer instead.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);

        var revisionId = await SeedPublishedRevisionAsync(
            db, orgId, supplierId, new PoMappingConfig()); // published BEFORE anyone designed a layout

        var orderId = await SeedIdenticalOrderAsync(db, orgId, supplierId);
        var order   = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revisionId;
        await db.SaveChangesAsync();

        // A layout promoted onto the live config AFTER the pin.
        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() }, CancellationToken.None);

        var (svc, captured) = BuildWithAuthority(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(captured()!));

        // The live layout did NOT leak into a pinned order...
        Assert.False(doc.RootElement.TryGetProperty("orderNumber", out _));
        Assert.False(doc.RootElement.TryGetProperty("items", out _));
        // ...and the fixed transformer's own shape is what came out.
        // (Compared by shape, not by bytes: the fixed JSON transform stamps a generatedAt timestamp,
        // so two renders of the same order are never byte-equal to each other.)
        Assert.Equal("PO-WP12-1", doc.RootElement.GetProperty("poNumber").GetString());
    }

    [Fact]
    public async Task PromoteAsync_OrderWithNoTree_DoesNotClearAnExistingPromotedTree()
    {
        // Promoting an ordinary order must not wipe the layout a previous promote saved — the same
        // "an empty value never overwrites a good one" rule the flat Output path already follows.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);

        await new PoMappingService(db).UpsertAsync(
            orgId, supplierId, new PoMappingConfig { OutputTree = DesignedTree() }, CancellationToken.None);

        var plainOrder = await SeedIdenticalOrderAsync(db, orgId, supplierId);
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, plainOrder, new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "cell:r1c1" },
            },
        }, CancellationToken.None);

        await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, plainOrder, CancellationToken.None);

        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.NotNull(stored!.OutputTree); // preserved
        Assert.Equal("root", stored.OutputTree!.Root.Name);
    }
}
