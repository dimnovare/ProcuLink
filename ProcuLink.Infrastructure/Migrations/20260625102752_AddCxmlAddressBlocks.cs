using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCxmlAddressBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bill_to_city",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_country",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_deliver_to",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_email",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_name",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_phone",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_postal_code",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_to_street",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_city",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_country",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_deliver_to",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_email",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_name",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_phone",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_postal_code",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ship_to_street",
                table: "purchase_orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bill_to_city",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_country",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_deliver_to",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_email",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_name",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_phone",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_postal_code",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "bill_to_street",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_city",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_country",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_deliver_to",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_email",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_name",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_phone",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_postal_code",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "ship_to_street",
                table: "purchase_orders");
        }
    }
}
