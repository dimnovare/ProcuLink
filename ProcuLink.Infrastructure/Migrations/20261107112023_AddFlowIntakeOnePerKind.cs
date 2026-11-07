using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowIntakeOnePerKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_flow_intakes_flow_id_kind",
                table: "flow_intakes",
                columns: new[] { "flow_id", "kind" },
                unique: true,
                filter: "kind IN ('email','api')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_flow_intakes_flow_id_kind",
                table: "flow_intakes");
        }
    }
}
