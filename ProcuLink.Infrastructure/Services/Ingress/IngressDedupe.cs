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
/// </summary>
public static class IngressDedupe
{
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
}
