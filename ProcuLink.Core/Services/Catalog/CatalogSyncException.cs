namespace ProcuLink.Core.Services.Catalog;

/// <summary>
/// Sanitized catalog-sync failure (security finding M4). The message is ALWAYS one of the
/// enumerated safe messages — never raw FluentFTP/SSH.NET/socket text, which can leak the
/// host, username, or server banner into Hangfire dashboards and Sentry. The inner
/// exception is deliberately NOT carried for the same reason: the raw exception is logged
/// at Debug level only, at the mapping site.
/// </summary>
public sealed class CatalogSyncException : Exception
{
    public CatalogSyncException(string safeMessage) : base(safeMessage) { }
}

/// <summary>
/// Thrown by the bounded download helper when a remote catalog file exceeds the ingress
/// byte cap (security finding H2 — the cap is enforced DURING the streamed copy, never
/// after full materialization; at most cap+1 bytes are ever read).
/// </summary>
public sealed class CatalogFileTooLargeException : Exception
{
    public CatalogFileTooLargeException(long maxBytes)
        : base($"Catalog file exceeds {maxBytes / (1024 * 1024)} MB.") { }
}
