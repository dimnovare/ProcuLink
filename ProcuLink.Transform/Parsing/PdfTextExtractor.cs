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
