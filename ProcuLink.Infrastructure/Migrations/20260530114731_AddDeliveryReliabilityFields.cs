using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryReliabilityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "delivery_due_at",
                table: "purchase_orders",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sla_breached",
                table: "purchase_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "acknowledged_at",
                table: "delivery_attempts",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "response_body",
                table: "delivery_attempts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delivery_due_at",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "sla_breached",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "response_body",
                table: "delivery_attempts");
        }
    }
}
