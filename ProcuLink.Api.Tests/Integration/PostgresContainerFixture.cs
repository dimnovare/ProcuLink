using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The ONE postgres:16 container the whole <c>postgres-container</c> collection shares.
///
/// <para>Every test class in that collection used to build its own container in
/// <c>InitializeAsync</c>. Measured on CI run 31116899631 (parsed per-test from the
/// <c>dotnet-test-results</c> trx): 56 <c>*PostgresTests</c> classes / 305 tests spent
/// <b>563.3 s</b> of class wall time to execute <b>25.2 s</b> of actual test time — 538.1 s,
/// 96 %, was container startup, averaging 9.6 s per class. That collection ran the full width
/// of the job and was the critical path.</para>
///
/// <para>Isolation is unchanged, only its unit moved. Every class already used a unique
/// database name; it now gets that same unique database <em>on the shared container</em>
/// instead of on a container of its own. No two classes ever share a database.</para>
///
/// <para>Serialisation is retained deliberately — see
/// <see cref="PostgresContainerCollection"/>. This fixture buys sharing; the collection
/// definition still buys one-class-at-a-time.</para>
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    /// <summary>
    /// Migrated exactly once, then cloned per class with <c>CREATE DATABASE … TEMPLATE</c>,
    /// which is a file copy rather than a replay of all 78 migrations.
    /// </summary>
    private const string TemplateDatabase = "proculink_template";

    /// <summary>Only ever connected to for DDL (create/drop the per-class databases).</summary>
    private const string MaintenanceDatabase = "postgres";

    private static readonly Regex SafeDatabasePrefix = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private PostgreSqlContainer? _pg;

    /// <summary>Points at <see cref="MaintenanceDatabase"/>; the <c>Database</c> is swapped per caller.</summary>
    private string? _hostConnectionString;

    /// <summary>
    /// Starts the container EAGERLY, here, and not on the first
    /// <see cref="CreateDatabaseAsync"/> call.
    ///
    /// <para>Deferring it was tried on this branch and measured, because the collection now also
    /// holds classes that never ask for a database and a lazy start would have spared them. It
    /// cost 30–44 s of assembly wall over two runs against a ±4 s run-to-run baseline: the
    /// ~15 s startup stops overlapping the rest of the assembly and lands inside the first
    /// Postgres class instead, where it is serialised behind the collection's one lane.
    /// <c>WidenedFieldBlastRadiusPostgresTests</c> went from 0.5 s in each of two pre-change runs
    /// to 17.2 s and 21.5 s in the two after it.</para>
    ///
    /// <para>The trade accepted instead: a <c>--filter</c> run selecting only a host-booting
    /// member (say <c>HealthEndpointTests</c>) starts a container it never uses. That is a
    /// developer-loop cost measured in seconds, paid by whoever filters; the lazy version charged
    /// the full-suite CI run, which is the one #168 was about.</para>
    /// </summary>
    public async Task InitializeAsync()
    {
        // Docker unreachable: every test in the collection is statically skipped by
        // [DockerRequiredFact]/[DockerRequiredTheory], so do not attempt a container.
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase(MaintenanceDatabase)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCommand(
                // Headroom for the classes that run with Pooling=false and open several physical
                // connections at once to make a claim race real; the default is 100.
                "-c", "max_connections=200",
                // A throwaway container that is destroyed at the end of the run has nothing to
                // recover, so the WAL durability work is pure cost — it is paid once by the
                // template migration and again by every CREATE DATABASE file copy.
                "-c", "fsync=off",
                "-c", "synchronous_commit=off",
                "-c", "full_page_writes=off")
            .Build();

        await _pg.StartAsync();

        var csb = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            // Npgsql's 15 s default is measured against an idle machine; a cold container on a
            // busy Docker host exceeds it on the FIRST connect, which reads as a failure of
            // whatever ran first rather than as a busy host.
            Timeout = 60,
            CommandTimeout = 180,
        };

        // Two verified facts, not a diagnosis: `docker inspect` shows Testcontainers publishing
        // the port on IPv4 only ("0.0.0.0:55267->5432/tcp", no "[::]" mapping), and on this
        // Windows host Dns.GetHostAddresses("localhost") returns ::1 first. Three classes used to
        // pin this literal individually; with one shared container a resolver-order failure would
        // take down the entire collection, so it is pinned once, here. Applied only when the host
        // already IS loopback, so a remote DOCKER_HOST keeps working.
        if (string.Equals(csb.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            csb.Host = "127.0.0.1";

        _hostConnectionString = csb.ConnectionString;

        await WaitUntilPostmasterRestartedAsync();
        await WaitUntilAcceptingTcpAsync();
        await CreateTemplateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        // _pg stays null when Docker is unavailable — InitializeAsync returns before building it.
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    /// <summary>
    /// Creates a fresh, fully-migrated database on the shared container and returns a connection
    /// string pointing at it. The caller applies its own <see cref="NpgsqlConnectionStringBuilder"/>
    /// tweaks (pooling, timeouts) to the returned string exactly as it did before.
    /// </summary>
    /// <param name="namePrefix">
    /// The class's own database prefix, e.g. <c>proculink_aiusage</c>. A <c>{Guid:N}</c> suffix is
    /// appended, so the name is unique per class run and no two classes share a database.
    /// </param>
    public async Task<string> CreateDatabaseAsync(string namePrefix)
    {
        var database = BuildDatabaseName(namePrefix);

        await using var admin = await OpenMaintenanceConnectionAsync();

        try
        {
            await ExecuteAsync(admin, $"""CREATE DATABASE "{database}" TEMPLATE "{TemplateDatabase}" """);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse)
        {
            // CREATE DATABASE … TEMPLATE refuses while any backend is connected to the template.
            // Nothing should ever connect to it after the one migration (which runs with
            // Pooling=false and is disposed), so this is a belt-and-braces path, not the design:
            // evict whatever is holding it and try once more rather than failing a whole class.
            await ExecuteAsync(admin, $"""
                SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                WHERE datname = '{TemplateDatabase}' AND pid <> pg_backend_pid()
                """);
            await ExecuteAsync(admin, $"""CREATE DATABASE "{database}" TEMPLATE "{TemplateDatabase}" """);
        }

        return new NpgsqlConnectionStringBuilder(_hostConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>
    /// Drops the database behind <paramref name="connectionString"/>. Best effort: the container is
    /// destroyed at the end of the run either way, so a teardown that cannot drop must not turn a
    /// passing class red. It is not decorative, though — 56 un-dropped clones of the template would
    /// accumulate real disk inside the container.
    /// </summary>
    public async Task DropDatabaseAsync(string? connectionString)
    {
        if (connectionString is null || _hostConnectionString is null)
            return;

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrEmpty(database) || database == MaintenanceDatabase || database == TemplateDatabase)
            return;

        // Classes that ran with Pooling=true (IngestDedupePostgresTests) leave idle pooled
        // connections behind; those are backends on the database about to be dropped.
        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var admin = await OpenMaintenanceConnectionAsync();
            // WITH (FORCE) terminates any remaining backend instead of erroring (PostgreSQL 13+).
            await ExecuteAsync(admin, $"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        }
        catch (Exception)
        {
            // Deliberately swallowed — see the summary above.
        }
    }

    private string BuildDatabaseName(string namePrefix)
    {
        if (_hostConnectionString is null)
            throw new InvalidOperationException(
                "The shared Postgres container was never started. A test class asked for a database " +
                $"without checking DockerProbe.UnavailableReason first ({DockerProbe.UnavailableReason ?? "no reason recorded"}).");

        // These names are interpolated into DDL, which no EF API can express. The prefixes are
        // compile-time constants in test code, but validating them keeps that true.
        if (!SafeDatabasePrefix.IsMatch(namePrefix))
            throw new ArgumentException(
                $"Database prefix '{namePrefix}' must match {SafeDatabasePrefix} to be safe to interpolate into DDL.",
                nameof(namePrefix));

        var database = $"{namePrefix}_{Guid.NewGuid():N}";
        if (database.Length > 63)
            throw new ArgumentException(
                $"Database name '{database}' is {database.Length} characters; PostgreSQL truncates identifiers at 63, " +
                "which would let two classes collide on the same database.",
                nameof(namePrefix));

        return database;
    }

    /// <summary>
    /// Applies the full migration chain ONCE, to <see cref="TemplateDatabase"/>. Every per-class
    /// database is then a <c>CREATE DATABASE … TEMPLATE</c> file copy of it, so the migrations
    /// still really run — they just run once instead of 56 times.
    /// </summary>
    private async Task CreateTemplateDatabaseAsync()
    {
        await using (var admin = await OpenMaintenanceConnectionAsync())
        {
            await ExecuteAsync(admin, $"""CREATE DATABASE "{TemplateDatabase}" """);
        }

        // Pooling=false, and nothing ever reconnects to the template after this block: a pooled
        // idle connection left behind here is exactly what makes CREATE DATABASE … TEMPLATE fail
        // with "source database is being accessed by other users".
        var templateConnectionString = new NpgsqlConnectionStringBuilder(_hostConnectionString)
        {
            Database = TemplateDatabase,
            Pooling = false,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(templateConnectionString, npgsql => npgsql.CommandTimeout(180))
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(options);
        await migrateDb.Database.MigrateAsync();
    }

    private async Task<NpgsqlConnection> OpenMaintenanceConnectionAsync()
    {
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_hostConnectionString) { Database = MaintenanceDatabase }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 180 };
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Waits for the SECOND "ready to accept connections", because the official postgres image
    /// starts two postmasters.
    ///
    /// <para><c>docker-entrypoint.sh</c> boots a TEMPORARY server to run <c>initdb</c> and the
    /// init scripts, shuts it down, and only then starts the real one. Verified against
    /// <c>postgres:16</c> on 2026-08-06 — its own log reads "database system is ready to accept
    /// connections" / "shutting down" / "database system is ready to accept connections".
    /// Testcontainers' wait strategy is <c>pg_isready</c>, which answers from the TEMPORARY server,
    /// so <c>StartAsync</c> can return ~2 s in and the shutdown then kills whatever the client is
    /// doing with "An existing connection was forcibly closed by the remote host" — reproduced
    /// three times here, once mid-migration. Per-class containers hit the same race; sharing one
    /// container makes it worth solving once, properly, instead of retrying around it.</para>
    ///
    /// <para>Best effort by design: if a future image stops double-starting, the count never
    /// reaches two and this falls through to <see cref="WaitUntilAcceptingTcpAsync"/>, which is
    /// the actual gate.</para>
    /// </summary>
    private async Task WaitUntilPostmasterRestartedAsync()
    {
        const string ReadyLine = "database system is ready to accept connections";
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            var (stdout, stderr) = await _pg!.GetLogsAsync(timestampsEnabled: false);
            var readyCount = CountOccurrences(stdout, ReadyLine) + CountOccurrences(stderr, ReadyLine);
            if (readyCount >= 2)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Requires three consecutive successful round trips rather than one, so a postmaster that is
    /// about to be shut down cannot answer a single probe and then drop the template migration.
    ///
    /// <para>Testcontainers' <c>pg_isready</c> answers over the UNIX socket and can go green before
    /// the postmaster is usable over TCP; the docker port proxy accepts the connection anyway and
    /// nothing replies, so the first command dies with "Timeout during reading attempt". Three
    /// classes carried a loop like this individually; with one shared container it runs once,
    /// before any class gets a database.</para>
    /// </summary>
    private async Task WaitUntilAcceptingTcpAsync()
    {
        const int RequiredConsecutiveProbes = 3;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var consecutive = 0;
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var probe = await OpenMaintenanceConnectionAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", probe);
                await cmd.ExecuteScalarAsync();

                if (++consecutive >= RequiredConsecutiveProbes)
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            catch (Exception ex)
            {
                last = ex;
                consecutive = 0;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException(
            $"The shared Postgres testcontainer never answered {RequiredConsecutiveProbes} consecutive probes within 90s.",
            last);
    }
}
