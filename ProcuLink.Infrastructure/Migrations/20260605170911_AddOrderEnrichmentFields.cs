using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderEnrichmentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "document_type",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "grand_total",
                table: "purchase_orders",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_terms",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sub_total",
                table: "purchase_orders",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supplier_name",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_total",
                table: "purchase_orders",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "delivery_date",
                table: "purchase_order_lines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "line_amount",
                table: "purchase_order_lines",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_rate",
                table: "purchase_order_lines",
                type: "numeric(7,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "document_type",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "grand_total",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "payment_terms",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "sub_total",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "supplier_name",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "tax_total",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "delivery_date",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "line_amount",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "tax_rate",
                table: "purchase_order_lines");
        }
    }
}
