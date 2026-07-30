using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-14 — the query-plan claim, MEASURED rather than asserted.
///
/// <para><b>What was wrong with the original claim.</b> The comment in
/// <c>ItemMappingService</c> said "the exact-match term is OR'd in FIRST so the planner keeps the
/// fully-indexed path available". That is false: Postgres normalises OR ordering, so writing the
/// <c>lower()</c> term first produces a byte-identical plan, and the planner abandons the unique
/// code index entirely in both spellings. There was also no EXPLAIN artefact anywhere in the PR —
/// the cost was stated from memory and under-reported (~55x buffers claimed, ~126x measured).</para>
///
/// <para><b>What running it revealed.</b> The surviving half of the original claim — "this is NOT a
/// sequential scan of the table" — is ALSO false. At 5 250 rows Postgres picks a plain
/// <c>Seq Scan on item_mappings</c>; the 315k-row measurement got a bitmap heap scan. Which plan
/// you get depends on table size and statistics, and nothing in the code pins it. What survives is
/// narrower and worth stating exactly: the PREDICATE is org+supplier scoped, so the rows RETURNED
/// are always one supplier's — a tenancy fact, not a cost bound on pages read.</para>
///
/// <para><b>What this test pins, and why it is not a microbenchmark.</b> Wall-clock thresholds are
/// flaky under Testcontainers contention with sibling sessions, so nothing here asserts on time or
/// on a buffer threshold. It pins two plan-shape FACTS — the exact-match predicate uses the unique
/// code index and the case-folded one does not — plus the tenancy property, separately, so
/// "seq scan" is never misread as "cross-tenant". A future functional index that restores the
/// index path makes the first assertion fail loudly, which is exactly when this comment needs
/// rewriting.</para>
///
/// <para>It also WRITES the EXPLAIN (ANALYZE, BUFFERS) output for both the pre-WP-14 and the WP-14
/// predicate to <c>docs/ops/wp14-item-mapping-explain.md</c>, so the plan is an artefact in the
/// repo rather than a number in a PR comment nobody can re-derive.</para>
///
/// <para>Docker-gated like every other <c>*PostgresTests</c>.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class ItemMappingBatchResolvePerfPostgresTests : IAsyncLifetime
{
    private const int SmallSupplierCodes = 250;
    private const int LargeSupplierCodes = 5_000;

    private readonly ITestOutputHelper _output;
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private string? _connectionString;

    private Guid _orgId;
    private Guid _smallSupplier;
    private Guid _largeSupplier;

    public ItemMappingBatchResolvePerfPostgresTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_perf_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        _connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();

        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        _orgId         = Guid.NewGuid();
        _smallSupplier = Guid.NewGuid();
        _largeSupplier = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);

        db.Organisations.Add(new Organisation
        {
            Id = _orgId, Name = "Perf Co", ClerkOrgId = $"org_{Guid.NewGuid():N}",
            Slug = $"perf-{Guid.NewGuid():N}"[..20], CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = _smallSupplier, OrgId = _orgId, Name = "Small", Code = "S", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = _largeSupplier, OrgId = _orgId, Name = "Large", Code = "L", CreatedAt = now,
        });
        await db.SaveChangesAsync();

        void AddCodes(Guid supplierId, int count, string prefix)
        {
            for (var i = 0; i < count; i++)
            {
                db.ItemMappings.Add(new ItemMapping
                {
                    Id               = Guid.NewGuid(),
                    OrgId            = _orgId,
                    SupplierId       = supplierId,
                    BuyerItemCode    = $"{prefix}-{i:D6}",
                    SupplierItemCode = $"SUP-{i:D6}",
                    Source           = "manual",
                    Confidence       = 1f,
                    CreatedAt        = now,
                    UpdatedAt        = now,
                });
            }
        }

        AddCodes(_smallSupplier, SmallSupplierCodes, "SMALL");
        AddCodes(_largeSupplier, LargeSupplierCodes, "LARGE");
        await db.SaveChangesAsync();

        // ANALYZE so the planner works from real statistics rather than defaults — otherwise the
        // plan this captures is not the plan production runs.
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE item_mappings;", conn);
        await analyze.ExecuteNonQueryAsync();
    }

    // ── The measurements ─────────────────────────────────────────────────────

    /// <summary>The predicate WP-14 ships: exact OR case-folded, both supplier-scoped.</summary>
    private const string Wp14Predicate = """
        SELECT id, buyer_item_code, supplier_item_code, updated_at
        FROM item_mappings
        WHERE org_id = @org AND supplier_id = @supplier
          AND (buyer_item_code = @code OR lower(buyer_item_code) = @folded)
        """;

    /// <summary>The pre-WP-14 predicate, kept for the side-by-side artefact.</summary>
    private const string PreWp14Predicate = """
        SELECT id, buyer_item_code, supplier_item_code, updated_at
        FROM item_mappings
        WHERE org_id = @org AND supplier_id = @supplier
          AND buyer_item_code = @code
        """;

    /// <summary>The same WP-14 predicate with the OR terms written the other way round.</summary>
    private const string Wp14PredicateOrderSwapped = """
        SELECT id, buyer_item_code, supplier_item_code, updated_at
        FROM item_mappings
        WHERE org_id = @org AND supplier_id = @supplier
          AND (lower(buyer_item_code) = @folded OR buyer_item_code = @code)
        """;

    private async Task<string> ExplainAsync(string sql, Guid supplierId, string code)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS) {sql}", conn);
        cmd.Parameters.AddWithValue("org", _orgId);
        cmd.Parameters.AddWithValue("supplier", supplierId);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("folded", code.ToLowerInvariant());

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sb.AppendLine(reader.GetString(0));
        return sb.ToString();
    }

    private static int SharedBuffersRead(string plan)
    {
        // "Buffers: shared hit=503" / "shared hit=4 read=2" — sum hit + read, which is the number
        // the cost discussion is actually about.
        var total = 0;
        foreach (Match m in Regex.Matches(plan, @"shared (?:hit=(\d+))?\s*(?:read=(\d+))?"))
        {
            if (m.Groups[1].Success) total += int.Parse(m.Groups[1].Value);
            if (m.Groups[2].Success) total += int.Parse(m.Groups[2].Value);
        }
        return total;
    }

    [DockerRequiredFact]
    public async Task TheCaseFoldedLookup_AbandonsTheUniqueCodeIndex()
    {
        // THE accepted cost, pinned as a plan-shape fact rather than a timing. The exact-match
        // predicate is served by the unique (org_id, supplier_id, buyer_item_code) index; adding the
        // lower() term makes that index unusable on its third column and the planner drops it.
        // Asserting the DIFFERENCE between the two plans (not "the new one is slow") means a future
        // change that restores the index path — a functional index, say — makes this fail loudly and
        // get the comment updated, which is the point.
        const string UniqueCodeIndex = "IX_item_mappings_org_id_supplier_id_buyer_item_code";

        var preWp14 = await ExplainAsync(PreWp14Predicate, _largeSupplier, "LARGE-000100");
        var wp14    = await ExplainAsync(Wp14Predicate, _largeSupplier, "LARGE-000100");

        _output.WriteLine($"pre-WP-14 buffers: {SharedBuffersRead(preWp14)}");
        _output.WriteLine($"WP-14 buffers:     {SharedBuffersRead(wp14)}");

        preWp14.Should().Contain(UniqueCodeIndex,
            "the exact-match predicate is fully served by the unique code index — if it is not, this "
            + "test is comparing against the wrong baseline");

        wp14.Should().NotContain(UniqueCodeIndex,
            "the case-folded predicate cannot use that index on its third column. This is the cost "
            + "being accepted; no comment may claim the index path survives");
    }

    [DockerRequiredFact]
    public async Task TheCaseFoldedLookup_StillReturnsOnlyTheScopedSuppliersRow()
    {
        // The plan may be a SEQ SCAN of the whole table (it is, at this table size — see the
        // artefact). That is a COST fact, not a tenancy one: the WHERE clause is org+supplier scoped
        // on every path, so the rows RETURNED are always one supplier's. Pinned separately from the
        // plan shape so nobody reads "seq scan" as "cross-tenant".
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(Wp14Predicate, conn);
        cmd.Parameters.AddWithValue("org", _orgId);
        cmd.Parameters.AddWithValue("supplier", _smallSupplier);
        cmd.Parameters.AddWithValue("code", "LARGE-000100");     // the OTHER supplier's code
        cmd.Parameters.AddWithValue("folded", "large-000100");

        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeFalse(
            "a code belonging to another supplier in the SAME organisation must not resolve, "
            + "whatever plan the database picks");
    }

    [DockerRequiredFact]
    public async Task WritingTheOrTermsEitherWayRound_ProducesTheSamePlan()
    {
        // Directly refutes "the exact-match term is OR'd in FIRST so the planner keeps the
        // fully-indexed path available". If a future edit reintroduces that belief, this fails.
        var asShipped = await ExplainAsync(Wp14Predicate, _largeSupplier, "LARGE-000100");
        var swapped   = await ExplainAsync(Wp14PredicateOrderSwapped, _largeSupplier, "LARGE-000100");

        // Compare the plan NODES and the work they do — not the measurements (costs, timings and
        // buffer counts vary run to run) and not the printed `Filter:` text, which echoes the
        // source's term order verbatim. The filter's WORDING is the one thing that does change;
        // that is a formatting artefact of EXPLAIN, not a different plan, and mistaking it for one
        // is how the original claim got made.
        static string Shape(string plan) => string.Join('\n', plan
            .Split('\n')
            .Where(l => !l.Contains("Buffers:")
                        && !l.Contains("Filter:")
                        && !l.Contains("Planning")
                        && !l.Contains("Execution"))
            .Select(l => Regex.Replace(l, @"\((?:cost|actual)[^)]*\)", string.Empty))
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0));

        Shape(swapped).Should().Be(Shape(asShipped),
            "the term order in the source has no effect on the plan node chosen or the rows it "
            + "discards, so no comment may claim writing the exact-match term first preserves an "
            + "index path");

        // …and both really do carry both terms, so the comparison above is not passing because the
        // predicates collapsed to something trivial.
        foreach (var plan in new[] { asShipped, swapped })
        {
            plan.Should().Contain("buyer_item_code = 'LARGE-000100'");
            plan.Should().Contain("lower(buyer_item_code) = 'large-000100'");
        }
    }

    [DockerRequiredFact]
    public async Task WritesTheExplainArtefact()
    {
        // The PR reported plan costs with no artefact to re-derive them from. This puts the real
        // output in the repo, side by side with the pre-WP-14 predicate, so the accepted cost is
        // checkable rather than quoted.
        var sb = new StringBuilder();
        sb.AppendLine("# WP-14 — item_mappings lookup plans (EXPLAIN ANALYZE, BUFFERS)");
        sb.AppendLine();
        sb.AppendLine("Generated by `ItemMappingBatchResolvePerfPostgresTests` on postgres:16 with");
        sb.AppendLine($"one organisation, a supplier holding **{SmallSupplierCodes}** codes and a second");
        sb.AppendLine($"holding **{LargeSupplierCodes}**, after `ANALYZE item_mappings`. Absolute timings are");
        sb.AppendLine("machine-dependent; the PLAN SHAPE and the buffer counts are the point.");
        sb.AppendLine();

        foreach (var (label, sql, supplier, code) in new[]
                 {
                     ("pre-WP-14 predicate (exact match only), small supplier",
                         PreWp14Predicate, _smallSupplier, "SMALL-000100"),
                     ("WP-14 predicate (exact OR lower()), small supplier",
                         Wp14Predicate, _smallSupplier, "SMALL-000100"),
                     ("pre-WP-14 predicate (exact match only), large supplier",
                         PreWp14Predicate, _largeSupplier, "LARGE-000100"),
                     ("WP-14 predicate (exact OR lower()), large supplier",
                         Wp14Predicate, _largeSupplier, "LARGE-000100"),
                     ("WP-14 predicate with the OR terms swapped, large supplier",
                         Wp14PredicateOrderSwapped, _largeSupplier, "LARGE-000100"),
                 })
        {
            var plan = await ExplainAsync(sql, supplier, code);
            sb.AppendLine($"## {label}");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.Append(plan);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine($"shared buffers (hit+read): **{SharedBuffersRead(plan)}**");
            sb.AppendLine();
        }

        sb.AppendLine("## What the founder is being asked to accept");
        sb.AppendLine();
        sb.AppendLine("The case-folded lookup gives up the unique `(org_id, supplier_id, buyer_item_code)`");
        sb.AppendLine("index on its third column. What replaces it is **whatever the planner picks**, and");
        sb.AppendLine("that is size-dependent: an index scan on `supplier_id` plus a filter at a few hundred");
        sb.AppendLine("rows, a plain **sequential scan of `item_mappings`** at a few thousand, a bitmap heap");
        sb.AppendLine("scan at hundreds of thousands. Earlier drafts of this PR asserted \"not a sequential");
        sb.AppendLine("scan\"; the plans above show otherwise.");
        sb.AppendLine();
        sb.AppendLine("The scan is a COST fact only. The predicate is org+supplier scoped on every path, so");
        sb.AppendLine("the rows returned are always one supplier's — tenancy does not depend on the plan.");
        sb.AppendLine();
        sb.AppendLine("The lookup runs ONCE PER ORDER (the batch resolver issues a single query for every");
        sb.AppendLine("line), not once per line, which is what keeps the total bounded. The fix, if the cost");
        sb.AppendLine("ever matters, is");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine("CREATE INDEX CONCURRENTLY ix_item_mappings_org_supplier_lower_code");
        sb.AppendLine("    ON item_mappings (org_id, supplier_id, lower(buyer_item_code));");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("which is DDL on a live production table and therefore the founder's call, not this");
        sb.AppendLine("change's. It is deliberately NOT included in the PR.");

        var path = Path.Combine(RepoRoot(), "docs", "ops", "wp14-item-mapping-explain.md");
        await File.WriteAllTextAsync(path, sb.ToString());
        _output.WriteLine(sb.ToString());

        File.Exists(path).Should().BeTrue();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        return dir!.FullName;
    }
}
