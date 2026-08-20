using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The trigram indexes must actually SERVE the queries written against them — and making them do
/// so must not change one retrieved candidate.
///
/// <para><b>The defect this pins against.</b> The <c>AddCatalogTrigramIndexes</c> migration ships
/// GIN <c>gin_trgm_ops</c> indexes on <c>supplier_products.code</c> / <c>name</c>. Those indexes
/// accelerate the <c>%</c> / <c>LIKE</c> / <c>ILIKE</c> operator family only. As shipped, neither
/// consumer used one: the retrieval pass filtered on a computed
/// <c>similarity(...) &gt;= 0.1</c> (never index-served → one sequential scan of the supplier's
/// whole catalog PER UNRESOLVED LINE, inside the parse job), and the catalog search compared
/// <c>lower(code) LIKE '%…%'</c> while the index is on <c>code</c>, not <c>lower(code)</c>.</para>
///
/// <para><b>The trap in the obvious fix.</b> A bare <c>%</c> pre-filter honours
/// <c>pg_trgm.similarity_threshold</c>, whose default is 0.3 — but the code ranks at
/// <c>&gt;= 0.1</c>, so the "obvious" fix silently DROPS every candidate scoring in [0.1, 0.3).
/// Silently narrowing retrieval is the failure mode that matters more than the scan, so the
/// equivalence test seeds a candidate deliberately inside that band and proves it survives,
/// against an oracle that is the OLD semantics written as raw SQL — never the new query's own
/// output.</para>
///
/// <para><b>Plan-shape facts, not timings</b> (same reasoning as
/// <see cref="ItemMappingBatchResolvePerfPostgresTests"/>): the SQL the service actually executes
/// is captured by an interceptor and re-planned with EXPLAIN, so the index-usage claim is about
/// the production query, not a hand-written lookalike. <c>enable_seqscan = off</c> makes the tiny
/// fixture behave like the large catalog the index exists for: if the operator cannot use the
/// index, the plan still says Seq Scan and the test fails loudly.</para>
///
/// <para>Docker-gated; skips cleanly where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class CatalogTrigramIndexUsagePostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private readonly SqlCaptureInterceptor _capture = new();

    /// <summary>The floor the retrieval pass ranks at — mirrored from CatalogRetrievalService.</summary>
    private const double SimilarityFloor = 0.1;

    /// <summary>pg_trgm's built-in default for <c>similarity_threshold</c> — the narrowing trap.</summary>
    private const double DefaultTrigramThreshold = 0.3;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_trgm");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs)
            .AddInterceptors(_capture)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds through the production writer (<see cref="SupplierCatalogService.UpsertManyAsync"/>),
    /// exactly like <see cref="CatalogManufacturerPartRetrievalPostgresTests"/>, so what the
    /// queries under test see is what production stores.
    /// </summary>
    private async Task<(Guid OrgId, Guid SupplierId)> SeedAsync(
        params (string Code, string? Name, string? Barcode, string? Mpn)[] products)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id = orgId, Name = "Fabrikam", Slug = $"fabrikam-{orgId:N}",
                ClerkOrgId = $"org_{orgId:N}",
                CreatedAt = DateTime.UtcNow,
            });
            db.Suppliers.Add(new Supplier
            {
                Id = supplierId, OrgId = orgId, Name = "Fabrikam Distribution", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            await new SupplierCatalogService(db).UpsertManyAsync(
                orgId, supplierId,
                products.Select(p => new SupplierProduct
                {
                    Code = p.Code, Name = p.Name, Barcode = p.Barcode, ManufacturerPartNumber = p.Mpn,
                }),
                CancellationToken.None);
        }

        // Real planner statistics, like production — otherwise EXPLAIN plans a different table.
        await using var conn = new NpgsqlConnection(_databaseConnectionString);
        await conn.OpenAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE supplier_products;", conn);
        await analyze.ExecuteNonQueryAsync();

        return (orgId, supplierId);
    }

    // ── (a) Retrieval pass 2 — trigram ranking ───────────────────────────────

    // The fuzzy fixture. The query line matches NO code / barcode / manufacturer part exactly, so
    // passes 1 and 1b contribute nothing and every retrieved row is the trigram pass's answer.
    private const string FuzzyBuyerCode = "ZQV-99871";
    private const string FuzzyDescription = "hex bolt steel M8";

    private static readonly (string Code, string? Name, string? Barcode, string? Mpn)[] FuzzyCatalog =
    [
        ("BOLT-M8-HEX-STEEL", "Hex bolt steel M8 zinc", null, null),  // scores well above 0.3
        ("BLT-8-ST", "M8 hex nut", null, null),                       // the trap band: [0.1, 0.3)
        ("GASKET-RUBBER-70", "Rubber gasket 70mm", null, null),       // below the 0.1 floor
    ];

    /// <summary>
    /// The OLD semantics, written directly as SQL against pg_trgm — the independent oracle. This is
    /// deliberately NOT built from the service's query, so it cannot inherit a narrowing bug.
    /// </summary>
    private async Task<IReadOnlyList<(string Code, double Score)>> OracleAsync(
        Guid orgId, Guid supplierId, string queryText)
    {
        await using var conn = new NpgsqlConnection(_databaseConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT code,
                   GREATEST(similarity(code, @q),
                            CASE WHEN name IS NULL THEN 0 ELSE similarity(name, @q) END) AS score
            FROM supplier_products
            WHERE org_id = @org AND supplier_id = @supplier AND is_active
            ORDER BY score DESC, code
            """, conn);
        cmd.Parameters.AddWithValue("q", queryText);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("supplier", supplierId);

        var rows = new List<(string, double)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetDouble(1)));
        return rows;
    }

    [DockerRequiredFact]
    public async Task TrigramPass_ReturnsExactlyTheFloorSemantics_IncludingTheBandBelowTheDefaultThreshold()
    {
        var (orgId, supplierId) = await SeedAsync(FuzzyCatalog);

        var queryText = $"{FuzzyBuyerCode} {FuzzyDescription}";
        var oracle = await OracleAsync(orgId, supplierId, queryText);

        // ── Anti-vacuity: the fixture really exercises the trap ──────────────────────────
        // If pg_trgm's scoring ever drifts these out of their bands, fail HERE with the scores,
        // not downstream with a confusing set difference.
        var byCode = oracle.ToDictionary(r => r.Code, r => r.Score);
        Assert.True(byCode["BOLT-M8-HEX-STEEL"] >= DefaultTrigramThreshold,
            $"fixture drift: strong candidate scores {byCode["BOLT-M8-HEX-STEEL"]:F3}, expected >= {DefaultTrigramThreshold}");
        Assert.True(byCode["BLT-8-ST"] >= SimilarityFloor && byCode["BLT-8-ST"] < DefaultTrigramThreshold,
            $"fixture drift: trap-band candidate scores {byCode["BLT-8-ST"]:F3}, expected in [{SimilarityFloor}, {DefaultTrigramThreshold})");
        Assert.True(byCode["GASKET-RUBBER-70"] < SimilarityFloor,
            $"fixture drift: noise candidate scores {byCode["GASKET-RUBBER-70"]:F3}, expected < {SimilarityFloor}");

        var expected = oracle.Where(r => r.Score >= SimilarityFloor).Select(r => r.Code).ToList();

        await using var db = new ProcuLinkDbContext(_options!);
        var result = await new CatalogRetrievalService(db).RetrieveCandidatesAsync(
            orgId, supplierId,
            [new CatalogRetrievalQuery(1, FuzzyBuyerCode, FuzzyDescription)],
            perQueryTopK: 10, overallCap: 50, CancellationToken.None);

        Assert.NotNull(result);
        // Same candidates, same order (score desc, then code) — a % pre-filter left on the 0.3
        // default drops "BLT-8-ST" and fails here.
        Assert.Equal(expected, result!.Select(p => p.Code).ToList());
    }

    [DockerRequiredFact]
    public async Task TrigramPass_ExecutesTheIndexServedOperator_AndThePlanUsesTheTrigramIndex()
    {
        var (orgId, supplierId) = await SeedAsync(FuzzyCatalog);

        _capture.Clear();
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var result = await new CatalogRetrievalService(db).RetrieveCandidatesAsync(
                orgId, supplierId,
                [new CatalogRetrievalQuery(1, FuzzyBuyerCode, FuzzyDescription)],
                perQueryTopK: 10, overallCap: 50, CancellationToken.None);
            Assert.NotNull(result); // null = fell back; the captured SQL would be meaningless
        }

        // The trigram SELECT the service really executed — identified by its similarity() ranking.
        var trigram = _capture.Commands.SingleOrDefault(c => c.Text.Contains("similarity("));
        Assert.NotNull(trigram);

        // Shape pin: the WHERE must carry the %-operator family (index-served), not filter on the
        // computed similarity() alone (never index-served).
        Assert.Contains(" % ", trigram!.Text);

        // Plan pin on the CAPTURED command, replayed with its own parameters. enable_seqscan=off
        // stands in for the large catalog: if % is missing (or the operator class cannot serve
        // it), Postgres still answers "Seq Scan" and this fails.
        var plan = await ExplainCapturedAsync(trigram);
        Assert.True(plan.Contains("ix_supplier_products_code_trgm"),
            $"trigram index not in plan:\n{plan}");
    }

    // ── (b) Catalog search ───────────────────────────────────────────────────

    private static readonly (string Code, string? Name, string? Barcode, string? Mpn)[] SearchCatalog =
    [
        ("ALPHA-Bolt-9", null, null, null),
        ("X1", "SteelWidget Pro", null, null),
        ("X2", null, "4750123456789", null),
        ("X3", null, null, "LTQ2500-BK"),
        ("AB%CD", null, null, null),   // literal % in the code — wildcard-escape pin
        ("AB_CD", null, null, null),   // literal _ in the code
        ("ABXCD", null, null, null),   // the row a leaked wildcard would wrongly match
    ];

    [DockerRequiredFact]
    public async Task CatalogSearch_KeepsCaseInsensitiveSubstringSemantics_AndWildcardsStayLiteral()
    {
        var (orgId, supplierId) = await SeedAsync(SearchCatalog);

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new SupplierCatalogService(db);

        async Task<List<string>> Search(string q) =>
            (await svc.ListAsync(orgId, supplierId, q, take: 50, CancellationToken.None))
            .Select(p => p.Code).ToList();

        // Case-insensitive substring, both directions of case difference, on every searched column.
        Assert.Equal(["ALPHA-Bolt-9"], await Search("alpha-bo"));
        Assert.Equal(["ALPHA-Bolt-9"], await Search("ALPHA-BO"));
        Assert.Equal(["X1"], await Search("steelwid"));
        Assert.Equal(["X2"], await Search("0123456"));
        Assert.Equal(["X3"], await Search("ltq2500-b"));

        // The old ToLower().Contains treated % and _ as LITERAL text. ILIKE must too — an
        // unescaped pattern would match ABXCD on both of these and fail.
        Assert.Equal(["AB%CD"], await Search("b%c"));
        Assert.Equal(["AB_CD"], await Search("b_c"));

        // Cross-tenant: another supplier's catalog stays invisible whatever the operator.
        var (otherOrg, otherSupplier) = await SeedAsync(("ALPHA-Bolt-9", null, null, null));
        Assert.Empty(await svc.ListAsync(otherOrg, supplierId, "alpha", 50, CancellationToken.None));
        _ = otherSupplier;
    }

    [DockerRequiredFact]
    public async Task CatalogSearch_TranslatesToIlike_NotLowerLike()
    {
        var (orgId, supplierId) = await SeedAsync(SearchCatalog);

        _capture.Clear();
        await using var db = new ProcuLinkDbContext(_options!);
        _ = await new SupplierCatalogService(db).ListAsync(orgId, supplierId, "alpha", 50, CancellationToken.None);

        var search = _capture.Commands.SingleOrDefault(c => c.Text.Contains("supplier_products") && c.Text.Contains("WHERE"));
        Assert.NotNull(search);

        // ILIKE is what the gin_trgm_ops indexes on code/name can serve; lower(col) LIKE is what
        // they cannot (the index is on code, not lower(code)). The OR still carries barcode and
        // manufacturer_part_number, which have no trigram index — so no full plan pin here, only
        // the operator shape that stops the indexed columns being wrapped away from their index.
        Assert.Contains("ILIKE", search!.Text);
        Assert.DoesNotContain("lower(", search.Text);
    }

    // ── EXPLAIN replay of a captured command ─────────────────────────────────

    /// <summary>
    /// Replays the captured production command as EXPLAIN and returns the plan. The claim this
    /// exists to prove is "the %-operator family CAN be served by the shipped trgm indexes" —
    /// not "the planner always picks them": at this fixture's scale the (supplier_id) btree is
    /// estimated cheaper, exactly the cost trade-off the planner is entitled to make. So the
    /// replay transaction (a) drops every competing non-trigram index — ROLLED BACK, nothing
    /// survives the call — and (b) turns off seq and plain index scans, leaving Postgres one
    /// honest way to answer: a bitmap scan over the trigram indexes if the operator is servable,
    /// or a (penalised, but printed) Seq Scan if it is not. The pre-fix similarity()-only
    /// predicate plans as that Seq Scan; the % pre-filter plans as the bitmap.
    /// </summary>
    private async Task<string> ExplainCapturedAsync(CapturedCommand cmd)
    {
        await using var conn = new NpgsqlConnection(_databaseConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setup = new NpgsqlCommand(
            """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN SELECT i.indexname FROM pg_indexes i
                         WHERE i.schemaname = 'public' AND i.tablename = 'supplier_products'
                           AND i.indexname NOT ILIKE '%trgm%'
                           AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conname = i.indexname)
                LOOP EXECUTE format('DROP INDEX %I', r.indexname); END LOOP;
            END$$;
            -- The service's own session state: threshold pinned strictly below the 0.1 floor so
            -- % admits everything the similarity() floor admits.
            SET LOCAL pg_trgm.similarity_threshold = 0.05;
            SET LOCAL enable_seqscan = off;
            SET LOCAL enable_indexscan = off;
            """, conn, tx))
            await setup.ExecuteNonQueryAsync();

        var sb = new StringBuilder();
        await using (var explain = new NpgsqlCommand($"EXPLAIN {cmd.Text}", conn, tx))
        {
            foreach (var (name, value) in cmd.Parameters)
                explain.Parameters.AddWithValue(name, value ?? DBNull.Value);

            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                sb.AppendLine(reader.GetString(0));
        }

        await tx.RollbackAsync();
        return sb.ToString();
    }

    private sealed record CapturedCommand(string Text, IReadOnlyList<(string Name, object? Value)> Parameters);

    /// <summary>
    /// Records every command the contexts under test execute, with parameter values, so shape and
    /// plan assertions run against the PRODUCTION SQL — not a hand-written approximation that
    /// could drift from it (the self-certifying-oracle trap).
    /// </summary>
    private sealed class SqlCaptureInterceptor : DbCommandInterceptor
    {
        private readonly List<CapturedCommand> _commands = [];

        public IReadOnlyList<CapturedCommand> Commands
        {
            get { lock (_commands) return _commands.ToList(); }
        }

        public void Clear() { lock (_commands) _commands.Clear(); }

        private void Record(DbCommand command)
        {
            var ps = command.Parameters.Cast<DbParameter>()
                .Select(p => (p.ParameterName, (object?)p.Value))
                .ToList();
            lock (_commands) _commands.Add(new CapturedCommand(command.CommandText, ps));
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
    }
}
