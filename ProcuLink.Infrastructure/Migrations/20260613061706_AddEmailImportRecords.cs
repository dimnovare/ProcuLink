using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailImportRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_import_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imap_message_id = table.Column<string>(type: "text", nullable: false),
                    attachment_hash = table.Column<string>(type: "text", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: true),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_import_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_import_records_org_id_imap_message_id_attachment_hash",
                table: "email_import_records",
                columns: new[] { "org_id", "imap_message_id", "attachment_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_import_records");
        }
    }
}
