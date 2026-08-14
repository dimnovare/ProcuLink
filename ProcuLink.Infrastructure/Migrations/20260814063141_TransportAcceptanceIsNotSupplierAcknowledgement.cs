using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// EXPAND half of an expand/contract rename: adds <c>delivery_attempts.transport_accepted_at</c>
    /// and backfills it from <c>acknowledged_at</c>, which is left physically in place and unmapped.
    ///
    /// <para><b>What the old column always meant, and what the new one means.</b> Every value in
    /// <c>acknowledged_at</c> was written by <c>DeliveryService.PersistAttemptAsync</c> as
    /// <c>result.Success ? now : null</c>, where <c>now</c> is the same instant it stamps on
    /// <c>attempted_at</c> — the moment OUR dispatch call returned. It was never an acknowledgement:
    /// it is transport acceptance, off our own clock, and on SFTP/FTPS/SMTP there is no back-channel
    /// that could carry a supplier's answer at all. The backfill is therefore value-preserving and
    /// meaning-preserving in the only direction that matters: <b>a backfilled row means exactly what
    /// a newly written row means</b>, because the name was the only thing that ever overstated it.
    /// Nothing here migrates an acknowledgement, because none was ever recorded.</para>
    ///
    /// <para><b>Why expand/contract and not a bare <c>RenameColumn</c>.</b> EF scaffolds this diff as
    /// a rename, and taking that would have been an outage. Migrations run only at API startup
    /// (<c>ProcuLink.Api/Program.cs</c> — <c>await db.Database.MigrateAsync()</c>); the Hangfire
    /// Worker is a SEPARATE Railway service that deploys independently and never migrates. It
    /// materialises <c>DeliveryAttempt</c> as a whole entity through <c>DeliveryService</c>
    /// (<c>DeliverOrderJob</c>, <c>RetryDeliveryJob</c>, the stranded-delivery sweep), so EF
    /// enumerates every mapped column on both read and insert. A rename drops the old name, and the
    /// still-running old Worker build would take Npgsql <c>42703</c> on every delivery until it
    /// redeployed. This repository has already paid for that exact shape once, with
    /// <c>webhook_secret_encrypted</c>.
    ///
    /// <para>With both columns present the deploy window is safe in both directions: the old Worker
    /// keeps reading and writing <c>acknowledged_at</c>, new code reads and writes
    /// <c>transport_accepted_at</c>, and neither sees a missing column. An order delivered by the old
    /// Worker mid-window leaves <c>transport_accepted_at</c> null, which is harmless — <c>PassportService</c>
    /// falls back to the order's own <c>delivered</c> status for that leg, so the passport still reports
    /// the delivery and still declines to call it a supplier acceptance.</para></para>
    ///
    /// <para><b>The CONTRACT half is deliberately deferred to a follow-up PR</b>, once both Railway
    /// services provably run this build: a hand-written migration dropping <c>acknowledged_at</c>.
    /// <c>dotnet ef migrations add</c> will not generate it, because the model already omits the
    /// property. <c>AcknowledgedAtColumnDropStaysDeferredTests</c> pins that this is a known debt and
    /// is meant to be deleted by the PR that adds the drop.</para>
    ///
    /// <para>Raw SQL is the sanctioned exception inside a migration — a backfill has nowhere else to
    /// run. Idempotent: re-running it re-copies the same values, and the <c>IF NOT EXISTS</c>-shaped
    /// guard is unnecessary because <c>AddColumn</c> already fails closed on a second apply.</para>
    /// </summary>
    public partial class TransportAcceptanceIsNotSupplierAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<System.DateTime>(
                name: "transport_accepted_at",
                table: "delivery_attempts",
                type: "timestamptz",
                nullable: true);

            // Value-preserving backfill. `acknowledged_at` is left in place for the old Worker build
            // and is dropped by the deferred contract migration, not here.
            migrationBuilder.Sql("""
UPDATE delivery_attempts
   SET transport_accepted_at = acknowledged_at
 WHERE acknowledged_at IS NOT NULL;
""");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Safe and lossless: <c>acknowledged_at</c> was never dropped, and it still holds every
        /// value this migration copied. Dropping the new column returns the schema to exactly its
        /// previous shape.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "transport_accepted_at",
                table: "delivery_attempts");
        }
    }
}
