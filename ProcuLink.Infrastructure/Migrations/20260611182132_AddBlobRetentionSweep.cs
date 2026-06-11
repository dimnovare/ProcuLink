using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Retention batch — two concerns in one migration (this batch's single migration):
    ///
    /// <para>1. BLOB-RETENTION SWEEP (founder-approved Flip C). Additive schema:
    /// <c>organisations.retention_days</c> (int, NULL = retention DISABLED — the default,
    /// so existing orgs are untouched), the append-only <c>retention_audit_log</c> evidence
    /// table (one row per opted-in org per sweep run, mode <c>dry_run</c>|<c>delete</c>),
    /// and the blob-purged markers <c>purchase_orders.source_file_purged_at</c> +
    /// <c>outbound_artifacts.blob_purged_at</c>. The sweep deletes ONLY file blobs of
    /// TERMINAL, aged orders; DB rows, hashes and the audit trail always stay.</para>
    ///
    /// <para>2. TRIGGER FILL-ONLY EXEMPTION (rider). Migration
    /// <c>20260611173547_AddReviewReasonAndPublishedRevisionImmutability</c> created
    /// <c>trg_revision_content_immutable_when_published</c> blocking any content UPDATE on a
    /// published <c>supplier_connection_revisions</c> row. The Fable re-backfill (merged
    /// <c>699b3c6</c>) must FILL <c>output_mapping_json</c> on published revisions created
    /// before output snapshotting existed — a NULL→value transition only. CREATE OR REPLACE
    /// the trigger function so exactly that transition is allowed:
    /// <c>output_mapping_json</c> may go NULL→value on a published row, but a non-null
    /// overwrite (value→value or value→NULL) and every OTHER content column change remain
    /// blocked. Idempotent (CREATE OR REPLACE; the trigger itself is untouched). Down()
    /// restores the original strict function verbatim.</para>
    ///
    /// <para>Raw SQL is allowed here — migrations are the sanctioned exception to the
    /// no-raw-SQL rule.</para>
    /// </summary>
    public partial class AddBlobRetentionSweep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "source_file_purged_at",
                table: "purchase_orders",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "blob_purged_at",
                table: "outbound_artifacts",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "retention_days",
                table: "organisations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "retention_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    files_deleted = table.Column<int>(type: "integer", nullable: false),
                    bytes_estimated = table.Column<long>(type: "bigint", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_retention_audit_log_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_retention_audit_log_org_id_run_at",
                table: "retention_audit_log",
                columns: new[] { "org_id", "run_at" });

            // ── Part 2: fill-only exemption for the published-revision immutability trigger ──
            // The Fable re-backfill needs to FILL output_mapping_json (NULL→value) on published
            // revisions created before output snapshotting. Replace ONLY the function body
            // (CREATE OR REPLACE — the trigger binding from 20260611173547 is untouched, so this
            // is idempotent and safe to re-run). The output_mapping_json predicate gains
            // "AND OLD.output_mapping_json IS NOT NULL": a NULL old value may be filled once;
            // a non-null value can still never be overwritten OR erased, and every other
            // content column remains fully blocked while status = 'published'.
            migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION proculink_block_published_revision_content_update() RETURNS trigger AS $$
BEGIN
    IF OLD.status = 'published' AND (
           NEW.input_mapping_json    IS DISTINCT FROM OLD.input_mapping_json
        OR (NEW.output_mapping_json  IS DISTINCT FROM OLD.output_mapping_json
            AND OLD.output_mapping_json IS NOT NULL) -- fill-only exemption: NULL→value allowed (re-backfill)
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
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the ORIGINAL strict trigger function (verbatim from
            // 20260611173547_AddReviewReasonAndPublishedRevisionImmutability) —
            // no fill-only exemption.
            migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION proculink_block_published_revision_content_update() RETURNS trigger AS $$
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
""");

            migrationBuilder.DropTable(
                name: "retention_audit_log");

            migrationBuilder.DropColumn(
                name: "source_file_purged_at",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "blob_purged_at",
                table: "outbound_artifacts");

            migrationBuilder.DropColumn(
                name: "retention_days",
                table: "organisations");
        }
    }
}
