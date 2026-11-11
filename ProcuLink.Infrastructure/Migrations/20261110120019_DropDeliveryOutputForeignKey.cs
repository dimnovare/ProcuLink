using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropDeliveryOutputForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_flow_deliveries_flow_outputs_output_id",
                table: "flow_deliveries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_flow_deliveries_flow_outputs_output_id",
                table: "flow_deliveries",
                column: "output_id",
                principalTable: "flow_outputs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
