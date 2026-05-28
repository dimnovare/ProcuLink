using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWave2IngressTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "imported_s3_objects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_name = table.Column<string>(type: "text", nullable: false),
                    object_key = table.Column<string>(type: "text", nullable: false),
                    etag = table.Column<string>(type: "text", nullable: false),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imported_s3_objects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "imported_sftp_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    remote_path = table.Column<string>(type: "text", nullable: false),
                    file_hash = table.Column<string>(type: "text", nullable: false),
                    imported_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imported_sftp_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "s3_ingress_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_name = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    access_key_id = table.Column<string>(type: "text", nullable: false),
                    encrypted_secret_key = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_s3_ingress_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_s3_ingress_configs_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sftp_ingress_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false, defaultValue: 22),
                    username = table.Column<string>(type: "text", nullable: false),
                    encrypted_password = table.Column<string>(type: "text", nullable: false),
                    remote_directory = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sftp_ingress_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_sftp_ingress_configs_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_imported_s3_objects_org_id_bucket_name_object_key",
                table: "imported_s3_objects",
                columns: new[] { "org_id", "bucket_name", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_imported_sftp_files_org_id_remote_path",
                table: "imported_sftp_files",
                columns: new[] { "org_id", "remote_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_s3_ingress_configs_org_id",
                table: "s3_ingress_configs",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_sftp_ingress_configs_org_id",
                table: "sftp_ingress_configs",
                column: "org_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "imported_s3_objects");

            migrationBuilder.DropTable(
                name: "imported_sftp_files");

            migrationBuilder.DropTable(
                name: "s3_ingress_configs");

            migrationBuilder.DropTable(
                name: "sftp_ingress_configs");
        }
    }
}
