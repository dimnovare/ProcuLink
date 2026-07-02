namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Size limits for the CATALOG pull channel (plan 2026-07-02 D5/P0.8). Catalog files are
/// legitimately much larger than purchase orders — the biggest measured real feed is 100MEGA's
/// StoItemBase at ~174 MB — so they get their own home instead of borrowing the 10 MB
/// <see cref="IngressLimits.MaxFileBytes"/> PO-ingress cap (which stays untouched).
///
/// Values fixed from the Phase 0 probes: Ingram inner 17.7 MB, REDACTED-PARTY 6.2 MB, 100MEGA 174 MB.
/// 256 MB covers 100MEGA with headroom while still bounding a hostile/endless server.
/// </summary>
public static class CatalogLimits
{
    /// <summary>Download cap for a catalog pull (bytes) — replaces the 10 MB PO-ingress cap on the catalog path only.</summary>
    public const long MaxCatalogFileBytes = 256L * 1024 * 1024;

    /// <summary>Uncompressed cap when transparently unwrapping a ZIP catalog archive (bytes).</summary>
    public const long MaxUncompressedBytes = 256L * 1024 * 1024;
}
