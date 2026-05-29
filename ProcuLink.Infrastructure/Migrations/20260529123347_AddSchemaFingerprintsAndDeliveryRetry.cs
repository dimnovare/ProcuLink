using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaFingerprintsAndDeliveryRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SchemaFingerprintHash",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                table: "delivery_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SchemaFingerprints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ColumnNameHash = table.Column<string>(type: "text", nullable: false),
                    DetectedFormat = table.Column<string>(type: "text", nullable: false),
                    SampleSupplierName = table.Column<string>(type: "text", nullable: true),
                    ParseSuccessCount = table.Column<int>(type: "integer", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaFingerprints", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaFingerprints");

            migrationBuilder.DropColumn(
                name: "SchemaFingerprintHash",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                table: "delivery_attempts");
        }
    }
}
