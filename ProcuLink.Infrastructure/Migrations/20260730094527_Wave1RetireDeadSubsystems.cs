using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Wave 1 "stop lying" — drops the storage for three subsystems that were proved to have no
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
    ///   <item><b>organisations.webhook_secret_encrypted</b> (WP-09) — the sole authenticator for
    ///     inbound webhook ingress. It had four read sites and NO writer, so every supplier callback
    ///     401'd from the day it shipped. NOT inbound email (Postmark, live) and NOT the org's
    ///     outbound webhook subscriptions (live) — both untouched.</item>
    /// </list>
    ///
    /// <para><c>Down</c> restores the tables, indexes, FKs and the column, so the migration is
    /// reversible in shape. It cannot restore ROWS — drop the tables only after the founder has
    /// accepted that the data is inert (no reader ever consumed it).</para>
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

            migrationBuilder.DropColumn(
                name: "webhook_secret_encrypted",
                table: "organisations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "webhook_secret_encrypted",
                table: "organisations",
                type: "text",
                nullable: true);

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
