using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowDeliveryRemoteReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "receipt_key",
                table: "flow_deliveries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_environment",
                table: "flow_deliveries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_reference",
                table: "flow_deliveries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "receipt_key",
                table: "flow_deliveries");

            migrationBuilder.DropColumn(
                name: "remote_environment",
                table: "flow_deliveries");

            migrationBuilder.DropColumn(
                name: "remote_reference",
                table: "flow_deliveries");
        }
    }
}
