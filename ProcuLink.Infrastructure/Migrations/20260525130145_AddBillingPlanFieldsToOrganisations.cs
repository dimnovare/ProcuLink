using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingPlanFieldsToOrganisations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account_status",
                table: "organisations",
                type: "text",
                nullable: false,
                defaultValue: "trialing");

            migrationBuilder.AddColumn<string>(
                name: "billing_email",
                table: "organisations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "billing_updated_at",
                table: "organisations",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_price_id",
                table: "organisations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_subscription_status",
                table: "organisations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "trial_ends_at",
                table: "organisations",
                type: "timestamptz",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "account_status",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "billing_email",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "billing_updated_at",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "stripe_price_id",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "stripe_subscription_status",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "trial_ends_at",
                table: "organisations");
        }
    }
}
