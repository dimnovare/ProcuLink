using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The <c>acknowledged_at</c> → <c>transport_accepted_at</c> rename is an EXPAND/CONTRACT, and this
/// file is what keeps the two halves in two deploys.
///
/// <para><b>The hazard.</b> Migrations run at API startup (<c>ProcuLink.Api/Program.cs</c> —
/// <c>await db.Database.MigrateAsync()</c>). The Worker (Railway service <c>aware-amazement</c>) is a
/// SEPARATE service that deploys independently and never migrates. EF enumerates every mapped column
/// for a non-projected entity query, and the Worker materialises whole <c>DeliveryAttempt</c> rows
/// through <c>DeliveryService</c> on its delivery paths (<c>DeliverOrderJob</c>,
/// <c>RetryDeliveryJob</c>, the stranded-delivery sweep) — on read AND on insert. Drop the column in
/// the same release that unmaps it and every still-old Worker throws Npgsql
/// <c>42703 — column d.acknowledged_at does not exist</c> on every delivery until it redeploys.
/// This is the <c>webhook_secret_encrypted</c> shape (PR #75) repeated on a hotter path: the failure
/// would be undelivered purchase orders, not a degraded poll.</para>
///
/// <para><b>The contract.</b> <c>20260814063141_TransportAcceptanceIsNotSupplierAcknowledgement</c>
/// ADDS <c>transport_accepted_at</c> and backfills it; the entity and the EF mapping move to the new
/// property in the same release. The physical <c>acknowledged_at</c> column survives, unmapped and
/// unread, until BOTH services run this build.</para>
///
/// <para><b>DELETE THIS FILE in the follow-up PR</b> that adds the drop migration. That deletion is
/// the point: it forces the contraction to be a deliberate act taken after both Railway services are
/// confirmed on the new build, rather than a line that quietly rides along with the code change.</para>
/// </summary>
public class AcknowledgedAtColumnDropStaysDeferredTests
{
    private const string OldColumn = "acknowledged_at";
    private const string NewColumn = "transport_accepted_at";

