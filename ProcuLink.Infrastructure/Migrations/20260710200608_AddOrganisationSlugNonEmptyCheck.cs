using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// P2 hardening (audit 2026-07-10): a DB-level CHECK so no organisation can ever have an
    /// EMPTY slug. <c>organisations.slug</c> was <c>NOT NULL DEFAULT ''</c> with only a plain
    /// unique index — nothing rejected the empty string. An empty-slug org is invisible to
    /// every slug-keyed operation, including the 2026-06-09 production purge that matched orgs
    /// BY SLUG. That is exactly how orphan org 75abde9a survived (empty slug, 0 memberships,
    /// unreachable). Inbound email is already safe — <c>InboundEmailRouter</c> rejects blank
    /// local parts — so this closes the data-hygiene hole, not an inbound-routing vuln.
    ///
    /// <para>The constraint is added <c>NOT VALID</c> ON PURPOSE. A plain <c>CHECK</c> validates
    /// every existing row at ADD time and would FAIL while the orphan row (empty slug) is still
    /// present in production — breaking the deploy. <c>NOT VALID</c> skips only the one-time scan
    /// of pre-existing rows; it STILL enforces on every future INSERT/UPDATE, so no new org can
    /// get an empty slug from the moment this runs. The remaining one-time
    /// <c>VALIDATE CONSTRAINT</c> (which scans the existing table) is a SEPARATE, founder-gated
    /// step run AFTER the orphan is cleaned up:</para>
    ///
    /// <code>ALTER TABLE organisations VALIDATE CONSTRAINT ck_organisations_slug_not_empty;</code>
    ///
    /// <para>Raw SQL is allowed here — migrations are the sanctioned exception to the no-raw-SQL
    /// rule, and EF cannot express <c>NOT VALID</c> via <c>HasCheckConstraint</c>. Following the
    /// same convention as the published-revision immutability trigger, this DB guard is NOT
    /// reflected in the model snapshot. Idempotent: <c>DROP CONSTRAINT IF EXISTS</c> before ADD.</para>
    /// </summary>
    public partial class AddOrganisationSlugNonEmptyCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
ALTER TABLE organisations DROP CONSTRAINT IF EXISTS ck_organisations_slug_not_empty;
ALTER TABLE organisations ADD CONSTRAINT ck_organisations_slug_not_empty CHECK (length(slug) > 0) NOT VALID;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE organisations DROP CONSTRAINT IF EXISTS ck_organisations_slug_not_empty;");
        }
    }
}
