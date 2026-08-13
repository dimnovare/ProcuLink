using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using Sentry;

namespace ProcuLink.Api.Startup;

/// <summary>
/// The background auto-migration that Program.cs kicks off from
/// <c>ApplicationStarted</c>, plus the idempotent post-migration backfills.
///
/// <para>It lives here rather than inline in Program.cs so the decision it makes — migrate, or
/// don't — is reachable from a test. It writes the process-global
/// <see cref="MigrationReadiness"/> flag, so "what makes it write, and when" is behaviour, not
/// plumbing.</para>
/// </summary>
public static class MigrationBootstrap
{
    /// <summary>
    /// Migration attempts before the fail-loud path runs. With <see cref="BackoffFor"/> the
    /// waiting between them totals 3 + 6 + 9 + 12 + 15 = 45 s.
    /// </summary>
    public const int MaxAttempts = 6;

    /// <summary>Backoff after a failed attempt: 3 s, 6 s, 9 s, 12 s, 15 s.</summary>
    public static TimeSpan BackoffFor(int attempt) => TimeSpan.FromSeconds(attempt * 3);

    /// <summary>
    /// Applies pending migrations, then runs the idempotent backfills, then marks readiness.
    /// Runs AFTER the HTTP server is listening so the Railway health check succeeds immediately.
    /// Neon Postgres has a cold-start delay on the first connection; retrying with backoff handles
    /// that gracefully.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var migLogger = loggerFactory.CreateLogger("ProcuLink.Migrations");

        // P2: State starts as Pending (the default) so /health/ready is
        // honest during Neon cold-start; it stays NOT-ready until
        // MigrateAsync() completes successfully. No explicit MarkPending()
        // call is needed here — the field initialises to Pending at process
        // start, which is the desired semantics.

        // ── Only a relational provider has a schema to migrate ────────────
        // MigrateAsync() throws unconditionally on a non-relational provider
        // ("Relational-specific methods can only be used when the context is using a
        // relational database provider"), so without this the loop below runs its full
        // 6 attempts and 45 s of backoff to arrive at a MarkFailed() that says nothing
        // about any database. That is not a hypothetical: six test files boot a
        // WebApplicationFactory<Program> over EF InMemory, and each one left a task
        // running past the end of the class that started it, writing the process-global
        // readiness flag at t≈45 s into whatever class happened to be asserting on it.
        //
        // Deployment is unaffected — Railway/Neon boot Npgsql, which IS relational, so
        // production still migrates and still fails loud below when it cannot. Readiness
        // is deliberately left untouched rather than marked Succeeded: nothing has been
        // proven about a schema here, and a Pending /health/ready is the honest answer.
        if (!db.Database.IsRelational())
        {
            migLogger.LogInformation(
                "Startup migration skipped: provider {Provider} is not relational, so there is no schema " +
                "to migrate. Readiness left as {State}.",
                db.Database.ProviderName, MigrationReadiness.State);
            return;
        }

        // ── Phantom-migration reconciliation ─────────────────────────────
        // Some Wave 3/4 migrations had their SQL applied to the prod DB
        // out-of-band (or via a previous deploy that crashed mid-migration),
        // but the __EFMigrationsHistory table doesn't record them as applied.
        // If we let MigrateAsync() proceed it would try to re-add the
        // organisations.slug column and Postgres would reject with
        // 42701 "column already exists". For each known phantom-prone
        // migration we check a sentinel DB object — if the object exists
        // AND the migration row is missing, we insert the history row so
        // MigrateAsync() skips re-applying the SQL.
        //
        // P1 kill-switch: set Migrations:ReconcilePhantom=false to bypass
        // once prod __EFMigrationsHistory is confirmed to contain all 5 IDs.
        // Default is true (current behaviour unchanged).
        var reconcilePhantom = configuration.GetValue("Migrations:ReconcilePhantom", defaultValue: true);
        if (reconcilePhantom)
        {
            try
            {
                await ReconcilePhantomMigrationsAsync(db, migLogger);
            }
            catch (Exception ex)
            {
                // Sentinel queries can transiently fail on Neon cold-start.
                // The retry loop below will get another shot; do not abort boot.
                migLogger.LogWarning(
                    "Phantom-migration reconciliation skipped due to error ({Message}). " +
                    "Proceeding to MigrateAsync — retry loop will handle transient failures.",
                    ex.Message);
            }
        }
        else
        {
            migLogger.LogInformation(
                "Phantom-migration reconciliation disabled via Migrations:ReconcilePhantom=false.");
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                LogAbandoned(migLogger, attempt);
                return;
            }

