using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddManufacturerPartNumberToCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "manufacturer_name",
                table: "supplier_products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer_part_number",
                table: "supplier_products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer_part_number_normalized",
                table: "supplier_products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer_name",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_products_org_id_supplier_id_mpn_normalized",
                table: "supplier_products",
                columns: new[] { "org_id", "supplier_id", "manufacturer_part_number_normalized" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_products_org_id_supplier_id_mpn_normalized",
                table: "supplier_products");

            migrationBuilder.DropColumn(
                name: "manufacturer_name",
                table: "supplier_products");

            migrationBuilder.DropColumn(
                name: "manufacturer_part_number",
                table: "supplier_products");

            migrationBuilder.DropColumn(
                name: "manufacturer_part_number_normalized",
                table: "supplier_products");

            migrationBuilder.DropColumn(
                name: "manufacturer_name",
                table: "purchase_order_lines");
        }
    }
}
