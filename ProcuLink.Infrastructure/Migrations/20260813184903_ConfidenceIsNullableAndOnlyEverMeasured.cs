using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Makes the three <c>confidence</c> columns nullable, and clears the values that were never
    /// measurements — so that a null means "nothing scored this" rather than being unreachable.
    ///
    /// <para><b>What happens to existing rows.</b></para>
    ///
    /// <para><c>item_mappings.confidence</c> — <b>every row is set to NULL.</b> Every value this
    /// column has ever held was written by the literal
    /// <c>source == Manual ? 1.0f : 0.8f</c> (or, from the sample seeder, a flat <c>1.0f</c> on an
    /// <c>imported</c> row). No model ever wrote to it: <c>MappingSource.Suggested</c> had zero
    /// callers in the entire codebase. There is therefore nothing to preserve — a stored 0.8 is not
    /// a degraded measurement, it is the constant that stood in for one — and nothing is
    /// recoverable, because the model's real number was discarded at the point of resolution and
    /// never written anywhere. The rows come back as "Not scored", which is what they always were.</para>
    ///
    /// <para><c>connection_revision_item_mappings.confidence</c> — <b>every row is set to NULL</b>,
    /// for the same reason: it is a point-in-time snapshot copied out of <c>item_mappings</c>, so it
    /// holds exactly the same two literals.</para>
    ///
    /// <para><c>purchase_order_lines.confidence</c> — <b>only the three flag values are cleared</b>
    /// (<c>0</c>, <c>0.5</c>, <c>1</c>). That column held a state flag,
    /// <c>resolved ? (parserFlagged ? 0.5f : 1.0f) : 0.0f</c>, but the bulk-accept path also promoted
    /// the model's genuine <c>ai_suggestion_confidence</c> into it, so it is the one column here that
    /// contains real measurements mixed in. Every other value can only have come from that promotion
    /// and is KEPT. All three constants are exactly representable in <c>real</c>, so the comparison is
    /// exact rather than approximate.</para>
    ///
    /// <para><b>The one lossy edge, stated plainly:</b> a bulk-accepted line whose model confidence
    /// was exactly <c>1.0</c> is indistinguishable from a resolved-line flag and is cleared with them.
    /// It cannot be otherwise — accepting a suggestion also nulls
    /// <c>ai_suggested_supplier_item_code</c>, so the two are byte-identical by the time this runs.
    /// The loss is in the safe direction: such a line reads "not scored" instead of "100%", never the
    /// reverse. <c>0</c> and <c>0.5</c> cannot be genuine model values on that path at all — the
    /// suggestion floor is 0.65.</para>
    ///
    /// <para>Data backfill is expressed as SQL because a migration is the only place it can run; the
    /// repo's "EF Core only" rule is about query code, and EF exposes no set-based update here.</para>
    /// </summary>
    public partial class ConfidenceIsNullableAndOnlyEverMeasured : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<float>(
                name: "confidence",
                table: "purchase_order_lines",
                type: "real",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real");

            migrationBuilder.AlterColumn<float>(
                name: "confidence",
                table: "item_mappings",
                type: "real",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real");

            migrationBuilder.AlterColumn<float>(
                name: "confidence",
                table: "connection_revision_item_mappings",
                type: "real",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real");

            // ── Backfill. See the class summary for why each rule is what it is. ──

            // Never a measurement, on any row: the column only ever held `Manual ? 1.0 : 0.8`.
            migrationBuilder.Sql(@"UPDATE item_mappings SET confidence = NULL;");

            // A snapshot of the same two literals.
            migrationBuilder.Sql(@"UPDATE connection_revision_item_mappings SET confidence = NULL;");

            // Clear the state flag ONLY. Any other value was promoted from a real model score by
            // the bulk-accept path and is preserved.
            migrationBuilder.Sql(@"
                UPDATE purchase_order_lines
                   SET confidence = NULL
                 WHERE confidence IN (0, 0.5, 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down cannot restore the cleared values — they were constants, and the code that wrote
            // them is gone. It restores the SHAPE only, defaulting nulls to 0, which is what the
            // pre-change column would have held for an unresolved line.
            migrationBuilder.AlterColumn<float>(
                name: "confidence",
                table: "purchase_order_lines",
                type: "real",
                nullable: false,
                defaultValue: 0f,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);

            migrationBuilder.AlterColumn<float>(
                name: "confidence",
                table: "item_mappings",
                type: "real",
                nullable: false,
                defaultValue: 0f,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);

            migrationBuilder.AlterColumn<float>(
                name: "confidence",
                table: "connection_revision_item_mappings",
                type: "real",
                nullable: false,
                defaultValue: 0f,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);
        }
    }
}
