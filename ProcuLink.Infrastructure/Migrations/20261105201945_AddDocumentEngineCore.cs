using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentEngineCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    fields_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    fields_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flows", x => x.id);
                    table.ForeignKey(
                        name: "FK_flows_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flow_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    source_file_key = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    billing_units = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    read_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    checks_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    dedup_key = table.Column<string>(type: "text", nullable: true),
                    is_sample = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_documents_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_documents_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_documents_flow_id",
                table: "flow_documents",
                column: "flow_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_documents_org_id_flow_id_dedup_key",
                table: "flow_documents",
                columns: new[] { "org_id", "flow_id", "dedup_key" });

            migrationBuilder.CreateIndex(
                name: "IX_flow_documents_org_id_flow_id_sha256",
                table: "flow_documents",
                columns: new[] { "org_id", "flow_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_flows_org_id",
                table: "flows",
                column: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_documents");

            migrationBuilder.DropTable(
                name: "flows");
        }
    }
}
