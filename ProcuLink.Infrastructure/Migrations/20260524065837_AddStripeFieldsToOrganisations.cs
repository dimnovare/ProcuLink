using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeFieldsToOrganisations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "pilot_extended_until",
                table: "organisations",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pilot_extension_requested_at",
                table: "organisations",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_customer_id",
                table: "organisations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_subscription_id",
                table: "organisations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "trial_started_at",
                table: "organisations",
                type: "timestamptz",
                nullable: false,
                defaultValueSql: "now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pilot_extended_until",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "pilot_extension_requested_at",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "stripe_customer_id",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "stripe_subscription_id",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "trial_started_at",
                table: "organisations");
        }
    }
}
