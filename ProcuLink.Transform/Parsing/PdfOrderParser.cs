using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses text-based PDF purchase orders. This is intentionally conservative:
/// scanned/OCR PDFs are out of scope until an OCR extraction layer is added.
/// </summary>
public sealed class PdfOrderParser : IPurchaseOrderParser
{
    private static readonly Regex PoNumberRegex = new(
        @"\b(?:po\s*(?:number|no\.?|#)|purchase\s*order)\s*[:#-]?\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OrderDateRegex = new(
        @"\b(?:order\s*date|date)\s*[:#-]?\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BuyerRegex = new(
        @"\b(?:buyer|customer|bill\s*to)\s*[:#-]?\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrencyRegex = new(
        @"\bcurrency\s*[:#-]?\s*(?<value>[A-Z]{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineWithPriceRegex = new(
        @"^\s*(?<line>\d+)\s+(?<code>[A-Z0-9][A-Z0-9._/-]*)\s+(?<desc>.+?)\s+(?<qty>-?\d+(?:[.,]\d+)?)\s+(?<unit>[A-Za-z]{1,12})\s+(?<price>-?\d+(?:[.,]\d+)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineWithoutPriceRegex = new(
        @"^\s*(?<line>\d+)\s+(?<code>[A-Z0-9][A-Z0-9._/-]*)\s+(?<desc>.+?)\s+(?<qty>-?\d+(?:[.,]\d+)?)\s+(?<unit>[A-Za-z]{1,12})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        using var document = PdfDocument.Open(fileStream);
        var textLines = ExtractTextLines(document)
            .Select(NormalizeWhitespace)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (textLines.Count == 0)
            return Task.FromResult(new ParsedOrder(null, null, null, null, Array.Empty<ParsedOrderLine>()));

        var parsed = new ParsedOrder(
            PoNumber:  FindFirstValue(textLines, PoNumberRegex),
            OrderDate: ParseDate(FindFirstValue(textLines, OrderDateRegex)),
            BuyerName: FindFirstValue(textLines, BuyerRegex),
            Currency:  FindFirstValue(textLines, CurrencyRegex)?.ToUpperInvariant(),
            Lines:     ParseLines(textLines));

        return Task.FromResult(parsed);
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

    private static IEnumerable<string> SplitPageText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var line in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            yield return line;
    }

    private static IReadOnlyList<ParsedOrderLine> ParseLines(IEnumerable<string> lines)
    {
        var parsedLines = new List<ParsedOrderLine>();

        foreach (var line in lines)
        {
            if (IsLikelyLineHeader(line))
                continue;

            var match = LineWithPriceRegex.Match(line);
            if (!match.Success)
                match = LineWithoutPriceRegex.Match(line);

            if (!match.Success)
                continue;

            var lineNumber = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var quantity = ParseDecimal(match.Groups["qty"].Value) ?? 0m;
            var unitPrice = match.Groups["price"].Success
                ? ParseDecimal(match.Groups["price"].Value)
                : null;

            parsedLines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: match.Groups["code"].Value.Trim(),
                Description:   NullIfEmpty(match.Groups["desc"].Value),
                Quantity:      quantity,
                Unit:          NullIfEmpty(match.Groups["unit"].Value),
                UnitPrice:     unitPrice));
        }

        return parsedLines;
    }

    private static bool IsLikelyLineHeader(string line) =>
        line.Contains("BuyerItemCode", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Item Code", StringComparison.OrdinalIgnoreCase)
        || line.Contains("UnitPrice", StringComparison.OrdinalIgnoreCase);

    private static string? FindFirstValue(IEnumerable<string> lines, Regex regex)
    {
        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success)
                continue;

            return NullIfEmpty(match.Groups["value"].Value);
        }

        return null;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy", "d.M.yyyy" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;

        return null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Contains(",", StringComparison.Ordinal) && !normalized.Contains(".", StringComparison.Ordinal))
            normalized = normalized.Replace(',', '.');

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
