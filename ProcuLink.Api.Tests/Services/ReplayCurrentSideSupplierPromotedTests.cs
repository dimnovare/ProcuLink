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
/// Launch batch 4A — the CURRENT side of a replay diff must mirror the live transform's effective
/// priority (per-order override → supplier-promoted <see cref="PoMappingConfig.Output"/> → fixed
/// transformer), so "what changes if I publish this revision?" compares against what delivery
/// ACTUALLY produces today.
/// </summary>
public class ReplayCurrentSideSupplierPromotedTests
{
    private static ProcuLinkDbContext MakeDb() =>
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
    };

    private static async Task<(Guid orgId, Guid supplierId, Guid connectionId, Guid revisionId)>
        SeedConnectionWithDraftRevisionAsync(ProcuLinkDbContext db)
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var revisionId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        var draftConfig = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "DraftRef", CanonicalField = "PoNumber" } },
        };

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Replay Supplier", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Replay Supplier",
            ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "draft", CreatedAt = now,
            OutputMappingJson = JsonSerializer.Serialize(draftConfig, CamelCase), OutputFormat = "csv",
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, connectionId, revisionId);
    }

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, JsonDocument? canonical = null)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-RPLY",
            Currency = "EUR", OrderDate = new DateOnly(2026, 6, 1), Status = "ready",
            CanonicalJson = canonical, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "BUY-1", SupplierItemCode = "SUP-1",
                    Description = "Widget", Quantity = 2m, Unit = "EA", UnitPrice = 5m,
                    NeedsReview = false,
                },
            },
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    private static OutputMappingConfig PromotedOutput() => new()
    {
        Header = { ["po"]   = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
        Lines  = { ["code"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
    };

    [Fact]
    public async Task Replay_CurrentSide_UsesSupplierPromotedOutput_WhenOrderHasNoOverride()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithDraftRevisionAsync(db);
        await SeedOrderAsync(db, orgId, supplierId); // no per-order override

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var svc = new ReplayService(db, Transformers()); // _poMappings falls back to the real service
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        // CURRENT side = the supplier-promoted layout (what delivery actually produces today)…
        Assert.NotNull(diff.CurrentOutput);
        Assert.StartsWith("OrderRef,ItemCode", diff.CurrentOutput!);
        // …while the DRAFT side is the revision's mapping — and the diff flags the change.
        Assert.NotNull(diff.DraftOutput);
        Assert.StartsWith("DraftRef", diff.DraftOutput!);
        Assert.True(diff.OutputChanged);
    }

    [Fact]
    public async Task Replay_CurrentSide_PerOrderOverrideStillOutranksPromotedOutput()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, revId) = await SeedConnectionWithDraftRevisionAsync(db);

        // Per-order override stored in canonical_json — must stay the highest-priority current side.
        var @override = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PerOrderRef", CanonicalField = "PoNumber" } },
            },
        };
        var canonical = JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string, object?> { [OrderMappingOverrideReader.CanonicalKey] = @override }, CamelCase));
        await SeedOrderAsync(db, orgId, supplierId, canonical);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var svc = new ReplayService(db, Transformers());
        var result = await svc.ReplayAsync(orgId, connId, revId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.CurrentOutput);
        Assert.StartsWith("PerOrderRef", diff.CurrentOutput!);          // override wins
        Assert.DoesNotContain("OrderRef,ItemCode", diff.CurrentOutput!); // promoted layout NOT used
    }
}
