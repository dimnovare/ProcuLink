using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// One postgres:16 for the whole class rather than one per test.
///
/// <para>xUnit constructs a test-class instance PER TEST, so the repo's usual per-test
/// <c>IAsyncLifetime</c> starts and migrates a fresh container for every <c>[Fact]</c> — the exact
/// load that produced 76 orphan containers and a wave of "Npgsql: Timeout during reading attempt"
/// failures on 2026-07-25. A class fixture is constructed once. The class stays in the
/// <c>postgres-container</c> collection so it still does not run alongside the other container
/// classes.</para>
/// </summary>
public sealed class SupplierSuggestionPostgresFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_supplier_detect");

        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
            Timeout = 10,
        };

        // Kept as a no-op guard. PostgresContainerFixture already pins the loopback literal on the
        // one shared container, for the reason this class first discovered: `docker inspect` shows
        // Testcontainers publishing the port on IPv4 only ("0.0.0.0:55267->5432/tcp", no "[::]"
        // mapping), and on this Windows host Dns.GetHostAddresses("localhost") returns ::1 first.
        // Applied only when the host already IS loopback, so a remote DOCKER_HOST keeps working.
        if (string.Equals(csb.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            csb.Host = "127.0.0.1";

        ConnectionString = csb.ConnectionString;

        await WaitUntilAcceptingTcpAsync();

        // The real AddSupplierAutoDetect migration still has to have applied — the new table, the
        // four supplier identity columns, the two sender-domain columns, the (org_id, code) index
        // and the partial unique index all have to exist for anything below to run at all. It runs
        // once, against the fixture's template database, and this database is a clone of it.
    }

    /// <summary>
    /// Retries the first connection instead of letting the migration below be the thing that
    /// discovers the container is not answering yet.
    ///
    /// <para>Testcontainers' PostgreSql wait strategy is <c>pg_isready</c>, which answers over the
    /// UNIX socket and can go green before the postmaster is usable over TCP; the docker port proxy
    /// accepts the connection anyway and nothing replies, so EF dies with "Timeout during reading
    /// attempt" — a message this repo has (correctly) learned to read as container contention. On
    /// 2026-07-27 that message appeared with 30+ Postgres containers alive from a parallel session,
    /// and the class failed in <c>InitializeAsync</c>, which reports as every test in the class
    /// erroring rather than as one slow start. This loop does not make a starved daemon work; it
    /// gives a briefly-unready container time to answer, and turns the starved case into one
    /// explicit error instead of a stack that points at the migration.</para>
    /// </summary>
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
/// Supplier auto-detect against real Postgres: the migration, the partial unique index, the
/// and the cross-supplier catalog probe (whose index path InMemory cannot tell apart from a broken
/// query). (The <c>assign-supplier</c> decision-recording facts retired with that endpoint in plan
/// 13b; the file-backed-parse fact went with the order pipeline's parse, 2026-11-13.)
/// </summary>
[Collection("postgres-container")]
public sealed class SupplierSuggestionPostgresTests : IClassFixture<SupplierSuggestionPostgresFixture>
{
    private readonly SupplierSuggestionPostgresFixture _fx;

    public SupplierSuggestionPostgresTests(SupplierSuggestionPostgresFixture fx) => _fx = fx;

    private DbContextOptions<ProcuLinkDbContext> Options() => _fx.Options();

    // ── Seeding ───────────────────────────────────────────────────────────────

    private async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string tag)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            // Unique per org: the container is shared across this class's tests, so a default
            // ClerkOrgId would collide on IX_organisations_clerk_org_id from the second seed on.
            ClerkOrgId = $"org_{tag}_{orgId:N}",
            Name = $"Org {tag}",
            Slug = $"org-{tag}-{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static Supplier AddSupplier(
        ProcuLinkDbContext db, Guid orgId, string name,
        string? vat = null, string? domain = null)
    {
        var s = new Supplier
        {
            Id = Guid.NewGuid(), OrgId = orgId, Name = name, CreatedAt = DateTime.UtcNow,
            VatNumber = vat, PrimaryDomain = domain,
        };
        db.Suppliers.Add(s);
        return s;
    }

    private static PurchaseOrderEntity AddOrder(
        ProcuLinkDbContext db, Guid orgId, string status = OrderStatusConstants.Unrouted,
        Guid? supplierId = null, string? senderDomain = null)
    {
        var o = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            PoNumber = $"PO-{Guid.NewGuid():N}"[..12], Currency = "EUR", Status = status,
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            InboundSenderDomain = senderDomain,
            InboundSenderDomainCapturedAt = senderDomain is null ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.PurchaseOrders.Add(o);
        return o;
    }

    private static OrderSupplierSuggestion AddSuggestion(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, Guid supplierId, int rank, double score)
    {
        var row = new OrderSupplierSuggestion
        {
            Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId, SupplierId = supplierId,
            Rank = rank, Score = score, Decision = null,
            SignalsJson = """[{"Signal":"supplier_name","Contribution":0.2,"Detail":"the supplier name on the document matches theirs"}]""",
            ModelVersion = SupplierSuggestionScoring.ModelVersion, CreatedAt = DateTime.UtcNow,
        };
        db.OrderSupplierSuggestions.Add(row);
        return row;
    }

    private SupplierSuggestionService NewService(ProcuLinkDbContext db) =>
        new(db, new SchemaFingerprintService(db, NullLogger<SchemaFingerprintService>.Instance),
            NullLogger<SupplierSuggestionService>.Instance);

    // ── Migration + schema ────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task Migration_roundTrips_theSuggestionRowAndTheNewSupplierColumns()
    {
        await using var db = new ProcuLinkDbContext(Options());
        var orgId = await SeedOrgAsync(db, "roundtrip");

        var supplier = AddSupplier(db, orgId, "Acme GmbH", vat: "EE101234567", domain: "acme.example");
        supplier.RegistrationNumber = "12345678";
        supplier.EdiCode = "1111111111116";
        var order = AddOrder(db, orgId, senderDomain: "acme.example");
        var row = AddSuggestion(db, orgId, order.Id, supplier.Id, 1, 0.6125);
        await db.SaveChangesAsync();

        await using var verify = new ProcuLinkDbContext(Options());

        var readSupplier = await verify.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplier.Id);
        Assert.Equal("EE101234567", readSupplier.VatNumber);
        Assert.Equal("12345678", readSupplier.RegistrationNumber);
        Assert.Equal("1111111111116", readSupplier.EdiCode);
        Assert.Equal("acme.example", readSupplier.PrimaryDomain);

        var readOrder = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal("acme.example", readOrder.InboundSenderDomain);
        Assert.NotNull(readOrder.InboundSenderDomainCapturedAt);

        var readRow = await verify.OrderSupplierSuggestions.AsNoTracking().SingleAsync(s => s.Id == row.Id);
        Assert.Equal(0.6125, readRow.Score, 4);
        Assert.Null(readRow.Decision);
        Assert.Contains("supplier_name", readRow.SignalsJson);
    }

    [DockerRequiredFact]
    public async Task LiveIdempotencyIndex_forbidsTwoUndecidedRowsForOneSupplier_butAllowsDecidedHistory()
    {
        await using var db = new ProcuLinkDbContext(Options());
        var orgId = await SeedOrgAsync(db, "idem");
        var supplier = AddSupplier(db, orgId, "Acme GmbH");
        var order = AddOrder(db, orgId);
        AddSuggestion(db, orgId, order.Id, supplier.Id, 1, 0.5);
        await db.SaveChangesAsync();

        // Two LIVE suggestions naming the same supplier for the same order would show an operator
        // the same candidate twice. The partial unique index is what forbids it.
        await using (var clash = new ProcuLinkDbContext(Options()))
        {
            AddSuggestion(clash, orgId, order.Id, supplier.Id, 2, 0.4);
            await Assert.ThrowsAsync<DbUpdateException>(() => clash.SaveChangesAsync());
        }

        // Decided rows are history and may repeat freely — the same supplier can legitimately be
        // superseded more than once over an order's life.
        await using var history = new ProcuLinkDbContext(Options());
        for (var i = 0; i < 2; i++)
            history.OrderSupplierSuggestions.Add(new OrderSupplierSuggestion
            {
                Id = Guid.NewGuid(), OrgId = orgId, OrderId = order.Id, SupplierId = supplier.Id,
                Rank = 1, Score = 0.5, Decision = OrderSupplierSuggestionDecision.Superseded,
                DecidedBy = "system", DecidedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            });
        await history.SaveChangesAsync();

        Assert.Equal(2, await history.OrderSupplierSuggestions.CountAsync(
            s => s.OrderId == order.Id && s.Decision == OrderSupplierSuggestionDecision.Superseded));
    }

    [DockerRequiredFact]
    public async Task RecordAsync_reScoringTheSameSupplier_refreshesInPlace_andDoesNotViolateTheIndex()
    {
        // Proves the claim the RecordAsync comment makes: refreshing in place rather than
        // supersede-then-insert is what keeps a re-score from asking Postgres to hold two undecided
        // rows for one (org, order, supplier) inside a single SaveChanges.
        await using var db = new ProcuLinkDbContext(Options());
        var orgId = await SeedOrgAsync(db, "rescore");
        var supplier = AddSupplier(db, orgId, "Acme GmbH");
        var order = AddOrder(db, orgId);
        await db.SaveChangesAsync();

        var service = NewService(db);
        var first = new SupplierSuggestion(supplier.Id, "Acme GmbH", 1, 0.40, "r",
            new[] { new SupplierSignalContribution(SupplierSignalKind.Name, 0.40, "name") });
        await service.RecordAsync(orgId, order.Id, new[] { first }, default);
        await db.SaveChangesAsync();

        var second = new SupplierSuggestion(supplier.Id, "Acme GmbH", 1, 0.75, "r",
            new[] { new SupplierSignalContribution(SupplierSignalKind.Identity, 0.75, "vat") });
        await service.RecordAsync(orgId, order.Id, new[] { second }, default);
        await db.SaveChangesAsync();   // would throw 23505 if it inserted a second live row

        await using var verify = new ProcuLinkDbContext(Options());
        var rows = await verify.OrderSupplierSuggestions.AsNoTracking()
            .Where(s => s.OrderId == order.Id).ToListAsync();

        var live = Assert.Single(rows);
        Assert.Null(live.Decision);
        Assert.Equal(0.75, live.Score, 3);
        Assert.Contains("vat", live.SignalsJson);
    }

    // ── Cross-supplier catalog probe (the new access pattern) ─────────────────

    [DockerRequiredFact]
    public async Task CatalogOverlap_probesAcrossSuppliers_onRealPostgres()
    {
        // Every other catalog read is scoped to a known (org, supplier). This one leaves the
        // supplier unconstrained, which is a genuinely different index path — and one InMemory
        // would happily satisfy even if the SQL were wrong.
        await using var db = new ProcuLinkDbContext(Options());
        var orgId = await SeedOrgAsync(db, "catalog");
        var most = AddSupplier(db, orgId, "Carries Most");
        var few  = AddSupplier(db, orgId, "Carries Few");
        var otherOrgId = await SeedOrgAsync(db, "catalog-other");
        var foreign = AddSupplier(db, otherOrgId, "Another Tenant");

        void Catalog(Guid supplierId, Guid owner, params string[] codes)
        {
            foreach (var code in codes)
                db.SupplierProducts.Add(new SupplierProduct
                {
                    Id = Guid.NewGuid(), OrgId = owner, SupplierId = supplierId, Code = code,
                    IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
        }

        Catalog(most.Id, orgId, "AAA", "BBB", "CCC");
        Catalog(few.Id, orgId, "AAA");
        Catalog(foreign.Id, otherOrgId, "AAA", "BBB", "CCC", "DDD");   // must never surface
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            new SupplierSuggestionInput(orgId, Guid.NewGuid(),
                LineCodes: new[] { "AAA", "BBB", "CCC", "DDD" }),
            default);

        // "Carries Most" clears the floor at 3 of 4 codes (0.25 × 0.75 = 0.1875). "Carries Few"
        // matches ONE code out of four — 0.0625, under MinimumScore — and is deliberately dropped:
        // one shared code is the kind of coincidence that makes a shortlist worse, not better.
        var only = Assert.Single(result);
        Assert.Equal(most.Id, only.SupplierId);
        Assert.DoesNotContain(result, r => r.SupplierId == few.Id);
        Assert.DoesNotContain(result, r => r.SupplierId == foreign.Id);
        Assert.Contains("3 of 4", only.Signals.Single(s => s.Signal == SupplierSignalKind.CatalogOverlap).Detail);
    }

    [DockerRequiredFact]
    public async Task SenderDomainHistory_groupsBySupplier_onRealPostgres()
    {
        await using var db = new ProcuLinkDbContext(Options());
        var orgId = await SeedOrgAsync(db, "history");
        var usual = AddSupplier(db, orgId, "Usual Destination");
        var rare  = AddSupplier(db, orgId, "Rare Destination");
        await db.SaveChangesAsync();

        for (var i = 0; i < 3; i++) AddOrder(db, orgId, "delivered", usual.Id, "acme.example");
        AddOrder(db, orgId, "delivered", rare.Id, "acme.example");
        AddOrder(db, orgId, "delivered", rare.Id, "elsewhere.com");   // different domain, must not count
        await db.SaveChangesAsync();

        var result = await NewService(db).SuggestAsync(
            new SupplierSuggestionInput(orgId, Guid.NewGuid(), InboundSenderDomain: "acme.example"), default);

        Assert.Equal(usual.Id, result[0].SupplierId);
        Assert.Contains("3 earlier orders", result[0].Signals.Single(s => s.Signal == SupplierSignalKind.SenderDomainHistory).Detail);
    }
}
