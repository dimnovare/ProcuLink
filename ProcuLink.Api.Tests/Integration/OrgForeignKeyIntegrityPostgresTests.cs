using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Behavioural half of the org-foreign-key fix. The model pins live in
/// <c>ProcuLink.Api.Tests/Architecture/OrgForeignKeyCoverageTests</c>; this proves the database
/// really behaves the way those pins claim.
///
/// <para>Fifteen mapped tables carried an organisation column that nothing checked. Nine of them
/// lead an index with it, so every query against them read as if the tenancy were enforced. The
/// three things a foreign key buys, and none of which an index does, are asserted here on real
/// Postgres:</para>
///
/// <list type="number">
/// <item>A row naming an organisation that does not exist is REJECTED at insert.</item>
/// <item>Deleting an organisation with RAW SQL — the path that bypasses every service, and the one
/// that actually produced this schema's first GDPR orphan — takes the derived ledgers with it.</item>
/// <item>That same delete is REFUSED while billing evidence exists, rather than silently taking the
/// record of what was charged.</item>
/// </list>
///
/// <para>A fourth test covers the other half of the migration, which is not about foreign keys at
/// all: the org-wide audit listing had no index that could serve its sort.</para>
///
/// <para>Needs REAL Postgres. EF InMemory enforces no foreign key, executes no raw SQL, and has no
/// planner. Docker-gated via <see cref="DockerRequiredFactAttribute"/>.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class OrgForeignKeyIntegrityPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_orgfk");

        var connectionString = new NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_databaseConnectionString);

    private ProcuLinkDbContext NewContext() => new(_options!);

    private async Task<Guid> SeedOrganisationAsync()
    {
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_orgfk_{orgId:N}",
            Name = "org-fk",
            Slug = $"org-fk-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
            TrialStartedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    /// <summary>
    /// Raw DDL/DML, deliberately: the whole point is the path that bypasses EF and every service.
    /// </summary>
    private async Task ExecuteRawAsync(string sql)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<string> ScalarTextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (await cmd.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }

    // ── 1. a row naming a tenant that does not exist ──────────────────────────

    [DockerRequiredFact]
    public async Task ALedgerRowNamingAnOrganisationThatDoesNotExist_IsRejected()
    {
        // idempotency_keys stands for the seven derived ledgers: before this migration its org_id
        // led the composite primary key, so the column was indexed, queried and completely
        // unchecked. Nothing stopped a write that computed the wrong tenant.
        var ghostOrg = Guid.NewGuid();

        var insert = async () => await ExecuteRawAsync($"""
            INSERT INTO idempotency_keys (org_id, key, order_id, created_at)
            VALUES ('{ghostOrg}', 'ghost-key', '{Guid.NewGuid()}', now());
            """);

        var refusal = (await insert.Should().ThrowAsync<Exception>(
            "an organisation id that names no organisation must not be storable")).Which;

        SqlStateOf(refusal).Should().Be(PostgresErrorCodes.ForeignKeyViolation,
            "the refusal must come from the foreign key (23503), not from a typo in the SQL or " +
            "some unrelated error — an assertion that accepted any exception would pass on both");
    }

    // ── 2. a raw organisation delete takes the derived ledgers with it ────────

    [DockerRequiredFact]
    public async Task ARawOrganisationDelete_CascadesTheDerivedLedgersAway()
    {
        var orgId = await SeedOrganisationAsync();
        var now = DateTime.UtcNow;

        await using (var db = NewContext())
        {
            db.AiUsageMonthly.Add(new AiUsageMonthly
            {
                OrgId = orgId, Year = 2026, Month = 8, TokensUsed = 1_234, UpdatedAt = now,
            });
            db.EmailImportRecords.Add(new EmailImportRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId, ImapMessageId = "<msg-1@example.test>",
                AttachmentHash = "hash-1", OrderId = Guid.NewGuid(), FileName = "po.csv", ImportedAt = now,
            });
            db.CanonicalFieldDefs.Add(new CanonicalFieldDef
            {
                Id = Guid.NewGuid(), OrgId = orgId, Key = "custom_field", Label = "Custom field",
                Scope = "header", Type = "string", CreatedAt = now, UpdatedAt = now,
            });
            db.SchemaFingerprints.Add(new SchemaFingerprint
            {
                Id = Guid.NewGuid(), OrganisationId = orgId, ColumnNameHash = new string('a', 64),
                DetectedFormat = "csv", ParseSuccessCount = 1, LastSeenAt = now, CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        // Raw SQL on purpose. DataErasureService is not in this path and never was: the historical
        // orphan in this schema came from a direct delete, which is exactly what the constraint has
        // to survive.
        await ExecuteRawAsync($"DELETE FROM organisations WHERE id = '{orgId}';");

        await using var check = NewContext();
        (await check.AiUsageMonthly.CountAsync(x => x.OrgId == orgId)).Should().Be(0,
            "the AI token counter is derived state and must not outlive its tenant");
        (await check.EmailImportRecords.CountAsync(x => x.OrgId == orgId)).Should().Be(0,
            "the IMAP dedupe ledger carries the organisation's own message ids and file names");
        (await check.CanonicalFieldDefs.CountAsync(x => x.OrgId == orgId)).Should().Be(0,
            "custom canonical fields describe one organisation's document model and nothing else");
        (await check.SchemaFingerprints.CountAsync(x => x.OrganisationId == orgId)).Should().Be(0,
            "schema fingerprints are org-scoped detection statistics with no cross-org sharing");
    }

    // ── 3. billing evidence refuses the same delete ───────────────────────────

    [DockerRequiredFact]
    public async Task ARawOrganisationDelete_IsRefused_WhileItsOverageBillingLedgerExists()
    {
        var orgId = await SeedOrganisationAsync();

        await using (var db = NewContext())
        {
            db.OverageBillingRecords.Add(new OverageBillingRecord
            {
                Id = Guid.NewGuid(), OrgId = orgId,
                BillingKey = $"{orgId}:2026-08-01T00:00:00.0000000+00:00",
                OverageOrders = 12, AmountCents = 600,
                StripeInvoiceItemId = "ii_test_orgfk", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var delete = async () => await ExecuteRawAsync($"DELETE FROM organisations WHERE id = '{orgId}';");

        var refusal = (await delete.Should().ThrowAsync<Exception>(
            "overage_billing_records is the record of money actually charged; the delete has to " +
            "fail and be dealt with deliberately rather than quietly taking the evidence")).Which;

        SqlStateOf(refusal).Should().Be(PostgresErrorCodes.ForeignKeyViolation,
            "the refusal must come from the RESTRICT foreign key (23503), not from some unrelated error");

        await using var check = NewContext();
        (await check.Organisations.CountAsync(o => o.Id == orgId)).Should().Be(1,
            "the refusal must leave the organisation in place, not half-delete it");
        (await check.OverageBillingRecords.CountAsync(x => x.OrgId == orgId)).Should().Be(1);
    }

    /// <summary>
    /// The counterweight to the test above, and the reason <c>org_plan_history</c> carries CASCADE
    /// rather than the RESTRICT its role as invoice working would suggest.
    ///
    /// <para><c>AppendOrgPlanHistoryAsync</c> writes a baseline plan-history row for EVERY
    /// organisation the moment it is created — nobody asks for it, and a free Pilot that is never
    /// charged gets one too. So a RESTRICT there would not block deletes for organisations with a
    /// billing trail; it would block every delete, forever, which is a ban rather than a decision
    /// and would break the first org-erasure path anyone writes. Both halves are asserted, because
    /// the argument only holds if the automatic row is really there.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task EveryNewOrganisationAlreadyHasPlanHistory_SoItCannotBeWhatBlocksADelete()
    {
        var orgId = await SeedOrganisationAsync();

        await using (var seeded = NewContext())
        {
            (await seeded.OrgPlanHistories.CountAsync(x => x.OrgId == orgId)).Should().BeGreaterThan(0,
                "AppendOrgPlanHistoryAsync writes a baseline row for every organisation at creation. " +
                "If that ever stops being true, RESTRICT on org_plan_history becomes reasonable again " +
                "and this whole argument needs revisiting");
        }

        // Add a second, explicit row so the delete has real history to carry, not just the baseline.
        await using (var db = NewContext())
        {
            db.OrgPlanHistories.Add(new OrgPlanHistory
            {
                Id = Guid.NewGuid(), OrgId = orgId, Plan = "operations",
                OrderLimitOverride = null, EffectiveFrom = DateTimeOffset.UtcNow.AddMonths(-2),
            });
            await db.SaveChangesAsync();
        }

        await ExecuteRawAsync($"DELETE FROM organisations WHERE id = '{orgId}';");

        await using var check = NewContext();
        (await check.Organisations.CountAsync(o => o.Id == orgId)).Should().Be(0,
            "an organisation with nothing but plan history must stay deletable — a constraint that " +
            "fires for every row is not a decision about any of them");
        (await check.OrgPlanHistories.CountAsync(x => x.OrgId == orgId)).Should().Be(0,
            "the plan history follows its organisation; the record of an actual charge is what " +
            "refuses the delete, and that lives in overage_billing_records");
    }

    // ── 4. the audit listing's index ──────────────────────────────────────────

    [DockerRequiredFact]
    public async Task TheOrgWideAuditListing_IsServedByAnIndexInsteadOfSortingTheWholeHistory()
    {
        var orgId = await SeedOrganisationAsync();

        // Enough rows for the planner to have a real choice. A handful would be a sequential scan
        // either way and the test would prove nothing about the index.
        await ExecuteRawAsync($"""
            INSERT INTO audit_events (id, org_id, user_id, entity_type, entity_id, action, created_at)
            SELECT gen_random_uuid(), '{orgId}', NULL, 'order', gen_random_uuid(), 'updated',
                   now() - (g || ' seconds')::interval
            FROM generate_series(1, 20000) AS g;
            """);
        await ExecuteRawAsync("ANALYZE audit_events;");

        // AuditController's org-wide listing, shape for shape: filter on org_id, order by
        // created_at descending, page. Deliberately no entity predicate — that is the case the
        // pre-existing (org_id, entity_type, entity_id, created_at) index cannot serve.
        var plan = await ScalarTextAsync($"""
            EXPLAIN (FORMAT JSON)
            SELECT id, action, created_at FROM audit_events
            WHERE org_id = '{orgId}'
            ORDER BY created_at DESC
            OFFSET 0 LIMIT 50;
            """);

        plan.Should().Contain("IX_audit_events_org_id_created_at_desc",
            "the listing must be served by the index built for it; the plan chosen was:\n" + plan);
        plan.Should().NotContain("Sort",
            "a Sort node means the planner read the organisation's whole audit history and ordered " +
            "it in memory, which is precisely what this index exists to stop. Plan:\n" + plan);
    }

    /// <summary>
    /// EF wraps provider failures in <see cref="DbUpdateException"/> and friends, so the assertions
    /// above unwrap rather than matching on the outermost type — a test that accepted any exception
    /// would pass on a typo in the SQL just as happily as on a real constraint refusal.
    /// </summary>
    private static string? SqlStateOf(Exception ex) => FindPostgresException(ex)?.SqlState;

    private static PostgresException? FindPostgresException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is PostgresException pg)
                return pg;
            ex = ex.InnerException;
        }

        return null;
    }
}
