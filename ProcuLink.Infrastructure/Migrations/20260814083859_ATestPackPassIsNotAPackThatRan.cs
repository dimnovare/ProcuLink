using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Adds <c>test_outcome</c> to <c>supplier_connection_revisions</c>: the authoritative verdict
    /// of the last connection test pack (<c>passed</c> / <c>failed</c> / <c>not_exercised</c>),
    /// which the boolean <c>test_passed</c> could not express.
    ///
    /// <para><b>Deliberately no backfill.</b> Existing rows keep <c>test_outcome = NULL</c> even
    /// where <c>test_passed = true</c>. That stored <c>true</c> is exactly the value under
    /// suspicion — the pack returned it for a supplier with no orders, for one-of-five orders
    /// rendering, and for output formats with no standards profile — so copying it into the new
    /// column would launder the evidence this column exists to distrust. Draft/test revisions
    /// re-run their checks (seconds); already-published revisions are unaffected, because the
    /// publish gate only runs on draft/test.</para>
    /// </summary>
    public partial class ATestPackPassIsNotAPackThatRan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "test_outcome",
                table: "supplier_connection_revisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "test_outcome",
                table: "supplier_connection_revisions");
        }
    }
}
