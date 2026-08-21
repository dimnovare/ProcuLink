using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TheAlertCooldownDidNotSurviveAWorkerRestart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "worker_health_alert_cooldowns",
                columns: table => new
                {
                    alert_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    was_bad = table.Column<bool>(type: "boolean", nullable: false),
                    last_alert_utc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_utc = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_health_alert_cooldowns", x => x.alert_key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_health_alert_cooldowns");
        }
    }
}
