using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailPollingFlagAndPollingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "email_polling_enabled",
                table: "organisations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: preserve the legacy "email_config <> '{}'" poller predicate exactly —
            // every org that already has a non-empty email config becomes a poller candidate,
            // so the indexed flag enqueues the same set of orgs the jsonb scan did.
            migrationBuilder.Sql(
                "UPDATE organisations SET email_polling_enabled = TRUE " +
                "WHERE email_config IS NOT NULL AND email_config::text <> '{}';");

            migrationBuilder.CreateIndex(
                name: "IX_sftp_ingress_configs_is_enabled",
                table: "sftp_ingress_configs",
                column: "is_enabled",
                filter: "is_enabled = true");

            migrationBuilder.CreateIndex(
                name: "IX_s3_ingress_configs_is_enabled",
                table: "s3_ingress_configs",
                column: "is_enabled",
                filter: "is_enabled = true");

            migrationBuilder.CreateIndex(
                name: "IX_organisations_email_polling_enabled",
                table: "organisations",
                column: "email_polling_enabled",
                filter: "email_polling_enabled = true");

            migrationBuilder.CreateIndex(
                name: "IX_item_mappings_org_id_supplier_id_updated_at",
                table: "item_mappings",
                columns: new[] { "org_id", "supplier_id", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sftp_ingress_configs_is_enabled",
                table: "sftp_ingress_configs");

            migrationBuilder.DropIndex(
                name: "IX_s3_ingress_configs_is_enabled",
                table: "s3_ingress_configs");

            migrationBuilder.DropIndex(
                name: "IX_organisations_email_polling_enabled",
                table: "organisations");

            migrationBuilder.DropIndex(
                name: "IX_item_mappings_org_id_supplier_id_updated_at",
                table: "item_mappings");

            migrationBuilder.DropColumn(
                name: "email_polling_enabled",
                table: "organisations");
        }
    }
}
