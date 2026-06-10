using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "rule_code",
                table: "supplier_acceptance_rules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "rule_definition_id",
                table: "supplier_acceptance_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rule_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: false),
                    field_path = table.Column<string>(type: "text", nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "text", nullable: false),
                    default_severity = table.Column<string>(type: "text", nullable: false),
                    default_expected_value = table.Column<string>(type: "text", nullable: true),
                    param_hint = table.Column<string>(type: "text", nullable: true),
                    ubl_ref = table.Column<string>(type: "text", nullable: true),
                    edifact_ref = table.Column<string>(type: "text", nullable: true),
                    x12_ref = table.Column<string>(type: "text", nullable: true),
                    cxml_ref = table.Column<string>(type: "text", nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_definitions", x => x.id);
                    table.ForeignKey(
                        name: "FK_rule_definitions_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_acceptance_rules_rule_definition_id",
                table: "supplier_acceptance_rules",
                column: "rule_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_rule_definitions_org_code",
                table: "rule_definitions",
                columns: new[] { "org_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rule_definitions");

            migrationBuilder.DropIndex(
                name: "IX_supplier_acceptance_rules_rule_definition_id",
                table: "supplier_acceptance_rules");

            migrationBuilder.DropColumn(
                name: "rule_code",
                table: "supplier_acceptance_rules");

            migrationBuilder.DropColumn(
                name: "rule_definition_id",
                table: "supplier_acceptance_rules");
        }
    }
}
