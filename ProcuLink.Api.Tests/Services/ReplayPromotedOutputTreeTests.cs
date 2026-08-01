using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WP-12 defect D7 — REPLAY must reproduce a tree-rendered order.
///
/// <para><c>OrderTransformService</c> claims, in a comment on its revision deserializer, that "the
/// live transform and the replay engine read the SAME snapshot identically". Promoting the output
/// tree broke that: the transform learned to read <c>InputMappingJson</c>'s
/// <c>PoMappingConfig.OutputTree</c>, while replay kept reading only <c>OutputMappingJson</c>. A
/// connection whose entire output is a designed layout therefore replayed as the FIXED document on
/// both sides — the diff screen shows "nothing changes" for a revision that changes everything.</para>
/// </summary>
public class ReplayPromotedOutputTreeTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IEnumerable<ITransformService> Transformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
        new XmlTransformService(),
    };

    private static OutputNodeTemplate JsonTree(string fieldName) => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf(fieldName, new OutputFieldRule { OutputPath = fieldName, CanonicalField = "PoNumber" })),
    };

    private static async Task<(Guid OrgId, Guid SupplierId, Guid ConnectionId, Guid RevisionId)>
        SeedAsync(ProcuLinkDbContext db, PoMappingConfig revisionBundle, string status = "draft")
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Replay Supplier", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Replay Supplier",
            ActiveRevisionId = status == "published" ? revisionId : null, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = status, CreatedAt = now,
            PublishedAt = status == "published" ? now : null,
            // Byte-identical to what ConnectionBackfillService snapshots (`= poMapping?.ConfigJson`).
            InputMappingJson = JsonSerializer.Serialize(revisionBundle, CamelCase),
            OutputFormat = "json",
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, connectionId, revisionId);
    }

    private static async Task<Guid> SeedOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-RPLY",
            Currency = "EUR", OrderDate = new DateOnly(2026, 7, 29), Status = "ready",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "BUY-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 2m, Unit = "EA", UnitPrice = 5m, NeedsReview = false,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private static IEffectiveConnectionConfigResolver Authority(ProcuLinkDbContext db) =>
        new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = "true",
            })
            .Build());

    [Fact]
    public async Task Replay_DraftSide_RendersThroughTheRevisionsOutputTree()
    {
        await using var db = NewDb();
        var (orgId, supplierId, connId, revId) =
            await SeedAsync(db, new PoMappingConfig { OutputTree = JsonTree("draftLayout") });
        await SeedOrderAsync(db, orgId, supplierId);

        var result = await new ReplayService(db, Transformers())
            .ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        Assert.NotNull(diff.DraftOutput);
        Assert.Contains("draftLayout", diff.DraftOutput!); // what publishing this revision would deliver
        Assert.True(diff.OutputChanged);                   // …and it differs from today's fixed document
    }

    [Fact]
    public async Task Replay_CurrentSide_UsesTheSupplierPromotedTree()
    {
        // The CURRENT side must mirror what delivery produces TODAY. A live promoted tree drives the
        // live transform, so replay showing the fixed document would invent a diff that is not real.
        await using var db = NewDb();
        var (orgId, supplierId, connId, revId) =
            await SeedAsync(db, new PoMappingConfig { OutputTree = JsonTree("draftLayout") });
        await SeedOrderAsync(db, orgId, supplierId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = JsonTree("liveLayout") }, CancellationToken.None);

        var result = await new ReplayService(db, Transformers())
            .ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        Assert.NotNull(diff.CurrentOutput);
        Assert.Contains("liveLayout", diff.CurrentOutput!);
        Assert.Contains("draftLayout", diff.DraftOutput!);
        Assert.True(diff.OutputChanged);
    }

    [Fact]
    public async Task Replay_CurrentSide_PinnedOrder_UsesItsOwnRevisionsTree_NotTheLiveOne()
    {
        // Assert the DIFFERENCE: a pinned order's current side comes from ITS revision snapshot, and
        // a layout promoted onto the live supplier config afterwards must not leak into it.
        await using var db = NewDb();
        var (orgId, supplierId, connId, revId) =
            await SeedAsync(db, new PoMappingConfig { OutputTree = JsonTree("draftLayout") });

        // A DIFFERENT published revision the order is pinned to.
        var pinnedRevisionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = pinnedRevisionId, ConnectionId = connId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 2, Status = "published", CreatedAt = now, PublishedAt = now,
            InputMappingJson = JsonSerializer.Serialize(
                new PoMappingConfig { OutputTree = JsonTree("pinnedLayout") }, CamelCase),
            OutputFormat = "json",
        });
        await db.SaveChangesAsync();

        var orderId = await SeedOrderAsync(db, orgId, supplierId);
        var order   = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.ConnectionRevisionId = pinnedRevisionId;
        await db.SaveChangesAsync();

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = JsonTree("liveLayout") }, CancellationToken.None);

        var result = await new ReplayService(db, Transformers(), effectiveConfig: Authority(db))
            .ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        Assert.NotNull(diff.CurrentOutput);
        Assert.Contains("pinnedLayout", diff.CurrentOutput!);
        Assert.DoesNotContain("liveLayout", diff.CurrentOutput!);
    }
}
