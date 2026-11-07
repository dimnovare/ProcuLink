using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutputSendWhen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "send_when_json",
                table: "flow_outputs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "send_when_json",
                table: "flow_outputs");
        }
    }
}
