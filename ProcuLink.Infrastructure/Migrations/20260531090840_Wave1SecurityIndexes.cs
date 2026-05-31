using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Wave1SecurityIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_profiles_org_id",
                table: "supplier_profiles");

            migrationBuilder.DropIndex(
                name: "IX_purchase_order_lines_order_id",
                table: "purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_delivery_attempts_org_id",
                table: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_org_id",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_profiles_org_id_supplier_id",
                table: "supplier_profiles",
                columns: new[] { "org_id", "supplier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_order_lines_order_id_needs_review",
                table: "purchase_order_lines",
                columns: new[] { "order_id", "needs_review" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_org_id_order_id_attempted_at",
                table: "delivery_attempts",
                columns: new[] { "org_id", "order_id", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_org_id_entity_type_entity_id_created_at",
                table: "audit_events",
                columns: new[] { "org_id", "entity_type", "entity_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_profiles_org_id_supplier_id",
                table: "supplier_profiles");

            migrationBuilder.DropIndex(
                name: "IX_purchase_order_lines_order_id_needs_review",
                table: "purchase_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_delivery_attempts_org_id_order_id_attempted_at",
                table: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_org_id_entity_type_entity_id_created_at",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_profiles_org_id",
                table: "supplier_profiles",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_order_lines_order_id",
                table: "purchase_order_lines",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_org_id",
                table: "delivery_attempts",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_org_id",
                table: "audit_events",
                column: "org_id");
        }
    }
}
