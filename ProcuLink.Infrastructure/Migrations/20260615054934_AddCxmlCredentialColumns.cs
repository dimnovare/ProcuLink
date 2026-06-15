using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCxmlCredentialColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cxml_config_json",
                table: "supplier_delivery_configs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encrypted_cxml_shared_secret",
                table: "supplier_delivery_configs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cxml_config_json",
                table: "supplier_delivery_configs");

            migrationBuilder.DropColumn(
                name: "encrypted_cxml_shared_secret",
                table: "supplier_delivery_configs");
        }
    }
}
