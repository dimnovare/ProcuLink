using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Additive-only: adds a partial index on <c>order_exceptions(created_at)</c> filtered to
    /// <c>state IN ('resolved','ignored')</c> to support the data-retention sweep predicate
    /// efficiently. The model snapshot is NOT modified (raw SQL only) so there is zero EF model
    /// drift. Reversible via <see cref="Down"/>: drops the index, leaving the schema byte-identical
    /// to today.
    /// </remarks>
    public partial class AddOrderExceptionRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Partial index: covers the retention-sweep predicate
            //   WHERE state IN ('resolved','ignored') AND created_at < @cutoff
            // Leading created_at lets the planner bound the age scan; the partial filter
            // automatically excludes 'open' rows, keeping the index small.
            // IF NOT EXISTS makes the Up idempotent (phantom-migration-safe).
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_order_exceptions_retention " +
                "ON order_exceptions (created_at) " +
                "WHERE state IN ('resolved','ignored');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_order_exceptions_retention;");
        }
    }
}
