using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaFingerprintUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_schema_fingerprints_org_id_column_name_hash",
                table: "SchemaFingerprints",
                columns: new[] { "OrganisationId", "ColumnNameHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_schema_fingerprints_org_id_column_name_hash",
                table: "SchemaFingerprints");
        }
    }
}
