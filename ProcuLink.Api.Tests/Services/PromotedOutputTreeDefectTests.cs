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
/// WP-12 refutation pack — the seven defects an adversarial review reproduced against the first cut
/// of "promote the designed output tree", plus the two invariants it broke outside the transform.
///
/// <para>Each region below pins ONE defect and states the rule it enforces. Every test here failed
/// before the fix; none of them is a re-assertion of the acceptance criterion (which
/// <see cref="SupplierPromotedOutputTreeTransformTests"/> owns). The recurring shape is
/// <b>assert the difference</b>: a promoted tree must win where it should and lose where it should,
/// so a test cannot pass by the consumption code simply not existing.</para>
/// </summary>
public class PromotedOutputTreeDefectTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Every transformer the defects touch — cXML and X12 included, which no WP-12 test used.</summary>
    private static ITransformService[] Transformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
        new CxmlTransformService(),
        new X12TransformService(),
    };

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(
        ProcuLinkDbContext db, bool revisionAuthority = false)
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
            Transformers(),
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            effectiveConfig: revisionAuthority
                ? new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [EffectiveConnectionConfigResolver.FlagKey] = "true",
                    })
                    .Build())
                : null);

        return (svc, () => captured);
    }

    private static async Task<(Guid OrgId, Guid SupplierId)> SeedSupplierAsync(ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Refuter Supplier", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    private static async Task<Guid> SeedOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-1",
            BuyerName  = "Refuter Buyer",
            OrderDate  = new DateOnly(2026, 7, 29),
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
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    /// <summary>
    /// What the visual designer actually produces when the operator picks cXML: nodes they drew PLUS
    /// the connection's sender identity. The emitter refuses cXML/X12, so only the identity is ever
    /// honoured — the nodes are decoration, which is exactly what makes this shape dangerous.
    /// </summary>
    private static OutputNodeTemplate CxmlEnvelopeTree(string fromIdentity) => new()
    {
        Format   = OutputFormat.CXml,
        Root     = CxmlDrawnRoot(),
        Envelope = new EnvelopeConfig
        {
            Cxml = new CxmlEnvelope { FromDomain = "NetworkId", FromIdentity = fromIdentity },
        },
    };

    /// <summary>The nodes an operator drew under a cXML template — never rendered by any emitter.</summary>
    private static OutputNode CxmlDrawnRoot() => OutputNode.Obj("cXML",
        OutputNode.FieldOf("neverRendered",
            new OutputFieldRule { OutputPath = "neverRendered", CanonicalField = "PoNumber" }));

    private static OutputNodeTemplate JsonTree(string fieldName) => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf(fieldName, new OutputFieldRule { OutputPath = fieldName, CanonicalField = "PoNumber" })),
    };

    private static OutputMappingConfig FlatOutput(string columnName) => new()
    {
        Header = { ["po"] = new OutputFieldRule { OutputPath = columnName, CanonicalField = "PoNumber" } },
    };

    private static async Task<byte[]> TransformOkAsync(
        OrderService svc, Func<byte[]?> captured, Guid orgId, Guid orderId, OutputFormat format)
    {
        var result = await svc.TransformAsync(orgId, orderId, format, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error);
        var bytes = captured();
        Assert.NotNull(bytes);
        return bytes!;
    }

    private static async Task<string?> DigestOfLatestArtifactAsync(ProcuLinkDbContext db, Guid orgId, Guid orderId) =>
        (await db.OutboundArtifacts.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.OrderId == orderId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstAsync()).ConfigDigest;

    /// <summary>Publishes a revision whose bundle is exactly what ConnectionBackfillService snapshots.</summary>
    private static async Task<Guid> SeedPublishedRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string? inputMappingJson, string? outputMappingJson = null)
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
            Id                = revisionId,
            ConnectionId      = connectionId,
            OrgId             = orgId,
            SupplierId        = supplierId,
            VersionNo         = 1,
            Status            = "published",
            CreatedAt         = now,
            PublishedAt       = now,
            InputMappingJson  = inputMappingJson,
            OutputMappingJson = outputMappingJson,
        });
        await db.SaveChangesAsync();
        return revisionId;
    }

    private static async Task PinAsync(ProcuLinkDbContext db, Guid orderId, Guid revisionId)
    {
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = revisionId;
        await db.SaveChangesAsync();
    }

    // ══ D1 — a per-order tree must ALWAYS win, in every format ══════════════════════════════════
    // The promoted tree was assigned unconditionally, so a per-order cXML/X12 tree (which by design
    // routes to the FIXED transformer and contributes only its envelope) was silently overwritten.

    [Fact]
    public async Task PerOrderCxmlEnvelopeTree_IsNotOverwrittenByAPromotedTree()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        // The supplier carries a promoted cXML envelope with a DIFFERENT identity.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = CxmlEnvelopeTree("SUPPLIER-IDENTITY") }, CancellationToken.None);

        // This order was configured with its own sender identity — the higher-priority seam.
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId,
            new OrderMappingOverride { OutputTree = CxmlEnvelopeTree("PER-ORDER-IDENTITY") }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var cxml = Encoding.UTF8.GetString(await TransformOkAsync(svc, captured, orgId, orderId, OutputFormat.CXml));

        Assert.Contains("PER-ORDER-IDENTITY", cxml);
        Assert.DoesNotContain("SUPPLIER-IDENTITY", cxml);

        // Assert the DIFFERENCE: the promoted identity is not merely ignored, it is what a sibling
        // order with no per-order tree delivers under. Without this half, deleting the promoted-tree
        // consumption entirely would satisfy the test.
        var sibling = await SeedOrderAsync(db, orgId, supplierId);
        var (svc2, captured2) = Build(db);
        var siblingCxml = Encoding.UTF8.GetString(
            await TransformOkAsync(svc2, captured2, orgId, sibling, OutputFormat.CXml));

        Assert.Contains("SUPPLIER-IDENTITY", siblingCxml);
        Assert.DoesNotContain("PER-ORDER-IDENTITY", siblingCxml);
    }

    [Fact]
    public async Task PerOrderCxmlTree_WithAPromotedJsonTree_StillDeliversCxml()
    {
        // The worst shape: the promoted tree's format flipped the transform onto the emitter, so the
        // artifact row said 'cxml' while the bytes were the promoted JSON document.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = JsonTree("promotedJsonField") }, CancellationToken.None);
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId,
            new OrderMappingOverride { OutputTree = CxmlEnvelopeTree("PER-ORDER-IDENTITY") }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var bytes = await TransformOkAsync(svc, captured, orgId, orderId, OutputFormat.CXml);
        var text  = Encoding.UTF8.GetString(bytes);

        Assert.Contains("<cXML", text);                     // the recorded format and the bytes agree
        Assert.DoesNotContain("promotedJsonField", text);   // the promoted layout did not hijack it
        Assert.Contains("PER-ORDER-IDENTITY", text);

        var artifact = await db.OutboundArtifacts.AsNoTracking()
            .SingleAsync(a => a.OrgId == orgId && a.OrderId == orderId);
        Assert.Equal("cxml", artifact.Format);

        // Assert the DIFFERENCE: the promoted JSON layout IS live for this supplier — it just cannot
        // reach an order that designed its own.
        var sibling = await SeedOrderAsync(db, orgId, supplierId);
        var (svc2, captured2) = Build(db);
        Assert.Contains("promotedJsonField", Encoding.UTF8.GetString(
            await TransformOkAsync(svc2, captured2, orgId, sibling, OutputFormat.Json)));
    }

    // ══ D2 — a fixed-format promoted tree contributes ONLY its envelope ═════════════════════════
    // It must never suppress the flat config at its own layer, and never turn an order the fixed
    // transformer would have handled into transform_failed.

    [Fact]
    public async Task PromotedCxmlTree_ContributesOnlyItsEnvelope_AtTheSupplierLayer()
    {
        // BOTH halves of the rule in one place, because they are one rule: a cXML/X12 tree may not
        // take over a CSV/JSON document, and it may not stop whatever WOULD have produced one.
        await using var db = NewDb();

        // (a) tree ALONE — the order must still deliver the fixed document, not transform_failed.
        var (orgAlone, supplierAlone) = await SeedSupplierAsync(db);
        var orderAlone = await SeedOrderAsync(db, orgAlone, supplierAlone);
        await new PoMappingService(db).UpsertAsync(orgAlone, supplierAlone,
            new PoMappingConfig { OutputTree = CxmlEnvelopeTree("SUPPLIER-IDENTITY") }, CancellationToken.None);

        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines).Include(o => o.Supplier)
            .FirstAsync(o => o.Id == orderAlone && o.OrgId == orgAlone);
        var fixedResult = await new CsvTransformService().TransformAsync(order, OutputFormat.Csv, CancellationToken.None);
        using var expected = new MemoryStream();
        fixedResult.Content.Position = 0;
        await fixedResult.Content.CopyToAsync(expected);

        var (svcAlone, capturedAlone) = Build(db);
        Assert.Equal(expected.ToArray(),
            await TransformOkAsync(svcAlone, capturedAlone, orgAlone, orderAlone, OutputFormat.Csv));

        // (b) tree ALONGSIDE a promoted flat mapping — the flat mapping still drives the document.
        var (orgBoth, supplierBoth) = await SeedSupplierAsync(db);
        var orderBoth = await SeedOrderAsync(db, orgBoth, supplierBoth);
        await new PoMappingService(db).UpsertAsync(orgBoth, supplierBoth, new PoMappingConfig
        {
            OutputTree = CxmlEnvelopeTree("SUPPLIER-IDENTITY"),
            Output     = FlatOutput("PromotedRef"),
        }, CancellationToken.None);

        var (svcBoth, capturedBoth) = Build(db);
        Assert.StartsWith("PromotedRef",
            Encoding.UTF8.GetString(await TransformOkAsync(svcBoth, capturedBoth, orgBoth, orderBoth, OutputFormat.Csv)));

        // (c) assert the DIFFERENCE: "only its envelope" is a real contribution, not a synonym for
        // "nothing". The same supplier delivering cXML must carry the promoted identity.
        var cxmlOrder = await SeedOrderAsync(db, orgBoth, supplierBoth);
        var (svcCxml, capturedCxml) = Build(db);
        Assert.Contains("SUPPLIER-IDENTITY",
            Encoding.UTF8.GetString(await TransformOkAsync(svcCxml, capturedCxml, orgBoth, cxmlOrder, OutputFormat.CXml)));
    }

    [Fact]
    public async Task PinnedRevision_WithBothATreeAndAFlatSnapshot_StillDeliversTheFlatSnapshot()
    {
        // The pinned reader returned as soon as it found a tree, discarding a published flat mapping
        // that had been delivering correctly.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        var revisionId = await SeedPublishedRevisionAsync(db, orgId, supplierId,
            inputMappingJson:  JsonSerializer.Serialize(
                new PoMappingConfig { OutputTree = CxmlEnvelopeTree("SUPPLIER-IDENTITY") }, CamelCase),
            outputMappingJson: JsonSerializer.Serialize(FlatOutput("PublishedRef"), CamelCase));
        await PinAsync(db, orderId, revisionId);

        var (svc, captured) = Build(db, revisionAuthority: true);
        var csv = Encoding.UTF8.GetString(await TransformOkAsync(svc, captured, orgId, orderId, OutputFormat.Csv));

        Assert.StartsWith("PublishedRef", csv);
        Assert.Contains("PO-1", csv);
        Assert.DoesNotContain("PoNumber,OrderDate", csv); // NOT the fixed transformer's document

        // Assert the DIFFERENCE: the discarded half is not the tree — BOTH halves of the pinned bundle
        // are live. Delivering the same pinned order as cXML must carry the snapshotted identity.
        var cxmlOrder = await SeedOrderAsync(db, orgId, supplierId);
        await PinAsync(db, cxmlOrder, revisionId);
        var (svcCxml, capturedCxml) = Build(db, revisionAuthority: true);
        Assert.Contains("SUPPLIER-IDENTITY",
            Encoding.UTF8.GetString(await TransformOkAsync(svcCxml, capturedCxml, orgId, cxmlOrder, OutputFormat.CXml)));
    }

    [Fact]
    public async Task PromotedX12Tree_CarriesTheSenderIdentityIntoTheIsaSegment()
    {
        // The positive half of D2: an envelope-only tree DOES have a job, and it must do it.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            OutputTree = new OutputNodeTemplate
            {
                Format   = OutputFormat.X12,
                Root     = OutputNode.Obj("x12"),
                Envelope = new EnvelopeConfig { X12 = new X12Envelope { IsaSenderId = "SUPX12ID" } },
            },
        }, CancellationToken.None);

        var (svc, captured) = Build(db);
        var x12 = Encoding.UTF8.GetString(await TransformOkAsync(svc, captured, orgId, orderId, OutputFormat.X12));

        Assert.StartsWith("ISA", x12);
        Assert.Contains("SUPX12ID", x12);
    }

    // ══ D3 — a null Root / null Children must never throw ═══════════════════════════════════════
    // System.Text.Json assigns null over an `init` default, so "root": null and "children": null are
    // both reachable from the [FromBody] PUT that writes SupplierPoMapping.ConfigJson verbatim.

    private const string NullRootConfigJson =
        """{"header":{},"lines":{},"outputTree":{"format":"json","root":null}}""";

    private const string NullChildrenConfigJson =
        """{"header":{},"lines":{},"outputTree":{"format":"json","root":{"name":"root","nodeType":"object","children":null}}}""";

    [Theory]
    [InlineData(NullRootConfigJson)]
    [InlineData(NullChildrenConfigJson)]
    public async Task PinnedSnapshotWithANullTreeNode_DoesNotEscapeAsAnUnhandledException(string snapshot)
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        var revisionId = await SeedPublishedRevisionAsync(db, orgId, supplierId, inputMappingJson: snapshot);
        await PinAsync(db, orderId, revisionId);

        var (svc, captured) = Build(db, revisionAuthority: true);

        // The guarantee is "never throws": a poisoned snapshot falls back to the fixed transformer.
        // Before the fix the NullReferenceException escaped TransformAsync entirely — no
        // transform_failed row, no exception row, and Hangfire retrying forever.
        var bytes = await TransformOkAsync(svc, captured, orgId, orderId, OutputFormat.Csv);
        Assert.StartsWith("PoNumber,OrderDate", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData(NullRootConfigJson)]
    [InlineData(NullChildrenConfigJson)]
    public async Task LiveSupplierConfigWithANullTreeNode_StillAppliesThePromotedFlatOutput(string snapshot)
    {
        // Assert the DIFFERENCE: swallowing the NullReferenceException "worked" but silently threw
        // away the supplier's usable flat output mapping with it.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        var withFlat = snapshot.Replace(
            """{"header":{},"lines":{},""",
            $$"""{"header":{},"lines":{},"output":{{JsonSerializer.Serialize(FlatOutput("PromotedRef"), CamelCase)}},""");

        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            ConfigJson = withFlat,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (svc, captured) = Build(db);
        var csv = Encoding.UTF8.GetString(await TransformOkAsync(svc, captured, orgId, orderId, OutputFormat.Csv));

        Assert.StartsWith("PromotedRef", csv);
    }

    [Fact]
    public async Task Promote_WithANullTreeRoot_DoesNotThrow()
    {
        // The identical unguarded dereference lived in PromoteMappingService, where it 500s the
        // "Save mappings for this supplier" endpoint instead of failing a background job.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.CanonicalJson = JsonDocument.Parse(
            "{\"" + OrderMappingOverrideReader.CanonicalKey +
            "\":{\"outputTree\":{\"format\":\"json\",\"root\":null}}}");
        await db.SaveChangesAsync();

        var result = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.NothingToPromote); // a tree that cannot emit anything is not a promotion
    }

    // ══ D4 — the provenance digest must describe the config that produced the bytes ═════════════

    [Fact]
    public async Task RevisionTreeDigest_ChangesWhenTheTreeChanges()
    {
        // The revision-tree path digested "revision:{id}:{OutputMappingJson}" — but the TREE comes
        // from InputMappingJson, and a tree-only revision has a null OutputMappingJson, so the stored
        // digest was SHA256("revision:{id}:") for every possible layout.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);

        var revisionId = await SeedPublishedRevisionAsync(db, orgId, supplierId,
            inputMappingJson: JsonSerializer.Serialize(new PoMappingConfig { OutputTree = JsonTree("firstLayout") }, CamelCase));

        var orderA = await SeedOrderAsync(db, orgId, supplierId);
        await PinAsync(db, orderA, revisionId);
        var (svcA, capturedA) = Build(db, revisionAuthority: true);
        var bytesA  = await TransformOkAsync(svcA, capturedA, orgId, orderA, OutputFormat.Json);
        var digestA = await DigestOfLatestArtifactAsync(db, orgId, orderA);

        // The SAME revision id, re-published with a different layout.
        var revision = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revisionId);
        revision.InputMappingJson = JsonSerializer.Serialize(
            new PoMappingConfig { OutputTree = JsonTree("secondLayout") }, CamelCase);
        await db.SaveChangesAsync();

        var orderB = await SeedOrderAsync(db, orgId, supplierId);
        await PinAsync(db, orderB, revisionId);
        var (svcB, capturedB) = Build(db, revisionAuthority: true);
        var bytesB  = await TransformOkAsync(svcB, capturedB, orgId, orderB, OutputFormat.Json);
        var digestB = await DigestOfLatestArtifactAsync(db, orgId, orderB);

        Assert.NotEqual(Encoding.UTF8.GetString(bytesA), Encoding.UTF8.GetString(bytesB)); // the bytes really differ
        Assert.NotNull(digestA);
        Assert.NotEqual(digestA, digestB);                                                 // …so the digests must too
    }

    [Fact]
    public async Task SupplierDigest_IsUnchangedByATreeThatDidNotDriveTheBytes()
    {
        // Byte-identical artifacts must not carry different digests. A cXML tree with no envelope
        // contributes literally nothing to a CSV document, yet it replaced the digest.
        await using var db = NewDb();

        var (orgFlat, supplierFlat) = await SeedSupplierAsync(db);
        var orderFlat = await SeedOrderAsync(db, orgFlat, supplierFlat);
        await new PoMappingService(db).UpsertAsync(orgFlat, supplierFlat,
            new PoMappingConfig { Output = FlatOutput("PromotedRef") }, CancellationToken.None);

        var (svc1, captured1) = Build(db);
        var bytesFlat  = await TransformOkAsync(svc1, captured1, orgFlat, orderFlat, OutputFormat.Csv);
        var digestFlat = await DigestOfLatestArtifactAsync(db, orgFlat, orderFlat);

        var (orgBoth, supplierBoth) = await SeedSupplierAsync(db);
        var orderBoth = await SeedOrderAsync(db, orgBoth, supplierBoth);
        await new PoMappingService(db).UpsertAsync(orgBoth, supplierBoth, new PoMappingConfig
        {
            Output     = FlatOutput("PromotedRef"),
            // A cXML tree with no envelope: it contributes literally nothing to a CSV document.
            OutputTree = new OutputNodeTemplate { Format = OutputFormat.CXml, Root = CxmlDrawnRoot() },
        }, CancellationToken.None);

        var (svc2, captured2) = Build(db);
        var bytesBoth  = await TransformOkAsync(svc2, captured2, orgBoth, orderBoth, OutputFormat.Csv);
        var digestBoth = await DigestOfLatestArtifactAsync(db, orgBoth, orderBoth);

        Assert.Equal(bytesFlat, bytesBoth);   // identical bytes…
        Assert.NotNull(digestFlat);
        Assert.Equal(digestFlat, digestBoth); // …identical provenance
    }

    [Fact]
    public async Task SupplierDigest_ChangesWhenAPromotedEnvelopeChangesTheBytes()
    {
        // The other half of the same rule: an envelope-only tree DOES change a cXML document, so it
        // must be visible in the digest.
        await using var db = NewDb();

        var (orgA, supplierA) = await SeedSupplierAsync(db);
        var orderA = await SeedOrderAsync(db, orgA, supplierA);
        await new PoMappingService(db).UpsertAsync(orgA, supplierA, new PoMappingConfig
        {
            Output     = FlatOutput("PromotedRef"),
            OutputTree = CxmlEnvelopeTree("IDENTITY-A"),
        }, CancellationToken.None);
        var (svcA, capturedA) = Build(db);
        var bytesA  = await TransformOkAsync(svcA, capturedA, orgA, orderA, OutputFormat.CXml);
        var digestA = await DigestOfLatestArtifactAsync(db, orgA, orderA);

        var (orgB, supplierB) = await SeedSupplierAsync(db);
        var orderB = await SeedOrderAsync(db, orgB, supplierB);
        await new PoMappingService(db).UpsertAsync(orgB, supplierB, new PoMappingConfig
        {
            Output     = FlatOutput("PromotedRef"),
            OutputTree = CxmlEnvelopeTree("IDENTITY-B"),
        }, CancellationToken.None);
        var (svcB, capturedB) = Build(db);
        var bytesB  = await TransformOkAsync(svcB, capturedB, orgB, orderB, OutputFormat.CXml);
        var digestB = await DigestOfLatestArtifactAsync(db, orgB, orderB);

        Assert.Contains("IDENTITY-A", Encoding.UTF8.GetString(bytesA));
        Assert.Contains("IDENTITY-B", Encoding.UTF8.GetString(bytesB));
        Assert.NotNull(digestA);
        Assert.NotEqual(digestA, digestB);
    }

    // ══ D5 — promote must report what it actually saved ════════════════════════════════════════

    [Fact]
    public async Task Promote_CxmlTree_ReportsTheIdentityItSaved_NotAFileLayout()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId,
            new OrderMappingOverride { OutputTree = CxmlEnvelopeTree("SUPPLIER-IDENTITY") }, CancellationToken.None);

        var result = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.OutputTreePromoted);
        Assert.DoesNotContain("file layout", result.Message);
        Assert.Contains("sender and receiver identity", result.Message);

        // Assert the DIFFERENCE: the honest cXML wording must not blur the honest JSON wording — a
        // JSON/XML/CSV tree really IS the file layout, and still says so.
        var jsonOrder = await SeedOrderAsync(db, orgId, supplierId);
        await new OrderMappingOverrideService(db).UpsertAsync(orgId, jsonOrder,
            new OrderMappingOverride { OutputTree = JsonTree("orderNumber") }, CancellationToken.None);

        var jsonResult = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, jsonOrder, CancellationToken.None);

        Assert.NotNull(jsonResult);
        Assert.True(jsonResult!.OutputTreePromoted);
        Assert.Contains("file layout", jsonResult.Message);
    }

    [Fact]
    public async Task Promote_CxmlTreeWithNoIdentity_ReportsNothingToSave()
    {
        // A cXML/X12 node tree is never rendered — only its envelope is honoured. Without one there
        // is nothing to save, and claiming otherwise is the "offer ⇔ works" violation.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        var orderId = await SeedOrderAsync(db, orgId, supplierId);

        await new OrderMappingOverrideService(db).UpsertAsync(orgId, orderId, new OrderMappingOverride
        {
            OutputTree = new OutputNodeTemplate
            {
                Format = OutputFormat.CXml,
                Root   = OutputNode.Obj("cXML",
                    OutputNode.FieldOf("neverRendered",
                        new OutputFieldRule { OutputPath = "neverRendered", CanonicalField = "PoNumber" })),
            },
        }, CancellationToken.None);

        var result = await new PromoteMappingService(db, new PoMappingService(db))
            .PromoteAsync(orgId, orderId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.OutputTreePromoted);
        Assert.True(result.NothingToPromote);
    }

}
