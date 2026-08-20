using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// One postgres:16 for the whole class rather than one per test.
///
/// <para>xUnit constructs a test-class instance PER TEST, so the repo's usual per-test
/// <c>IAsyncLifetime</c> starts and migrates a fresh container for every <c>[Fact]</c> — the load
/// that produced 76 orphan containers on 2026-07-25. A class fixture is constructed once. The
/// class stays in the <c>postgres-container</c> collection so it does not run alongside the other
/// container classes. Copied from <see cref="SupplierSuggestionPostgresFixture"/>.</para>
/// </summary>
public sealed class ItemMappingCaseParityPostgresFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_itemmap_case");

        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
            Timeout = 10,
        };

        // Testcontainers publishes the port on IPv4 only while this Windows host resolves
        // "localhost" to ::1 first; pin the loopback literal when the host already IS loopback.
        if (string.Equals(csb.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            csb.Host = "127.0.0.1";

        ConnectionString = csb.ConnectionString;

        await WaitUntilAcceptingTcpAsync();
    }

    private async Task WaitUntilAcceptingTcpAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var probe = new Npgsql.NpgsqlConnection(ConnectionString);
                await probe.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand("SELECT 1", probe);
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException(
            "Postgres testcontainer never accepted a TCP connection within 90s.", last);
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    public DbContextOptions<ProcuLinkDbContext> Options() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(ConnectionString!).Options;
}

