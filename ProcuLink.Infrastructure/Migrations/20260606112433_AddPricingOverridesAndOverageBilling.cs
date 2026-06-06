using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingOverridesAndOverageBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "order_limit_override",
                table: "organisations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "supplier_limit_override",
                table: "organisations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "trial_ends_at_override",
                table: "organisations",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "overage_billing_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_key = table.Column<string>(type: "text", nullable: false),
                    overage_orders = table.Column<int>(type: "integer", nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    stripe_invoice_item_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overage_billing_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_overage_billing_records_org_id_billing_key",
                table: "overage_billing_records",
                columns: new[] { "org_id", "billing_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "overage_billing_records");

            migrationBuilder.DropColumn(
                name: "order_limit_override",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "supplier_limit_override",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "trial_ends_at_override",
                table: "organisations");
        }
    }
}
