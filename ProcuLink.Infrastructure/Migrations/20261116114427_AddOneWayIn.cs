using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOneWayIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_inbox",
                table: "flows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "flow_documents",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sender_key",
                table: "flow_documents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sort_unconfirmed",
                table: "flow_documents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "flow_sort_memory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_sort_memory", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_sort_memory_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_sort_memory_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_flows_one_inbox_per_org",
                table: "flows",
                column: "org_id",
                unique: true,
                filter: "is_inbox");

            migrationBuilder.CreateIndex(
                name: "IX_flow_documents_org_id_kind",
                table: "flow_documents",
                columns: new[] { "org_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_flow_sort_memory_flow_id",
                table: "flow_sort_memory",
                column: "flow_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_sort_memory_org_id_sender_key_kind",
                table: "flow_sort_memory",
                columns: new[] { "org_id", "sender_key", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_sort_memory");

            migrationBuilder.DropIndex(
                name: "ux_flows_one_inbox_per_org",
                table: "flows");

            migrationBuilder.DropIndex(
                name: "IX_flow_documents_org_id_kind",
                table: "flow_documents");

            migrationBuilder.DropColumn(
                name: "is_inbox",
                table: "flows");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "flow_documents");

            migrationBuilder.DropColumn(
                name: "sender_key",
                table: "flow_documents");

            migrationBuilder.DropColumn(
                name: "sort_unconfirmed",
                table: "flow_documents");
        }
    }
}
