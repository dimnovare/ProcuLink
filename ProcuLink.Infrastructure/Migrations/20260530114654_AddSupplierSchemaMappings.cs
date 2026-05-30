using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierSchemaMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_schema_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    column_name_hash = table.Column<string>(type: "text", nullable: false),
                    detected_format = table.Column<string>(type: "text", nullable: false),
                    field_mapping = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    learned_from_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    observation_count = table.Column<int>(type: "integer", nullable: false),
                    last_learned_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_schema_mappings", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_schema_mappings_organisations_organisation_id",
                        column: x => x.organisation_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_supplier_schema_mappings_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_schema_mappings_organisation_id_supplier_id_column~",
                table: "supplier_schema_mappings",
                columns: new[] { "organisation_id", "supplier_id", "column_name_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_schema_mappings_supplier_id",
                table: "supplier_schema_mappings",
                column: "supplier_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_schema_mappings");
        }
    }
}
