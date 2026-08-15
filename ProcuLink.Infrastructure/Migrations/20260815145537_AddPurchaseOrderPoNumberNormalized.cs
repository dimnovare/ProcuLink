using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// B-6 — gives purchase orders a comparison key so a repeated PO number can be DETECTED.
    /// Before this, nothing in production ever looked an order up by PO number
    /// (<c>git grep "PoNumber =="</c> outside tests returned nothing) and every ingress ledger keyed
    /// on transport identity only, so the same PO arriving by two channels was two orders with no
    /// shared key.
    ///
    /// <para><b>The index is deliberately NOT UNIQUE.</b> A buyer legitimately re-sends a corrected
    /// PO under the same number; a unique constraint would reject it at the database and lose a real
    /// order. See the class doc on <c>OrderExceptionService</c> for the full argument.</para>
    ///
    /// <para><b>Deploy-time locking.</b> Three statements, in this order and for this reason:</para>
    /// <list type="number">
    ///   <item><c>ADD COLUMN … text NULL</c> with no DEFAULT is a catalog-only change on Postgres 11+
    ///   — no table rewrite, ACCESS EXCLUSIVE held for microseconds.</item>
    ///   <item>The backfill runs BEFORE the index is created, so the index is built once over final
    ///   data instead of being built and then churned. It takes row locks only (no table lock), and
    ///   readers are never blocked by it.</item>
    ///   <item><c>CREATE INDEX</c> (not CONCURRENTLY) takes a SHARE lock that blocks WRITES to
    ///   purchase_orders while it builds. This matches the precedent set by
    ///   <c>AddCatalogTrigramIndexes</c>: EF migrations run inside a transaction and CONCURRENTLY
    ///   cannot, so the choice is this brief write-pause or moving index creation out of the
    ///   migration system entirely. At current table size the pause is well under a Railway deploy's
    ///   health-check window. If purchase_orders ever grows to where that stops being true, the fix is
    ///   a separate out-of-band CONCURRENTLY step, not a silently long migration.</item>
    /// </list>
    /// Reversible: Down drops the index then the column, losing only derived data.
    /// </summary>
    public partial class AddPurchaseOrderPoNumberNormalized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "po_number_normalized",
                table: "purchase_orders",
                type: "text",
                nullable: true);

            // Backfill. NULL in this column has exactly one meaning — "carries no PO number anyone
            // asserted, so it takes part in no duplicate comparison" — and that meaning only holds
            // if every pre-existing row is filled in here. Leaving historical rows NULL would make
            // NULL ambiguous between "placeholder" and "not computed", which is precisely the kind
            // of overloaded absence that makes a detector quietly stop detecting.
            //
            // Placeholders are recognised by PATTERN, which is a guess — but it is a guess made
            // ONCE, here, against historical rows that carry no other evidence, and never again:
            // from this migration forward PoNumberIdentity sets the column explicitly at write
            // time. The pattern matches the OLD placeholder shape `PO-yyyyMMddHHmmss` (the
            // collision-prone one this change replaces) and the new suffixed shape, so a re-run
            // after a partial deploy is still correct.
            migrationBuilder.Sql(@"
                UPDATE purchase_orders
                   SET po_number_normalized = upper(btrim(po_number))
                 WHERE po_number_normalized IS NULL
                   AND po_number IS NOT NULL
                   AND btrim(po_number) <> ''
                   AND po_number !~ '^PO-[0-9]{14}(-[0-9A-Fa-f]{6})?$'
                   AND is_sample = false;");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_org_id_po_number_normalized",
                table: "purchase_orders",
                columns: new[] { "org_id", "po_number_normalized" },
                filter: "po_number_normalized IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_org_id_po_number_normalized",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "po_number_normalized",
                table: "purchase_orders");
        }
    }
}
