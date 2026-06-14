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
/// Read-parity (launch batch 7 follow-up) — the CURRENT side of a replay diff must mirror what
/// delivery ACTUALLY produces today. For a PINNED order under the
/// <c>Connections:RevisionAuthority</c> flag that means revision-driven: per-order override → the
/// PINNED revision's output snapshot → fixed transformer, at the PINNED revision's snapshotted
/// format — with the live supplier-promoted mapping NOT consulted (OrderTransformService's
/// reproducibility rule). GOLDEN guard: flag OFF / unpinned current sides stay byte-identical to the
/// launch-batch-4A behaviour (per-order override → supplier-promoted → fixed).
/// </summary>
public class ReplayCurrentSideRevisionAuthorityTests
{
    private static ProcuLinkDbContext MakeDb() =>
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

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IEnumerable<ITransformService> Transformers() => new ITransformService[]
    {
        new CsvTransformService(),
        new JsonTransformService(),
    };

    /// <summary>
    /// Seeds a connection with a PUBLISHED revision A (the pinned/runtime contract: "PinnedRef"
    /// output, configurable format) and a DRAFT revision B (the one being replayed: "DraftRef", csv).
    /// </summary>
    private static async Task<(Guid orgId, Guid supplierId, Guid connectionId, Guid publishedRevId, Guid draftRevId)>
        SeedConnectionAsync(ProcuLinkDbContext db, string publishedFormat = "csv")
    {
        var orgId          = Guid.NewGuid();
        var supplierId     = Guid.NewGuid();
        var connectionId   = Guid.NewGuid();
        var publishedRevId = Guid.NewGuid();
        var draftRevId     = Guid.NewGuid();
        var now            = DateTime.UtcNow;

        var publishedConfig = new OutputMappingConfig
        {
            Header = { ["po"]   = new OutputFieldRule { OutputPath = "PinnedRef",  CanonicalField = "PoNumber" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PinnedCode", CanonicalField = "SupplierItemCode" } },
        };
        var draftConfig = new OutputMappingConfig
        {
            Header = { ["po"] = new OutputFieldRule { OutputPath = "DraftRef", CanonicalField = "PoNumber" } },
        };

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Replay Supplier", CreatedAt = now });
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Replay Supplier",
            ActiveRevisionId = publishedRevId, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = publishedRevId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", CreatedAt = now, PublishedAt = now,
            OutputMappingJson = JsonSerializer.Serialize(publishedConfig, CamelCase), OutputFormat = publishedFormat,
        });
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = draftRevId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 2, Status = "draft", CreatedAt = now,
            OutputMappingJson = JsonSerializer.Serialize(draftConfig, CamelCase), OutputFormat = "csv",
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, connectionId, publishedRevId, draftRevId);
    }

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        Guid? pinnedRevisionId = null, JsonDocument? canonical = null)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-RP-RPLY",
            ConnectionRevisionId = pinnedRevisionId,
            Currency = "EUR", OrderDate = new DateOnly(2026, 6, 11), Status = "ready",
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

    private static ReplayService Service(ProcuLinkDbContext db, bool flagEnabled) =>
        new(db, Transformers(), poMappings: null, effectiveConfig: Resolver(db, flagEnabled));

    // ── Flag ON + pinned: the current side is revision-driven, never supplier-promoted ──────────

    [Fact]
    public async Task FlagOn_PinnedOrder_CurrentSideUsesPinnedRevisionOutput_NotSupplierPromoted()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, publishedRevId, draftRevId) = await SeedConnectionAsync(db);
        await SeedOrderAsync(db, orgId, supplierId, pinnedRevisionId: publishedRevId);

        // A DIVERGENT live supplier-promoted mapping — must be ignored for the pinned current side.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var result = await Service(db, flagEnabled: true)
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        // CURRENT side = the PINNED revision's layout (what delivery actually produces today)…
        Assert.NotNull(diff.CurrentOutput);
        Assert.StartsWith("PinnedRef,PinnedCode", diff.CurrentOutput!);
        Assert.DoesNotContain("OrderRef,ItemCode", diff.CurrentOutput!); // live promoted layout NOT used
        // …while the DRAFT side is the replayed revision's mapping — and the diff flags the change.
        Assert.NotNull(diff.DraftOutput);
        Assert.StartsWith("DraftRef", diff.DraftOutput!);
        Assert.True(diff.OutputChanged);
    }

    [Fact]
    public async Task FlagOn_PinnedOrder_PerOrderOverrideStillOutranksPinnedRevision()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, publishedRevId, draftRevId) = await SeedConnectionAsync(db);

        var @override = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "PerOrderRef", CanonicalField = "PoNumber" } },
            },
        };
        var canonical = JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string, object?> { [OrderMappingOverrideReader.CanonicalKey] = @override }, CamelCase));
        await SeedOrderAsync(db, orgId, supplierId, pinnedRevisionId: publishedRevId, canonical: canonical);

        var result = await Service(db, flagEnabled: true)
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.CurrentOutput);
        Assert.StartsWith("PerOrderRef", diff.CurrentOutput!);     // the per-order override wins
        Assert.DoesNotContain("PinnedRef", diff.CurrentOutput!);   // revision layout NOT used
    }

    [Fact]
    public async Task FlagOn_PinnedOrder_CurrentSideFormatComesFromPinnedRevision()
    {
        var db = MakeDb();
        // The pinned (published) revision delivers JSON; the draft under replay is CSV.
        var (orgId, supplierId, connId, publishedRevId, draftRevId) = await SeedConnectionAsync(db, publishedFormat: "json");
        await SeedOrderAsync(db, orgId, supplierId, pinnedRevisionId: publishedRevId);

        var result = await Service(db, flagEnabled: true)
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);

        // CURRENT side renders in the pinned revision's format (JSON, native override builder)…
        Assert.NotNull(diff.CurrentOutput);
        using (var doc = JsonDocument.Parse(diff.CurrentOutput!))
            Assert.Equal("PO-RP-RPLY", doc.RootElement.GetProperty("header").GetProperty("PinnedRef").GetString());
        // …while the DRAFT side stays in the draft revision's CSV.
        Assert.NotNull(diff.DraftOutput);
        Assert.StartsWith("DraftRef", diff.DraftOutput!);
    }

    // ── GOLDEN — flag OFF / unpinned: the 4A current side is unchanged ──────────────────────────

    [Fact]
    public async Task FlagOff_PinnedOrder_CurrentSideStillSupplierPromoted_ByteIdentical()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, publishedRevId, draftRevId) = await SeedConnectionAsync(db);
        await SeedOrderAsync(db, orgId, supplierId, pinnedRevisionId: publishedRevId);

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        // Flag OFF — the pin must be invisible (4A behaviour) …
        var flagOff = await Service(db, flagEnabled: false)
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);
        // … and identical to a service constructed WITHOUT the resolver (pre-read-parity baseline).
        var noResolver = await new ReplayService(db, Transformers())
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(flagOff);
        Assert.NotNull(noResolver);
        var diff = Assert.Single(flagOff!.Orders);
        Assert.NotNull(diff.CurrentOutput);
        Assert.StartsWith("OrderRef,ItemCode", diff.CurrentOutput!); // promoted layout, NOT PinnedRef
        Assert.Equal(Assert.Single(noResolver!.Orders).CurrentOutput, diff.CurrentOutput); // byte-identical
    }

    [Fact]
    public async Task FlagOn_UnpinnedOrder_CurrentSideStillSupplierPromoted()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, _, draftRevId) = await SeedConnectionAsync(db);
        await SeedOrderAsync(db, orgId, supplierId); // NOT pinned

        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var result = await Service(db, flagEnabled: true)
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.CurrentOutput);
        Assert.StartsWith("OrderRef,ItemCode", diff.CurrentOutput!); // unpinned keeps the 4A behaviour
    }

    [Fact]
    public async Task FlagOn_PinnedOrder_NullRevisionOutput_CurrentSideIsFixed_NotSupplierPromoted()
    {
        var db = MakeDb();
        var (orgId, supplierId, connId, _, draftRevId) = await SeedConnectionAsync(db);

        // A second published revision with NO output snapshot (backfilled rev-1 shape) — pin to it.
        var bareRevId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = bareRevId, ConnectionId = connId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 3, Status = "published", CreatedAt = now, PublishedAt = now,
            OutputMappingJson = null, OutputFormat = "csv",
        });
        await db.SaveChangesAsync();
        await SeedOrderAsync(db, orgId, supplierId, pinnedRevisionId: bareRevId);

        // A live promoted mapping exists — a PINNED order must NOT consult it: fixed transformer drives.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId,
            new PoMappingConfig { Output = PromotedOutput() }, CancellationToken.None);

        var result = await Service(db, flagEnabled: true)
            .ReplayAsync(orgId, connId, draftRevId, new ReplayRequest(), CancellationToken.None);

        Assert.NotNull(result);
        var diff = Assert.Single(result!.Orders);
        Assert.NotNull(diff.CurrentOutput);
        Assert.DoesNotContain("OrderRef,ItemCode", diff.CurrentOutput!); // promoted NOT consulted
        // The FIXED CSV transformer's layout drives the current side (backfilled rev-1 contract).
        Assert.StartsWith("PoNumber,OrderDate,Currency,BuyerName,SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal", diff.CurrentOutput!);
    }
}
