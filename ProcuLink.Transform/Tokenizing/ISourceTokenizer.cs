namespace ProcuLink.Transform.Tokenizing;

/// <summary>
/// Extracts every addressable value from a raw source file as a flat list of
/// <see cref="SourceToken"/> records, each with a stable Id that can be saved in
/// <c>SourceFieldRule.SourceToken</c> and resolved at re-derive time.
///
/// Implementations: <see cref="SourceTokenizer"/> (CSV + XML concrete; other formats return empty list).
/// </summary>
public interface ISourceTokenizer
{
    /// <summary>
    /// Tokenise the source file and return every addressable value.
    /// <list type="bullet">
    ///   <item>CSV → every cell (including header row).</item>
    ///   <item>XML → every leaf element text and every attribute value.</item>
    ///   <item>All other formats → empty list (tokenisation not yet implemented).</item>
    /// </list>
    /// Never throws for an unsupported format; returns an empty list.
    /// May throw <see cref="System.IO.IOException"/> or format-specific parse exceptions
    /// for genuinely malformed input.
    /// </summary>
    /// <param name="sourceBytes">Raw source-file bytes.</param>
    /// <param name="fileExtension">Lowercase file extension including the dot, e.g. <c>".csv"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SourceToken>> TokenizeAsync(
        byte[] sourceBytes,
        string fileExtension,
        CancellationToken ct = default);
}
