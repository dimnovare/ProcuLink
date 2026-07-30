using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Wave 1 "stop lying" — drops the storage for two subsystems that were proved to have no
    /// consumer (see <c>RetiredSubsystemsStayRetiredTests</c> for the standing orphan guard):
    ///
    /// <list type="bullet">
    ///   <item><b>output_templates</b> (WP-06) — nothing in parse, mapping, transform, delivery or
    ///     revision ever read <c>config_json</c>. The UI's "N suppliers" count was a join on
    ///     matching output-format strings, not a real assignment, and the editor's POST body never
    ///     bound to the column, so edits were silently discarded.</item>
    ///   <item><b>validation_rules</b> (WP-07) — a second rule engine that never ran. The only
    ///     consumer of its service was its own controller; the drawer's "Triggered 0 times" was
    ///     literally true. <c>supplier_acceptance_rules</c> is the engine that actually evaluates.
    ///     The six seeded defaults are deliberately NOT migrated into it: they were never
    ///     evaluated, so importing them would start blocking orders that flow fine today.</item>
    /// </list>
    ///
    /// <para><b>WP-09 (<c>organisations.webhook_secret_encrypted</c>) is deliberately NOT dropped
    /// here — this is the EXPAND half of an expand/contract, and the contract half is a separate,
    /// later deploy.</b> The column is the retired inbound-webhook authenticator; this release stops
    /// MAPPING it (the property is gone from <c>Organisation</c> and from
    /// <c>ProcuLinkDbContext</c>), but the physical column stays, unread, until both services are
    /// running code that no longer maps it.</para>
    ///
    /// <para><b>Why the drop cannot ride along.</b> Migrations run at API startup
    /// (<c>ProcuLink.Api/Program.cs</c> — <c>await db.Database.MigrateAsync()</c>). The Worker
    /// (Railway service <c>aware-amazement</c>) is a SEPARATE service that deploys independently and
    /// never migrates. EF enumerates every mapped column for a non-projected entity query, and two
    /// hot paths materialise the whole <c>Organisation</c>:
    /// <list type="bullet">
    ///   <item><c>StripeBillingService.LoadOrgAsync</c> — reached from
    ///     <c>EmailPollOrgJob</c>'s <c>HasFeatureAsync</c> gate on EVERY IMAP poll cycle.</item>
    ///   <item><c>EmailSettingsService.UpdateAsync</c>.</item>
    /// </list>
    /// So from the moment a new API migrated until the Worker redeployed, the still-old Worker would
    /// throw Npgsql <c>42703 — column o.webhook_secret_encrypted does not exist</c> on every org
    /// load: IMAP polling and every Worker-side billing gate would stop, for an unbounded window if
    /// the Worker deploy lagged. Per CLAUDE.md the Worker is mandatory — nothing parses or delivers
    /// without it. The two <c>DROP TABLE</c>s above are harmless in that same window because no
    /// build, old or new, queries those tables at all.</para>
    ///
    /// <para><b>Required deploy sequence.</b>
    /// <list type="number">
    ///   <item>Merge + deploy THIS migration. API and Worker both stop mapping the column; the
    ///     column still exists, so an old replica still draining is fine too.</item>
    ///   <item>Confirm BOTH Railway services (<c>ProcuLink</c> API and <c>aware-amazement</c>
    ///     Worker) are running the new build.</item>
    ///   <item>Only then merge the follow-up migration that drops the
    ///     <c>webhook_secret_encrypted</c> column from <c>organisations</c>. It is a hand-written
    ///     migration with NO model change (the model already omits the property, so
    ///     <c>dotnet ef migrations add</c> will not generate it), and it must delete
    ///     <c>Wave1ColumnDropStaysDeferredTests</c>, which exists to make step 3 a deliberate act.</item>
    /// </list></para>
    ///
    /// <para><b><c>Down</c> restores SHAPE, never ROWS.</b> It recreates the two tables with their
    /// indexes and FKs, so the migration is reversible as a schema change — but every row in
    /// <c>output_templates</c> and <c>validation_rules</c> is destroyed IRREVERSIBLY the moment this
    /// runs on production, and there is no export. "Inert" (nothing reads it) is not "empty":
    /// <c>validation_rules</c> ships six seeded defaults per org, and the LIVE sibling engine keeps
    /// its own per-org rows in <c>rule_definitions</c> / <c>supplier_acceptance_rules</c>, which this
    /// migration does not touch.</para>
    ///
    /// <para><b>FOUNDER CHECK — run against production BEFORE merging.</b> Nothing here enforces it:
    /// no automatic export, no guard that blocks the migration. Deciding that this data may be
    /// destroyed is a founder call, and this query is what that call is made on.</para>
    /// <code>
    /// SELECT 'output_templates  (DROPPED)' AS tbl,
    ///        count(*) AS rows, count(DISTINCT org_id) AS scoped_to FROM output_templates
    /// UNION ALL SELECT 'validation_rules  (DROPPED)',
    ///        count(*),         count(DISTINCT org_id)            FROM validation_rules
    /// UNION ALL SELECT 'rule_definitions  (LIVE, kept)',
    ///        count(*),         count(DISTINCT org_id)            FROM rule_definitions
    /// UNION ALL SELECT 'supplier_acceptance_rules  (LIVE, kept)',
    ///        count(*),         count(DISTINCT profile_id)        FROM supplier_acceptance_rules;
    /// </code>
    /// <para>(<c>supplier_acceptance_rules</c> is scoped by <c>profile_id</c>, not <c>org_id</c> —
    /// hence the differing last column. Verified to run on a schema built from these migrations.)</para>
    /// <para>A non-zero count on either DROPPED row is not automatically a blocker — the orphan proof
    /// says nothing ever read those rows — but it IS the number the founder is agreeing to lose.</para>
    /// </summary>
    public partial class Wave1RetireDeadSubsystems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "output_templates");

            migrationBuilder.DropTable(
                name: "validation_rules");

            // NO DropColumn for organisations.webhook_secret_encrypted — see the class doc above.
            // The column is unmapped by this release and dropped by a LATER, separate migration,
            // once both the API and the independently-deployed Worker carry the unmapped entity.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "output_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_output_templates", x => x.id);
                    table.ForeignKey(
                        name: "FK_output_templates_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "validation_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    auto_block = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    entity = table.Column<string>(type: "text", nullable: false),
                    last_triggered_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    trigger_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_validation_rules_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_output_templates_org_id",
                table: "output_templates",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_rules_org_id",
                table: "validation_rules",
                column: "org_id");
        }
    }
}
