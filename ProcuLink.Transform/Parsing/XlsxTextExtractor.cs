using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Renders an .xlsx workbook into a deterministic, structure-preserving plain-text
/// representation suitable as the source text for the LLM structured-order extractor
/// (the same one the PDF text path uses). This is the XLSX analogue of
/// <see cref="PdfTextExtractor"/>: it does NOT interpret the data — it faithfully
/// flattens every non-empty cell so a real-world LABELLED / SECTIONED purchase-order
/// workbook (e.g. <c>PurchaseOrderNr → value</c> down the page, then a
/// <c>Lines LineNr / Lines ManufPN / Lines Quantity</c> section) survives into text
/// the LLM can read — which the row1=headers <see cref="XlsxOrderParser"/> cannot.
///
/// Output shape (one line per non-empty cell, grouped by worksheet, in row-major
/// order so a label and its value stay adjacent):
/// <code>
/// # Sheet: Order
/// A1: PurchaseOrderNr | 226131000790
/// A2: Currency | PLN
/// A4: Lines LineNr | Lines ManufPN | Lines Quantity
/// A5: 1 | ABC-123 | 10
/// </code>
/// Cells in the same spreadsheet row are joined with " | " so tabular sections read
/// as rows; numeric cells are emitted via their raw invariant value (never a
/// culture-formatted string), mirroring <see cref="XlsxOrderParser"/>'s locale-safety.
/// </summary>
public static class XlsxTextExtractor
{
    /// <summary>
    /// Hard cap on emitted characters so a pathological many-thousand-row workbook
    /// can't blow the downstream LLM token budget. The extractor truncates rather
    /// than fails; the deterministic parser remains the fallback for anything beyond.
    /// Comfortably above any real PO workbook.
    /// </summary>
    public const int MaxOutputChars = 60_000;

    /// <summary>
    /// Extracts a flattened text rendering of every worksheet's used range.
    /// Never returns null; returns an empty string for a workbook with no used cells.
    /// Throws only if the workbook cannot be opened at all (the caller treats that as
    /// "extraction unavailable" and falls back to the deterministic parser).
    /// </summary>
    public static string ExtractText(Stream fileStream)
    {
        using var workbook = XlsxCompressionFallback.OpenWorkbook(fileStream);

        var sb = new StringBuilder();

        foreach (var worksheet in workbook.Worksheets)
        {
            var usedRange = worksheet.RangeUsed();
            if (usedRange is null)
                continue;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append("# Sheet: ").Append(worksheet.Name).Append('\n');

            foreach (var row in usedRange.RowsUsed())
            {
                if (sb.Length >= MaxOutputChars)
                    return Truncate(sb);

                // Emit the cell address of the first populated cell in the row, then the
                // row's non-empty cells joined with " | " in column order. The address
                // anchor is cheap provenance the LLM can lean on; the join keeps a
                // label adjacent to its value on the same row.
                string? anchor = null;
                var cells = new List<string>();
                foreach (var cell in row.CellsUsed())
                {
                    anchor ??= cell.Address.ToStringRelative(includeSheet: false);
                    var text = FormatCell(cell);
                    if (!string.IsNullOrEmpty(text))
                        cells.Add(text);
                }

                if (cells.Count == 0)
                    continue;

                sb.Append(anchor).Append(": ").Append(string.Join(" | ", cells)).Append('\n');
            }
        }

        return sb.Length > MaxOutputChars ? Truncate(sb) : sb.ToString();
    }

    private static string Truncate(StringBuilder sb) =>
        sb.ToString(0, Math.Min(sb.Length, MaxOutputChars));

    /// <summary>
    /// Renders one cell to text. Numbers are read from the cell's underlying numeric
    /// value under InvariantCulture (no culture-dependent string round-trip — mirrors
    /// <see cref="XlsxOrderParser"/>'s locale-safety so a comma-decimal server can't
    /// silently 10×/100× a value before the LLM ever sees it). Dates render ISO; all
    /// other types fall back to the trimmed display string.
    /// </summary>
    private static string FormatCell(IXLCell cell)
    {
        switch (cell.DataType)
        {
            case XLDataType.Number when cell.TryGetValue<double>(out var d):
                return d.ToString("0.##########", CultureInfo.InvariantCulture);
            case XLDataType.DateTime when cell.TryGetValue<DateTime>(out var dt):
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case XLDataType.Boolean when cell.TryGetValue<bool>(out var b):
                return b ? "TRUE" : "FALSE";
            default:
                return cell.GetString().Trim();
        }
    }
}
