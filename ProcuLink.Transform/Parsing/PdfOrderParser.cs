using System.Globalization;
using System.Text.RegularExpressions;
using ProcuLink.Core.Services.Ocr;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses text-based PDF purchase orders. Falls back to <see cref="IDocumentOcrService"/>
/// when PdfPig extracts no text (image-only / scanned PDFs) and an OCR provider is configured.
/// </summary>
public sealed class PdfOrderParser : IPurchaseOrderParser
{
    private readonly IDocumentOcrService? _ocrService;

    public PdfOrderParser() : this(null) { }

    public PdfOrderParser(IDocumentOcrService? ocrService)
    {
        _ocrService = ocrService;
    }

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

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        // Buffer up-front so PdfPig and the OCR fallback can each consume a fresh stream
        // over the same bytes — input streams are not guaranteed to be seekable.
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Timeout-bounded so a pathological PDF can't hang the parse pipeline indefinitely.
        var textLines = (await PdfTextExtractor.ExtractLinesAsync(bytes, ct)).ToList();

        // OCR fallback for scanned / image-only PDFs (no-op when OCR provider not configured).
        if (textLines.Count == 0 && _ocrService is { IsAvailable: true })
        {
            using var ocrStream = new MemoryStream(bytes);
            var ocrText = await _ocrService.ExtractTextAsync(ocrStream, "application/pdf", ct);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                textLines = PdfTextExtractor.NormalizeLines(
                    PdfTextExtractor.SplitPageText(ocrText)).ToList();
            }
        }

        if (textLines.Count == 0)
            return new ParsedOrder(null, null, null, null, Array.Empty<ParsedOrderLine>());

        return new ParsedOrder(
            PoNumber:  FindFirstValue(textLines, PoNumberRegex),
            OrderDate: ParseDate(FindFirstValue(textLines, OrderDateRegex)),
            BuyerName: FindFirstValue(textLines, BuyerRegex),
            Currency:  FindFirstValue(textLines, CurrencyRegex)?.ToUpperInvariant(),
            Lines:     ParseLines(textLines));
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
