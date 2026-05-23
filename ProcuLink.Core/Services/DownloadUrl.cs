namespace ProcuLink.Core.Services;

/// <summary>Pre-signed download URL returned to the frontend for artifact download.</summary>
public record DownloadUrl(string Url, DateTime ExpiresAt);
