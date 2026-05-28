using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIsSampleFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code",
                table: "suppliers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_sample",
                table: "suppliers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_sample",
                table: "purchase_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "is_sample",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "is_sample",
                table: "purchase_orders");
        }
    }
}
