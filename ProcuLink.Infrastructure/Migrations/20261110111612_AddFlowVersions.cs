using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "published_version_no",
                table: "flows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "flow_version",
                table: "flow_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "flow_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    published_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    published_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_versions_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_versions_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_versions_flow_id_version_no",
                table: "flow_versions",
                columns: new[] { "flow_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flow_versions_org_id",
                table: "flow_versions",
                column: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_versions");

            migrationBuilder.DropColumn(
                name: "published_version_no",
                table: "flows");

            migrationBuilder.DropColumn(
                name: "flow_version",
                table: "flow_documents");
        }
    }
}
