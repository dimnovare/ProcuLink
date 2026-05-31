using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Tests for the AppliedCount usage counter and MappingCorrection ledger written
/// by <see cref="ItemMappingService.UpsertAsync"/>.
/// </summary>
public class ItemMappingServiceCorrectionTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ItemMappingService MakeSvc(ProcuLinkDbContext db) =>
        new(db);

    [Fact]
    public async Task UpsertAsync_NewMapping_StartsWithAppliedCountOne()
    {
        await using var db  = MakeDb();
        var svc        = MakeSvc(db);
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-001",
            MappingSource.Manual, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();
        mapping.AppliedCount.Should().Be(1);
        db.MappingCorrections.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ExistingSameCode_IncrementsCountNoCorrection()
    {
        await using var db  = MakeDb();
        var svc        = MakeSvc(db);
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-001",
            MappingSource.Manual, CancellationToken.None);
        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-001",
            MappingSource.Manual, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();
        mapping.AppliedCount.Should().Be(2);
        db.MappingCorrections.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_ExistingCodeChanged_WritesCorrection()
    {
        await using var db  = MakeDb();
        var svc        = MakeSvc(db);
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-OLD",
            MappingSource.Manual, CancellationToken.None);
        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-NEW",
            MappingSource.Manual, CancellationToken.None);

        var correction = await db.MappingCorrections.SingleAsync();
        correction.OldSupplierItemCode.Should().Be("SUP-OLD");
        correction.NewSupplierItemCode.Should().Be("SUP-NEW");
        correction.OrgId.Should().Be(orgId);
    }

    [Fact]
    public async Task UpsertAsync_ExistingCodeChanged_AppliedCountStillIncrements()
    {
        await using var db  = MakeDb();
        var svc        = MakeSvc(db);
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-OLD",
            MappingSource.Manual, CancellationToken.None);
        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-NEW",
            MappingSource.Manual, CancellationToken.None);

        var mapping = await db.ItemMappings.SingleAsync();
        mapping.AppliedCount.Should().Be(2);
    }

    [Fact]
    public async Task UpsertAsync_CodeChangedTwice_WritesTwoCorrections()
    {
        await using var db  = MakeDb();
        var svc        = MakeSvc(db);
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-A",
            MappingSource.Manual, CancellationToken.None);
        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-B",
            MappingSource.Manual, CancellationToken.None);
        await svc.UpsertAsync(orgId, supplierId, "BUYER-001", "SUP-C",
            MappingSource.Manual, CancellationToken.None);

        db.MappingCorrections.Should().HaveCount(2);

        var corrections = await db.MappingCorrections
            .OrderBy(c => c.CorrectedAt)
            .ToListAsync();

        corrections[0].OldSupplierItemCode.Should().Be("SUP-A");
        corrections[0].NewSupplierItemCode.Should().Be("SUP-B");
        corrections[1].OldSupplierItemCode.Should().Be("SUP-B");
        corrections[1].NewSupplierItemCode.Should().Be("SUP-C");
    }
}