/// <summary>
/// WP-14 — the item-code case rule proven against REAL Postgres.
///
/// <para>Why this class exists separately from <c>ItemMappingCaseParityTests</c>: that one runs on
/// the EF InMemory provider, where <c>.ToLower()</c> is executed by the CLR over an in-memory list.
/// It cannot fail for the reasons that matter here — whether Npgsql TRANSLATES the comparison at
/// all (an untranslatable expression throws, or silently evaluates client-side after pulling the
/// table), and whether the database's default collation is case-sensitive (it is: <c>=</c> on
/// <c>text</c> under <c>en_US.utf8</c> distinguishes case, which is precisely why the original
/// ordinal predicate lost mappings on production and not in any unit test).</para>
///
/// <para>Docker is unavailable on the dev box, so every fact here SKIPS locally and runs in CI.
/// That is expected: the InMemory class keeps the contract honest during development, and this
/// class is the one that can actually catch a translation or collation regression.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class ItemMappingCaseParityPostgresTests
    : IClassFixture<ItemMappingCaseParityPostgresFixture>
{
    private readonly ItemMappingCaseParityPostgresFixture _fx;

    public ItemMappingCaseParityPostgresTests(ItemMappingCaseParityPostgresFixture fx) => _fx = fx;

    private ProcuLinkDbContext NewDb() => new(_fx.Options());

    private const string Stored = "WIDGET-1";
    private const string Target = "SUP-9";

    private async Task<(Guid orgId, Guid supplierId)> SeedAsync(ProcuLinkDbContext db, bool withCatalog = true)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Case Org",
            Slug = $"case-{orgId:N}", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId, OrgId = orgId, Name = "Case Supplier", CreatedAt = now,
        });
        db.ItemMappings.Add(new ItemMapping
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            BuyerItemCode = Stored, SupplierItemCode = Target,
            Source = "manual", Confidence = 1f, CreatedAt = now, UpdatedAt = now,
        });
        if (withCatalog)
        {
            db.SupplierProducts.Add(new SupplierProduct
            {
                Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
                Code = Stored, IsActive = true, CreatedAt = now, UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    [DockerRequiredTheory]
    [InlineData("WIDGET-1")]
    [InlineData("widget-1")]
    [InlineData("Widget-1")]
    [InlineData("wIdGeT-1")]
    public async Task LearnedMappingAndCatalog_AgreeOnCase_OnRealPostgres(string queried)
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedAsync(db);

        var service = new ItemMappingService(db);

        // If the case-folded predicate failed to translate, EF throws here rather than quietly
        // returning the wrong answer — which is itself the assertion we want on this provider.
        var live    = await service.ResolveAsync(orgId, supplierId, queried, CancellationToken.None);
        var catalog = await OrderServiceShared.BuildCatalogLookupAsync(
            db, orgId, supplierId, new[] { queried.Trim() }, CancellationToken.None);

        (live is not null).Should().Be(catalog.ContainsKey(queried.Trim()),
            "on real Postgres (case-sensitive default collation) the learned-mapping resolver and "
            + "the catalog lookup must still agree on '{0}'", queried);
        live.Should().Be(Target);
    }

    [DockerRequiredFact]
    public async Task ResolveMany_IsTranslated_AndCaseInsensitive_OnRealPostgres()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedAsync(db);

        var many = await new ItemMappingService(db).ResolveManyAsync(
            orgId, supplierId, new[] { "widget-1", "WIDGET-1", "nope-1" }, CancellationToken.None);

        many["widget-1"].Should().Be(Target);
        many["WIDGET-1"].Should().Be(Target);
        many["nope-1"].Should().BeNull("an unknown code must still resolve to nothing");
    }

    [DockerRequiredFact]
    public async Task Upsert_WithDifferentCasing_UpdatesInPlace_OnRealPostgres()
    {
        // On Postgres the unique index (org_id, supplier_id, buyer_item_code) is case-SENSITIVE, so
        // a case-blind upsert would happily insert the twin and only the resolver would notice.
        await using var db = NewDb();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Upsert Org",
            Slug = $"upsert-{orgId:N}", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "S", CreatedAt = now });
        await db.SaveChangesAsync();

        var service = new ItemMappingService(db);
        await service.UpsertAsync(orgId, supplierId, "B-1", "SUP-A", MappingSource.Manual, confidence: null, CancellationToken.None);
        await service.UpsertAsync(orgId, supplierId, "b-1", "SUP-B", MappingSource.Manual, confidence: null, CancellationToken.None);

        var rows = await db.ItemMappings.AsNoTracking()
            .Where(m => m.OrgId == orgId && m.SupplierId == supplierId)
            .ToListAsync();

        rows.Should().HaveCount(1);
        rows[0].SupplierItemCode.Should().Be("SUP-B");
    }

    [DockerRequiredFact]
    public async Task CaseFolding_DoesNotBecomeSeparatorFolding_OnRealPostgres()
    {
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedAsync(db, withCatalog: false);

        var service = new ItemMappingService(db);
        (await service.ResolveAsync(orgId, supplierId, "WIDGET1", CancellationToken.None))
            .Should().BeNull("supplier item codes are a namespace the supplier controls — only "
                             + "manufacturer part numbers get separators stripped");
    }

    [DockerRequiredFact]
    public async Task ScopedCatalogFetch_TranslatesAndStaysBounded_OnRealPostgres()
    {
        // The scoped-fetch WHERE (lower() over five key columns + the legacy null-normalised arm)
        // must TRANSLATE on Npgsql — an untranslatable predicate throws here rather than quietly
        // fetching everything. CatalogLookupScopedFetchTests proves the equivalence contract on
        // InMemory; this fact proves the same query shape and its case rule against a real
        // postgres:16 with its case-sensitive default collation.
        await using var db = NewDb();
        var (orgId, supplierId) = await SeedAsync(db, withCatalog: false);
        var now = DateTime.UtcNow;

        var wanted = new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = "SCOPED-1", Barcode = "4006381333931",
            ManufacturerPartNumber = "LTQ2500-BK-BTK1", ManufacturerPartNumberNormalized = "LTQ2500BKBTK1",
            ExternalId = "EXT-77", IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        var unrelated = new SupplierProduct
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            Code = "UNRELATED-1", IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        db.SupplierProducts.AddRange(wanted, unrelated);
        await db.SaveChangesAsync();

        var catalog = await OrderServiceShared.BuildCatalogLookupAsync(
            db, orgId, supplierId,
            new[] { "scoped-1", "ltq2500 bk btk1", "LTQ2500BKBTK1" }, CancellationToken.None);

        catalog.Should().ContainKey("SCOPED-1", "a case-variant probe must still reach the row through SQL lower()");
        catalog["SCOPED-1"].Id.Should().Be(wanted.Id);
        catalog.Should().ContainKey("LTQ2500BKBTK1", "the normalised-MPN column is a fetch key");
        catalog.Values.Select(p => p.Id).Should().NotContain(unrelated.Id,
            "a row no probe key can reach must not be fetched — that boundedness is the entire fix");
    }
}
