using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailConfigToOrganisations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email_config",
                table: "organisations",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email_config",
                table: "organisations");
        }
    }
}
