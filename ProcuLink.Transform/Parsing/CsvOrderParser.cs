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
        await foreach (var row in csv.GetRecordsAsync<RawRow>(ct))
            rows.Add(row);

        return BuildParsedOrder(rows);
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

    private static ParsedOrder BuildParsedOrder(List<RawRow> rows)
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
            var quantity    = decimal.TryParse(raw.Quantity,  NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)   ? qty   : 0m;
            var unitPrice   = decimal.TryParse(raw.UnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : (decimal?)null;
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
