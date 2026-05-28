using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyersRulesTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "buyers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buyers", x => x.id);
                    table.ForeignKey(
                        name: "FK_buyers_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "output_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    config_json = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
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
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    entity = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    auto_block = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    trigger_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_triggered_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
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
                name: "IX_buyers_org_id",
                table: "buyers",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_output_templates_org_id",
                table: "output_templates",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_validation_rules_org_id",
                table: "validation_rules",
                column: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "buyers");

            migrationBuilder.DropTable(
                name: "output_templates");

            migrationBuilder.DropTable(
                name: "validation_rules");
        }
    }
}
