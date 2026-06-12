using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogHttpSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auth_config_encrypted",
                table: "supplier_catalog_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auth_method",
                table: "supplier_catalog_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "http_method",
                table: "supplier_catalog_sources",
                type: "text",
                nullable: true,
                defaultValue: "GET");

            migrationBuilder.AddColumn<string>(
                name: "url",
                table: "supplier_catalog_sources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auth_config_encrypted",
                table: "supplier_catalog_sources");

            migrationBuilder.DropColumn(
                name: "auth_method",
                table: "supplier_catalog_sources");

            migrationBuilder.DropColumn(
                name: "http_method",
                table: "supplier_catalog_sources");

            migrationBuilder.DropColumn(
                name: "url",
                table: "supplier_catalog_sources");
        }
    }
}
