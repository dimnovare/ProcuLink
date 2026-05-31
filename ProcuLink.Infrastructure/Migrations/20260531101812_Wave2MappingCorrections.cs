using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Wave2MappingCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "applied_count",
                table: "item_mappings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "mapping_corrections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mapping_id = table.Column<Guid>(type: "uuid", nullable: false),
                    old_supplier_item_code = table.Column<string>(type: "text", nullable: false),
                    new_supplier_item_code = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    corrected_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mapping_corrections", x => x.id);
                    table.ForeignKey(
                        name: "FK_mapping_corrections_item_mappings_mapping_id",
                        column: x => x.mapping_id,
                        principalTable: "item_mappings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mapping_corrections_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mapping_corrections_mapping_id",
                table: "mapping_corrections",
                column: "mapping_id");

            migrationBuilder.CreateIndex(
                name: "IX_mapping_corrections_org_id_mapping_id_corrected_at",
                table: "mapping_corrections",
                columns: new[] { "org_id", "mapping_id", "corrected_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mapping_corrections");

            migrationBuilder.DropColumn(
                name: "applied_count",
                table: "item_mappings");
        }
    }
}
