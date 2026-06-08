using System.Globalization;
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
        // Buffer the stream so we can peek for delimiter detection
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, ct);
        ms.Position = 0;

        var delimiter = DetectDelimiter(ms);
        ms.Position = 0;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter              = delimiter,
            HeaderValidated        = null!,
            MissingFieldFound      = null!,
            PrepareHeaderForMatch  = args => NormalizeHeader(args.Header)
        };

        using var reader = new StreamReader(ms);
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
        // If the header row contains ';' and no ',' then the file is semicolon-delimited
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
            var quantity    = ParseDecimalFlexible(raw.Quantity,  european) ?? 0m;
            var unitPrice   = ParseDecimalFlexible(raw.UnitPrice, european);
            var buyerCode   = NullIfEmpty(raw.BuyerItemCode ?? raw.ItemCode) ?? string.Empty;

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: buyerCode,
                Description:   NullIfEmpty(raw.Description),
                Quantity:      quantity,
                Unit:          NullIfEmpty(raw.Unit),
                UnitPrice:     unitPrice
            ));

            autoLineNum++;
        }

        return new ParsedOrder(poNumber, orderDate, buyerName, currency, lines);
    }

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
    /// </summary>
    private static decimal? ParseDecimalFlexible(string? raw, bool european)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0 || s == "-") return null;

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

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : (decimal?)null;
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
