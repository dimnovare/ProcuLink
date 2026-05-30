using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_confirmations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    supplier_reference = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_file_name = table.Column<string>(type: "text", nullable: true),
                    source_file_key = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_confirmations", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_confirmations_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_order_confirmations_purchase_orders_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_confirmation_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_confirmation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    buyer_item_code = table.Column<string>(type: "text", nullable: true),
                    supplier_item_code = table.Column<string>(type: "text", nullable: true),
                    ordered_quantity = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ordered_unit_price = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ordered_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    confirmed_quantity = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    confirmed_unit_price = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    confirmed_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_confirmation_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_confirmation_lines_order_confirmations_order_confirma~",
                        column: x => x.order_confirmation_id,
                        principalTable: "order_confirmations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_order_confirmation_lines_purchase_order_lines_purchase_orde~",
                        column: x => x.purchase_order_line_id,
                        principalTable: "purchase_order_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id_supplier_id",
                table: "purchase_orders",
                columns: new[] { "org_id", "supplier_id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_confirmation_lines_order_confirmation_id",
                table: "order_confirmation_lines",
                column: "order_confirmation_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_confirmation_lines_purchase_order_line_id",
                table: "order_confirmation_lines",
                column: "purchase_order_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_confirmations_org_id_purchase_order_id",
                table: "order_confirmations",
                columns: new[] { "org_id", "purchase_order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_confirmations_purchase_order_id",
                table: "order_confirmations",
                column: "purchase_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_confirmation_lines");

            migrationBuilder.DropTable(
                name: "order_confirmations");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id_supplier_id",
                table: "purchase_orders");
        }
    }
}
