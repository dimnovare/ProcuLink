using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Two integrity invariants a 2026-08-20 read-only DB audit found were true in the data but
    /// enforced by application code alone. Both become the database's responsibility here.
    ///
    /// <para><b>1. delivery_attempts.idempotency_key had no index and no uniqueness.</b>
    /// The column was added bare (20260716083147_AddDeliveryAttemptIdempotencyKey); the table's
    /// only index was (org_id, order_id, attempted_at). Duplicate-send protection lived entirely
    /// in DeliveryService.OpenDispatchAttemptAsync's read-then-insert plus the delivery status
    /// CAS. The new unique index is PARTIAL — <c>idempotency_key IS NOT NULL AND
    /// status = 'dispatching'</c> — and the filter is the whole design, not a refinement:
    /// the key is DETERMINISTIC per (order, artifact) (DeliveryIdempotencyKey.Build), so every
    /// retry of the same artifact legitimately inserts a NEW row with the SAME key once the
    /// previous row is terminal, and a crash-recovery re-adopt reuses the surviving 'dispatching'
    /// row rather than inserting. Full uniqueness on (org_id, idempotency_key) would therefore
    /// break the retry ladder on its second rung; uniqueness over IN-FLIGHT rows is exactly the
    /// invariant the application check assumes and cannot guarantee under a race. Null keys
    /// (legacy / test-fire rows, DeliveryService.cs:817) are excluded so they never collide with
    /// each other.</para>
    ///
    /// <para>The second, non-unique index on idempotency_key alone serves
    /// DeliveryBounceHandler's correlation lookup, which is by key ALONE — deliberately org-blind,
    /// because the provider webhook payload names no tenant and none of it is trusted to; the
    /// attempt row IS the tenant boundary. The unique index cannot serve that lookup (it leads on
    /// org_id and covers only in-flight rows, while a bounce almost always lands on a terminal
    /// row), so before this index every bounce webhook was a sequential scan.</para>
    ///
    /// <para><b>2. order_supplier_suggestions had no FK to purchase_orders.</b> Its only FK was
    /// the org one, so a delete of an order neither cascaded here nor errored — rows silently kept
    /// a dangling order_id plus SignalsJson (document-derived identity text) and decided_by (an
    /// operator's Clerk user id). This table has already produced exactly that GDPR orphan once.
    /// DataErasureService deletes the rows explicitly and keeps doing so (belt and braces), but
    /// only the FK covers the paths that bypass it: raw SQL (which is how the historical prod
    /// deletes actually ran), future code, ops surgery. CASCADE because suggestions are derived,
    /// order-scoped content with no independent lifecycle. Column types match — both sides are
    /// uuid.</para>
    ///
    /// <para><b>Defensive cleanup first, because absence of violations cannot be proven from
    /// here.</b> (a) Orphaned suggestion rows may exist precisely because the FK was missing and
    /// raw-SQL deletes have bypassed the erasure service before — they are deleted before the FK
    /// is added (they are exactly the rows the FK exists to prevent; keeping them would fail the
    /// migration and preserve the orphan). (b) If duplicate in-flight attempt rows for the same
    /// (org, key) exist, they are the race artifact the unique index exists to prevent; all but
    /// the newest are finalised as 'unconfirmed' — the same terminal status the unknown-outcome
    /// park uses for "a send may have happened, the outcome was never observed" — rather than
    /// deleted, because an attempt row is evidence and the trail is the product.</para>
    /// </summary>
    public partial class TwoIntegrityInvariantsWereHeldShutByApplicationCodeAlone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (b) Finalise all but the newest duplicate in-flight row per (org_id, idempotency_key)
            // as 'unconfirmed' (see the class doc). Ties on attempted_at break by id so the
            // predicate is total. No-op when the application-side protection has held — expected.
            migrationBuilder.Sql("""
                UPDATE delivery_attempts a
                SET status = 'unconfirmed',
                    error_message = COALESCE(a.error_message,
                        'Finalised by migration TwoIntegrityInvariantsWereHeldShutByApplicationCodeAlone: '
                        || 'a newer in-flight attempt existed for the same idempotency key, so this row''s '
                        || 'outcome was never going to be observed.')
                WHERE a.status = 'dispatching'
                  AND a.idempotency_key IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM delivery_attempts b
                      WHERE b.org_id = a.org_id
                        AND b.idempotency_key = a.idempotency_key
                        AND b.status = 'dispatching'
                        AND (b.attempted_at > a.attempted_at
                             OR (b.attempted_at = a.attempted_at AND b.id > a.id)));
                """);

            // (a) Delete suggestion rows whose order is already gone (see the class doc). No-op
            // when every delete so far went through DataErasureService — expected, not assumed.
            migrationBuilder.Sql("""
                DELETE FROM order_supplier_suggestions s
                WHERE NOT EXISTS (
                    SELECT 1 FROM purchase_orders p WHERE p.id = s.order_id);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_order_supplier_suggestions_order_id",
                table: "order_supplier_suggestions",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_idempotency_key",
                table: "delivery_attempts",
                column: "idempotency_key",
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_delivery_attempts_org_id_idempotency_key_dispatching",
                table: "delivery_attempts",
                columns: new[] { "org_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL AND status = 'dispatching'");

            migrationBuilder.AddForeignKey(
                name: "FK_order_supplier_suggestions_purchase_orders_order_id",
                table: "order_supplier_suggestions",
                column: "order_id",
                principalTable: "purchase_orders",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The defensive cleanups are not reversed: the demoted attempt rows were already
            // unobservable, and the deleted suggestion rows were already orphans.
            migrationBuilder.DropForeignKey(
                name: "FK_order_supplier_suggestions_purchase_orders_order_id",
                table: "order_supplier_suggestions");

            migrationBuilder.DropIndex(
                name: "IX_order_supplier_suggestions_order_id",
                table: "order_supplier_suggestions");

            migrationBuilder.DropIndex(
                name: "IX_delivery_attempts_idempotency_key",
                table: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "UX_delivery_attempts_org_id_idempotency_key_dispatching",
                table: "delivery_attempts");
        }
    }
}
