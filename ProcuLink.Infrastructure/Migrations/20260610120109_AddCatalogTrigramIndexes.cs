using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Group V10 — indexed catalog retrieval at scale. Enables the pg_trgm extension and adds
    /// GIN trigram indexes on supplier_products.code + name (the fuzzy similarity-ranking pass),
    /// plus a btree on (org_id, supplier_id, barcode) for exact barcode lookup.
    ///
    /// EXACT code lookup (org_id, supplier_id, code) is already served by the pre-existing UNIQUE
    /// index, so V10 does not add a second btree for code. The GIN trigram indexes are created via
    /// raw SQL because EF model config cannot express the gin_trgm_ops operator class; they are
    /// Postgres-only and have no effect on the EF InMemory provider used by the tests. All raw SQL
    /// is IF NOT EXISTS so a deploy retry is a no-op.
    /// </summary>
    public partial class AddCatalogTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exact barcode (GTIN/EAN) lookup — the strongest match key when buyers carry it.
            migrationBuilder.CreateIndex(
                name: "IX_supplier_products_org_id_supplier_id_barcode",
                table: "supplier_products",
                columns: new[] { "org_id", "supplier_id", "barcode" });

            // pg_trgm enables trigram similarity ranking (similarity(), the % operator, and
            // GIN gin_trgm_ops). IF NOT EXISTS so the migration is idempotent / safe to re-run.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // GIN trigram indexes back the fuzzy-candidate query (similarity over code + name) so
            // retrieval never loads the whole catalog into memory. Not CONCURRENTLY — migrations
            // run in a transaction; per-(org,supplier) catalogs are small enough that the brief
            // deploy-time lock is acceptable.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_supplier_products_code_trgm " +
                "ON supplier_products USING gin (code gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_supplier_products_name_trgm " +
                "ON supplier_products USING gin (name gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_supplier_products_name_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_supplier_products_code_trgm;");

            // Leave the pg_trgm extension in place on Down — other objects may depend on it, and
            // dropping a shared extension on rollback is riskier than leaving it dormant.

            migrationBuilder.DropIndex(
                name: "IX_supplier_products_org_id_supplier_id_barcode",
                table: "supplier_products");
        }
    }
}
