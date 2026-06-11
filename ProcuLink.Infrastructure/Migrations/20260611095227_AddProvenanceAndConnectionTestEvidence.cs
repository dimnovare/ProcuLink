using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProvenanceAndConnectionTestEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "test_passed",
                table: "supplier_connection_revisions",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "test_result_json",
                table: "supplier_connection_revisions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "tested_at",
                table: "supplier_connection_revisions",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "supplier_connection_revisions",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "artifact_sha256",
                table: "outbound_artifacts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_digest",
                table: "outbound_artifacts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "connection_revision_id",
                table: "outbound_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "artifact_sha256",
                table: "delivery_attempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_digest",
                table: "delivery_attempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "connection_revision_id",
                table: "delivery_attempts",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "test_passed",
                table: "supplier_connection_revisions");

            migrationBuilder.DropColumn(
                name: "test_result_json",
                table: "supplier_connection_revisions");

            migrationBuilder.DropColumn(
                name: "tested_at",
                table: "supplier_connection_revisions");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "supplier_connection_revisions");

            migrationBuilder.DropColumn(
                name: "artifact_sha256",
                table: "outbound_artifacts");

            migrationBuilder.DropColumn(
                name: "config_digest",
                table: "outbound_artifacts");

            migrationBuilder.DropColumn(
                name: "connection_revision_id",
                table: "outbound_artifacts");

            migrationBuilder.DropColumn(
                name: "artifact_sha256",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "config_digest",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "connection_revision_id",
                table: "delivery_attempts");
        }
    }
}
