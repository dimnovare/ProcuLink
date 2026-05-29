namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// Result of a smart format-detection pass over an uploaded file.
///
/// All fields are best-effort and may be <c>null</c> when the detector cannot
/// confidently extract them from the first 1024 bytes / first 5 lines peek.
/// </summary>
/// <param name="Format">
/// One of <c>"csv"</c>, <c>"xlsx"</c>, <c>"pdf"</c>, <c>"cxml"</c>, <c>"ubl"</c>,
/// <c>"edifact"</c>, <c>"x12"</c>, <c>"xml"</c> (generic XML), or <c>"unknown"</c>.
/// </param>
/// <param name="Confidence">A value in [0.0, 1.0] representing the detector's certainty.</param>
/// <param name="SuggestedParser">
/// Fully-qualified or simple class name of the recommended <c>IPurchaseOrderParser</c>
/// implementation (e.g. <c>"CxmlOrderParser"</c>, <c>"UblOrderParser"</c>).
/// May be <c>null</c> when <see cref="Format"/> is <c>"unknown"</c>.
/// </param>
/// <param name="DetectedPoNumber">PO number / order id extracted from the peek, if any.</param>
/// <param name="DetectedSupplier">Best-effort supplier name extracted from the peek, if any.</param>
/// <param name="EstimatedLineCount">
/// Estimated line count from the peek window. For CSV this is non-empty rows minus header.
/// For XML it counts <c>cac:OrderLine</c> / <c>ItemOut</c> occurrences in the buffered prefix.
/// </param>
/// <param name="Reasoning">
/// Human-readable list of signals that led to the chosen format/confidence (debug aid +
/// power-user expert-mode display per Phase 6 standards-visibility rule).
/// </param>
public sealed record DetectedFormat(
    string Format,
    double Confidence,
    string? SuggestedParser,
    string? DetectedPoNumber,
    string? DetectedSupplier,
    int? EstimatedLineCount,
    IReadOnlyList<string> Reasoning,
    /// <summary>
    /// How many times this org has previously parsed a file with this exact column layout.
    /// Populated by <see cref="ProcuLink.Core.Services.Detection.FingerprintBoost.Apply"/> when a
    /// schema fingerprint match is found; null when the layout is new or fingerprinting did not run.
    /// </summary>
    int? SeenCount = null,
    /// <summary>
    /// Column header names extracted from the file during detection (for CSV/XLSX).
    /// Passed to <see cref="ISchemaFingerprintService"/> to avoid a second file download.
    /// Null for header-less formats (XML, EDI, PDF).
    /// </summary>
    IReadOnlyList<string>? ColumnHeaders = null);

/// <summary>
/// Smart "drop any file" format detector. Implementations must:
/// <list type="bullet">
///   <item>Be safe against malformed input — never throw; on failure return
///         <c>Format = "unknown"</c> with reasoning explaining the failure.</item>
///   <item>Rewind the input <see cref="Stream"/> back to position 0 before returning
///         so callers can pass the same stream on to the actual parser.</item>
///   <item>Operate on a seekable stream. If <c>!content.CanSeek</c> the implementation
///         must copy to a <see cref="System.IO.MemoryStream"/> internally.</item>
/// </list>
/// </summary>
public interface IFormatDetector
{
    /// <summary>
    /// Inspects <paramref name="content"/> (and optionally <paramref name="fileName"/> for
    /// extension hints) and returns the most likely file format plus confidence + metadata.
    /// </summary>
    Task<DetectedFormat> DetectAsync(Stream content, string? fileName, CancellationToken ct);
}
