using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clerk_user_id = table.Column<string>(type: "text", nullable: false),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    p256dh = table.Column<string>(type: "text", nullable: false),
                    auth = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    last_checked_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    summary_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_push_devices_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_push_devices_org_id_endpoint",
                table: "push_devices",
                columns: new[] { "org_id", "endpoint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_devices");
        }
    }
}
