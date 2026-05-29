namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// Result of a best-effort pass that enumerates the SOURCE field names present in an
/// uploaded supplier file, for ANY supported format. Powers the "magic mapping" UI,
/// which needs to show the user the columns/fields detected in their own file before
/// they map them onto the canonical PO model.
/// </summary>
/// <param name="Format">
/// Short format label the extractor settled on — one of <c>"csv"</c>, <c>"xlsx"</c>,
/// <c>"cxml"</c>, <c>"ubl"</c>, <c>"xml"</c>, <c>"edifact"</c>, <c>"x12"</c>,
/// <c>"pdf"</c>, or <c>"unknown"</c>. Mirrors <see cref="DetectedFormat.Format"/>.
/// </param>
/// <param name="Columns">
/// De-duplicated, order-preserving list of source field names:
/// <list type="bullet">
///   <item>CSV / XLSX — the header-row cell values.</item>
///   <item>XML (UBL / cXML / generic) — distinct leaf-element local-names plus notable
///         attribute names rendered as <c>Element@attr</c> (e.g. <c>OrderRequestHeader@orderID</c>).</item>
///   <item>EDIFACT / X12 — the distinct segment tags present (e.g. <c>BGM</c>, <c>LIN</c>, <c>BEG</c>).</item>
///   <item>PDF — best-effort; may be empty.</item>
/// </list>
/// Never <c>null</c> — an empty list means nothing usable was detected.
/// </param>
public sealed record SourceColumnSet(string Format, IReadOnlyList<string> Columns)
{
    /// <summary>An empty result for the given format (no detectable source fields).</summary>
    public static SourceColumnSet Empty(string format) => new(format, Array.Empty<string>());
}

/// <summary>
/// Extracts the SOURCE column / field names from an uploaded purchase-order file so the
/// mapping UI can present them for ANY format, not just CSV.
///
/// Implementations must:
/// <list type="bullet">
///   <item>Be safe against malformed input — never throw; on failure return an empty
///         <see cref="SourceColumnSet"/> with the best-known format label.</item>
///   <item>Operate on a seekable stream, or copy a non-seekable stream internally.</item>
///   <item>Be deterministic and side-effect free (no persistence, no network).</item>
/// </list>
/// </summary>
public interface ISourceColumnExtractor
{
    /// <summary>
    /// Inspects <paramref name="content"/> and returns the detected source field names.
    /// </summary>
    /// <param name="content">The file bytes. May be non-seekable; implementations buffer as needed.</param>
    /// <param name="fileName">
    /// Original file name (or storage key) — used for the extension hint when
    /// <paramref name="formatHint"/> is not supplied.
    /// </param>
    /// <param name="formatHint">
    /// Optional format label already determined by the caller (e.g. from
    /// <see cref="IFormatDetector"/> / <see cref="DetectedFormat.Format"/>). When supplied
    /// and recognised, it takes precedence over the extension hint.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<SourceColumnSet> ExtractAsync(
        Stream content,
        string? fileName,
        string? formatHint,
        CancellationToken ct);
}
