using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceReadFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "due_date",
                table: "purchase_orders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_iban",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_reference",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "seller_tax_id",
                table: "purchase_orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "due_date",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "payment_iban",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "payment_reference",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "seller_tax_id",
                table: "purchase_orders");
        }
    }
}
