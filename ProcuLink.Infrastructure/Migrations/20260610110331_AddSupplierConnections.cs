using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "connection_revision_id",
                table: "purchase_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "connection_revision_item_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_item_code = table.Column<string>(type: "text", nullable: false),
                    supplier_item_code = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<float>(type: "real", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_revision_item_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "connection_revision_test_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    sample_source_file_key = table.Column<string>(type: "text", nullable: true),
                    expected_output_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_revision_test_cases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "supplier_connection_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    effective_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    published_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    input_mapping_json = table.Column<string>(type: "jsonb", nullable: true),
                    output_mapping_json = table.Column<string>(type: "jsonb", nullable: true),
                    output_format = table.Column<string>(type: "text", nullable: true),
                    delivery_protocol = table.Column<string>(type: "text", nullable: true),
                    delivery_config_json = table.Column<string>(type: "jsonb", nullable: true),
                    delivery_auto_deliver = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    credentials_ref = table.Column<string>(type: "text", nullable: true),
                    acceptance_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acceptance_version_no = table.Column<int>(type: "integer", nullable: true),
                    catalog_mode = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_connection_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_connection_revisions_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    active_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_connections_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_supplier_connections_supplier_connection_revisions_active_r~",
                        column: x => x.active_revision_id,
                        principalTable: "supplier_connection_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supplier_connections_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_revision_item_mappings_revision_id",
                table: "connection_revision_item_mappings",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_connection_revision_test_cases_revision_id",
                table: "connection_revision_test_cases",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_connection_revisions_connection_version",
                table: "supplier_connection_revisions",
                columns: new[] { "connection_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_connection_revisions_org_supplier_status",
                table: "supplier_connection_revisions",
                columns: new[] { "org_id", "supplier_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_connections_active_revision_id",
                table: "supplier_connections",
                column: "active_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_connections_org_supplier",
                table: "supplier_connections",
                columns: new[] { "org_id", "supplier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_connections_supplier_id",
                table: "supplier_connections",
                column: "supplier_id");

            migrationBuilder.AddForeignKey(
                name: "FK_connection_revision_item_mappings_supplier_connection_revis~",
                table: "connection_revision_item_mappings",
                column: "revision_id",
                principalTable: "supplier_connection_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_connection_revision_test_cases_supplier_connection_revision~",
                table: "connection_revision_test_cases",
                column: "revision_id",
                principalTable: "supplier_connection_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_connection_revisions_supplier_connections_connecti~",
                table: "supplier_connection_revisions",
                column: "connection_id",
                principalTable: "supplier_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_supplier_connections_supplier_connection_revisions_active_r~",
                table: "supplier_connections");

            migrationBuilder.DropTable(
                name: "connection_revision_item_mappings");

            migrationBuilder.DropTable(
                name: "connection_revision_test_cases");

            migrationBuilder.DropTable(
                name: "supplier_connection_revisions");

            migrationBuilder.DropTable(
                name: "supplier_connections");

            migrationBuilder.DropColumn(
                name: "connection_revision_id",
                table: "purchase_orders");
        }
    }
}
