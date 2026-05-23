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
        using var workbook   = new XLWorkbook(fileStream);
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
            var quantityStr  = GetColumnValue(row, headerMap, "Quantity");
            var unit         = GetColumnValue(row, headerMap, "Unit");
            var unitPriceStr = GetColumnValue(row, headerMap, "UnitPrice", "Price");

            var quantity  = decimal.TryParse(quantityStr,  NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)   ? qty   : 0m;
            var unitPrice = decimal.TryParse(unitPriceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : (decimal?)null;

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
