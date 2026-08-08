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

        var (orderDate, orderDateAmbiguous) = DateParsing.TryParseHeaderDate(orderDateStr);

        // Parse data rows
        var dataRows = rows.Skip(1).Where(r => !r.IsEmpty()).ToList();

        // Establish the decimal convention across the WHOLE SHEET before reading any cell.
        // Only TEXT cells need this — a typed numeric cell is a raw double with no separator
        // to misread — but a text cell reading "73,22" was previously parsed with
        // NumberStyles.Any under InvariantCulture and became 7322: a hundredfold error on a
        // purchase-order price, in the one parser that never set NeedsReview, so nothing
        // flagged it and no human ever saw it.
        //
        // A workbook declares no locale of its own (there is no delimiter to read a locale
        // off), so there are only two sources of truth: the column's own numbers, then the
        // whole sheet's. Where neither decides, the cell is flagged rather than guessed at.
        var qtyTokens   = dataRows.Select(r => GetTextNumericToken(r, headerMap, "Quantity")).ToList();
        var priceTokens = dataRows.Select(r => GetTextNumericToken(r, headerMap, "UnitPrice", "Price")).ToList();
        var document    = NumberParsing.InferDecimalConvention(qtyTokens.Concat(priceTokens));
        var qtyConvention = NumberParsing.FirstKnown(
            NumberParsing.InferDecimalConvention(qtyTokens), document);
        var priceConvention = NumberParsing.FirstKnown(
            NumberParsing.InferDecimalConvention(priceTokens), document);

        int autoLineNum = 1;
        var lines = new List<ParsedOrderLine>(dataRows.Count);

        foreach (var row in dataRows)
        {
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
            // routes text-typed cells through the shared locale-aware reader.
            var (qtyVal,   qtyAmbiguous)   = GetNumericColumnValue(row, headerMap, qtyConvention, "Quantity");
            var (priceVal, priceAmbiguous) = GetNumericColumnValue(row, headerMap, priceConvention, "UnitPrice", "Price");

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: buyerCode,
                Description:   description,
                Quantity:      qtyVal ?? 0m,
                Unit:          unit,
                UnitPrice:     priceVal,
                // Refuse to deliver a silently-wrong number. XLSX was the ONLY parser that
                // never set this, which is exactly why its hundredfold price error reached
                // suppliers unseen. Mirrors the CsvOrderParser/PdfOrderParser contract.
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous)
            ));

            autoLineNum++;
        }

        return Task.FromResult(new ParsedOrder(poNumber, orderDate, buyerName, currency, lines,
            // A text-typed date cell declares no ordering: "03/04/2026" is a genuine coin-flip.
            // (A date-TYPED cell never reaches here — ClosedXML hands those back already
            // resolved, so this only flags the string path that carries the defect.)
            NeedsReview:  orderDateAmbiguous,
            ReviewReason: DateParsing.BuildAmbiguityReason(orderDateAmbiguous, "order date", orderDateStr)));
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
    /// Reads a numeric column. When the underlying cell is a number (or a formula yielding
    /// one), its raw <see cref="double"/> value is taken directly — no locale-dependent
    /// string round-trip, and never ambiguous. Text-typed cells (numbers stored as text,
    /// e.g. "4.50" or "73,22") go through the shared locale-aware reader under the
    /// sheet-wide <paramref name="convention"/>.
    ///
    /// <para>Returns <c>(value, ambiguous)</c>. This used to parse text cells with
    /// <c>NumberStyles.Any</c> under InvariantCulture, which read "73,22" as 7322, and it
    /// returned a bare <c>decimal?</c> with no way to say "I could not read this" — so a
    /// garbage cell became quantity 0 and a European price became a hundredfold error, both
    /// completely silently.</para>
    /// </summary>
    private static (decimal? Value, bool Ambiguous) GetNumericColumnValue(
        IXLRangeRow row, Dictionary<string, int> headerMap, DecimalConvention convention, params string[] aliases)
    {
        foreach (var cell in ResolveCells(row, headerMap, aliases))
        {
            // Prefer the typed numeric value — this is the culture-safe path.
            if (cell.DataType == XLDataType.Number && cell.TryGetValue<double>(out var d))
                return ((decimal)d, false);

            var s = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(s))
                return NumberParsing.TryParseFlexibleDecimal(s, convention);
        }

        return (null, false);
    }

    /// <summary>
    /// The raw text of a numeric cell, used to infer the sheet's decimal convention. Only
    /// TEXT cells are returned: a typed numeric cell holds a raw double with no separator to
    /// interpret, so it is evidence of nothing and must not sway the inference.
    /// </summary>
    private static string? GetTextNumericToken(IXLRangeRow row, Dictionary<string, int> headerMap, params string[] aliases)
    {
        foreach (var cell in ResolveCells(row, headerMap, aliases))
        {
            if (cell.DataType == XLDataType.Number) return null;

            var s = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(s)) return s;
        }

        return null;
    }

    /// <summary>Non-empty cells for the first alias columns present in the header map.</summary>
    private static IEnumerable<IXLCell> ResolveCells(
        IXLRangeRow row, Dictionary<string, int> headerMap, string[] aliases)
    {
        var originCol = row.FirstCell().Address.ColumnNumber;

        foreach (var alias in aliases)
        {
            if (!headerMap.TryGetValue(alias, out var absCol)) continue;

            var relCol = absCol - originCol + 1;
            if (relCol < 1) continue;

            var cell = row.Cell(relCol);
            if (cell.IsEmpty()) continue;

            yield return cell;
        }
    }

}
