using Microsoft.EntityFrameworkCore;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// Shared idempotency helper for the pull-ingress channels (SFTP / S3 / IMAP email).
///
/// <para>All three channels use a CLAIM-FIRST ordering: the dedupe-ledger row
/// (<c>ImportedSftpFile</c> / <c>ImportedS3Object</c> / <c>EmailImportRecord</c>) is inserted and
/// committed BEFORE the order stub is created. The ledger's unique index is the real guarantee —
/// a Hangfire retry, a Railway SIGTERM re-run, or a concurrent same-org poll landing in the window
/// hits the unique violation and SKIPS the file instead of creating a duplicate order (and a
/// duplicate supplier delivery + duplicate €0.50 overage). The pre-insert existence check is only
/// a fast path; this unique-index claim is the actual guard.</para>
///
/// <para><b>Resume-on-conflict.</b> Committing the claim first is not enough on its own: if the
/// order stub then fails <em>transiently</em> (an R2 blip, a Neon timeout, a Railway SIGTERM after
/// the claim commits), an "on-any-existing-claim SKIP" policy would treat the file as SEEN forever —
/// the purchase order is never imported, retried, or surfaced: a SILENT LOST ORDER, worse than the
/// duplicate the claim prevents. So each claim carries a <b>pre-generated OrderId</b>, stored
/// atomically with the claim insert (there is no separate "backfill" step whose failure could open
/// a window), and the order is created under that exact primary key. When a poll finds a
/// pre-existing claim it asks <see cref="ClaimSatisfiedAsync"/>:
/// <list type="bullet">
/// <item><description><c>OrderId == Guid.Empty</c> → SKIP (legacy row migrated before this column,
/// or a claim marked terminally failed after a permanent stub error — bounds a bad file);</description></item>
/// <item><description>the order already exists under <c>OrderId</c> → SKIP (a true duplicate,
/// including the "order committed but a post-step crashed" case);</description></item>
/// <item><description>the order does NOT exist under <c>OrderId</c> → RESUME (a transient failure
/// left it un-created — recreate it, reusing the same id so the retry is a find-or-create).</description></item>
/// </list>
/// A concurrent same-org poll that loses the claim-insert race hits <see cref="IsUniqueViolation"/>
/// and SKIPS (it must not resume — the winner is mid-flight); resume is reached only through the
/// pre-existing-claim path (a genuine earlier attempt), which <c>[DisableConcurrentExecution(orgId)]</c>
/// keeps sequential in production, with the order primary key as the final backstop.</para>
/// </summary>
public static class IngressDedupe
{
    /// <summary>
    /// Sentinel <c>OrderId</c> meaning "SKIP unconditionally": a legacy ledger row migrated before
    /// the column existed, or a claim marked terminally failed after a PERMANENT stub-creation error
    /// (empty/unsupported content) so a genuinely-bad file is not retried forever.
    /// </summary>
    public static readonly Guid TerminalOrderId = Guid.Empty;

    /// <summary>
    /// True when a <see cref="DbUpdateException"/> was caused by a Postgres unique-index violation
    /// (SQLSTATE 23505) — i.e. a concurrent poll or a job retry already claimed this file. Walks the
    /// inner-exception chain and duck-types the <c>SqlState</c> property (Npgsql's
    /// <c>PostgresException.SqlState</c>) so this assembly needs no hard Npgsql dependency. Any other
    /// <see cref="DbUpdateException"/> is a genuine persistence failure and must propagate.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var sqlState = e.GetType().GetProperty("SqlState")?.GetValue(e) as string;
            if (sqlState == "23505")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resume-vs-skip decision for a PRE-EXISTING claim: <c>true</c> ⇒ the claim is satisfied, SKIP
    /// (terminal/legacy sentinel, or the order already exists); <c>false</c> ⇒ the order was never
    /// created (a transient failure abandoned it), so RESUME by recreating it under the same id.
    /// Only ever called with the committed claim's stored <paramref name="claimOrderId"/>; the
    /// <c>Guid.Empty</c> short-circuit means the purchase-orders table is not queried for the
    /// terminal/legacy case (so trimmed in-memory test contexts that ignore it are unaffected).
    /// </summary>
    public static Task<bool> ClaimSatisfiedAsync(
        ProcuLinkDbContext db, Guid orgId, Guid claimOrderId, CancellationToken ct)
        => claimOrderId == TerminalOrderId
            ? Task.FromResult(true)
            : db.PurchaseOrders.AnyAsync(o => o.OrgId == orgId && o.Id == claimOrderId, ct);
}
