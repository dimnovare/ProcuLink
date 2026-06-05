using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Shared PDF text-layer extraction used by both the deterministic
/// <see cref="PdfOrderParser"/> and the LLM-backed structured extractor.
///
/// Words are clustered into lines by their vertical position (a small y-bucket)
/// and ordered left-to-right so column layouts survive in reading order — this
/// is what lets a downstream LLM (or the regex parser) see a table the way a
/// human would. Returns an empty result for image-only / scanned PDFs that have
/// no extractable text layer.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>
    /// Generous upper bound on a single PdfPig parse so a pathological / hostile PDF
    /// can't hang a Worker thread indefinitely. Comfortably above any real PO/invoice.
    /// </summary>
    public static readonly TimeSpan DefaultParseTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Extracts normalised, non-empty text lines from a PDF byte buffer, in
    /// reading order. Empty when the PDF has no text layer.
    /// </summary>
    public static IReadOnlyList<string> ExtractLines(byte[] pdfBytes)
    {
        using var pdfStream = new MemoryStream(pdfBytes);
        using var document = PdfDocument.Open(pdfStream);
        return NormalizeLines(ExtractTextLines(document));
    }

    /// <summary>
    /// Extracts the full text of a PDF as a single newline-joined string —
    /// convenient as the source text for an LLM prompt and for anti-hallucination
    /// validation. Empty string when the PDF has no text layer.
    /// </summary>
    public static string ExtractText(byte[] pdfBytes) =>
        string.Join("\n", ExtractLines(pdfBytes));

    /// <summary>
    /// Timeout-bounded <see cref="ExtractLines(byte[])"/>. PdfPig's parse is synchronous
    /// and not cooperatively cancellable, so this bounds the CALLER's wait: on timeout (or
    /// external cancellation) the in-flight parse task is abandoned and a
    /// <see cref="TimeoutException"/> (resp. <see cref="OperationCanceledException"/>) is
    /// thrown, which the callers map to a clean parse failure / deterministic fallback —
    /// the pipeline is never blocked indefinitely on one document.
    /// </summary>
    public static Task<IReadOnlyList<string>> ExtractLinesAsync(byte[] pdfBytes, CancellationToken ct = default) =>
        ExtractLinesAsync(pdfBytes, DefaultParseTimeout, ct);

    /// <inheritdoc cref="ExtractLinesAsync(byte[], CancellationToken)"/>
    public static async Task<IReadOnlyList<string>> ExtractLinesAsync(
        byte[] pdfBytes, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); // fail fast on an already-cancelled token

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var work = Task.Run(() => ExtractLines(pdfBytes), CancellationToken.None);
        var finished = await Task.WhenAny(work, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
        if (finished == work)
            return await work.ConfigureAwait(false);

        // Timed out or externally cancelled — abandon the (leaked) parse task and surface
        // the right exception. External cancellation wins over the timeout classification.
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"PDF text extraction exceeded {timeout.TotalSeconds:0}s.");
    }

    /// <summary>Timeout-bounded <see cref="ExtractText(byte[])"/>. See <see cref="ExtractLinesAsync(byte[], CancellationToken)"/>.</summary>
    public static async Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken ct = default) =>
        string.Join("\n", await ExtractLinesAsync(pdfBytes, ct).ConfigureAwait(false));

    /// <inheritdoc cref="ExtractTextAsync(byte[], CancellationToken)"/>
    public static async Task<string> ExtractTextAsync(byte[] pdfBytes, TimeSpan timeout, CancellationToken ct = default) =>
        string.Join("\n", await ExtractLinesAsync(pdfBytes, timeout, ct).ConfigureAwait(false));

    /// <summary>
    /// Normalises whitespace on each line and drops blanks. Public so callers
    /// (e.g. an OCR fallback) can run their own raw text through the same
    /// normalisation the PDF path uses.
    /// </summary>
    public static IReadOnlyList<string> NormalizeLines(IEnumerable<string> lines) =>
        lines
            .Select(NormalizeWhitespace)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    /// <summary>Splits a raw text blob into lines on any newline convention.</summary>
    public static IEnumerable<string> SplitPageText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            yield return line;
    }

    private static IEnumerable<string> ExtractTextLines(PdfDocument document)
    {
        foreach (var page in document.GetPages())
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
            {
                foreach (var line in SplitPageText(page.Text))
                    yield return line;
                continue;
            }

            foreach (var line in WordsToLines(words))
                yield return line;
        }
    }

    private static IEnumerable<string> WordsToLines(IReadOnlyCollection<Word> words)
    {
        return words
            .GroupBy(word => Math.Round(word.BoundingBox.Bottom / 4.0) * 4.0)
            .OrderByDescending(group => group.Key)
            .Select(group => string.Join(" ",
                group.OrderBy(word => word.BoundingBox.Left)
                    .Select(word => word.Text)));
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();
}
