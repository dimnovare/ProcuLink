using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Wave2BuyerNameColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_name",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id_buyer_name",
                table: "purchase_orders",
                columns: new[] { "org_id", "buyer_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id_buyer_name",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "buyer_name",
                table: "purchase_orders");
        }
    }
}
