using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
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
/// WP-12 — the visual output designer's tree (<see cref="OrderMappingOverride.OutputTree"/>) must
/// SURVIVE the order it was designed on. Before this packet the tree lived only in
/// <c>purchase_orders.canonical_json</c>: an operator could design a supplier's exact document,
/// deliver it, and the NEXT order from the same supplier silently reverted to the fixed transformer.
///
/// Acceptance criteria pinned here:
/// <list type="letter">
///   <item><b>a</b> — design a tree on order A, promote it, upload an identical order B → B renders
///        BYTE-IDENTICALLY with zero designer interaction.</item>
///   <item><b>b</b> — a MALFORMED promoted tree falls back to the fixed transformer and logs a
///        warning; it never throws and never fails the order.</item>
///   <item><b>c</b> — the per-order override still outranks the promoted tree.</item>
///   <item><b>d</b> — an empty or absent promoted tree changes nothing (byte-for-byte identical to
///        the fixed transformer).</item>
/// </list>
///
/// <see cref="OutputPrecedenceLadder_HighestConfiguredRungWins"/> is the table-driven guard for the
/// FULL precedence ladder — the risk in this packet is inserting a rung in the wrong place.
/// </summary>
public class PromotedOutputTreeTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Captures log entries so the "falls back QUIETLY but not SILENTLY" contract is assertable.</summary>
    private sealed class CapturingLogger : ILogger<OrderService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static IEffectiveConnectionConfigResolver Resolver(ProcuLinkDbContext db, bool enabled) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = enabled ? "true" : "false",
            })
            .Build());

    /// <summary>
    /// OrderService wired with the REAL <see cref="PoMappingService"/> (so the supplier-promoted read
    /// path is exercised end-to-end), the real CSV/JSON/XML transformers, and a byte-capturing storage
    /// mock. Returns the captured artifact bytes plus the captured log entries.
    /// </summary>
    private static (OrderService Svc, Func<byte[]?> Bytes, CapturingLogger Log) Build(
        ProcuLinkDbContext db, IEffectiveConnectionConfigResolver? effectiveConfig = null)
    {
        byte[]? captured = null;
        var logger = new CapturingLogger();

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
            logger,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: effectiveConfig);

        return (svc, () => captured, logger);
    }

    /// <summary>Seeds one supplier + one resolved, transform-ready order with two lines.</summary>
    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string poNumber, bool seedSupplier)
    {
        if (seedSupplier)
            db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Tree Supplier", CreatedAt = DateTime.UtcNow });

        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = poNumber,
            BuyerName  = "WP12 Buyer",
            OrderDate  = new DateOnly(2026, 7, 1),
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

    /// <summary>The fixed transformer's exact bytes for an order — the byte-identical golden.</summary>
    private static async Task<byte[]> FixedTransformBytesAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, OutputFormat format)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.Supplier)
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

    /// <summary>
    /// The supplier's exact required document, designed visually: a renamed root key, a nested
    /// address object, and a repeating "items" array — structure that is IMPOSSIBLE to express with
    /// the flat header/lines rule map, which is exactly why losing it on the next order matters.
    /// </summary>
    private static OutputNodeTemplate DesignedTree() => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("supplierOrderRef",
                new OutputFieldRule { OutputPath = "supplierOrderRef", CanonicalField = "PoNumber" }),
            OutputNode.Obj("party",
                OutputNode.FieldOf("buyer",
                    new OutputFieldRule { OutputPath = "buyer", CanonicalField = "BuyerName" })),
            OutputNode.Arr("items",
                OutputNode.Obj("item",
                    OutputNode.FieldOf("sku",
                        new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" }),
                    OutputNode.FieldOf("qty",
                        new OutputFieldRule { OutputPath = "qty", CanonicalField = "Quantity" })))),
    };

    // ── serialisation round-trip: the AST must survive the ConfigJson column intact ───────────

    /// <summary>
    /// The promoted tree rides in the SAME <c>SupplierPoMapping.ConfigJson</c> column as the flat
    /// mapping, serialised with <c>PoMappingService</c>'s options. Namespaces and IncludeWhen are the
    /// two parts a "copy the leaf rules across" implementation silently drops — a UBL document would
    /// lose its <c>cbc:</c> binding and a zero-quantity line would reappear, both of which the
    /// supplier rejects while the UI still shows a saved mapping.
    /// </summary>
    [Fact]
    public void PoMappingConfig_OutputTree_RoundTripsNamespacesAndIncludeWhenConditionals()
    {
        // The exact options PoMappingService persists ConfigJson with.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        var config = new PoMappingConfig
        {
            OutputTree = new OutputNodeTemplate
            {
                Format     = OutputFormat.Xml,
                Namespaces = new Dictionary<string, string>
                {
                    ["cbc"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
                    ["cac"] = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
                },
                Root = new OutputNode
                {
                    Name        = "Order",
                    NodeType    = OutputNodeType.Object,
                    Namespace   = "urn:oasis:names:specification:ubl:schema:xsd:Order-2",
                    IncludeWhen = "order.Currency == \"EUR\"",
                    Children =
                    {
                        new OutputNode
                        {
                            Name      = "ID",
                            NodeType  = OutputNodeType.Field,
                            Prefix    = "cbc",
                            Namespace = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
                            Rule      = new OutputFieldRule { OutputPath = "ID", CanonicalField = "PoNumber" },
                        },
                        new OutputNode
                        {
                            Name       = "OrderLine",
                            NodeType   = OutputNodeType.Array,
                            Collection = "lines",
                            Children =
                            {
                                new OutputNode
                                {
                                    Name        = "Line",
                                    NodeType    = OutputNodeType.Object,
                                    IncludeWhen = "line.Quantity > 0",
                                    Children =
                                    {
                                        new OutputNode
                                        {
                                            Name     = "qty",
                                            NodeType = OutputNodeType.Attribute,
                                            Rule     = new OutputFieldRule { OutputPath = "qty", CanonicalField = "Quantity" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var round = JsonSerializer.Deserialize<PoMappingConfig>(JsonSerializer.Serialize(config, options), options);

        var tree = round!.OutputTree;
        Assert.NotNull(tree);
        Assert.Equal(OutputFormat.Xml, tree!.Format);

        // Namespaces — the root declaration map AND the per-node prefix/uri binding.
        Assert.Equal(2, tree.Namespaces!.Count);
        Assert.Equal("urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2", tree.Namespaces["cac"]);
        Assert.Equal("urn:oasis:names:specification:ubl:schema:xsd:Order-2", tree.Root.Namespace);
        var id = tree.Root.Children.Single(c => c.Name == "ID");
        Assert.Equal("cbc", id.Prefix);
        Assert.Equal("urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2", id.Namespace);

        // IncludeWhen conditionals — at header scope AND inside the repeating array item template.
        Assert.Equal("order.Currency == \"EUR\"", tree.Root.IncludeWhen);
        var lineTemplate = tree.Root.Children.Single(c => c.Name == "OrderLine").Children.Single();
        Assert.Equal("line.Quantity > 0", lineTemplate.IncludeWhen);

        // Node kinds and leaf rules survive too (an attribute must not decay into an element).
        Assert.Equal(OutputNodeType.Array, tree.Root.Children.Single(c => c.Name == "OrderLine").NodeType);
        Assert.Equal(OutputNodeType.Attribute, lineTemplate.Children.Single().NodeType);
        Assert.Equal("Quantity", lineTemplate.Children.Single().Rule!.CanonicalField);
    }

    // ── promotion must TELL the operator the design was saved ─────────────────────────────────

    [Fact]
    public async Task Promote_TreeOnlyOverride_ReportsTheTreeAndIsNotNothingToPromote()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-REPORT", seedSupplier: true);

        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderId, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);

        var result = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.OutputTreePromoted);
        // A tree is a whole document, not countable field rules — so the counts stay zero, and
        // NothingToPromote MUST NOT be derived from them alone or this reads as an empty success.
        Assert.Equal(0, result.TotalFieldsPromoted);
        Assert.False(result.NothingToPromote);
        Assert.Contains("output document design", result.Message);
    }

    [Fact]
    public async Task Promote_EmptyTree_IsStillNothingToPromote_AndNeverWipesTheSupplierDesign()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderA     = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-KEEP-A", seedSupplier: true);
        var orderB     = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-KEEP-B", seedSupplier: false);

        var promote = new PromoteMappingService(db, new PoMappingService(db));

        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);
        Assert.True((await promote.PromoteAsync(orgId, orderA, CancellationToken.None))!.OutputTreePromoted);

        // Order B carries an EMPTY tree (a half-open designer, a cleared canvas). Promoting it must
        // report nothing to save and must NOT erase the supplier's existing design.
        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderB, new OrderMappingOverride { OutputTree = new OutputNodeTemplate() }, CancellationToken.None);

        var result = await promote.PromoteAsync(orgId, orderB, CancellationToken.None);
        Assert.False(result!.OutputTreePromoted);
        Assert.True(result.NothingToPromote);

        var stored = await new PoMappingService(db).GetAsync(orgId, supplierId, CancellationToken.None);
        Assert.NotNull(stored!.OutputTree);
        Assert.Equal("supplierOrderRef", stored.OutputTree!.Root.Children[0].Name);
    }

    // ── (a) BYTE PARITY — the whole point of the packet ───────────────────────────────────────

    [Fact]
    public async Task PromotedTree_OrderB_RendersByteIdenticallyToOrderA_WithNoDesignerInteraction()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var orderA = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12", seedSupplier: true);
        var orderB = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12", seedSupplier: false);

        // Order A: the operator designs the supplier's exact document.
        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);

        var (svcA, bytesA, _) = Build(db);
        var resultA = await svcA.TransformAsync(orgId, orderA, OutputFormat.Json, CancellationToken.None);
        Assert.True(resultA.IsSuccess, resultA.Error);
        var designedBytes = bytesA()!;

        // The operator presses "Save mappings for this supplier".
        var promote = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderA, CancellationToken.None);
        Assert.NotNull(promote);
        Assert.False(promote!.NothingToPromote); // the tree IS promotable work — never a silent no-op

        // Order B: an identical upload with ZERO designer interaction.
        var (svcB, bytesB, _) = Build(db);
        var resultB = await svcB.TransformAsync(orgId, orderB, OutputFormat.Json, CancellationToken.None);
        Assert.True(resultB.IsSuccess, resultB.Error);

        Assert.Equal(designedBytes, bytesB()); // BYTE-IDENTICAL — the design survived the order
    }

    // ── (b) a malformed promoted tree falls back to the fixed transformer, loudly-in-logs ─────

    [Fact]
    public async Task PromotedTree_Malformed_FallsBackToFixedTransformer_AndWarns_NeverThrows()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-BAD", seedSupplier: true);
        // CSV is the timestamp-free fixed transform, so "byte-identical" is a real assertion here.
        var expected   = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        // A structurally BROKEN promoted tree (root is a number, not a node) stored straight into
        // the supplier's ConfigJson — the shape a partial write / hand-edit / future schema drift
        // produces. It must never take the supplier's orders down.
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            ConfigJson = """{"header":{},"lines":{},"outputTree":{"format":"json","root":12345}}""",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (svc, bytes, log) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);      // never a throw, never a failed order
        Assert.Equal(expected, bytes());                  // byte-identical fixed-transformer output
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("supplier", StringComparison.OrdinalIgnoreCase));
    }

    // ── (c) the per-order override still wins over the promoted tree ──────────────────────────

    [Fact]
    public async Task PromotedTree_PerOrderOverrideStillWins()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var orderA = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-A", seedSupplier: true);
        var orderB = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-B", seedSupplier: false);

        await new OrderMappingOverrideService(db).UpsertAsync(
            orgId, orderA, new OrderMappingOverride { OutputTree = DesignedTree() }, CancellationToken.None);
        await new PromoteMappingService(db, new PoMappingService(db)).PromoteAsync(orgId, orderA, CancellationToken.None);

        // Order B carries its OWN per-order output override — it must outrank the promoted tree.
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderB, new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["w"] = new OutputFieldRule { OutputPath = "perOrderWins", FixedValue = "yes" } },
            },
        }, CancellationToken.None);

        var (svc, bytes, _) = Build(db);
        var result = await svc.TransformAsync(orgId, orderB, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var json = Encoding.UTF8.GetString(bytes()!);
        Assert.Contains("perOrderWins", json);
        Assert.DoesNotContain("supplierOrderRef", json); // the promoted tree did NOT drive the output
    }

    // ── (d) an empty / absent promoted tree changes nothing ───────────────────────────────────

    [Theory]
    [InlineData("""{"header":{},"lines":{}}""")]                                    // absent
    [InlineData("""{"header":{},"lines":{},"outputTree":null}""")]                  // explicit null
    [InlineData("""{"header":{},"lines":{},"outputTree":{"format":"json"}}""")]     // empty root
    public async Task PromotedTree_AbsentOrEmpty_IsByteIdenticalToFixedTransformer(string configJson)
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = await SeedOrderAsync(db, orgId, supplierId, "PO-WP12-EMPTY", seedSupplier: true);
        // CSV is the timestamp-free fixed transform, so "byte-identical" is a real assertion here.
        var expected   = await FixedTransformBytesAsync(db, orgId, orderId, OutputFormat.Csv);

        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            ConfigJson = configJson,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (svc, bytes, _) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(expected, bytes());
    }

    // ── THE PRECEDENCE LADDER (table-driven) ──────────────────────────────────────────────────

    /// <summary>The output-mode rungs, HIGHEST precedence first. The value is the marker each emits.</summary>
    public enum Rung
    {
        PerOrderTree,
        PerOrderTemplate,
        PerOrderFlat,
        PinnedRevision,
        SupplierPromotedTree,
        SupplierPromotedFlat,
        Fixed,
    }

    private static readonly string[] AllMarkers =
    {
        "perOrderTree", "perOrderTemplate", "perOrderFlat",
        "pinnedRevision", "supplierPromotedTree", "supplierPromotedFlat",
    };

    /// <summary>
    /// Pins the FULL precedence ladder in one place, because inserting the supplier-promoted TREE
    /// rung is the whole risk of WP-12: every rung below the configured one is ALSO configured, so a
    /// mis-ordered branch shows up as the wrong marker rather than as a silently-absent feature.
    /// </summary>
    [Theory]
    [InlineData(Rung.PerOrderTree)]
    [InlineData(Rung.PerOrderTemplate)]
    [InlineData(Rung.PerOrderFlat)]
    [InlineData(Rung.PinnedRevision)]
    [InlineData(Rung.SupplierPromotedTree)]
    [InlineData(Rung.SupplierPromotedFlat)]
    [InlineData(Rung.Fixed)]
    public async Task OutputPrecedenceLadder_HighestConfiguredRungWins(Rung highest)
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = await SeedOrderAsync(db, orgId, supplierId, "PO-LADDER", seedSupplier: true);

        // Configure the named rung AND every rung below it, so precedence — not presence — is tested.
        var @override = new OrderMappingOverride();
        if (highest <= Rung.PerOrderTree)
            @override = @override with { OutputTree = MarkerTree("perOrderTree") };
        if (highest <= Rung.PerOrderTemplate)
            @override = @override with { OutputTemplate = """{"winner":"perOrderTemplate"}""" };
        if (highest <= Rung.PerOrderFlat)
            @override = @override with { Output = MarkerOutput("perOrderFlat") };

        if (@override.OutputTree is not null || @override.OutputTemplate is not null || @override.Output is not null)
            await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, @override, CancellationToken.None);

        // The supplier-promoted rungs live on the supplier's reusable config...
        var supplierConfigJson = highest <= Rung.SupplierPromotedTree
            ? SupplierConfigJson("supplierPromotedFlat", "supplierPromotedTree")
            : highest <= Rung.SupplierPromotedFlat
                ? SupplierConfigJson("supplierPromotedFlat", treeMarker: null)
                : null;

        if (supplierConfigJson is not null)
        {
            db.SupplierPoMappings.Add(new SupplierPoMapping
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                ConfigJson = supplierConfigJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }

        // ...and the pinned-revision rung on a published revision the order pins to.
        IEffectiveConnectionConfigResolver? resolver = null;
        if (highest <= Rung.PinnedRevision)
        {
            var connectionId = Guid.NewGuid();
            var revisionId   = Guid.NewGuid();
            db.SupplierConnections.Add(new SupplierConnection
            {
                Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Ladder",
                ActiveRevisionId = null, CreatedBy = "test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
            {
                Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
                VersionNo = 1, Status = "published", EffectiveFrom = DateTime.UtcNow,
                PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, CreatedBy = "test",
                OutputMappingJson = MarkerOutputJson("pinnedRevision"),
            });
            var pinned = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
            pinned.ConnectionRevisionId = revisionId;
            resolver = Resolver(db, enabled: true);
        }

        await db.SaveChangesAsync();

        var (svc, bytes, _) = Build(db, resolver);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var text = Encoding.UTF8.GetString(bytes()!);

        var expectedMarker = highest == Rung.Fixed ? null : ToMarker(highest);
        foreach (var marker in AllMarkers)
        {
            if (marker == expectedMarker)
                Assert.Contains(marker, text);
            else
                Assert.DoesNotContain(marker, text);
        }
    }

    private static string ToMarker(Rung rung) => rung switch
    {
        Rung.PerOrderTree         => "perOrderTree",
        Rung.PerOrderTemplate     => "perOrderTemplate",
        Rung.PerOrderFlat         => "perOrderFlat",
        Rung.PinnedRevision       => "pinnedRevision",
        Rung.SupplierPromotedTree => "supplierPromotedTree",
        Rung.SupplierPromotedFlat => "supplierPromotedFlat",
        _                         => throw new ArgumentOutOfRangeException(nameof(rung)),
    };

    private static OutputNodeTemplate MarkerTree(string marker) => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("winner", new OutputFieldRule { OutputPath = "winner", FixedValue = marker })),
    };

    private static OutputMappingConfig MarkerOutput(string marker) => new()
    {
        Header = { ["w"] = new OutputFieldRule { OutputPath = "winner", FixedValue = marker } },
    };

    private static string MarkerOutputJson(string marker) =>
        "{\"header\":{\"w\":{\"outputPath\":\"winner\",\"fixedValue\":\"" + marker + "\"}},\"lines\":{}}";

    private static string MarkerTreeJson(string marker) =>
        "{\"format\":\"json\",\"root\":{\"name\":\"root\",\"nodeType\":\"object\",\"children\":["
        + "{\"name\":\"winner\",\"nodeType\":\"field\",\"rule\":{\"outputPath\":\"winner\",\"fixedValue\":\""
        + marker + "\"}}]}}";

    private static string SupplierConfigJson(string? flatMarker, string? treeMarker)
    {
        var parts = new List<string> { "\"header\":{}", "\"lines\":{}" };
        if (flatMarker is not null) parts.Add("\"output\":" + MarkerOutputJson(flatMarker));
        if (treeMarker is not null) parts.Add("\"outputTree\":" + MarkerTreeJson(treeMarker));
        return "{" + string.Join(",", parts) + "}";
    }

    // ── revision snapshot: the tree must ride into the connection bundle ──────────────────────

    [Fact]
    public void TryExtractPromotedOutputJson_PrefersThePromotedTreeOverTheFlatOutput()
    {
        // A supplier config that carries BOTH a flat promoted output and a promoted tree: the
        // revision bundle must snapshot the TREE (the higher-precedence design), otherwise a pinned
        // order reproduces the wrong document forever.
        var configJson = SupplierConfigJson("supplierPromotedFlat", "supplierPromotedTree");

        var snapshot = ConnectionBackfillService.TryExtractPromotedOutputJson(configJson);

        Assert.NotNull(snapshot);
        Assert.Contains("supplierPromotedTree", snapshot);
        using var doc = JsonDocument.Parse(snapshot!);
        Assert.True(doc.RootElement.TryGetProperty("root", out _)); // the tree shape, not header/lines
    }

    [Fact]
    public void TryExtractPromotedOutputJson_MalformedTree_ReturnsNull_NeverThrows()
    {
        var snapshot = ConnectionBackfillService.TryExtractPromotedOutputJson(
            """{"header":{},"lines":{},"outputTree":{"format":"json","root":12345}}""");

        Assert.Null(snapshot); // the fixed transformer stays in control
    }

    [Fact]
    public async Task PinnedRevision_TreeSnapshot_ReproducesTheDesignedDocument()
    {
        // A pinned order whose revision snapshotted a TREE must render from that tree — the
        // reproducibility promise the revision bundle exists for.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = await SeedOrderAsync(db, orgId, supplierId, "PO-PINNED-TREE", seedSupplier: true);

        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Pinned",
            ActiveRevisionId = null, CreatedBy = "test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", EffectiveFrom = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, CreatedBy = "test",
            OutputMappingJson = MarkerTreeJson("pinnedTreeSnapshot"),
        });
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revisionId;
        await db.SaveChangesAsync();

        var (svc, bytes, _) = Build(db, Resolver(db, enabled: true));
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.Json, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes()!));
        Assert.Equal("pinnedTreeSnapshot", doc.RootElement.GetProperty("winner").GetString());
    }
}
