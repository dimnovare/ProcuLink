using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// WP-12 defect D7 — replay renders trees BLIND.
///
/// <para><c>ReplayService.Render</c> calls <c>OutputTemplateEmitter.Emit(tree, order, override)</c>
/// with no <c>sourceTokens</c> and no <c>catalogLookup</c>, while delivery
/// (<c>OrderTransformService</c>) and the preview (<c>OrdersController</c>) both pass them. Any leaf
/// bound to a source token (F-1) or to a catalog field therefore renders EMPTY in replay and REAL in
/// delivery — and because BOTH replay sides are equally blind, a revision that changes only
/// token-bound leaves reports "nothing changes". That is precisely the failure this PR's own comment
/// claims replay now catches.</para>
/// </summary>
public class ReplayTreeSourceTokenAndCatalogTests
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
        new CsvTransformService(), new JsonTransformService(), new XmlTransformService(),
    };

    /// <summary>A header leaf bound to a SOURCE TOKEN (F-1), the binding replay cannot see.</summary>
    private static OutputNodeTemplate TokenBoundTree(string tokenId) => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("ref", new OutputFieldRule { OutputPath = "ref", SourceToken = tokenId })),
    };

    /// <summary>A LINE leaf bound to the catalog row the delivery path pre-injects.</summary>
    private static OutputNodeTemplate CatalogBoundTree() => new()
    {
        Format = OutputFormat.Json,
        Root = OutputNode.Obj("root",
            OutputNode.Arr("items", OutputNode.Obj("item",
                OutputNode.FieldOf("catalogCode",
                    new OutputFieldRule { OutputPath = "catalogCode", CanonicalField = "__catalog_code" })))),
    };

    private static async Task<(Guid OrgId, Guid SupplierId, Guid ConnectionId, Guid RevisionId)>
        SeedAsync(ProcuLinkDbContext db, PoMappingConfig revisionBundle)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "D7 Supplier", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "D7 Supplier",
            CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "draft", CreatedAt = now,
            InputMappingJson = JsonSerializer.Serialize(revisionBundle, CamelCase),
            OutputFormat = "json",
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, connectionId, revisionId);
    }

    /// <summary>An order carrying a persisted SOURCE CAPTURE — the token universe delivery re-derives.</summary>
    private static async Task<Guid> SeedOrderWithTokensAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-D7",
            Currency = "EUR", OrderDate = new DateOnly(2026, 7, 30), Status = "ready",
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
        db.SourceCaptures.Add(new SourceCapture
        {
            Id = Guid.NewGuid(), OrderId = orderId, OrgId = orgId, Format = "csv",
            CapturedAt = DateTime.UtcNow,
            TokensJson = JsonDocument.Parse(
                """
                [
                  { "id": "cell:r1c1", "label": "Vendor Ref",   "value": "VENDOR-REF-A", "group": null },
                  { "id": "cell:r1c2", "label": "Customer Ref", "value": "CUST-REF-B",   "group": null }
                ]
                """),
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    // ══ D7 — the draft side must see the same token universe delivery sees ═══════════════════════

    [Fact]
    public async Task Replay_RendersATokenBoundLeaf_WithItsRealSourceValue()
    {
        await using var db = NewDb();
        var (orgId, supplierId, connId, revId) =
            await SeedAsync(db, new PoMappingConfig { OutputTree = TokenBoundTree("cell:r1c1") });
        await SeedOrderWithTokensAsync(db, orgId, supplierId);

        var result = await new ReplayService(db, Transformers())
            .ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.DraftOutput);
        Assert.Contains("VENDOR-REF-A", diff.DraftOutput!);
    }

    [Fact]
    public async Task Replay_DetectsARevisionThatChangesOnlyTokenBoundLeaves()
    {
        // The exact failure mode: both sides render the token-bound leaf as "", so the diff screen
        // reports "nothing changes" for a revision that rebinds the supplier's reference field.
        await using var db = NewDb();
        var (orgId, supplierId, connId, revId) =
            await SeedAsync(db, new PoMappingConfig { OutputTree = TokenBoundTree("cell:r1c2") });
        await SeedOrderWithTokensAsync(db, orgId, supplierId);

        // Live/current side: the SAME layout bound to a DIFFERENT source token.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { OutputTree = TokenBoundTree("cell:r1c1") }, CancellationToken.None);

        var result = await new ReplayService(db, Transformers())
            .ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        var diff = Assert.Single(result!.Orders);
        Assert.Contains("VENDOR-REF-A", diff.CurrentOutput!);
        Assert.Contains("CUST-REF-B", diff.DraftOutput!);
        Assert.True(diff.OutputChanged);
    }

    [Fact]
    public async Task Replay_RendersACatalogBoundLeaf_WithTheRealCatalogValue()
    {
        // The second blind binding. Delivery batch-loads the supplier catalog and pre-injects the
        // matched row; replay passed no lookup at all, so every catalog leaf rendered empty.
        await using var db = NewDb();
        var (orgId, supplierId, connId, revId) =
            await SeedAsync(db, new PoMappingConfig { OutputTree = CatalogBoundTree() });
        await SeedOrderWithTokensAsync(db, orgId, supplierId);

        db.SupplierProducts.Add(new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = "SUP-1", Name = "Widget", IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await new ReplayService(db, Transformers())
            .ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.DraftOutput);
        Assert.Contains("SUP-1", diff.DraftOutput!);
    }
}
