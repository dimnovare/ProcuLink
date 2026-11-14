namespace ProcuLink.Core.Services.Catalog;

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
