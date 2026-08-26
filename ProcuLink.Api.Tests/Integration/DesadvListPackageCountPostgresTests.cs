using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Pins <c>DesadvService.ListAsync</c>'s package count on REAL Postgres.
///
/// <para><b>Why this needs a database.</b> The count is a correlated subquery — a
/// <c>_db.AsnPackages.Count(...)</c> written inside the ASN projection — and whether EF can
/// TRANSLATE that is a question only a real provider answers. An untranslatable projection throws
/// at request time, not at build time, so a green build proves nothing about it. Before 2026-08-26
/// the ASN list had no package count at all and <c>asn_packages</c> had no reader anywhere in this
/// repo; the reader arrived with the deletion of <c>GET /api/asns/{id}</c>, which is what
/// <c>OrphanGuardTests</c> then required.</para>
///
/// <para>The second case is the one that would go quietly wrong: the count is filtered by
/// <c>OrganisationId</c> as well as by ASN, and without that predicate a package row belonging to
/// another tenant would be counted into this tenant's list.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class DesadvListPackageCountPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_asn_count");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private DesadvService NewService(ProcuLinkDbContext db) =>
        new(db, new Mock<IFileStorageService>().Object);

    private static Organisation Org(Guid id, string tag) => new()
    {
        Id            = id,
        ClerkOrgId    = $"org_{tag}_{id:N}",
        Name          = $"ASN Org {tag}",
        Slug          = $"asn-{tag}-{id:N}",
        Plan          = "operations",
        AccountStatus = "active",
        CreatedAt     = DateTime.UtcNow,
    };

    private static AdvanceShippingNoticeEntity Asn(Guid id, Guid orgId, string shipmentId, DateTime createdAt) => new()
    {
        Id             = id,
        OrganisationId = orgId,
        ShipmentId     = shipmentId,
        DespatchDate   = new DateOnly(2026, 8, 26),
        Status         = "received",
        SourceFileName = $"{shipmentId}.edi",
        CreatedAt      = createdAt,
        UpdatedAt      = createdAt,
    };

    private static AsnPackageEntity Package(Guid asnId, Guid orgId, string packageId) => new()
    {
        Id                      = Guid.NewGuid(),
        AdvanceShippingNoticeId = asnId,
        OrganisationId          = orgId,
        PackageId               = packageId,
    };

    [DockerRequiredFact]
    public async Task Asn_list_reports_the_package_count_for_each_notice()
    {
        var orgId    = Guid.NewGuid();
        var withTwo  = Guid.NewGuid();
        var withNone = Guid.NewGuid();
        var now      = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(Org(orgId, "count"));
            await db.SaveChangesAsync();

            // Newest first, so the two-package notice leads the list.
            db.AdvanceShippingNotices.Add(Asn(withNone, orgId, "SHIP-EMPTY", now.AddMinutes(-5)));
            db.AdvanceShippingNotices.Add(Asn(withTwo,  orgId, "SHIP-TWO",   now));
            await db.SaveChangesAsync();

            db.AsnPackages.Add(Package(withTwo, orgId, "PKG-1"));
            db.AsnPackages.Add(Package(withTwo, orgId, "PKG-2"));
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var list = await NewService(db).ListAsync(orgId, CancellationToken.None);

            Assert.Equal(2, list.Count);

            var two = Assert.Single(list, a => a.Id == withTwo);
            Assert.Equal(2, two.PackageCount);
            Assert.Equal("SHIP-TWO", two.ShipmentId);

            // An ASN with no packages must report 0, not be dropped from the list by the join.
            var none = Assert.Single(list, a => a.Id == withNone);
            Assert.Equal(0, none.PackageCount);
        }
    }

    [DockerRequiredFact]
    public async Task Package_count_ignores_another_tenants_package_row()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var asnA = Guid.NewGuid();
        var now  = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(Org(orgA, "a"));
            db.Organisations.Add(Org(orgB, "b"));
            await db.SaveChangesAsync();

            db.AdvanceShippingNotices.Add(Asn(asnA, orgA, "SHIP-A", now));
            await db.SaveChangesAsync();

            // One legitimate package, and one carrying org B's tenant id while pointing at org A's
            // notice. Only the ASN-id predicate would count both; the org predicate is what makes
            // the answer 1. This is the shape the org scope exists to refuse.
            db.AsnPackages.Add(Package(asnA, orgA, "PKG-A"));
            db.AsnPackages.Add(Package(asnA, orgB, "PKG-LEAKED"));
            await db.SaveChangesAsync();
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var list = await NewService(db).ListAsync(orgA, CancellationToken.None);

            var a = Assert.Single(list);
            Assert.Equal(1, a.PackageCount);
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            // Org B owns no notice, so it sees no rows at all — the leaked package row does not
            // conjure one into its list either.
            var list = await NewService(db).ListAsync(orgB, CancellationToken.None);
            Assert.Empty(list);
        }
    }
}
