using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultSupplierForPullIngress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_supplier_id",
                table: "sftp_ingress_configs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "default_supplier_id",
                table: "s3_ingress_configs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sftp_ingress_configs_default_supplier_id",
                table: "sftp_ingress_configs",
                column: "default_supplier_id");

            migrationBuilder.CreateIndex(
                name: "IX_s3_ingress_configs_default_supplier_id",
                table: "s3_ingress_configs",
                column: "default_supplier_id");

            migrationBuilder.AddForeignKey(
                name: "FK_s3_ingress_configs_suppliers_default_supplier_id",
                table: "s3_ingress_configs",
                column: "default_supplier_id",
                principalTable: "suppliers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_sftp_ingress_configs_suppliers_default_supplier_id",
                table: "sftp_ingress_configs",
                column: "default_supplier_id",
                principalTable: "suppliers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_s3_ingress_configs_suppliers_default_supplier_id",
                table: "s3_ingress_configs");

            migrationBuilder.DropForeignKey(
                name: "FK_sftp_ingress_configs_suppliers_default_supplier_id",
                table: "sftp_ingress_configs");

            migrationBuilder.DropIndex(
                name: "IX_sftp_ingress_configs_default_supplier_id",
                table: "sftp_ingress_configs");

            migrationBuilder.DropIndex(
                name: "IX_s3_ingress_configs_default_supplier_id",
                table: "s3_ingress_configs");

            migrationBuilder.DropColumn(
                name: "default_supplier_id",
                table: "sftp_ingress_configs");

            migrationBuilder.DropColumn(
                name: "default_supplier_id",
                table: "s3_ingress_configs");
        }
    }
}
