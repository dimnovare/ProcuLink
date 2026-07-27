using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierAutoDetect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "edi_code",
                table: "suppliers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_domain",
                table: "suppliers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "registration_number",
                table: "suppliers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vat_number",
                table: "suppliers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inbound_sender_domain",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "inbound_sender_domain_captured_at",
                table: "purchase_orders",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_supplier_suggestions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    signals_json = table.Column<string>(type: "jsonb", nullable: true),
                    model_version = table.Column<string>(type: "text", nullable: true),
                    decision = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_supplier_suggestions", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_supplier_suggestions_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_products_org_id_code",
                table: "supplier_products",
                columns: new[] { "org_id", "code" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id_inbound_sender_domain",
                table: "purchase_orders",
                columns: new[] { "org_id", "inbound_sender_domain" });

            migrationBuilder.CreateIndex(
                name: "IX_order_supplier_suggestions_live_idempotency",
                table: "order_supplier_suggestions",
                columns: new[] { "org_id", "order_id", "supplier_id" },
                unique: true,
                filter: "decision IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_order_supplier_suggestions_org_id_order_id_rank",
                table: "order_supplier_suggestions",
                columns: new[] { "org_id", "order_id", "rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_supplier_suggestions");

            migrationBuilder.DropIndex(
                name: "IX_supplier_products_org_id_code",
                table: "supplier_products");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id_inbound_sender_domain",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "edi_code",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "primary_domain",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "registration_number",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "vat_number",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "inbound_sender_domain",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "inbound_sender_domain_captured_at",
                table: "purchase_orders");
        }
    }
}
