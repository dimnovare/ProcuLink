using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id",
                table: "purchase_orders");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id_created_at",
                table: "purchase_orders",
                columns: new[] { "org_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id_status",
                table: "purchase_orders",
                columns: new[] { "org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_sla_breached_delivery_due_at",
                table: "purchase_orders",
                columns: new[] { "sla_breached", "delivery_due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_status_updated_at",
                table: "purchase_orders",
                columns: new[] { "status", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id_created_at",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id_status",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_sla_breached_delivery_due_at",
                table: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_status_updated_at",
                table: "purchase_orders");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id",
                table: "purchase_orders",
                column: "org_id");
        }
    }
}
