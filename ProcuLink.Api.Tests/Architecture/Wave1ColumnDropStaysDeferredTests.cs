using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-09 is an EXPAND/CONTRACT, and this file is the thing that keeps the two halves in two
/// deploys.
///
/// <para><b>The hazard.</b> Migrations run at API startup
/// (<c>ProcuLink.Api/Program.cs</c> — <c>await db.Database.MigrateAsync()</c>). The Worker (Railway
/// service <c>aware-amazement</c>) is a SEPARATE service that deploys independently and never
/// migrates. EF enumerates every mapped column for a non-projected entity query, and the Worker
/// materialises the whole <c>Organisation</c> on its hottest path —
/// <c>EmailPollOrgJob</c> → <c>HasFeatureAsync</c> → <c>StripeBillingService.LoadOrgAsync</c>, on
/// every IMAP poll cycle (and again in <c>EmailSettingsService.UpdateAsync</c>). Drop the column in
/// the same release that unmaps it and every still-old Worker (or draining API replica) throws
/// Npgsql <c>42703 — column o.webhook_secret_encrypted does not exist</c> until it redeploys.
/// Reproduced on the post-<c>Up</c> schema; the verbatim error is quoted in PR #75.</para>
///
/// <para><b>The contract.</b> This release UNMAPS the column —
/// <c>RetiredSubsystemsStayRetiredTests.InboundWebhookIngress_HasNoConsumerAnywhere</c> proves that
/// half, by asserting no source file mentions the entity property any more (this file cannot name
/// that property, because doing so would itself trip that scan). The physical column survives,
/// unread, until BOTH services run the unmapped build. This file proves the other half — that no
/// checked-in migration has contracted yet.</para>
///
/// <para><b>DELETE THIS FILE in the follow-up PR</b> that adds the drop migration. That deletion is
/// the point: it forces the contraction to be a deliberate act taken after the deploy sequence in
/// <c>Wave1RetireDeadSubsystems</c>'s doc comment has actually been carried out, rather than a line
/// that quietly rides along with the code change.</para>
/// </summary>
public class Wave1ColumnDropStaysDeferredTests
{
    private const string Column = "webhook_secret_encrypted";

    /// <summary>
    /// No migration's <c>Up</c> may drop the column yet. Matches the OPERATION, not prose: EF
    /// renders the drop as <c>DropColumn(</c> followed by the column name (named or positional), so
    /// a doc comment that merely discusses the drop cannot satisfy — or trip — this assertion.
    ///
    /// <para><b>Only <c>Up</c> is scanned</b>, and deliberately so: the migration that ADDED the
    /// column (<c>20260529052831_AddWebhookSecretToOrganisation</c>) drops it in its <c>Down</c>, and
    /// that is correct — a rollback of the ADD must remove what the ADD created. Scanning whole files
    /// flagged it, which is the false positive this split exists to avoid.</para>
    /// </summary>
    [Fact]
    public void NoMigration_DropsTheWebhookSecretColumn_Yet()
    {
        var dropRx = new Regex(
            @"DropColumn\s*(?:<[^>]*>\s*)?\(\s*(?:name:\s*)?""" + Column + @"""",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var migrationsDir = Path.Combine(RepoRoot(), "ProcuLink.Infrastructure", "Migrations");
        Directory.Exists(migrationsDir).Should().BeTrue("the migrations folder must be where this test looks");

        var files = Directory.EnumerateFiles(migrationsDir, "*.cs", SearchOption.AllDirectories).ToList();
        files.Should().NotBeEmpty("the scan must actually find migration files");

        var offenders = files
            .Where(f => dropRx.IsMatch(UpBody(File.ReadAllText(f))))
            .Select(f => Path.GetFileName(f))
            .ToList();

        offenders.Should().BeEmpty(
            $"organisations.{Column} is UNMAPPED by this release but must NOT be dropped in it. The API " +
            "migrates at startup; the Worker (aware-amazement) deploys separately and does not migrate, and " +
            "it loads the full Organisation entity on every IMAP poll cycle — so dropping the column here " +
            "throws Npgsql 42703 on every Worker org load until the Worker redeploys, for an unbounded " +
            "window. Ship the drop in a LATER migration, after both services run the unmapped build, and " +
            "delete this test file in that same PR. Offending migration(s): " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// The premise the sequencing rests on: the API is the ONLY service that applies migrations. If
    /// the Worker ever starts migrating too, the expand/contract window changes shape (both services
    /// would race to migrate) and the sequence above must be re-derived rather than trusted.
    /// </summary>
    [Fact]
    public void OnlyTheApi_AppliesMigrations()
    {
        var migrateRx = new Regex(@"Database\s*\.\s*Migrate(Async)?\s*\(", RegexOptions.Compiled);
        var root      = RepoRoot();

        var workerHits = MigrateCallSites(root, "ProcuLink.Worker", migrateRx);

        workerHits.Should().BeEmpty(
            "the Worker deploying independently WITHOUT migrating is exactly why a column drop cannot ride " +
            "along with the code change that unmaps it. If the Worker now migrates, re-derive the " +
            "expand/contract sequence in Wave1RetireDeadSubsystems' doc comment instead of deleting this " +
            "assertion. Hit(s): " + string.Join(", ", workerHits));

        // ...and the API genuinely does, so the sequence has a step 1 at all. Scanned across the
        // whole project rather than pinned to Program.cs: the claim is about which SERVICE migrates,
        // and pinning the file made an ordinary refactor read as "the API stopped migrating". It
        // did exactly that when the startup task moved to Startup/MigrationBootstrap.cs — the API
        // still migrated, the guard just could not see it any more.
        var apiHits = MigrateCallSites(root, "ProcuLink.Api", migrateRx);

        apiHits.Should().NotBeEmpty(
            "the API is the service that applies migrations, so the expand/contract sequence has a step 1 " +
            "at all. No Database.MigrateAsync call anywhere in ProcuLink.Api means either it was deleted — " +
            "in which case NOTHING migrates the deployed schema — or the scan broke.");
    }

    /// <summary>
    /// Every <c>Database.Migrate(Async)</c> call site in one project's sources, repo-relative. Both
    /// halves of the claim above go through this, so neither can drift into scanning a narrower
    /// corpus than the other.
    /// </summary>
    private static List<string> MigrateCallSites(string root, string project, Regex migrateRx) =>
        Directory
            .EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
            .Where(f => migrateRx.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

    /// <summary>
    /// The text of a migration's <c>Up</c> method — everything between its signature and the
    /// <c>Down</c> signature that always follows it in an EF-generated migration. A file with no
    /// <c>Up</c> (the <c>.Designer.cs</c> snapshots) yields an empty body, which is correct: a
    /// snapshot describes state, it performs no operation.
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
