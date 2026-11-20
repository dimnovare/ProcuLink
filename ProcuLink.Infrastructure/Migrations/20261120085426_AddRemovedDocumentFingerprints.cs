using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRemovedDocumentFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "removed_document_fingerprints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dedup_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    document_created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    removed_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_removed_document_fingerprints", x => x.id);
                    table.ForeignKey(
                        name: "FK_removed_document_fingerprints_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_removed_document_fingerprints_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_removed_document_fingerprints_flow_id",
                table: "removed_document_fingerprints",
                column: "flow_id");

            migrationBuilder.CreateIndex(
                name: "IX_removed_document_fingerprints_org_id_flow_id_dedup_key_hash",
                table: "removed_document_fingerprints",
                columns: new[] { "org_id", "flow_id", "dedup_key_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_removed_document_fingerprints_org_id_sha256",
                table: "removed_document_fingerprints",
                columns: new[] { "org_id", "sha256" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "removed_document_fingerprints");
        }
    }
}
