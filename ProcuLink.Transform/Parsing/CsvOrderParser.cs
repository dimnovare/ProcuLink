using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses CSV purchase order files (comma or semicolon delimited).
/// Column names are normalised to lowercase before matching, so headers like
/// "BuyerItemCode", "buyerItemCode", and "Buyer Item Code" all resolve correctly.
/// Supported column aliases:
///   line number   — linenumber
///   buyer code    — buyeritemcode, itemcode
///   description   — description
///   quantity      — quantity
///   unit          — unit
///   unit price    — unitprice, price
///   po number     — ponumber
///   order date    — orderdate
///   buyer name    — buyername
///   currency      — currency
/// </summary>
public sealed class CsvOrderParser : IPurchaseOrderParser
{
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".csv", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        // Buffer + NORMALIZE so the parse is byte-deterministic across platforms. The
        // embedded onboarding fixture parsed correctly on Windows but to an EMPTY order on
        // Linux (prod) — same parser, divergent bytes: git stores the CSV LF-normalized, the
        // Windows working copy is CRLF, and the trailing blank line was counted as an extra
        // empty row on one platform but not the other. Decoding UTF-8 explicitly (BOM-safe),
        // unifying line endings to \n, and dropping trailing blank lines removes every
        // OS/encoding-dependent variable before CsvHelper sees the text.
        using var src = new MemoryStream();
        await fileStream.CopyToAsync(src, ct);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var text = utf8.GetString(src.ToArray());
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];  // strip a UTF-8 BOM
        // Collapse every line ending to \n, drop trailing blank lines, then re-emit CRLF.
        // CRLF is the form proven to parse correctly on BOTH platforms (Windows dev + real
        // customer CSV uploads on Linux prod); the git-normalized LF-only embedded fixture
        // was the only input that parsed to an empty order on Linux, so we never feed bare LF.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n', ' ', '\t').Replace("\n", "\r\n");
        var ms = new MemoryStream(utf8.GetBytes(text));

        var delimiter = DetectDelimiter(ms);
        ms.Position = 0;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter              = delimiter,
            HeaderValidated        = null!,
            MissingFieldFound      = null!,
            PrepareHeaderForMatch  = args => NormalizeHeader(args.Header)
        };

        using var reader = new StreamReader(ms, utf8);
        using var csv    = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<RawRowMap>();
        var rows = new List<RawRow>();
        try
        {
            await foreach (var row in csv.GetRecordsAsync<RawRow>(ct))
                rows.Add(row);
        }
        catch (CsvHelper.ReaderException)
        {
            // No header column matched any known alias, so CsvHelper has no members to
            // map and raises "No members are mapped for type 'RawRow'". This is a
            // malformed / unrecognised CSV, not an engine fault: degrade to an empty
            // order rather than leaking a third-party exception with an internal type
            // name. The upstream ingestion layer surfaces an empty/no-line order as a
            // reviewable failure ("we couldn't read this file").
            return new ParsedOrder(null, null, null, null, Array.Empty<ParsedOrderLine>());
        }

        // A ';'-delimited file is the reliable signal of a European locale (comma is
        // the decimal separator there), so numbers are parsed accordingly.
        return BuildParsedOrder(rows, european: delimiter == ";");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string DetectDelimiter(Stream stream)
    {
        using var peekReader = new StreamReader(stream, leaveOpen: true);
        var firstLine = peekReader.ReadLine() ?? string.Empty;
        // Tab-delimited (TSV): a tab in the header with no comma is the reliable signal.
        // Sniffed before ';' so a tab file is never mistaken for a single-column CSV.
        if (firstLine.Contains('\t') && !firstLine.Contains(',')) return "\t";
        // If the header row contains ';' and no ',' then the file is semicolon-delimited
        // (and, being a European convention, the comma is the decimal separator there).
        return firstLine.Contains(';') && !firstLine.Contains(',') ? ";" : ",";
    }

    private static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;

        return new string(header
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static ParsedOrder BuildParsedOrder(List<RawRow> rows, bool european)
    {
        // Header-level fields come from the first non-null value across all rows
        var poNumber   = rows.Select(r => r.PoNumber  ).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var buyerName  = rows.Select(r => r.BuyerName ).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var currency   = rows.Select(r => r.Currency  ).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var orderDate  = ParseDate(rows.Select(r => r.OrderDate).FirstOrDefault(v => !string.IsNullOrEmpty(v)));

        int autoLineNum = 1;
        var lines = new List<ParsedOrderLine>(rows.Count);

        foreach (var raw in rows)
        {
            var lineNumber  = int.TryParse(raw.LineNumber, out var ln) ? ln : autoLineNum;
            var (qtyVal,   qtyAmbiguous)   = ParseDecimalFlexible(raw.Quantity,  european);
            var (priceVal, priceAmbiguous) = ParseDecimalFlexible(raw.UnitPrice, european);
            var buyerCode   = NullIfEmpty(raw.BuyerItemCode ?? raw.ItemCode) ?? string.Empty;

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: buyerCode,
                Description:   NullIfEmpty(raw.Description),
                Quantity:      qtyVal ?? 0m,
                Unit:          NullIfEmpty(raw.Unit),
                UnitPrice:     priceVal,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the
                // parser could not unambiguously read (e.g. scientific notation "1.5e2",
                // letters) flags the line so it surfaces for human review.
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous)
            ));

            autoLineNum++;
        }

        return new ParsedOrder(poNumber, orderDate, buyerName, currency, lines);
    }

    /// <summary>
    /// Short "why was this flagged" string for the review UI. Null when nothing was
    /// ambiguous (the line is not parser-flagged).
    /// </summary>
    private static string? BuildAmbiguityReason(bool qtyAmbiguous, bool priceAmbiguous) =>
        (qtyAmbiguous, priceAmbiguous) switch
        {
            (true,  true)  => "The quantity and unit price could not be read unambiguously from the source file.",
            (true,  false) => "The quantity could not be read unambiguously from the source file.",
            (false, true)  => "The unit price could not be read unambiguously from the source file.",
            _              => null,
        };

    /// <summary>
    /// Parse a decimal that may be US ("1,234.56", "73.22") or European
    /// ("1.234,56", "73,22", "1.000") notation. Rules:
    ///  • both separators present → the LAST one is the decimal separator;
    ///  • only ',' → decimal, UNLESS it's a single comma with exactly 3 trailing
    ///    digits and the file is NOT European (then it's a US thousands group);
    ///  • only '.' → decimal, UNLESS the file IS European AND it's a single dot
    ///    with exactly 3 trailing digits (then it's a European thousands group).
    /// `european` is inferred from a ';' delimiter. This prevents the silent
    /// 10×/100× corruption where "73,22" was read as 7322 under InvariantCulture.
    ///
    /// Returns <c>(value, ambiguous)</c>. <c>ambiguous</c> is true when the token
    /// could NOT be read unambiguously and the parser refuses to guess — the caller
    /// flags the line for review instead of emitting a silently-wrong number. A blank
    /// token is NOT ambiguous (it is a legitimately empty optional value → null).
    /// </summary>
    private static (decimal? Value, bool Ambiguous) ParseDecimalFlexible(string? raw, bool european)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, false);

        // Guard against the silent-wrong-value class: the digit/separator filter below
        // strips EVERY non-numeric character, so a stray letter would be deleted and the
        // remaining digits concatenated (e.g. "1.5e2" → "1.52" — a plausible, catastrophic
        // mis-price). Only genuine numeric noise may be silently stripped: whitespace
        // (incl. NBSP/thin-space thousands separators) and currency symbols. ANY other
        // character (letters such as an 'e' exponent, '%', etc.) means the token is
        // ambiguous → refuse it and let the line go to review.
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '+') continue;
            if (char.IsWhiteSpace(c)) continue;
            if (char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol) continue;
            return (null, true);
        }

        var s = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0 || s == "-") return (null, false);

        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        char? decimalSep;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSep = lastComma > lastDot ? ',' : '.';                 // both → last wins
        }
        else if (lastComma >= 0)
        {
            bool single = s.IndexOf(',') == lastComma;
            int trailing = s.Length - lastComma - 1;
            decimalSep = (european || !(single && trailing == 3)) ? ',' : null;
        }
        else if (lastDot >= 0)
        {
            bool single = s.IndexOf('.') == lastDot;
            int trailing = s.Length - lastDot - 1;
            decimalSep = (european && single && trailing == 3) ? null : '.';
        }
        else
        {
            decimalSep = null;                                            // pure integer
        }

        string normalized = decimalSep is char ds
            ? s.Replace(ds == '.' ? "," : ".", "").Replace(ds, '.')      // strip groups, decimal → '.'
            : s.Replace(",", "").Replace(".", "");                       // integer / thousands-only

        // The token contained only numeric characters but still didn't parse (e.g. "1-2-3",
        // "--5", or a lone separator) — treat as ambiguous so it surfaces for review rather
        // than being silently dropped to null.
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (d, false) : (null, true);
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy", "d.M.yyyy" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;
        return null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Internal CSV row model ─────────────────────────────────────────────

    private sealed class RawRow
    {
        public string? PoNumber      { get; set; }
        public string? OrderDate     { get; set; }
        public string? Currency      { get; set; }
        public string? BuyerName     { get; set; }
        public string? LineNumber    { get; set; }
        // Two separate properties so ClassMap can assign via different column names
        public string? BuyerItemCode { get; set; }
        public string? ItemCode      { get; set; }
        public string? Description   { get; set; }
        public string? Quantity      { get; set; }
        public string? Unit          { get; set; }
        public string? UnitPrice     { get; set; }
    }

    private sealed class RawRowMap : ClassMap<RawRow>
    {
        public RawRowMap()
        {
            Map(m => m.PoNumber     ).Name("ponumber", "purchaseordernumber", "ordernumber", "po");
            Map(m => m.OrderDate    ).Name("orderdate");
            Map(m => m.Currency     ).Name("currency");
            Map(m => m.BuyerName    ).Name("buyername");
            Map(m => m.LineNumber   ).Name("linenumber", "lineno", "line");
            Map(m => m.BuyerItemCode).Name("buyeritemcode", "buyercode", "sku");
            Map(m => m.ItemCode     ).Name("itemcode", "item");
            Map(m => m.Description  ).Name("description");
            Map(m => m.Quantity     ).Name("quantity", "qty");
            Map(m => m.Unit         ).Name("unit");
            // "price" is an alias for "unitprice"
            Map(m => m.UnitPrice    ).Name("unitprice", "price");
        }
    }
}
