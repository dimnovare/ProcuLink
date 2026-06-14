using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLosslessCanonicalCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_order_ref",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "incoterms",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_method",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contract_number",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_part_number",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "discount_percent",
                table: "purchase_order_lines",
                type: "numeric(7,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer_part_number",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "net_amount",
                table: "purchase_order_lines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipient",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unspsc",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_parties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    street = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    postal_code = table.Column<string>(type: "text", nullable: true),
                    country = table.Column<string>(type: "text", nullable: true),
                    vat = table.Column<string>(type: "text", nullable: true),
                    reg_nr = table.Column<string>(type: "text", nullable: true),
                    edi_code = table.Column<string>(type: "text", nullable: true),
                    reference = table.Column<string>(type: "text", nullable: true),
                    contact_name = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_parties", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_parties_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_captures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    tokens_json = table.Column<string>(type: "jsonb", nullable: true),
                    raw_text = table.Column<string>(type: "text", nullable: true),
                    page_refs = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_captures", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_captures_purchase_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_parties_order_id",
                table: "order_parties",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_parties_org_id_order_id",
                table: "order_parties",
                columns: new[] { "org_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "IX_source_captures_order_id",
                table: "source_captures",
                column: "order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_parties");

            migrationBuilder.DropTable(
                name: "source_captures");

            migrationBuilder.DropColumn(
                name: "buyer_order_ref",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "contact_email",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "contact_name",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "incoterms",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "shipping_method",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "contract_number",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "customer_part_number",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "discount_percent",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "manufacturer_part_number",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "net_amount",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "recipient",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "unspsc",
                table: "purchase_order_lines");
        }
    }
}
