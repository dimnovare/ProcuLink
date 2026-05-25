using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiMappingSuggestionsToOrderLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_suggested_supplier_item_code",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "ai_suggestion_confidence",
                table: "purchase_order_lines",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_suggestion_provenance",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_suggestion_reason",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ai_suggested_supplier_item_code",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "ai_suggestion_confidence",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "ai_suggestion_provenance",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "ai_suggestion_reason",
                table: "purchase_order_lines");
        }
    }
}
