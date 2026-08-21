namespace ProcuLink.Core.Services.Ingress;

/// <summary>
/// Size limits for the CATALOG pull channel (plan 2026-07-02 D5/P0.8). Catalog files are
/// legitimately much larger than purchase orders — the biggest measured real feed is a large
/// attribute-style <c>StoItemBase</c> XML export at ~174 MB — so they get their own home instead
/// of borrowing the 10 MB <see cref="IngressLimits.MaxFileBytes"/> PO-ingress cap (which stays
/// untouched).
///
/// Values fixed from the Phase 0 probes: 17.7 MB inside a ZIP-wrapped price list, 6.2 MB for a
/// ';'-delimited named-column feed, 174 MB for the attribute-style XML feed. 256 MB covers that
/// 174 MB feed with headroom while still bounding a hostile/endless server.
/// </summary>
public static class CatalogLimits
{
    /// <summary>Download cap for a catalog pull (bytes) — replaces the 10 MB PO-ingress cap on the catalog path only.</summary>
    public const long MaxCatalogFileBytes = 256L * 1024 * 1024;

    /// <summary>Uncompressed cap when transparently unwrapping a ZIP catalog archive (bytes).</summary>
    public const long MaxUncompressedBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Download cap for the INTERACTIVE test-fetch probe (bytes) — the one catalog fetch that runs
    /// inside the request-serving API process rather than the Worker.
    ///
    /// <para>
    /// The probe exists to prove credentials, reachability and column shape before a feed is
    /// enabled; it is not an import. Running it at the full 256 MB ingestion cap meant one operator
    /// testing a large distributor feed could buffer a quarter-gigabyte in the process every tenant
    /// shares. 32 MB covers every catalog measured in the Phase 0 probes except the 174 MB
    /// attribute-style XML feed — which is precisely the shape that caused the pressure — while
    /// cutting the worst case by 8x. The scheduled Worker sync keeps
    /// <see cref="MaxCatalogFileBytes"/> unchanged.
    /// </para>
    /// </summary>
    public const long MaxInteractiveTestFetchBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Uncompressed cap when unwrapping a ZIP during the interactive test-fetch (bytes). Twice the
    /// download cap, so a compressed feed that fits the probe can still be inspected, and a zip
    /// bomb inside a 32 MB archive still cannot expand to the full ingestion ceiling in the API.
    /// </summary>
    public const long MaxInteractiveUncompressedBytes = 64L * 1024 * 1024;
}
