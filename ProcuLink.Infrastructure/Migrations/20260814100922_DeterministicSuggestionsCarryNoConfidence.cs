using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Clears <c>purchase_order_lines.ai_suggestion_confidence</c> on the rows where it was never a
    /// measurement: the two DETERMINISTIC suggestion producers in <c>OrderIngestionService</c>, both
    /// of which stamped a literal <c>0.95f</c>.
    ///
    /// <para>No schema change — the column is already <c>float?</c>. This migration exists only to
    /// stop already-ingested rows from continuing to render "AI confidence 95%" in the review UI
    /// after the code stops writing it. Without it the fix would apply to new orders only, and every
    /// line currently sitting in review would keep the fabricated number indefinitely.</para>
    ///
    /// <para><b>Which rows, and why the match is exact.</b> The two producers write a fixed
    /// provenance string alongside the confidence:</para>
    /// <list type="bullet">
    ///   <item><c>catalog: manufacturer part number</c> — an exact hit in the supplier's own catalog,
    ///   found by an indexed query.</item>
    ///   <item><c>catalog: supplier code equals the manufacturer part number</c> — same query, the
    ///   other arm.</item>
    ///   <item><c>source document: manufacturer part number</c> — a literal echo of a code the
    ///   document prints.</item>
    /// </list>
    ///
    /// <para>Those three strings are matched by equality, not by prefix. The model path builds its
    /// provenance in <c>OpenAiMappingService.BuildProvenance</c>, which returns either
    /// <c>"Matched catalog product …"</c>, or <c>"OpenAI structured output"</c>, or a string the
    /// model itself supplied — so a prefix match on <c>catalog%</c> would have been safe against the
    /// first two but not against the third, which is arbitrary model-authored text. Equality against
    /// the three literals removes that exposure: a real model score can only be cleared if a model
    /// emitted one of these sentences verbatim.</para>
    ///
    /// <para><b>Why the value itself is not part of the WHERE clause.</b> On these three provenances
    /// the column can only ever have held <c>0.95</c> — no model runs on either branch, so there is
    /// no measurement to preserve and nothing to distinguish. Adding <c>= 0.95</c> would also make
    /// the match depend on float equality: <c>0.95</c> is not exactly representable in binary, so the
    /// comparison would need an explicit <c>real</c> cast to land in the same precision the column
    /// stores, and a near-miss would silently SKIP a row — leaving the fabricated number live, which
    /// is the failure direction that matters here. Matching on provenance alone cannot under-clear.
    /// (Contrast the previous migration, which did compare values, because that column genuinely held
    /// real scores mixed in with the flags.)</para>
    ///
    /// <para>Expressed as SQL because a migration is the only place a data backfill can run; the
    /// repo's "EF Core only" rule governs query code, and EF exposes no set-based update here.</para>
    /// </summary>
    public partial class DeterministicSuggestionsCarryNoConfidence : Migration
    {
        private const string DeterministicProvenanceList =
            "'catalog: manufacturer part number', " +
            "'catalog: supplier code equals the manufacturer part number', " +
            "'source document: manufacturer part number'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE purchase_order_lines
                   SET ai_suggestion_confidence = NULL
                 WHERE ai_suggestion_confidence IS NOT NULL
                   AND ai_suggestion_provenance IN ({DeterministicProvenanceList});
                """);
        }

        /// <summary>
        /// Deliberately empty, and not recoverable by any other means.
        ///
        /// <para>Reversing this would mean writing <c>0.95</c> back onto these rows — re-fabricating
        /// the exact number the change exists to remove. There is no stored original to restore
        /// because the 0.95 never came from anywhere: it was a literal in the source. A no-op Down is
        /// therefore the honest reverse, and rolling back the code without rolling back the data
        /// leaves the column simply unpopulated, which the read path already handles (null is the
        /// normal answer for a line nothing scored).</para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
