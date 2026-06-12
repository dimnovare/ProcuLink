using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierCatalogSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_catalog_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    encrypted_password = table.Column<string>(type: "text", nullable: true),
                    remote_path = table.Column<string>(type: "text", nullable: false),
                    file_format = table.Column<string>(type: "text", nullable: false, defaultValue: "auto"),
                    sync_interval_hours = table.Column<int>(type: "integer", nullable: false, defaultValue: 24),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    last_sync_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_sync_status = table.Column<string>(type: "text", nullable: true),
                    last_sync_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_sync_created = table.Column<int>(type: "integer", nullable: true),
                    last_sync_updated = table.Column<int>(type: "integer", nullable: true),
                    last_sync_skipped = table.Column<int>(type: "integer", nullable: true),
                    last_file_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_catalog_sources", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_catalog_sources_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_supplier_catalog_sources_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_catalog_sources_org_id_supplier_id",
                table: "supplier_catalog_sources",
                columns: new[] { "org_id", "supplier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_catalog_sources_supplier_id",
                table: "supplier_catalog_sources",
                column: "supplier_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_catalog_sources");
        }
    }
}
