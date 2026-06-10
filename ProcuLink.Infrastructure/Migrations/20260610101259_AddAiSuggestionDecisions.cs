using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSuggestionDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_suggestion_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    suggested_supplier_item_code = table.Column<string>(type: "text", nullable: false),
                    chosen_supplier_item_code = table.Column<string>(type: "text", nullable: true),
                    candidate_set_json = table.Column<string>(type: "jsonb", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    model_version = table.Column<string>(type: "text", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_suggestion_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_suggestion_decisions_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_suggestion_decisions_idempotency",
                table: "ai_suggestion_decisions",
                columns: new[] { "org_id", "order_id", "line_number", "suggested_supplier_item_code", "decision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_suggestion_decisions_org_id_order_id_decided_at",
                table: "ai_suggestion_decisions",
                columns: new[] { "org_id", "order_id", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_suggestion_decisions");
        }
    }
}
