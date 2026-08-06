using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoSendDryRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_transform",
                table: "supplier_delivery_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "auto_send_dry_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    would_have_sent = table.Column<bool>(type: "boolean", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: true),
                    output_format = table.Column<string>(type: "text", nullable: true),
                    decision_digest = table.Column<string>(type: "text", nullable: true),
                    blocker_count = table.Column<int>(type: "integer", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: true),
                    evaluated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auto_send_dry_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_auto_send_dry_runs_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_auto_send_dry_runs_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auto_send_dry_runs_order_id",
                table: "auto_send_dry_runs",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_auto_send_dry_runs_org_id_order_id",
                table: "auto_send_dry_runs",
                columns: new[] { "org_id", "order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auto_send_dry_runs_org_id_would_have_sent_evaluated_at",
                table: "auto_send_dry_runs",
                columns: new[] { "org_id", "would_have_sent", "evaluated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auto_send_dry_runs");

            migrationBuilder.DropColumn(
                name: "auto_transform",
                table: "supplier_delivery_configs");
        }
    }
}
