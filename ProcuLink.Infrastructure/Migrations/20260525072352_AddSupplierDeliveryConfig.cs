using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierDeliveryConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_delivery_attempts_purchase_orders_order_id",
                table: "delivery_attempts");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                table: "delivery_attempts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "supplier_delivery_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    auto_deliver = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: false),
                    encrypted_credentials = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_delivery_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_supplier_delivery_configs_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_supplier_delivery_configs_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_delivery_configs_org_id_supplier_id",
                table: "supplier_delivery_configs",
                columns: new[] { "org_id", "supplier_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_delivery_configs_supplier_id",
                table: "supplier_delivery_configs",
                column: "supplier_id");

            migrationBuilder.AddForeignKey(
                name: "FK_delivery_attempts_purchase_orders_order_id",
                table: "delivery_attempts",
                column: "order_id",
                principalTable: "purchase_orders",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_delivery_attempts_purchase_orders_order_id",
                table: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "supplier_delivery_configs");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                table: "delivery_attempts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_delivery_attempts_purchase_orders_order_id",
                table: "delivery_attempts",
                column: "order_id",
                principalTable: "purchase_orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