    /// <summary>
    /// No migration's <c>Up</c> may drop the old column yet. Matches the OPERATION, not prose: EF
    /// renders the drop as <c>DropColumn(</c> followed by the column name (named or positional), so a
    /// doc comment discussing the future drop — this repo writes long ones — can neither satisfy nor
    /// trip the assertion.
    ///
    /// <para><b>Only <c>Up</c> is scanned</b>, matching the sibling
    /// <c>Wave1ColumnDropStaysDeferredTests</c>: a <c>Down</c> that removes what its own <c>Up</c>
    /// created is correct and must not be flagged.</para>
    /// </summary>
    [Fact]
    public void NoMigration_DropsTheAcknowledgedAtColumn_Yet()
    {
        var dropRx = new Regex(
            @"DropColumn\s*(?:<[^>]*>\s*)?\(\s*(?:name:\s*)?""" + OldColumn + @"""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var files = MigrationFiles();

        var offenders = files
            .Where(f => dropRx.IsMatch(UpBody(File.ReadAllText(f))))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty(
            $"delivery_attempts.{OldColumn} is UNMAPPED by this release but must NOT be dropped in it. The " +
            "API migrates at startup; the Worker (aware-amazement) deploys separately and does not migrate, " +
            "and it materialises whole DeliveryAttempt rows on every delivery and retry — so dropping the " +
            "column here throws Npgsql 42703 on every Worker delivery until the Worker redeploys, for an " +
            "unbounded window. Ship the drop in a LATER migration, after both services run this build, and " +
            "delete this test file in that same PR. Offending migration(s): " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Anti-vacuity. Without this, the assertion above is satisfied by a repository in which the
    /// expand half was never written at all — "nothing drops the column" is trivially true when
    /// nothing touches it. This pins that the EXPAND really landed: some migration's <c>Up</c> adds
    /// <c>transport_accepted_at</c>, and some migration's <c>Up</c> copies the old column into it.
    /// </summary>
    [Fact]
    public void TheExpandHalf_AddsAndBackfillsTheNewColumn()
    {
        var addRx = new Regex(
            @"AddColumn\s*(?:<[^>]*>\s*)?\(\s*(?:name:\s*)?""" + NewColumn + @"""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // The backfill is raw SQL (a migration is the only place it can run), so it is matched as the
        // assignment it is, not by file name.
        var backfillRx = new Regex(
            NewColumn + @"\s*=\s*" + OldColumn,
            RegexOptions.Compiled | RegexOptions.Singleline);

        var ups = MigrationFiles().Select(f => UpBody(File.ReadAllText(f))).ToList();

        ups.Should().Contain(u => addRx.IsMatch(u),
            $"the expand half must add delivery_attempts.{NewColumn}. If this fails, the deferral guard above " +
            "is guarding nothing.");

        ups.Should().Contain(u => backfillRx.IsMatch(u),
            $"the expand half must backfill {NewColumn} from {OldColumn}. Every existing value was written as " +
            "`result.Success ? now : null` — a transport-acceptance instant off our own clock — so the copy is " +
            "value- and meaning-preserving, and skipping it would strand every historical attempt with a null " +
            "the passport would have to explain.");
    }

    /// <summary>
    /// The premise the sequencing rests on: the API is the ONLY service that applies migrations. If
    /// the Worker ever starts migrating too, both services race to migrate and the expand/contract
    /// window changes shape — re-derive it rather than trusting the sequence above.
    ///
    /// <para>Duplicated from <c>Wave1ColumnDropStaysDeferredTests</c> ON PURPOSE: that file is
    /// scheduled for deletion by the webhook-secret drop PR, and this file's contract must not
    /// silently lose its premise when that happens.</para>
    /// </summary>
    [Fact]
    public void OnlyTheApi_AppliesMigrations()
    {
        var migrateRx = new Regex(@"Database\s*\.\s*Migrate(Async)?\s*\(", RegexOptions.Compiled);
        var root      = RepoRoot();

        MigrateCallSites(root, "ProcuLink.Worker", migrateRx).Should().BeEmpty(
            "the Worker deploying independently WITHOUT migrating is exactly why a column drop cannot ride " +
            "along with the code change that unmaps it.");

        MigrateCallSites(root, "ProcuLink.Api", migrateRx).Should().NotBeEmpty(
            "the API is the service that applies migrations, so the sequence has a step 1 at all.");
    }

    private static List<string> MigrationFiles()
    {
        var migrationsDir = Path.Combine(RepoRoot(), "ProcuLink.Infrastructure", "Migrations");
        Directory.Exists(migrationsDir).Should().BeTrue("the migrations folder must be where this test looks");

        var files = Directory.EnumerateFiles(migrationsDir, "*.cs", SearchOption.AllDirectories).ToList();
        files.Should().NotBeEmpty("the scan must actually find migration files");
        return files;
    }

    private static List<string> MigrateCallSites(string root, string project, Regex migrateRx) =>
        Directory
            .EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
            .Where(f => migrateRx.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

    /// <summary>
    /// The text of a migration's <c>Up</c> — everything between its signature and the <c>Down</c>
    /// that always follows. A file with no <c>Up</c> (the <c>.Designer.cs</c> snapshots) yields an
    /// empty body, which is correct: a snapshot describes state, it performs no operation.
    /// </summary>
    private static string UpBody(string source)
    {
        var up = source.IndexOf("void Up(", StringComparison.Ordinal);
        if (up < 0) return string.Empty;

        var down = source.IndexOf("void Down(", up, StringComparison.Ordinal);
        return down < 0 ? source[up..] : source[up..down];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcuLink.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must run from inside the ProcuLink checkout (ProcuLink.slnx not found above the test binaries)");
        return dir!.FullName;
    }
}
