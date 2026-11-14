using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowReadCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flow_read_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    tokens_in = table.Column<int>(type: "integer", nullable: false),
                    tokens_out = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_read_calls", x => x.id);
                    table.ForeignKey(
                        name: "FK_flow_read_calls_flow_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "flow_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_flow_read_calls_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flow_read_calls_document_id",
                table: "flow_read_calls",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_flow_read_calls_org_id_created_at",
                table: "flow_read_calls",
                columns: new[] { "org_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_read_calls");
        }
    }
}
