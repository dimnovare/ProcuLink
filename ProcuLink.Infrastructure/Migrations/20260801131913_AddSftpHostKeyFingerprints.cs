using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSftpHostKeyFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "host_key_fingerprints",
                table: "supplier_catalog_sources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_key_fingerprints",
                table: "sftp_ingress_configs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "host_key_fingerprints",
                table: "supplier_catalog_sources");

            migrationBuilder.DropColumn(
                name: "host_key_fingerprints",
                table: "sftp_ingress_configs");
        }
    }
}
