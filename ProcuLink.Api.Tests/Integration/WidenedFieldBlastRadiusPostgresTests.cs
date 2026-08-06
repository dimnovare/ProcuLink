using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Xunit;
using Xunit.Abstractions;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-14 — makes the blast radius KNOWN instead of assumed empty.
///
/// <para><b>The claim this exists to stop being asserted.</b> Widening the output row bag from 21
/// keys to 53 is byte-identical ONLY for suppliers whose stored output config does not name one of
/// the 32 new fields. For a supplier that DOES name one, the delivered document changes on deploy:
/// an empty cell becomes populated, and — through a <c>Fallback</c> manipulator whose first
/// argument used to be unresolvable — a value can change into a DIFFERENT value. Pinned revisions
/// do not protect, because the row bag is code rather than part of the snapshot, and
/// <c>ReplayService</c> re-renders through the same path.</para>
///
/// <para><b>The artefact.</b> <c>docs/ops/wp14-widened-field-blast-radius.sql</c> is a READ-ONLY
/// query the founder runs against production (or a read replica) before merging. This test executes
/// THAT FILE — not a copy of it — against a seeded local Postgres, so the script that ships is the
/// script that was proven to work. A test with its own inline SQL would prove a sibling of the
/// artefact, which is worth nothing.</para>
///
/// <para><b>The report.</b> The run writes its report to the gitignored artifacts directory
/// (<see cref="TestReportArtifacts"/>) and COMPARES it against the committed
/// <c>docs/ops/wp14-blast-radius-local-run.md</c> — it does not overwrite it. The seed ids are
/// therefore fixed rather than fresh per run, so the report is byte-stable and the committed copy
/// is a claim that stays checkable instead of a file every test run rewrites with new GUIDs.</para>
///
/// <para><b>Non-vacuity.</b> The seed contains both POSITIVES (a canonicalField rule, a Fallback
/// manipulator param, a Scriban expression, a per-order override, a published revision) and
/// NEGATIVES (a config naming only pre-WP-14 fields; a supplier column whose NAME happens to be a
/// new field but which carries no rule). The test asserts the query finds every positive and no
/// negative — a scan that matched everything, or nothing, would fail.</para>
///
/// <para>Docker-gated like every other <c>*PostgresTests</c>. Nothing here writes to production;
/// the SQL file is additionally asserted to contain no mutating keyword.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class WidenedFieldBlastRadiusPostgresTests(
    PostgresContainerFixture postgres,
    ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>The committed report this run is checked against, and the name it is written under.</summary>
    private const string ReportFileName = "wp14-blast-radius-local-run.md";

    // Fixed, not fresh: they appear in the report, and a report with new GUIDs every run cannot be
    // compared to anything. Each test method gets its own database, so these cannot collide.
    private static readonly Guid OrgId     = new("b1a57000-0000-4d14-8a00-000000000001");
    private static readonly Guid SupplierA = new("b1a57000-0000-4d14-8a00-00000000000a");
    private static readonly Guid SupplierB = new("b1a57000-0000-4d14-8a00-00000000000b");
    private static readonly Guid SupplierC = new("b1a57000-0000-4d14-8a00-00000000000c");

    private readonly ITestOutputHelper _output = output;
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_blast");

        _connectionString = new NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── Locating the shipped artefact ────────────────────────────────────────

    private static string ScriptPath() =>
        Path.Combine(TestReportArtifacts.RepoRoot(), "docs", "ops", "wp14-widened-field-blast-radius.sql");

    private static string CommittedReportPath() =>
        Path.Combine(TestReportArtifacts.RepoRoot(), "docs", "ops", ReportFileName);

    /// <summary>
    /// The 32 names WP-14 added, derived from the declared field lists rather than retyped, so the
    /// SQL literal cannot silently drift from what the code actually exposes.
    /// </summary>
    private static IReadOnlyList<string> NewlyExposedNames()
    {
        // Pre-WP-14 the row bag held these 10 header + 11 line keys.
        var preWp14 = new HashSet<string>(StringComparer.Ordinal)
        {
            "PoNumber", "OrderDate", "BuyerName", "Currency", "SupplierName",
            "SubTotal", "TaxTotal", "GrandTotal", "PaymentTerms", "RequestedDeliveryDate",
            "LineNumber", "BuyerItemCode", "SupplierItemCode", "Description",
            "Quantity", "Unit", "UnitPrice", "LineTotal", "LineAmount", "TaxRate", "DeliveryDate",
        };

        return CanonicalRowFields.Header
            .Concat(CanonicalRowFields.Line)
            .Where(n => !preWp14.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // ── The safety property ──────────────────────────────────────────────────

    [Fact]
    public void TheScript_IsReadOnly()
    {
        // It is handed to the founder to run against production. A mutating statement sneaking in
        // later must fail here, not there.
        var sql = File.ReadAllText(ScriptPath());

        var forbidden = new[]
        {
            "INSERT ", "UPDATE ", "DELETE ", "DROP ", "ALTER ", "TRUNCATE ", "CREATE ", "GRANT ", "COPY ",
        };

        // Strip comment lines first: the header prose legitimately names these words.
        var body = string.Join('\n', File.ReadAllLines(ScriptPath())
            .Where(l => !l.TrimStart().StartsWith("--")));

        foreach (var keyword in forbidden)
            body.ToUpperInvariant().Should().NotContain(keyword,
                "the blast-radius script is run against production and must only ever SELECT");

        sql.Should().Contain("SELECT", "a query that selects nothing cannot report anything");
    }

    [Fact]
    public void TheScriptsFieldList_MatchesWhatWp14ActuallyExposed()
    {
        // Guards the artefact against the code moving on without it.
        var sql = File.ReadAllText(ScriptPath());

        var newlyExposed = NewlyExposedNames();

        // The sweep below is only evidence if there is something to sweep. NewlyExposedNames() is
        // DERIVED — subtract the pre-WP-14 set from the declared field lists — so a rename on either
        // side can silently empty it, and the loop would then confirm the script probes every one of
        // zero fields and report green.
        newlyExposed.Should().HaveCount(32,
            "WP-14 widened the row bag from 21 keys to 53, so exactly 32 names must reach the check "
            + "below; a shorter list means this test stopped covering fields it still claims to cover");

        foreach (var name in newlyExposed)
            sql.Should().Contain($"('{name}')",
                "the blast-radius script must probe every newly exposed field; '{0}' is missing", name);
    }

    // ── The run ──────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task TheQuery_FindsEveryAffectedConfig_AndNothingElse()
    {
        var orgId      = OrgId;
        var supplierA  = SupplierA;   // POSITIVE — canonicalField rule on a new name
        var supplierB  = SupplierB;   // POSITIVE — Fallback manipulator param + Scriban
        var supplierC  = SupplierC;   // NEGATIVE — only pre-WP-14 names
        var now        = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.Organisations.Add(new Organisation
            {
                Id = orgId, Name = "Blast Radius Co", ClerkOrgId = "org_wp14blastradiusfixture",
                Slug = "blast-radius-fixture", CreatedAt = now,
            });
            foreach (var (id, name) in new[] { (supplierA, "A"), (supplierB, "B"), (supplierC, "C") })
            {
                db.Suppliers.Add(new Supplier
                {
                    Id = id, OrgId = orgId, Name = $"Supplier {name}", Code = name,
                    CreatedAt = now,
                });
            }
            await db.SaveChangesAsync();

            // POSITIVE 1 — the machine-generated shape. OutputNodeTemplateInferrer auto-binds this
            // for any sample column containing "manufacturerpart", so it is not hypothetical.
            db.SupplierPoMappings.Add(NewPoMapping(orgId, supplierA, now, """
                {"header":{"po":{"outputPath":"po","canonicalField":"PoNumber","fieldManipulators":[]}},
                 "lines":{"mpn":{"outputPath":"mpn","canonicalField":"ManufacturerPartNumber","fieldManipulators":[]}}}
                """));

            // POSITIVE 2 — a Fallback manipulator whose FIRST argument was unresolvable before
            // WP-14 and resolves now: the value CHANGES rather than filling a blank. Plus a Scriban
            // expression naming another new field.
            db.SupplierPoMappings.Add(NewPoMapping(orgId, supplierB, now, """
                {"header":{"ship":{"outputPath":"ship","fieldManipulators":[
                    {"type":"Fallback","params":{"fields":"ShipToName,BuyerName"}}]},
                  "note":{"outputPath":"note","expression":"{{order.Incoterms}}-{{order.ShipToCity}}","fieldManipulators":[]}},
                 "lines":{}}
                """));

            // NEGATIVE — a perfectly ordinary config naming only pre-WP-14 fields.
            db.SupplierPoMappings.Add(NewPoMapping(orgId, supplierC, now, """
                {"header":{"po":{"outputPath":"po","canonicalField":"PoNumber","fieldManipulators":[]},
                           "cur":{"outputPath":"cur","canonicalField":"Currency","fieldManipulators":[]}},
                 "lines":{"qty":{"outputPath":"qty","canonicalField":"Quantity","fieldManipulators":[]}}}
                """));

            await db.SaveChangesAsync();
        }

        // ── run the SHIPPED script ───────────────────────────────────────────
        var rows = await RunScriptAsync();

        var report = BuildReport(rows);
        var reportPath = await TestReportArtifacts.WriteAsync(ReportFileName, report);
        _output.WriteLine($"report written to {reportPath}");
        _output.WriteLine(report);

        // POSITIVES found …
        rows.Should().Contain(r => r.ScopeId == supplierA.ToString()
                                   && r.ReferencedField == "ManufacturerPartNumber",
            "the auto-bound ManufacturerPartNumber rule is exactly the config that starts emitting "
            + "a value it never emitted before");

        rows.Should().Contain(r => r.ScopeId == supplierB.ToString() && r.ReferencedField == "ShipToName",
            "a Fallback whose first argument becomes resolvable returns a DIFFERENT value");
        rows.Should().Contain(r => r.ScopeId == supplierB.ToString() && r.ReferencedField == "Incoterms");
        rows.Should().Contain(r => r.ScopeId == supplierB.ToString() && r.ReferencedField == "ShipToCity");

        // … and the negative NOT found. Without this the query could be `WHERE true`.
        rows.Should().NotContain(r => r.ScopeId == supplierC.ToString(),
            "a config naming only pre-WP-14 fields is unaffected and must not be reported — a scan "
            + "that flags everything tells the founder nothing");

        // Last, because the assertions above are the subject and this one is the paperwork: the
        // committed copy is what a reader sees without running anything, so it has to stay TRUE.
        // Compared, never overwritten — a test that rewrote it would make it agree with itself by
        // construction and leave the working tree dirty after every run.
        Normalised(await File.ReadAllTextAsync(CommittedReportPath())).Should().Be(Normalised(report),
            "docs/ops/{0} must still describe what the shipped script returns; if this run is the "
            + "correct new answer, copy {1} over it", ReportFileName, reportPath);
    }

    [DockerRequiredFact]
    public async Task TheQuery_ReportsNothing_WhenNoConfigNamesANewField()
    {
        // The answer the founder most wants is "zero rows". It has to be reachable, or a non-empty
        // result carries no information.
        var rows = await RunScriptAsync();

        rows.Should().BeEmpty(
            "an empty database has no affected configs; if this reports rows the scan is matching "
            + "something other than a stored configuration");
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private sealed record Hit(
        string SourceTable, Guid RowId, Guid OrgId, string? ScopeId, string ReferencedField, string Shape);

    /// <summary>Line endings differ between the checkout and the run; the CONTENT is the claim.</summary>
    private static string Normalised(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private async Task<List<Hit>> RunScriptAsync()
    {
        var sql = await File.ReadAllTextAsync(ScriptPath());

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var hits = new List<Hit>();
        while (await reader.ReadAsync())
        {
            hits.Add(new Hit(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return hits;
    }

    private static string BuildReport(IReadOnlyList<Hit> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# WP-14 blast radius — LOCAL TEST DATA");
        sb.AppendLine();
        sb.AppendLine("Produced by executing `docs/ops/wp14-widened-field-blast-radius.sql` against a");
        sb.AppendLine("seeded local postgres:16. **This is local fixture data, NOT production.** The");
        sb.AppendLine("same script must be run against a production read replica before merge — that");
        sb.AppendLine("number is the founder's to obtain, and this file cannot stand in for it.");
        sb.AppendLine();
        sb.AppendLine("This file is a COMMITTED SNAPSHOT, not a test output.");
        sb.AppendLine("`WidenedFieldBlastRadiusPostgresTests` compares its run against this text and");
        sb.AppendLine("fails on a mismatch; it never rewrites it. To update, copy the report the run");
        sb.AppendLine("leaves in `artifacts/test-reports/` over this file.");
        sb.AppendLine();
        sb.AppendLine($"Rows found in the local fixture: **{rows.Count}**");
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine("_(no affected configuration in the fixture)_");
            return sb.ToString();
        }

        sb.AppendLine("| source table | org | scope | field | shape |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows.OrderBy(r => r.SourceTable).ThenBy(r => r.ReferencedField, StringComparer.Ordinal))
            sb.AppendLine($"| {r.SourceTable} | {r.OrgId} | {r.ScopeId} | {r.ReferencedField} | {r.Shape} |");

        return sb.ToString();
    }

    private static SupplierPoMapping NewPoMapping(Guid orgId, Guid supplierId, DateTime now, string configJson) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            ConfigJson = configJson,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
}
