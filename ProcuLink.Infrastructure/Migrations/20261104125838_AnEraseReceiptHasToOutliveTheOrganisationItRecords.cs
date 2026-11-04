using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnEraseReceiptHasToOutliveTheOrganisationItRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject_org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_clerk_org_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_audit_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_audit_events_subject_clerk_org_id",
                table: "platform_audit_events",
                column: "subject_clerk_org_id");

            migrationBuilder.CreateIndex(
                name: "IX_platform_audit_events_subject_org_id",
                table: "platform_audit_events",
                column: "subject_org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_audit_events");
        }
    }
}
