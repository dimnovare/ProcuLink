using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

/// <summary>
/// Tests the supplier-scoped field-mapping moat: capture (create / merge / observation count),
/// lookup (match / miss / org + supplier isolation / order-independent hash), the reinforce-by-hash
/// path used by Resolve, and the no-op guards for header-less files and empty mappings.
/// </summary>
public class SupplierSchemaMappingServiceTests
{
    private static readonly string[] LayoutAHeaders = { "po_number", "supplier_code", "sku", "qty", "price" };

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierSchemaMappingService NewService(ProcuLinkDbContext db) =>
        new(db, NullLogger<SupplierSchemaMappingService>.Instance);

    private static Dictionary<string, string> Map(params (string buyer, string supplier)[] pairs) =>
        pairs.ToDictionary(p => p.buyer, p => p.supplier);

    // ── CaptureAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_CreatesRow_OnFirstSuccessfulMap()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        await NewService(db).CaptureAsync(
            orgId, supplierId, Guid.NewGuid(), LayoutAHeaders, "csv",
            Map(("BUYER-1", "SUP-A"), ("BUYER-2", "SUP-B")), CancellationToken.None);

        var row = await db.SupplierSchemaMappings.SingleAsync();
        row.OrganisationId.Should().Be(orgId);
        row.SupplierId.Should().Be(supplierId);
        row.DetectedFormat.Should().Be("csv");
        row.ObservationCount.Should().Be(1);
        row.ColumnNameHash.Should().Be(SchemaFingerprintHasher.ComputeColumnNameHash(LayoutAHeaders));
    }

    [Fact]
    public async Task Capture_NormalisesBuyerKeys_ToLowercaseTrimmed()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        await NewService(db).CaptureAsync(
            orgId, supplierId, null, LayoutAHeaders, "csv",
            Map(("  Buyer-1 ", "SUP-A")), CancellationToken.None);

        // A lookup with a differently-cased/spaced buyer code must find the same supplier code.
        var match = await NewService(db).LookupAsync(orgId, supplierId, LayoutAHeaders, CancellationToken.None);
        match.Should().NotBeNull();
        match!.FieldMapping.Should().ContainKey("buyer-1");
        match.FieldMapping["buyer-1"].Should().Be("SUP-A");
    }

    [Fact]
    public async Task Capture_MergesAndIncrements_ForSameSupplierAndLayout()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgId, supplierId, Guid.NewGuid(), LayoutAHeaders, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);
        await svc.CaptureAsync(orgId, supplierId, Guid.NewGuid(), LayoutAHeaders, "csv",
            Map(("buyer-2", "SUP-B")), CancellationToken.None);

        var row = await db.SupplierSchemaMappings.SingleAsync();
        row.ObservationCount.Should().Be(2, "same supplier + layout collapses to one row");

        var match = await svc.LookupAsync(orgId, supplierId, LayoutAHeaders, CancellationToken.None);
        match!.FieldMapping.Should().HaveCount(2);
        match.FieldMapping["buyer-1"].Should().Be("SUP-A");
        match.FieldMapping["buyer-2"].Should().Be("SUP-B");
    }

    [Fact]
    public async Task Capture_NewerMappingWins_OnBuyerCodeConflict()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgId, supplierId, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "OLD-CODE")), CancellationToken.None);
        await svc.CaptureAsync(orgId, supplierId, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "NEW-CODE")), CancellationToken.None);

        var match = await svc.LookupAsync(orgId, supplierId, LayoutAHeaders, CancellationToken.None);
        match!.FieldMapping["buyer-1"].Should().Be("NEW-CODE", "the most recent successful map wins");
    }

    [Fact]
    public async Task Capture_KeepsSuppliersSeparate_ForSameLayout()
    {
        var orgId = Guid.NewGuid();
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgId, supplierA, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "A-CODE")), CancellationToken.None);
        await svc.CaptureAsync(orgId, supplierB, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "B-CODE")), CancellationToken.None);

        (await db.SupplierSchemaMappings.CountAsync())
            .Should().Be(2, "the same layout for two suppliers is two distinct learned mappings");

        var matchA = await svc.LookupAsync(orgId, supplierA, LayoutAHeaders, CancellationToken.None);
        var matchB = await svc.LookupAsync(orgId, supplierB, LayoutAHeaders, CancellationToken.None);
        matchA!.FieldMapping["buyer-1"].Should().Be("A-CODE");
        matchB!.FieldMapping["buyer-1"].Should().Be("B-CODE");
    }

    [Fact]
    public async Task Capture_DoesNothing_WhenNoColumnHeaders()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        await NewService(db).CaptureAsync(
            orgId, supplierId, null, columnHeaders: null, "pdf",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        (await db.SupplierSchemaMappings.AnyAsync()).Should().BeFalse("header-less formats have no layout to key on");
    }

    [Fact]
    public async Task Capture_DoesNothing_WhenFieldMappingEmpty()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        await NewService(db).CaptureAsync(
            orgId, supplierId, null, LayoutAHeaders, "csv",
            new Dictionary<string, string>(), CancellationToken.None);

        (await db.SupplierSchemaMappings.AnyAsync()).Should().BeFalse("nothing resolved means nothing to learn");
    }

    [Fact]
    public async Task Capture_SkipsBlankPairs()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        await NewService(db).CaptureAsync(
            orgId, supplierId, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "SUP-A"), ("buyer-2", "   "), ("   ", "SUP-C")), CancellationToken.None);

        var match = await NewService(db).LookupAsync(orgId, supplierId, LayoutAHeaders, CancellationToken.None);
        match!.FieldMapping.Should().ContainSingle().Which.Key.Should().Be("buyer-1");
    }

    // ── LookupAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_MatchesRegardlessOfHeaderOrder()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgId, supplierId, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        var shuffled = new[] { "qty", "price", "po_number", "sku", "supplier_code" };
        var match = await svc.LookupAsync(orgId, supplierId, shuffled, CancellationToken.None);

        match.Should().NotBeNull("the layout hash is order-independent");
        match!.ObservationCount.Should().Be(1);
        match.FieldMapping["buyer-1"].Should().Be("SUP-A");
    }

    [Fact]
    public async Task Lookup_ReturnsNull_ForUnseenLayout()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        var match = await NewService(db).LookupAsync(
            orgId, supplierId, new[] { "never", "seen", "before" }, CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task Lookup_ReturnsNull_ForDifferentSupplier()
    {
        var orgId = Guid.NewGuid();
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgId, supplierA, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        var match = await svc.LookupAsync(orgId, supplierB, LayoutAHeaders, CancellationToken.None);
        match.Should().BeNull("a mapping learned for one supplier must not pre-fill another supplier");
    }

    [Fact]
    public async Task Lookup_IsOrgScoped()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgA, supplierId, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        var match = await svc.LookupAsync(orgB, supplierId, LayoutAHeaders, CancellationToken.None);
        match.Should().BeNull("learned mappings must never leak across organisations");
    }

    [Fact]
    public async Task Lookup_ReturnsNull_ForNullHeaders()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        var match = await NewService(db).LookupAsync(orgId, supplierId, null, CancellationToken.None);
        match.Should().BeNull();
    }

    // ── ReinforceByHashAsync (Resolve path) ───────────────────────────────────

    [Fact]
    public async Task ReinforceByHash_UpsertsUsingPrecomputedHash()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(LayoutAHeaders);

        await svc.ReinforceByHashAsync(orgId, supplierId, Guid.NewGuid(), hash, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        // A header-based lookup for the same layout must find the row created via the hash path,
        // proving both paths agree on the same key.
        var match = await svc.LookupAsync(orgId, supplierId, LayoutAHeaders, CancellationToken.None);
        match.Should().NotBeNull();
        match!.FieldMapping["buyer-1"].Should().Be("SUP-A");
    }

    [Fact]
    public async Task ReinforceByHash_MergesIntoRowCreatedByCapture()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();
        var svc = NewService(db);

        await svc.CaptureAsync(orgId, supplierId, null, LayoutAHeaders, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(LayoutAHeaders);
        await svc.ReinforceByHashAsync(orgId, supplierId, null, hash, "csv",
            Map(("buyer-2", "SUP-B")), CancellationToken.None);

        var row = await db.SupplierSchemaMappings.SingleAsync();
        row.ObservationCount.Should().Be(2, "capture + reinforce target the same row");

        var match = await svc.LookupAsync(orgId, supplierId, LayoutAHeaders, CancellationToken.None);
        match!.FieldMapping.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReinforceByHash_DoesNothing_WhenHashNull()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await using var db = NewDb();

        await NewService(db).ReinforceByHashAsync(orgId, supplierId, null, columnNameHash: null, "csv",
            Map(("buyer-1", "SUP-A")), CancellationToken.None);

        (await db.SupplierSchemaMappings.AnyAsync()).Should().BeFalse();
    }
}
