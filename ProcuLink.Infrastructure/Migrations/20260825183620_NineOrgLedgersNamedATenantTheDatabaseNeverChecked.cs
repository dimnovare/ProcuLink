using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Fifteen mapped tables carried an organisation column that the database never checked.
    ///
    /// <para>A 2026-08-25 model audit asked one question of every mapped entity — "does this type
    /// carry an organisation column, and if so is there a foreign key behind it?" — and fifteen
    /// answered yes then no. Nine of them lead an index with that column, so every query against
    /// them looks org-scoped and reads as if the tenancy were enforced: <c>idempotency_keys</c>,
    /// <c>ai_usage_monthly</c>, <c>overage_billing_records</c>, <c>org_plan_history</c>,
    /// <c>imported_sftp_files</c>, <c>imported_s3_objects</c>, <c>email_import_records</c>,
    /// <c>canonical_field_defs</c>, <c>order_parties</c> and <c>SchemaFingerprints</c> (ten by that
    /// test — the audit's count of nine missed <c>SchemaFingerprints</c>, which is mapped by
    /// convention rather than by an explicit <c>HasColumnName("org_id")</c> and so is invisible to
    /// a source grep). The remaining five — <c>source_captures</c>, <c>order_confirmation_lines</c>,
    /// <c>invoice_lines</c>, <c>asn_packages</c>, <c>asn_package_lines</c> — carry the column
    /// without leading an index on it, and had exactly the same gap.</para>
    ///
    /// <para><b>Why an index is not a constraint.</b> An index makes the lookup fast; it does not
    /// make the value mean anything. Without the foreign key, an organisation id in these tables is
    /// a number that resembles a tenant, and any write path that computed the wrong one, or any
    /// delete that bypassed the application, left rows referring to a tenant that does not exist.
    /// This has already happened once in this schema — <c>order_supplier_suggestions</c> kept a
    /// dangling order id, document-derived identity text and an operator's Clerk user id after an
    /// erasure, and the fix (migration
    /// <c>TwoIntegrityInvariantsWereHeldShutByApplicationCodeAlone</c>, 2026-08-20) is the pattern
    /// this migration follows. The same reasoning applies one level up: the tenant root.</para>
    ///
    /// <para><b>Delete behaviour is a per-table decision, not a default.</b></para>
    ///
    /// <para>RESTRICT — the row is billing evidence and an organisation delete must fail rather than
    /// take it:
    /// <list type="bullet">
    ///   <item><description><c>overage_billing_records</c> — the record of money actually charged
    ///   through Stripe, keyed for replay safety. Cascading it away would destroy the only proof of
    ///   what was billed and to whom.</description></item>
    /// </list>
    /// Nothing in the codebase deletes an organisation today (no <c>Organisations.Remove</c>, no
    /// raw delete), so RESTRICT breaks no existing path. That is the point: whoever writes the
    /// first org-delete path has to decide what happens to the billing trail, in the open, instead
    /// of inheriting a silent cascade.</para>
    ///
    /// <para><b><c>org_plan_history</c> was considered for RESTRICT and deliberately given CASCADE.</b>
    /// It is the working behind every overage invoice — the as-of metering reader resolves the plan
    /// and order-limit override for a billed window out of it — which is the same argument that puts
    /// <c>overage_billing_records</c> under RESTRICT. The difference is who has a row.
    /// <c>ProcuLinkDbContext.AppendOrgPlanHistoryAsync</c> writes a baseline row for EVERY
    /// organisation at creation, including a free Pilot that is never charged. A RESTRICT there
    /// would therefore not mean "billing evidence blocks this delete"; it would mean no organisation
    /// can ever be deleted — a blanket prohibition wearing a constraint's clothes, and one that
    /// breaks the first org-erasure path anyone writes. A constraint that fires for every row is not
    /// a decision about any of them. The charge itself stays protected by
    /// <c>overage_billing_records</c>, which exists only when money actually moved.</para>
    ///
    /// <para>CASCADE — derived, tenant-scoped or order-scoped content with no life of its own:
    /// <list type="bullet">
    ///   <item><description><c>idempotency_keys</c> — request-dedup ledger derived from traffic.
    ///   </description></item>
    ///   <item><description><c>ai_usage_monthly</c> — internal token counter for cost control and
    ///   the AI-spend alert. Never invoiced, so it is not billing evidence.</description></item>
    ///   <item><description><c>imported_sftp_files</c>, <c>imported_s3_objects</c>,
    ///   <c>email_import_records</c> — ingress dedupe ledgers. They exist to stop a poller
    ///   re-importing, and no poller runs for an organisation that no longer exists.
    ///   </description></item>
    ///   <item><description><c>canonical_field_defs</c> — tenant configuration describing one
    ///   organisation's own document model.</description></item>
    ///   <item><description><c>SchemaFingerprints</c> — derived detection statistics, explicitly
    ///   org-scoped by design with no cross-org sharing.</description></item>
    ///   <item><description><c>order_parties</c> and <c>source_captures</c> — the highest-PII rows
    ///   in the schema (contact name / email / phone, and the full extracted document text). Both
    ///   already cascade with their purchase order; this closes the second, unconstrained route by
    ///   which one could be stranded.</description></item>
    ///   <item><description><c>order_confirmation_lines</c>, <c>invoice_lines</c>,
    ///   <c>asn_packages</c>, <c>asn_package_lines</c> — children whose parent already cascades from
    ///   the organisation. They carried the organisation column with nothing behind it, so a delete
    ///   that bypassed the parent chain left them behind.</description></item>
    /// </list></para>
    ///
    /// <para><b>Existing rows: repaired, cleaned, or exempted — never assumed clean.</b> Whether
    /// production holds a violating row cannot be proven from here, and a foreign key that fails
    /// mid-deploy is an outage. Each group is handled by its own shape:</para>
    ///
    /// <para>(a) The five content children that already have an enforced parent foreign key —
    /// <c>order_parties</c>, <c>source_captures</c>, <c>order_confirmation_lines</c>,
    /// <c>invoice_lines</c>, <c>asn_packages</c>, and <c>asn_package_lines</c> below it — are
    /// REPAIRED, not deleted. Their parent is guaranteed to exist (that is what the parent FK
    /// means), and the parent knows the true organisation, so a violating row can only be one whose
    /// organisation column was written wrong. Copying the parent's value fixes it and is strictly a
    /// tenancy correction: a row filed under an organisation that does not exist was being read
    /// under the wrong tenant, or under none. Deleting real order and invoice content to satisfy a
    /// new constraint would be the wrong trade. <c>asn_packages</c> is repaired before
    /// <c>asn_package_lines</c> so the lines inherit an already-corrected value.</para>
    ///
    /// <para>(b) The seven derived ledgers with no parent to ask — <c>idempotency_keys</c>,
    /// <c>ai_usage_monthly</c>, <c>imported_sftp_files</c>, <c>imported_s3_objects</c>,
    /// <c>email_import_records</c>, <c>canonical_field_defs</c>, <c>SchemaFingerprints</c> — have
    /// their violating rows DELETED. There is nowhere to recover a correct organisation id from,
    /// and each row is dedupe or derived state that means nothing without its tenant. The ingress
    /// ledgers are safe to drop for the same reason they cascade: an organisation that does not
    /// exist has no poller to re-import anything.</para>
    ///
    /// <para>(c) The two billing-evidence tables — <c>overage_billing_records</c> and
    /// <c>org_plan_history</c> — are EXEMPTED from validation instead: their constraints are created
    /// <c>NOT VALID</c>, which enforces every insert and update from now on but does not scan what
    /// is already there. Deleting a billing row to make a constraint pass would destroy the exact
    /// evidence that motivates the constraint, and failing the deploy over one is worse. This holds
    /// for <c>org_plan_history</c> even though its delete behaviour is CASCADE: before this
    /// migration an organisation could be deleted by raw SQL while both tables kept rows, so an
    /// orphaned plan-history row is exactly the basis of an orphaned charge record, and dropping one
    /// while retaining the other would leave a charge nobody can explain. If a violating row does
    /// exist it stays visible and can be dealt with by hand; running <c>VALIDATE CONSTRAINT</c>
    /// later is the way to find out, and it is deliberately not run here.</para>
    ///
    /// <para><b>Also, one index that is not about foreign keys at all.</b>
    /// <c>IX_audit_events_org_id_created_at_desc</c>. The org-wide audit listing filters on
    /// <c>org_id</c>, orders by <c>created_at</c> descending and pages with Skip/Take, with no
    /// entity predicate. The only index on the table led
    /// <c>(org_id, entity_type, entity_id, created_at)</c>, which cannot serve that sort: with no
    /// equality on <c>entity_type</c> the ordering the index provides is unusable, so every listing
    /// read the organisation's whole audit history and sorted it. The new index is descending on
    /// <c>created_at</c> to match the query's own direction, so paging stays a forward index scan
    /// rather than a sort.</para>
    /// </summary>
    public partial class NineOrgLedgersNamedATenantTheDatabaseNeverChecked : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── (a) Repair content children from the parent that knows the true organisation ──
            // Each of these already has an enforced FK to its parent, so the parent row is
            // guaranteed to exist and a violating organisation id can only be a mis-written value.
            // Every statement is a no-op on healthy data — expected, not assumed.
            migrationBuilder.Sql("""
                UPDATE order_parties c
                SET org_id = p.org_id
                FROM purchase_orders p
                WHERE p.id = c.order_id
                  AND NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = c.org_id);
                """);

            migrationBuilder.Sql("""
                UPDATE source_captures c
                SET org_id = p.org_id
                FROM purchase_orders p
                WHERE p.id = c.order_id
                  AND NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = c.org_id);
                """);

            migrationBuilder.Sql("""
                UPDATE order_confirmation_lines c
                SET org_id = p.org_id
                FROM order_confirmations p
                WHERE p.id = c.order_confirmation_id
                  AND NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = c.org_id);
                """);

            migrationBuilder.Sql("""
                UPDATE invoice_lines c
                SET organisation_id = p.organisation_id
                FROM invoices p
                WHERE p.id = c.invoice_id
                  AND NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = c.organisation_id);
                """);

            // asn_packages before asn_package_lines: the lines inherit an already-corrected value.
            migrationBuilder.Sql("""
                UPDATE asn_packages c
                SET organisation_id = p.organisation_id
                FROM advance_shipping_notices p
                WHERE p.id = c.advance_shipping_notice_id
                  AND NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = c.organisation_id);
                """);

            migrationBuilder.Sql("""
                UPDATE asn_package_lines c
                SET organisation_id = p.organisation_id
                FROM asn_packages p
                WHERE p.id = c.package_id
                  AND NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = c.organisation_id);
                """);

            // ── (b) Delete violating rows in the derived ledgers, which have no parent to ask ──
            migrationBuilder.Sql("""
                DELETE FROM idempotency_keys t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t.org_id);
                """);

            migrationBuilder.Sql("""
                DELETE FROM ai_usage_monthly t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t.org_id);
                """);

            migrationBuilder.Sql("""
                DELETE FROM imported_sftp_files t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t.org_id);
                """);

            migrationBuilder.Sql("""
                DELETE FROM imported_s3_objects t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t.org_id);
                """);

            migrationBuilder.Sql("""
                DELETE FROM email_import_records t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t.org_id);
                """);

            migrationBuilder.Sql("""
                DELETE FROM canonical_field_defs t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t.org_id);
                """);

            migrationBuilder.Sql("""
                DELETE FROM "SchemaFingerprints" t
                WHERE NOT EXISTS (SELECT 1 FROM organisations o WHERE o.id = t."OrganisationId");
                """);

            // ── Supporting indexes for the new foreign keys ──────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_source_captures_org_id",
                table: "source_captures",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_confirmation_lines_org_id",
                table: "order_confirmation_lines",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_organisation_id",
                table: "invoice_lines",
                column: "organisation_id");

            migrationBuilder.CreateIndex(
                name: "IX_asn_packages_organisation_id",
                table: "asn_packages",
                column: "organisation_id");

            migrationBuilder.CreateIndex(
                name: "IX_asn_package_lines_organisation_id",
                table: "asn_package_lines",
                column: "organisation_id");

            // ── The org-wide audit listing's missing index (see the class doc) ───────────────
            migrationBuilder.CreateIndex(
                name: "IX_audit_events_org_id_created_at_desc",
                table: "audit_events",
                columns: new[] { "org_id", "created_at" },
                descending: new[] { false, true });

            // ── CASCADE foreign keys ─────────────────────────────────────────────────────────
            migrationBuilder.AddForeignKey(
                name: "FK_ai_usage_monthly_organisations_org_id",
                table: "ai_usage_monthly",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asn_package_lines_organisations_organisation_id",
                table: "asn_package_lines",
                column: "organisation_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asn_packages_organisations_organisation_id",
                table: "asn_packages",
                column: "organisation_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_field_defs_organisations_org_id",
                table: "canonical_field_defs",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_email_import_records_organisations_org_id",
                table: "email_import_records",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_idempotency_keys_organisations_org_id",
                table: "idempotency_keys",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_imported_s3_objects_organisations_org_id",
                table: "imported_s3_objects",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_imported_sftp_files_organisations_org_id",
                table: "imported_sftp_files",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_invoice_lines_organisations_organisation_id",
                table: "invoice_lines",
                column: "organisation_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_order_confirmation_lines_organisations_org_id",
                table: "order_confirmation_lines",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_order_parties_organisations_org_id",
                table: "order_parties",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SchemaFingerprints_organisations_OrganisationId",
                table: "SchemaFingerprints",
                column: "OrganisationId",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_source_captures_organisations_org_id",
                table: "source_captures",
                column: "org_id",
                principalTable: "organisations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // ── The billing-evidence foreign keys, created NOT VALID ─────────────────────────
            // Written as raw DDL rather than AddForeignKey because NOT VALID has no MigrationBuilder
            // expression. The constraint names are exactly the ones EF derives from the model, so
            // the snapshot stays truthful and DropForeignKey below finds them. NOT VALID enforces
            // every future insert and update while leaving existing rows unscanned — see (c) in the
            // class doc for why these two are exempted rather than cleaned or risked.
            //
            // The delete behaviours differ, and that difference is the point: RESTRICT on the record
            // of an actual charge, CASCADE on the plan history every organisation has from birth.
            migrationBuilder.Sql("""
                ALTER TABLE overage_billing_records
                ADD CONSTRAINT "FK_overage_billing_records_organisations_org_id"
                FOREIGN KEY (org_id) REFERENCES organisations (id) ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE org_plan_history
                ADD CONSTRAINT "FK_org_plan_history_organisations_org_id"
                FOREIGN KEY (org_id) REFERENCES organisations (id) ON DELETE CASCADE NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The repairs and cleanups in (a) and (b) are not reversed: a corrected organisation id
            // was wrong before it was corrected, and a deleted row referred to a tenant that does
            // not exist. Neither is worth restoring.
            migrationBuilder.DropForeignKey(
                name: "FK_ai_usage_monthly_organisations_org_id",
                table: "ai_usage_monthly");

            migrationBuilder.DropForeignKey(
                name: "FK_asn_package_lines_organisations_organisation_id",
                table: "asn_package_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_asn_packages_organisations_organisation_id",
                table: "asn_packages");

            migrationBuilder.DropForeignKey(
                name: "FK_canonical_field_defs_organisations_org_id",
                table: "canonical_field_defs");

            migrationBuilder.DropForeignKey(
                name: "FK_email_import_records_organisations_org_id",
                table: "email_import_records");

            migrationBuilder.DropForeignKey(
                name: "FK_idempotency_keys_organisations_org_id",
                table: "idempotency_keys");

            migrationBuilder.DropForeignKey(
                name: "FK_imported_s3_objects_organisations_org_id",
                table: "imported_s3_objects");

            migrationBuilder.DropForeignKey(
                name: "FK_imported_sftp_files_organisations_org_id",
                table: "imported_sftp_files");

            migrationBuilder.DropForeignKey(
                name: "FK_invoice_lines_organisations_organisation_id",
                table: "invoice_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_order_confirmation_lines_organisations_org_id",
                table: "order_confirmation_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_order_parties_organisations_org_id",
                table: "order_parties");

            migrationBuilder.DropForeignKey(
                name: "FK_org_plan_history_organisations_org_id",
                table: "org_plan_history");

            migrationBuilder.DropForeignKey(
                name: "FK_overage_billing_records_organisations_org_id",
                table: "overage_billing_records");

            migrationBuilder.DropForeignKey(
                name: "FK_SchemaFingerprints_organisations_OrganisationId",
                table: "SchemaFingerprints");

            migrationBuilder.DropForeignKey(
                name: "FK_source_captures_organisations_org_id",
                table: "source_captures");

            migrationBuilder.DropIndex(
                name: "IX_source_captures_org_id",
                table: "source_captures");

            migrationBuilder.DropIndex(
                name: "IX_order_confirmation_lines_org_id",
                table: "order_confirmation_lines");

            migrationBuilder.DropIndex(
                name: "IX_invoice_lines_organisation_id",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_org_id_created_at_desc",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_asn_packages_organisation_id",
                table: "asn_packages");

            migrationBuilder.DropIndex(
                name: "IX_asn_package_lines_organisation_id",
                table: "asn_package_lines");
        }
    }
}
