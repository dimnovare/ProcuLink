using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Wave2AcceptanceProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_validation_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_validation_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_validation_results_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_acceptance_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: true),
                    output_format = table.Column<string>(type: "text", nullable: true),
                    effective_from = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    effective_to = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_acceptance_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_acceptance_profiles_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_acceptance_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    field_path = table.Column<string>(type: "text", nullable: false),
                    @operator = table.Column<string>(name: "operator", type: "text", nullable: false),
                    expected_value = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    block_on_fail = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_acceptance_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_acceptance_rules_supplier_acceptance_profiles_prof~",
                        column: x => x.profile_id,
                        principalTable: "supplier_acceptance_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_validation_results_org_id_order_id",
                table: "order_validation_results",
                columns: new[] { "org_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_acceptance_profiles_org_supplier_status",
                table: "supplier_acceptance_profiles",
                columns: new[] { "org_id", "supplier_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_acceptance_profiles_org_supplier_version",
                table: "supplier_acceptance_profiles",
                columns: new[] { "org_id", "supplier_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_acceptance_rules_profile_id",
                table: "supplier_acceptance_rules",
                column: "profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_validation_results");

            migrationBuilder.DropTable(
                name: "supplier_acceptance_rules");

            migrationBuilder.DropTable(
                name: "supplier_acceptance_profiles");
        }
    }
}
