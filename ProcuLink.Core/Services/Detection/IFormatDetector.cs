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
/// <param name="Confidence">
/// A heuristic score in [0.0, 1.0], or <c>null</c> when nothing scored this detection.
///
/// <para><b>Only <see cref="FormatDetectionBasis.Heuristic"/> may carry a number.</b> This used to be
/// a non-nullable <c>double</c> documented as "the detector's certainty", which left the arms that
/// match a spec-mandated FILE HEADER no way to say "there is no score" — so they invented one.
/// <c>%PDF-</c> at offset 0 shipped as <c>0.95</c>. The leading bytes either are that sequence or
/// they are not; 0.95 fabricates a 5% doubt nobody measured, and
/// <see cref="FingerprintBoost.Apply"/> then did arithmetic on the invented number and narrated the
/// result to the operator ("Confidence boosted from 0.95 to 0.98"). The upload wizard printed it as
/// a percentage next to the format name.</para>
///
/// <para><see cref="Basis"/> is how an arm says which kind of answer this is, so the UI can name the
/// evidence instead of scoring it. Same shape as <c>AiMappingSuggestion.Basis</c> and
/// <c>MappingSuggestionDto.Basis</c>, from the sibling fixes on the mapping-suggestion paths.</para>
/// </param>
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
    double? Confidence,
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
    IReadOnlyList<string>? ColumnHeaders = null,
    /// <summary>
    /// What kind of answer this is — see <see cref="FormatDetectionBasis"/>. The default is
    /// <see cref="FormatDetectionBasis.Heuristic"/> because an arm that supplies a number and
    /// forgets to state its basis is at worst over-modest, never over-confident.
    /// </summary>
    string Basis = FormatDetectionBasis.Heuristic);

/// <summary>
/// Values for <see cref="DetectedFormat.Basis"/>, mirrored by the frontend's
/// <c>DetectFormatResult</c> in <c>src/lib/api-client.ts</c>. Same shape as
/// <c>MappingSuggestionBasis</c> and <c>AiMappingSuggestionBasis</c>.
///
/// <para>The frontend half landed FIRST, deliberately — project-proculink#206. Until it did, its
/// <c>confidence</c> was a non-nullable <c>number</c> and <c>UploadWorkbench.tsx</c> rendered a null
/// as <c>Math.round(null * 100)</c> = <b>0%</b>, because <c>null * 100</c> is 0 rather than NaN.
/// On a percentage ramp 0% reads as "certainly wrong", so shipping this side first would have
/// replaced a fabricated 95% with a louder lie, on the one detection this detector is certain about.
/// The frontend now reads <see cref="MagicBytes"/> and names the evidence instead of scoring it.</para>
///
/// <para>The invariant, enforced by <c>FormatDetectorBasisInvariantTests</c>:
/// <b><see cref="Heuristic"/> carries a number and the other two never do.</b></para>
/// </summary>
public static class FormatDetectionBasis
{
    /// <summary>
    /// The format's own leading signature, at the offset the specification puts it — <c>%PDF-</c> at
    /// byte 0, an <c>ISA</c> interchange header, the EDIFACT <c>UNA:+.?'</c> service string advice.
    /// A byte comparison, so the answer is a FACT and <see cref="DetectedFormat.Confidence"/> is null:
    /// there is no doubt here to express as a fraction, and no fraction to boost.
    /// </summary>
    public const string MagicBytes = "magic_bytes";

    /// <summary>
    /// Content sniffing with real ambiguity — a marker found somewhere in the peek window rather than
    /// at a defined offset, a filename extension taken as evidence, a separator-frequency guess.
    /// These carry a score, and only these may. The score is a hand-tuned prior expressing the
    /// ordering between arms, not a measurement, which is exactly what a heuristic is.
    /// </summary>
    public const string Heuristic = "heuristic";

    /// <summary>
    /// Nothing matched, or the peek could not be read at all. <see cref="DetectedFormat.Format"/> is
    /// <c>"unknown"</c> and <see cref="DetectedFormat.Confidence"/> is null — this used to be
    /// <c>0.0</c>, which is a number on the same ramp where 0% reads as "certainly wrong" rather
    /// than "not determined". The reasoning list says what was tried.
    /// </summary>
    public const string Undetermined = "undetermined";
}

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
