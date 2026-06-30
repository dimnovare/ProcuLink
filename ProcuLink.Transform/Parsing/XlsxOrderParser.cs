using System.Globalization;
using ClosedXML.Excel;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses XLSX purchase order files using ClosedXML.
/// Reads the first worksheet. Row 1 is treated as the header row.
/// Headers are matched case-insensitively after trimming whitespace.
/// Supported column aliases (same as CsvOrderParser):
///   buyer code  — BuyerItemCode, ItemCode
///   unit price  — UnitPrice, Price
/// </summary>
public sealed class XlsxOrderParser : IPurchaseOrderParser
{
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".xlsx", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        using var workbook   = XlsxCompressionFallback.OpenWorkbook(fileStream);
        var worksheet        = workbook.Worksheets.First();
        var rows             = worksheet.RangeUsed()?.RowsUsed().ToList();

        if (rows == null || rows.Count < 2)
            return Task.FromResult(new ParsedOrder(null, null, null, null, Array.Empty<ParsedOrderLine>()));

        // Build case-insensitive column index map from the header row
        var headerRow = rows[0];
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Cells())
        {
            var name = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(name))
                headerMap.TryAdd(name, cell.Address.ColumnNumber);
        }

        // Extract header-level fields from the first data row that has a value
        string? poNumber  = null;
        string? buyerName = null;
        string? currency  = null;
        string? orderDateStr = null;

        for (int i = 1; i < rows.Count && (poNumber == null || buyerName == null || currency == null || orderDateStr == null); i++)
        {
            poNumber     ??= GetColumnValue(rows[i], headerMap, "PoNumber");
            buyerName    ??= GetColumnValue(rows[i], headerMap, "BuyerName");
            currency     ??= GetColumnValue(rows[i], headerMap, "Currency");
            orderDateStr ??= GetColumnValue(rows[i], headerMap, "OrderDate");
        }

        var orderDate = ParseDate(orderDateStr);

        // Parse data rows
        int autoLineNum = 1;
        var lines = new List<ParsedOrderLine>(rows.Count - 1);

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];

            // Skip entirely blank rows
            if (row.IsEmpty()) continue;

            var lineNumStr   = GetColumnValue(row, headerMap, "LineNumber");
            var lineNumber   = int.TryParse(lineNumStr, out var ln) ? ln : autoLineNum;

            var buyerCode    = GetColumnValue(row, headerMap, "BuyerItemCode", "ItemCode") ?? string.Empty;
            var description  = GetColumnValue(row, headerMap, "Description");
            var unit         = GetColumnValue(row, headerMap, "Unit");

            // Numeric cells are read via the cell's underlying numeric value, NOT a
            // string round-trip: ClosedXML's GetString() formats numbers with the
            // CURRENT culture (e.g. "12,5" on an EU/comma-decimal server), which then
            // mis-parses under InvariantCulture ("12,5" → 125, a silent 10× error).
            // GetNumericColumnValue takes the raw double when the cell is numeric and
            // only falls back to invariant string parsing for text-typed cells.
            var quantity  = GetNumericColumnValue(row, headerMap, "Quantity") ?? 0m;
            var unitPrice = GetNumericColumnValue(row, headerMap, "UnitPrice", "Price");

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: buyerCode,
                Description:   description,
                Quantity:      quantity,
                Unit:          unit,
                UnitPrice:     unitPrice
            ));

            autoLineNum++;
        }

        return Task.FromResult(new ParsedOrder(poNumber, orderDate, buyerName, currency, lines));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the trimmed cell value for the first alias column found in the header map.
    /// Returns null if no alias exists or the cell is empty.
    /// </summary>
    private static string? GetColumnValue(IXLRangeRow row, Dictionary<string, int> headerMap, params string[] aliases)
    {
        var originCol = row.FirstCell().Address.ColumnNumber;

        foreach (var alias in aliases)
        {
            if (!headerMap.TryGetValue(alias, out var absCol)) continue;

            var relCol = absCol - originCol + 1;
            if (relCol < 1) continue;

            var value = row.Cell(relCol).GetString().Trim();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Reads a numeric column culture-invariantly. When the underlying cell is a
    /// number (or a formula yielding one), its raw <see cref="double"/> value is taken
    /// directly — no locale-dependent string round-trip. Falls back to invariant
    /// string parsing for text-typed cells (numbers stored as text, e.g. "4.50"),
    /// and returns null when the column is absent or the value cannot be parsed.
    /// </summary>
    private static decimal? GetNumericColumnValue(IXLRangeRow row, Dictionary<string, int> headerMap, params string[] aliases)
    {
        var originCol = row.FirstCell().Address.ColumnNumber;

        foreach (var alias in aliases)
        {
            if (!headerMap.TryGetValue(alias, out var absCol)) continue;

            var relCol = absCol - originCol + 1;
            if (relCol < 1) continue;

            var cell = row.Cell(relCol);
            if (cell.IsEmpty()) continue;

            // Prefer the typed numeric value — this is the culture-safe path.
            if (cell.DataType == XLDataType.Number && cell.TryGetValue<double>(out var d))
                return (decimal)d;

            // Text-typed cell: parse the trimmed string under InvariantCulture so
            // "4.50" is read as four-point-five regardless of server locale.
            var s = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(s)
                && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            // A non-empty, non-numeric cell (e.g. "five") — degrade to null here so
            // the caller's default (0 for qty) applies; never throw.
            if (!string.IsNullOrEmpty(s))
                return null;
        }

        return null;
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
}
