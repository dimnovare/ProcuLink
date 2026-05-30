using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="ItemMappingService.ResolveManyAsync"/> — the batched
/// equivalent of <see cref="ItemMappingService.ResolveAsync"/> that the order parse
/// loop uses to resolve every line in a single org+supplier-scoped query.
/// </summary>
public class ItemMappingServiceResolveManyTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ItemMapping Mapping(Guid orgId, Guid supplierId, string buyer, string supplier) =>
        new()
        {
            Id               = Guid.NewGuid(),
            OrgId            = orgId,
            SupplierId       = supplierId,
            BuyerItemCode    = buyer,
            SupplierItemCode = supplier,
            Confidence       = 1.0f,
            Source           = "manual",
            CreatedAt        = DateTime.UtcNow,
            UpdatedAt        = DateTime.UtcNow,
        };

    [Fact]
    public async Task ResolveManyAsync_ReturnsCorrectMappings_ForMultipleCodes_InOneCall()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.AddRange(
            Mapping(orgId, supplierId, "BUYER-1", "SUP-1"),
            Mapping(orgId, supplierId, "BUYER-2", "SUP-2"),
            Mapping(orgId, supplierId, "BUYER-3", "SUP-3"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        var result = await svc.ResolveManyAsync(
            orgId, supplierId,
            new[] { "BUYER-1", "BUYER-2", "BUYER-3" },
            CancellationToken.None);

        result.Should().HaveCount(3);
        result["BUYER-1"].Should().Be("SUP-1");
        result["BUYER-2"].Should().Be("SUP-2");
        result["BUYER-3"].Should().Be("SUP-3");
    }

    [Fact]
    public async Task ResolveManyAsync_UnmappedCode_IsPresentAsKey_WithNullValue()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(Mapping(orgId, supplierId, "BUYER-1", "SUP-1"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        var result = await svc.ResolveManyAsync(
            orgId, supplierId,
            new[] { "BUYER-1", "BUYER-MISSING" },
            CancellationToken.None);

        result.Should().HaveCount(2);
        result["BUYER-1"].Should().Be("SUP-1");
        result.Should().ContainKey("BUYER-MISSING");
        result["BUYER-MISSING"].Should().BeNull();
    }

    [Fact]
    public async Task ResolveManyAsync_IsScopedToOrgAndSupplier()
    {
        await using var db = NewDb();
        var orgA       = Guid.NewGuid();
        var orgB       = Guid.NewGuid();
        var supplier1  = Guid.NewGuid();
        var supplier2  = Guid.NewGuid();

        // Same buyer code exists under a different org and a different supplier.
        db.ItemMappings.AddRange(
            Mapping(orgA, supplier1, "SHARED", "A-SUP1"),
            Mapping(orgB, supplier1, "SHARED", "B-SUP1"),   // wrong org
            Mapping(orgA, supplier2, "SHARED", "A-SUP2"));  // wrong supplier
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        var result = await svc.ResolveManyAsync(
            orgA, supplier1, new[] { "SHARED" }, CancellationToken.None);

        result.Should().ContainKey("SHARED");
        result["SHARED"].Should().Be("A-SUP1");
    }

    [Fact]
    public async Task ResolveManyAsync_TrimsAndDeduplicates_AndSkipsBlankCodes()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.Add(Mapping(orgId, supplierId, "BUYER-1", "SUP-1"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        var result = await svc.ResolveManyAsync(
            orgId, supplierId,
            new[] { "  BUYER-1  ", "BUYER-1", "   ", "" },
            CancellationToken.None);

        // Blank/whitespace skipped; the two BUYER-1 variants collapse to one trimmed key.
        result.Should().HaveCount(1);
        result.Should().ContainKey("BUYER-1");
        result["BUYER-1"].Should().Be("SUP-1");
    }

    [Fact]
    public async Task ResolveManyAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        await using var db = NewDb();
        var svc = new ItemMappingService(db);

        var result = await svc.ResolveManyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Array.Empty<string>(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveManyAsync_AgreesWith_ResolveAsync_PerCode()
    {
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.ItemMappings.AddRange(
            Mapping(orgId, supplierId, "A", "SA"),
            Mapping(orgId, supplierId, "B", "SB"));
        await db.SaveChangesAsync();

        var svc = new ItemMappingService(db);

        var codes = new[] { "A", "B", "C" };
        var batch = await svc.ResolveManyAsync(orgId, supplierId, codes, CancellationToken.None);

        foreach (var code in codes)
        {
            var single = await svc.ResolveAsync(orgId, supplierId, code, CancellationToken.None);
            batch[code].Should().Be(single, $"batch and single-line resolve must agree for '{code}'");
        }
    }
}