            try
            {
                // Deliberately NOT passed ct: cancelling mid-MigrateAsync would abort a DDL batch
                // that is part-way through the migration chain, and a half-applied schema is worse
                // than a shutdown that waits. The token guards the WAITING — the loop entry above
                // and the backoff below — which is where a stopping host spends ~45 of every 45 s.
                await db.Database.MigrateAsync();
                migLogger.LogInformation("Database migrations applied (attempt {Attempt}).", attempt);
                // P2: transition to Succeeded — /health/ready becomes Healthy.
                MigrationReadiness.MarkSucceeded();

                await RunPostMigrationBackfillsAsync(scope.ServiceProvider, db, migLogger);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= MaxAttempts)
                    break; // Final attempt failed — fall through to fail-loud handling.

                var delay = BackoffFor(attempt);
                migLogger.LogWarning(
                    "Migration attempt {Attempt}/{MaxAttempts} failed ({Message}). Retrying in {Delay}s…",
                    attempt, MaxAttempts, ex.Message, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    LogAbandoned(migLogger, attempt + 1);
                    return;
                }
            }
        }

        // ── Fail-loud on final failure ────────────────────────────────────────
        // All retries exhausted. We deliberately do NOT crash the process: the
        // HTTP server (and liveness probe) stays up so partial functionality and
        // diagnostics remain available. But the failure must be VISIBLE:
        //   1. LogError (structured) — shows in Railway logs.
        //   2. SentrySdk.CaptureException — Sentry is wired on the API WebHost, so
        //      this raises an alert with the actual migration exception.
        //   3. MigrationReadiness.MarkFailed() — flips /health/ready to Unhealthy
        //      so Railway/monitoring can detect the stale-schema state.
        migLogger.LogError(
            lastError,
            "All {MaxAttempts} migration attempts failed — app is running but DB schema may be outdated. " +
            "Marking readiness UNHEALTHY (/health/ready) and reporting to Sentry.",
            MaxAttempts);

        if (lastError is not null)
            SentrySdk.CaptureException(lastError);
        else
            SentrySdk.CaptureMessage(
                "Database migrations failed after all retry attempts (no exception captured).",
                SentryLevel.Error);

        MigrationReadiness.MarkFailed();
    }

    /// <summary>
    /// The host is stopping mid-retry. Readiness is deliberately NOT marked failed: a shutdown is
    /// not a migration failure, nothing has been proven about the schema, and marking it would page
    /// on every ordinary deploy. Leaving the flag alone also means the task cannot outlive the host
    /// that owns it and write into another process's — or another test class's — assertions.
    /// </summary>
    private static void LogAbandoned(ILogger migLogger, int nextAttempt) =>
        migLogger.LogInformation(
            "Startup migration abandoned before attempt {Attempt}/{MaxAttempts}: the host is shutting " +
            "down. Readiness left as {State}.",
            nextAttempt, MaxAttempts, MigrationReadiness.State);

    /// <summary>
    /// The idempotent, best-effort backfills that run once migrations have applied. Every one of
    /// them is individually try/caught: a backfill failure must NOT keep the app from serving,
    /// because the schema is already correct by the time they run.
    /// </summary>
    private static async Task RunPostMigrationBackfillsAsync(
        IServiceProvider scopedServices, ProcuLinkDbContext db, ILogger migLogger)
    {
        // ── Group V1: idempotent connection backfill ─────────────────
        // Turn each supplier's current loose config into a published "revision 1"
        // under a SupplierConnection (zero behaviour change). Idempotent — the
        // UNIQUE(org_id, supplier_id) connection guard makes re-runs a no-op, so
        // it's safe to call on every boot. Best-effort: a backfill failure must
        // NOT keep the app from serving (migrations already succeeded).
        try
        {
            var backfill = scopedServices.GetRequiredService<IConnectionBackfillService>();
            var createdCount = await backfill.BackfillAllAsync(CancellationToken.None);
            migLogger.LogInformation(
                "Group V1 connection backfill complete: {Count} new connection(s) created.", createdCount);
        }
        catch (Exception backfillEx)
        {
            migLogger.LogError(backfillEx,
                "Group V1 connection backfill failed (app stays up; orders fall back to live config).");
            SentrySdk.CaptureException(backfillEx);
        }

        // ── Launch batch 7 review fix: promoted-output re-backfill ───
        // Earlier V1 backfills left output_mapping_json NULL even when the supplier's
        // PoMappingConfig carried a promoted Output section (batch 4A) — flag-ON pinned
        // orders would silently lose the promoted layout. Fills ONLY null snapshots on
        // "system:backfill" rows; idempotent; per-row skip+warn keeps it compatible with
        // the published-row immutability trigger (AddReviewReasonAndPublishedRevision-
        // Immutability — it must run BEFORE that trigger migration is applied, or the
        // trigger must exempt NULL→value output fills, so rows are repaired rather than
        // skipped). Best-effort, like V1.
        try
        {
            var rebackfill = scopedServices.GetRequiredService<IConnectionBackfillService>();
            var repairedCount = await rebackfill.RebackfillPromotedOutputAsync(CancellationToken.None);
            migLogger.LogInformation(
                "Promoted-output re-backfill complete: {Count} backfilled revision(s) repaired.", repairedCount);
        }
        catch (Exception rebackfillEx)
        {
            migLogger.LogError(rebackfillEx,
                "Promoted-output re-backfill failed (app stays up; affected pinned orders use the fixed transformer until repaired).");
            SentrySdk.CaptureException(rebackfillEx);
        }

        // ── Credential binding backfill ──────────────────────────────
        // Re-encrypt pre-binding (version 1) credential blobs into the bound envelope, so
        // they gain the tenant + purpose + scope binding. Idempotent — a version-2 blob is
        // skipped — so it is safe on every boot. Best-effort: dual read means a blob left
        // on version 1 still works, so a failure here must NOT keep the app from serving.
        try
        {
            var rebind = scopedServices.GetRequiredService<ICredentialBindingBackfillService>();
            var reboundCount = await rebind.RebindLegacyCredentialsAsync(CancellationToken.None);
            migLogger.LogInformation(
                "Credential binding backfill complete: {Count} credential(s) rebound.", reboundCount);
        }
        catch (Exception rebindEx)
        {
            migLogger.LogError(rebindEx,
                "Credential binding backfill failed (app stays up; unbound credentials still decrypt).");
            SentrySdk.CaptureException(rebindEx);
        }

        // ── Inbound-email address backfill ───────────────────────────
        // Give every organisation a high-entropy inbound address, and register its existing public
        // slug as an EXPIRING address so mail already in flight — and every address book that has
        // the old address in it — keeps arriving across this deploy. Idempotent: an organisation
        // that already has both kinds is skipped, so this is safe on every boot.
        //
        // Best-effort like its neighbours, but the failure mode is worth naming: with no rows,
        // NOTHING resolves and inbound mail is deferred rather than lost — the router answers a
        // transient rejection, so the provider keeps re-delivering for ~10.5 hours and then files
        // the message as re-fireable. So a failure here delays inbound mail; it does not drop it,
        // and it must not stop the app serving every other channel.
        try
        {
            var inboundAddresses = scopedServices.GetRequiredService<IInboundAddressService>();
            var addressCount = await inboundAddresses.BackfillMissingAsync(CancellationToken.None);
            migLogger.LogInformation(
                "Inbound-address backfill complete: {Count} address row(s) inserted.", addressCount);
        }
        catch (Exception inboundAddressEx)
        {
            migLogger.LogError(inboundAddressEx,
                "Inbound-address backfill failed (app stays up; inbound mail is DEFERRED by the " +
                "provider's retries until this succeeds, not dropped).");
            SentrySdk.CaptureException(inboundAddressEx);
        }

        // ── Group V4: idempotent rule-definition seed + link ─────────
        // Seed the global rule catalog as org-scoped RuleDefinitions and link existing
        // free-floating acceptance rules to a matching definition. ZERO evaluation change
        // (rule scalar columns are never touched). Idempotent — UNIQUE(org_id, code) +
        // the "RuleDefinitionId is null" link guard make re-runs a no-op. Best-effort:
        // a failure must NOT keep the app from serving (migrations already succeeded).
        try
        {
            var ruleBackfill = scopedServices.GetRequiredService<IRuleDefinitionBackfillService>();
            var (defs, links) = await ruleBackfill.BackfillAllAsync(CancellationToken.None);
            migLogger.LogInformation(
                "Group V4 rule-definition backfill complete: {Defs} definition(s) seeded, {Links} rule(s) linked.",
                defs, links);
        }
        catch (Exception ruleBackfillEx)
        {
            migLogger.LogError(ruleBackfillEx,
                "Group V4 rule-definition backfill failed (app stays up; acceptance evaluation unaffected).");
            SentrySdk.CaptureException(ruleBackfillEx);
        }

        // ── Billing: idempotent org-plan-history baseline seed ───────
        // Orgs that predate org_plan_history get ONE baseline row from
        // their current plan + order-limit override (effective_from =
        // CreatedAt) so as-of overage metering has a floor; orgs with
        // any history row are skipped, so re-running every boot is a
        // no-op. Best-effort: a seed failure must NOT keep the app from
        // serving — metering falls back to current org values for
        // unseeded orgs (the pre-history behaviour).
        try
        {
            var seeded = await ProcuLink.Infrastructure.Services.OrgPlanHistorySeeder
                .SeedMissingBaselinesAsync(db, CancellationToken.None);
            migLogger.LogInformation(
                "Org plan-history baseline seed complete: {Count} baseline row(s) inserted.", seeded);
        }
        catch (Exception planHistorySeedEx)
        {
            migLogger.LogError(planHistorySeedEx,
                "Org plan-history baseline seed failed (app stays up; unseeded orgs meter with current plan values).");
            SentrySdk.CaptureException(planHistorySeedEx);
        }
    }

    // ── Phantom-migration helpers ────────────────────────────────────────────
    //
    // Phantom-prone migrations and their sentinel checks. Each entry pairs a
    // migration ID with a SQL boolean expression that is `true` when the
    // migration's SQL has clearly already been applied to the DB. If the
    // sentinel matches AND __EFMigrationsHistory has no row for the migration,
    // we insert one so EF treats it as applied.
    private static (string Id, string SentinelDescription, string SentinelSql)[] PhantomMigrations() => new[]
    {
        ("20260528120215_AddInvoicesAndLines",
         "table 'invoices' exists",
         "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'invoices')"),
        ("20260528120226_AddAdvanceShippingNotices",
         "table 'advance_shipping_notices' exists",
         "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'advance_shipping_notices')"),
        ("20260528120230_AddTenantApiKeysAndOrgSlug",
         "column 'organisations.slug' exists",
         "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'organisations' AND column_name = 'slug')"),
        ("20260528120235_AddIntegrationSubscriptions",
         "table 'integration_subscriptions' exists",
         "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'integration_subscriptions')"),
        ("20260528150709_AddIsSampleFlags",
         "column 'purchase_orders.is_sample' exists",
         "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'purchase_orders' AND column_name = 'is_sample')"),
    };

    private static async Task ReconcilePhantomMigrationsAsync(ProcuLinkDbContext db, ILogger logger)
    {
        // P1 — own the connection lifetime: open it explicitly here and close it
        // in a finally so it is returned to the pool before MigrateAsync runs.
        // Previously the connection was opened but never explicitly closed/disposed,
        // relying on EF scope disposal; that is a resource leak.
        var conn = db.Database.GetDbConnection();
        var openedByUs = conn.State != System.Data.ConnectionState.Open;
        if (openedByUs)
            await conn.OpenAsync();

        try
        {
            // Step 1: Does __EFMigrationsHistory exist? If not, MigrateAsync will
            // create it on first run and there is nothing phantom to reconcile.
            bool historyTableExists;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                    "WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory')";
                var result = await cmd.ExecuteScalarAsync();
                historyTableExists = result is bool b && b;
            }

            if (!historyTableExists)
            {
                logger.LogInformation(
                    "__EFMigrationsHistory does not exist — fresh database. Skipping phantom-migration check.");
                return;
            }

            // Step 2: Read an existing ProductVersion to stay consistent, fallback to 8.0.0.
            var productVersion = "8.0.0";
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT \"ProductVersion\" FROM \"__EFMigrationsHistory\" LIMIT 1";
                var result = await cmd.ExecuteScalarAsync();
                if (result is string s && !string.IsNullOrWhiteSpace(s))
                    productVersion = s;
            }

            // Step 3: Load applied migration IDs.
            var appliedIds = new HashSet<string>(StringComparer.Ordinal);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    appliedIds.Add(reader.GetString(0));
            }

            // Step 4: For each phantom-prone migration, check sentinel and insert
            // history row if needed.
            foreach (var (id, sentinelDescription, sentinelSql) in PhantomMigrations())
            {
                if (appliedIds.Contains(id))
                    continue; // Already recorded — nothing to do.

                bool sentinelExists;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sentinelSql;
                    var result = await cmd.ExecuteScalarAsync();
                    sentinelExists = result is bool b && b;
                }

                if (!sentinelExists)
                    continue; // Genuinely new migration — let MigrateAsync apply it.

                logger.LogWarning(
                    "Phantom migration {Id} detected (sentinel: {Sentinel}). Inserting history row.",
                    id, sentinelDescription);

                await using var insert = conn.CreateCommand();
                insert.CommandText =
                    "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                    "VALUES (@migId, @productVersion)";
                var migIdParam = insert.CreateParameter();
                migIdParam.ParameterName = "@migId";
                migIdParam.Value = id;
                insert.Parameters.Add(migIdParam);
                var pvParam = insert.CreateParameter();
                pvParam.ParameterName = "@productVersion";
                pvParam.Value = productVersion;
                insert.Parameters.Add(pvParam);
                await insert.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            // Close only if we were the ones who opened it; do not close a
            // connection that MigrateAsync (or EF itself) had already opened.
            if (openedByUs && conn.State == System.Data.ConnectionState.Open)
                await conn.CloseAsync();
        }
    }
}
