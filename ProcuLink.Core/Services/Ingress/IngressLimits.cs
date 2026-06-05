namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Shared size limit for files arriving on the non-HTTP ingress channels
/// (SFTP, S3/R2, IMAP, hosted inbound email). The HTTP upload path is already
/// capped at 10 MB (<c>OrdersController.MaxUploadBytes</c> + the framework
/// <c>RequestSizeLimit</c>); this keeps the pull/push channels consistent so a
/// pathological multi-GB file can't be buffered into memory and pushed through
/// the parse pipeline. A file at or under this size is accepted; an oversized
/// file is skipped with a warning (never silently dropped).
/// </summary>
public static class IngressLimits
{
    /// <summary>Maximum accepted file size for non-HTTP ingress, in bytes (10 MB — matches the HTTP cap).</summary>
    public const long MaxFileBytes = 10L * 1024 * 1024;
}
