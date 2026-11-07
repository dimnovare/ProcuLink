using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "flow_id",
                table: "org_inbound_addresses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "flow_documents",
                type: "text",
                nullable: false,
                defaultValue: "upload");

            migrationBuilder.AddColumn<string>(
                name: "source_ref",
                table: "flow_documents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "flow_intake_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_intake_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_intake_items_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flow_intakes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    encrypted_credentials = table.Column<string>(type: "text", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_run_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    last_status = table.Column<string>(type: "text", nullable: true),
                    last_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_intakes", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_intakes_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_intakes_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_intake_items_org_id",
                table: "flow_intake_items",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_intake_items_scope_id_item_key",
                table: "flow_intake_items",
                columns: new[] { "scope_id", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_intakes_flow_id",
                table: "flow_intakes",
                column: "flow_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_intakes_org_id_flow_id",
                table: "flow_intakes",
                columns: new[] { "org_id", "flow_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_intake_items");

            migrationBuilder.DropTable(
                name: "flow_intakes");

            migrationBuilder.DropColumn(
                name: "flow_id",
                table: "org_inbound_addresses");

            migrationBuilder.DropColumn(
                name: "source",
                table: "flow_documents");

            migrationBuilder.DropColumn(
                name: "source_ref",
                table: "flow_documents");
        }
    }
}
