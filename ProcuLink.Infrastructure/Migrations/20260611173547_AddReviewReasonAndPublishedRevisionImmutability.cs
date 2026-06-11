using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// P2 hardening batch — two additive changes in one migration:
    ///
    /// <para>1. <c>purchase_order_lines.review_reason</c> (text, nullable): a short
    /// human-readable "why was this line flagged for review" written at parse time
    /// (unresolved supplier code, parser numeric ambiguity, AI anti-hallucination
    /// mismatch, scanned-PDF vision extraction). Old rows stay null — that is fine.</para>
    ///
    /// <para>2. DB-LEVEL IMMUTABILITY for published connection revisions: a Postgres
    /// trigger on <c>supplier_connection_revisions</c> rejects any UPDATE that CHANGES a
    /// content (bundle) column — input_mapping_json, output_mapping_json, output_format,
    /// delivery_protocol, delivery_config_json, delivery_auto_deliver, credentials_ref,
    /// acceptance_profile_id, acceptance_version_no — while <c>OLD.status = 'published'</c>.
    /// Every order pins a ConnectionRevisionId for reproducibility/replay/rollback, so a
    /// published bundle must be immutable even against raw SQL / a future buggy code path.
    /// Lifecycle and evidence columns stay mutable on purpose: <c>status</c> +
    /// <c>effective_to</c> (PublishAsync/RollbackAsync archive the prior published
    /// revision; ArchiveAsync retires one) and <c>test_result_json</c>/<c>tested_at</c>/
    /// <c>test_passed</c> (RunTestPackAsync may record evidence on any revision).
    /// RollbackAsync clones — it never mutates an old revision — so it is unaffected.
    /// The comparison uses IS DISTINCT FROM, so no-op SETs of the same value pass.
    /// Raw SQL is allowed here (migrations are the sanctioned exception to the
    /// no-raw-SQL rule). Idempotent: DROP IF EXISTS before CREATE.</para>
    /// </summary>
    public partial class AddReviewReasonAndPublishedRevisionImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review_reason",
                table: "purchase_order_lines",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_revision_content_immutable_when_published ON supplier_connection_revisions;
DROP FUNCTION IF EXISTS proculink_block_published_revision_content_update();

CREATE FUNCTION proculink_block_published_revision_content_update() RETURNS trigger AS $$
BEGIN
    IF OLD.status = 'published' AND (
           NEW.input_mapping_json    IS DISTINCT FROM OLD.input_mapping_json
        OR NEW.output_mapping_json   IS DISTINCT FROM OLD.output_mapping_json
        OR NEW.output_format         IS DISTINCT FROM OLD.output_format
        OR NEW.delivery_protocol     IS DISTINCT FROM OLD.delivery_protocol
        OR NEW.delivery_config_json  IS DISTINCT FROM OLD.delivery_config_json
        OR NEW.delivery_auto_deliver IS DISTINCT FROM OLD.delivery_auto_deliver
        OR NEW.credentials_ref       IS DISTINCT FROM OLD.credentials_ref
        OR NEW.acceptance_profile_id IS DISTINCT FROM OLD.acceptance_profile_id
        OR NEW.acceptance_version_no IS DISTINCT FROM OLD.acceptance_version_no
    ) THEN
        RAISE EXCEPTION 'supplier_connection_revisions: the content of a published revision is immutable (revision %, version %). Create a new draft revision instead.',
            OLD.id, OLD.version_no
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_revision_content_immutable_when_published
    BEFORE UPDATE ON supplier_connection_revisions
    FOR EACH ROW
    EXECUTE FUNCTION proculink_block_published_revision_content_update();
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DROP TRIGGER IF EXISTS trg_revision_content_immutable_when_published ON supplier_connection_revisions;
DROP FUNCTION IF EXISTS proculink_block_published_revision_content_update();
""");

            migrationBuilder.DropColumn(
                name: "review_reason",
                table: "purchase_order_lines");
        }
    }
}
