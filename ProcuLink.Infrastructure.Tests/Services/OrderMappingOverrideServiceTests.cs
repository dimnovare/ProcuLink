using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="OrderMappingOverrideService"/> (heart-piece-flex Phase 1): the per-order
/// override stored under the <c>"mappingOverride"</c> key of <c>canonical_json</c>. Covers org-scoped
/// read/write, that the write preserves sibling canonical_json keys (buyerName / enrichment), and that
/// an absent / cross-tenant / malformed key reads as null.
/// </summary>
public class OrderMappingOverrideServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(Guid orgId, Guid orderId)> SeedOrderAsync(
        ProcuLinkDbContext db, JsonDocument? canonical = null)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            PoNumber      = "PO-1",
            Currency      = "EUR",
            OrderDate     = new DateOnly(2026, 1, 1),
            Status        = "ready",
            CanonicalJson = canonical,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static OrderMappingOverride SampleOverride() => new()
    {
        CustomFields =
        {
            new CustomField { Key = "gln", Label = "Buyer GLN", Scope = "header", Value = "4012345000009" },
        },
        Output = new OutputMappingConfig
        {
            Header =
            {
                ["po"] = new OutputFieldRule { OutputPath = "PONumber", CanonicalField = "PoNumber" },
            },
            Lines =
            {
                ["sku"] = new OutputFieldRule { OutputPath = "SKU", CanonicalField = "SupplierItemCode" },
            },
        },
    };

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenOrderHasNoCanonicalJson()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, canonical: null);
        var svc = new OrderMappingOverrideService(db);

        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenCanonicalJsonHasNoOverrideKey()
    {
        await using var db = NewDb();
        var canonical = JsonDocument.Parse("""{ "buyerName": "Acme Ltd", "poNumber": "PO-1" }""");
        var (orgId, orderId) = await SeedOrderAsync(db, canonical);
        var svc = new OrderMappingOverrideService(db);

        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForCrossTenantOrder()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = new OrderMappingOverrideService(db);

        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);

        // A different org may NOT read the override.
        var result = await svc.GetAsync(Guid.NewGuid(), orderId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ThenGetAsync_RoundTripsTheOverride()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = new OrderMappingOverrideService(db);

        var saved = await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);
        saved.Should().BeTrue();

        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CustomFields.Should().ContainSingle();
        result.CustomFields[0].Key.Should().Be("gln");
        result.CustomFields[0].Value.Should().Be("4012345000009");
        result.Output.Should().NotBeNull();
        result.Output!.Header.Should().ContainKey("po");
        result.Output.Header["po"].CanonicalField.Should().Be("PoNumber");
        result.Output.Lines["sku"].OutputPath.Should().Be("SKU");
    }

    [Fact]
    public async Task UpsertAsync_PreservesExistingCanonicalKeys_LikeBuyerName()
    {
        await using var db = NewDb();
        var canonical = JsonDocument.Parse(
            """{ "source": "csv_upload", "buyerName": "Keep Me Ltd", "orderDate": "2026-01-01" }""");
        var (orgId, orderId) = await SeedOrderAsync(db, canonical);
        var svc = new OrderMappingOverrideService(db);

        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);

        // Re-read the raw canonical_json — buyerName / source / orderDate must survive the override write.
        var stored = await db.PurchaseOrders.AsNoTracking()
            .Where(x => x.Id == orderId)
            .Select(x => x.CanonicalJson)
            .FirstAsync();

        stored.Should().NotBeNull();
        var root = stored!.RootElement;
        root.GetProperty("buyerName").GetString().Should().Be("Keep Me Ltd");
        root.GetProperty("source").GetString().Should().Be("csv_upload");
        root.GetProperty("orderDate").GetString().Should().Be("2026-01-01");
        root.TryGetProperty("mappingOverride", out _).Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_OverwritesAPreviousOverride_WithoutDuplicatingTheKey()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = new OrderMappingOverrideService(db);

        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);

        var updated = new OrderMappingOverride
        {
            CustomFields = { new CustomField { Key = "k2", Label = "Second", Scope = "header", Value = "v2" } },
            Output = new OutputMappingConfig
            {
                Header = { ["x"] = new OutputFieldRule { OutputPath = "X", FixedValue = "literal" } },
            },
        };
        await svc.UpsertAsync(orgId, orderId, updated, CancellationToken.None);

        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);
        result!.CustomFields.Should().ContainSingle(c => c.Key == "k2");
        result.Output!.Header.Should().ContainKey("x");
        result.Output.Header.Should().NotContainKey("po"); // replaced, not merged
    }

    [Fact]
    public async Task UpsertAsync_ReturnsFalse_ForCrossTenantOrder_AndDoesNotWrite()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = new OrderMappingOverrideService(db);

        var saved = await svc.UpsertAsync(Guid.NewGuid(), orderId, SampleOverride(), CancellationToken.None);

        saved.Should().BeFalse();

        // The real owner still sees no override.
        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);
        result.Should().BeNull();
    }

    // ── MV-1: a mapping edit on an already-transformed order resets it to 'ready' so the next Send
    //         re-transforms instead of shipping the stale artifact. An UNCHANGED upsert does not. ──

    private static async Task<(Guid orgId, Guid orderId)> SeedOrderWithStatusAsync(
        ProcuLinkDbContext db, string status)
    {
        var orgId      = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = Guid.NewGuid(),
            PoNumber = "PO-MV1", Currency = "EUR", OrderDate = new DateOnly(2026, 1, 1),
            Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    private static async Task<string> StatusOf(ProcuLinkDbContext db, Guid orderId) =>
        await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).FirstAsync();

    [Theory]
    [InlineData(ProcuLink.Core.Constants.OrderStatusConstants.ReadyToDeliver)]
    [InlineData(ProcuLink.Core.Constants.OrderStatusConstants.Transforming)]
    [InlineData(ProcuLink.Core.Constants.OrderStatusConstants.Delivered)]
    public async Task UpsertAsync_ChangedOverride_PastReady_ResetsStatusToReady(string startStatus)
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderWithStatusAsync(db, startStatus);
        var svc = new OrderMappingOverrideService(db);

        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);

        (await StatusOf(db, orderId)).Should().Be(ProcuLink.Core.Constants.OrderStatusConstants.Ready);
    }

    [Fact]
    public async Task UpsertAsync_UnchangedOverride_LeavesStatusUntouched()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderWithStatusAsync(
            db, ProcuLink.Core.Constants.OrderStatusConstants.ReadyToDeliver);
        var svc = new OrderMappingOverrideService(db);

        // First write transitions the order back to 'ready'; re-seed it to ready_to_deliver to isolate
        // the "unchanged re-save" case (InMemory provider — mutate the tracked entity + SaveChanges).
        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);
        var tracked = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        tracked.Status = ProcuLink.Core.Constants.OrderStatusConstants.ReadyToDeliver;
        await db.SaveChangesAsync();

        // Re-save the SAME override content — no re-transform should be forced.
        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);

        (await StatusOf(db, orderId)).Should()
            .Be(ProcuLink.Core.Constants.OrderStatusConstants.ReadyToDeliver);
    }

    [Fact]
    public async Task UpsertAsync_ChangedOverride_UpstreamOfTransform_DoesNotResetStatus()
    {
        // An order still in pending_review/ready has no artifact to make stale — the upsert must not
        // touch its status (a 'ready' order stays 'ready'; a 'pending_review' stays pending_review).
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderWithStatusAsync(
            db, ProcuLink.Core.Constants.OrderStatusConstants.PendingReview);
        var svc = new OrderMappingOverrideService(db);

        await svc.UpsertAsync(orgId, orderId, SampleOverride(), CancellationToken.None);

        (await StatusOf(db, orderId)).Should()
            .Be(ProcuLink.Core.Constants.OrderStatusConstants.PendingReview);
    }

    [Fact]
    public async Task UpsertAsync_RoundTripsPerLineCustomFieldValues()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db);
        var svc = new OrderMappingOverrideService(db);

        var ov = new OrderMappingOverride
        {
            CustomFields =
            {
                new CustomField
                {
                    Key = "lineGln", Label = "Per-line GLN", Scope = "line",
                    LineValues = new Dictionary<int, string> { [1] = "A", [2] = "B" },
                },
            },
        };
        await svc.UpsertAsync(orgId, orderId, ov, CancellationToken.None);

        var result = await svc.GetAsync(orgId, orderId, CancellationToken.None);
        var cf = result!.CustomFields.Single();
        cf.Scope.Should().Be("line");
        cf.LineValues.Should().NotBeNull();
        cf.LineValues![1].Should().Be("A");
        cf.LineValues[2].Should().Be("B");
    }
}
