using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Launch batch 7 — <see cref="SupplierAcceptanceService.ValidateOrderAsync"/> must validate a
/// PINNED order against the revision's BOUND acceptance profile version (bind-by-id, org-scoped)
/// when the <c>Connections:RevisionAuthority</c> flag is ON — not against whatever profile is
/// live-active at validation time. A revision that binds NO profile honestly validates with no
/// rules. Flag off / unpinned / orphan pin / dangling binding → the live active profile,
/// byte-identical to the pre-batch-7 behaviour.
/// </summary>
public class RevisionAuthorityValidationTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierAcceptanceService Service(ProcuLinkDbContext db, bool flagEnabled) =>
        new(db, new EffectiveConnectionConfigResolver(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EffectiveConnectionConfigResolver.FlagKey] = flagEnabled ? "true" : "false",
            })
            .Build()));

    private sealed record Seeded(
        Guid OrgId, Guid SupplierId, Guid OrderId,
        SupplierAcceptanceProfile BoundV1, SupplierAcceptanceProfile LiveActiveV2);

    /// <summary>
    /// One EUR order + two acceptance profile versions:
    /// v1 (archived; currency must equal EUR — PASSES) and v2 (ACTIVE; currency must equal USD —
    /// FAILS the order). The pinned revision binds v1, so authority vs live is observable as
    /// pass-vs-fail.
    /// </summary>
    private static async Task<Seeded> SeedAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "ValCo", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-VAL-1", OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
            Status = "ready", CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "S-1", Description = "Widget",
                    Quantity = 1m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1f,
                },
            },
        });

        var v1 = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "archived", CreatedAt = now,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency",
                    Operator = "equals", ExpectedValue = "EUR", Severity = "error", BlockOnFail = true,
                },
            },
        };
        var v2 = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 2, Status = "active", CreatedAt = now,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency",
                    Operator = "equals", ExpectedValue = "USD", Severity = "error", BlockOnFail = true,
                },
            },
        };
        db.SupplierAcceptanceProfiles.AddRange(v1, v2);
        await db.SaveChangesAsync();
        return new Seeded(orgId, supplierId, orderId, v1, v2);
    }

    private static async Task<SupplierConnectionRevision> SeedRevisionAsync(
        ProcuLinkDbContext db, Seeded seeded, Guid? acceptanceProfileId, int? acceptanceVersionNo,
        bool pinOrder = true)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), ConnectionId = connectionId, OrgId = seeded.OrgId,
            SupplierId = seeded.SupplierId, VersionNo = 1, Status = "published",
            CreatedAt = now, PublishedAt = now,
            AcceptanceProfileId = acceptanceProfileId,
            AcceptanceVersionNo = acceptanceVersionNo,
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = seeded.OrgId, SupplierId = seeded.SupplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);

        if (pinOrder)
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == seeded.OrderId);
            order.ConnectionRevisionId = revision.Id;
        }
        await db.SaveChangesAsync();
        return revision;
    }

    [Fact]
    public async Task FlagOn_Pinned_ValidatesAgainstRevisionBoundVersion_WhileLiveActiveDiffers()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);
        await SeedRevisionAsync(db, seeded, seeded.BoundV1.Id, seeded.BoundV1.VersionNo);

        var results = await Service(db, flagEnabled: true)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.NotNull(results);
        var result = Assert.Single(results!);
        Assert.Equal(seeded.BoundV1.Id, result.ProfileId); // the BOUND v1, not the live-active v2
        Assert.Equal("pass", result.Status);               // EUR rule passes the EUR order
    }

    [Fact]
    public async Task FlagOff_Pinned_ValidatesAgainstLiveActiveProfile()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);
        await SeedRevisionAsync(db, seeded, seeded.BoundV1.Id, seeded.BoundV1.VersionNo);

        var results = await Service(db, flagEnabled: false)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.NotNull(results);
        var result = Assert.Single(results!);
        Assert.Equal(seeded.LiveActiveV2.Id, result.ProfileId); // pre-batch-7 behaviour
        Assert.Equal("fail", result.Status);                    // USD rule fails the EUR order
    }

    [Fact]
    public async Task FlagOn_Unpinned_ValidatesAgainstLiveActiveProfile()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);
        await SeedRevisionAsync(db, seeded, seeded.BoundV1.Id, seeded.BoundV1.VersionNo, pinOrder: false);

        var results = await Service(db, flagEnabled: true)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(seeded.LiveActiveV2.Id, Assert.Single(results!).ProfileId);
    }

    [Fact]
    public async Task FlagOn_Pinned_RevisionBindsNoProfile_ValidatesWithNoRules_Honest()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);
        // The published contract carries NO validation binding — that is the honest authority:
        // no rules, even though a live-active profile exists.
        await SeedRevisionAsync(db, seeded, acceptanceProfileId: null, acceptanceVersionNo: null);

        var results = await Service(db, flagEnabled: true)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task FlagOn_OrphanPin_FallsBackToLiveActiveProfile_NoThrow()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == seeded.OrderId);
        order.ConnectionRevisionId = Guid.NewGuid(); // dangling pin
        await db.SaveChangesAsync();

        var results = await Service(db, flagEnabled: true)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(seeded.LiveActiveV2.Id, Assert.Single(results!).ProfileId);
    }

    [Fact]
    public async Task FlagOn_Pinned_DanglingProfileBinding_FallsBackToLiveActiveProfile()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);
        // The revision binds a profile id that no longer exists — defensive live fallback.
        await SeedRevisionAsync(db, seeded, acceptanceProfileId: Guid.NewGuid(), acceptanceVersionNo: 9);

        var results = await Service(db, flagEnabled: true)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(seeded.LiveActiveV2.Id, Assert.Single(results!).ProfileId);
    }

    [Fact]
    public async Task FlagOn_Pinned_Revalidation_OverwritesPriorResults_WithRevisionBoundOnes()
    {
        await using var db = NewDb();
        var seeded = await SeedAsync(db);

        // First validation runs flag-OFF (live v2, fail) — e.g. before the cutover.
        var before = await Service(db, flagEnabled: false)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);
        Assert.Equal("fail", Assert.Single(before!).Status);

        // After the flag flips, re-validation must replace the rows with the revision-bound result.
        await SeedRevisionAsync(db, seeded, seeded.BoundV1.Id, seeded.BoundV1.VersionNo);
        var after = await Service(db, flagEnabled: true)
            .ValidateOrderAsync(seeded.OrgId, seeded.OrderId, CancellationToken.None);

        Assert.Equal("pass", Assert.Single(after!).Status);
        Assert.Equal(1, await db.OrderValidationResults.CountAsync()); // prior rows removed
    }
}
