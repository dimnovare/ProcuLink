using System.IO.Compression;
using System.Text.RegularExpressions;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Catalog;

/// <summary>
/// Shared CSV/XLSX parser for supplier product catalogs. Extracted VERBATIM from the
/// private statics in <c>SuppliersController</c> (the browser upload import path) so the
/// API upload endpoint, the API-key push endpoint, and the Worker pull channel all parse
/// catalog files byte-for-byte identically.
///
/// Hardening added at extraction time (plan 2026-06-12, finding H4):
///  • <see cref="MaxCatalogRows"/> row cap (50,000 data rows) — both formats abort with
///    <see cref="CatalogTooLargeException"/> instead of materializing unbounded drafts.
///  • XLSX zip-bomb pre-guard: BEFORE <c>XLWorkbook</c> touches the stream, the file is
///    opened as a plain <see cref="ZipArchive"/> and rejected when any entry declares an
///    uncompressed size over 64 MB, the entries sum to over 128 MB, or a worksheet's
///    declared <c>&lt;dimension&gt;</c> spans more rows than the cap (a forged dimension
///    otherwise makes ClosedXML allocate the range during load).
///  • After workbook load, the used-range row count is checked again before any cell is
///    read, and rows are counted during iteration as a backstop.
/// </summary>
public static class SupplierCatalogFileParser
{
    /// <summary>Maximum accepted DATA rows (excluding the header) per catalog file.</summary>
    public const int MaxCatalogRows = 50_000;

    /// <summary>Culture-invariant user-facing row-cap message (used in errors + sync status).</summary>
    internal static readonly string RowCapMessage =
        $"Catalog file exceeds the {MaxCatalogRows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} row limit.";

    /// <summary>H4 — max declared uncompressed size for any single zip entry in an XLSX.</summary>
    internal const long MaxXlsxEntryBytes = 64L * 1024 * 1024;

    /// <summary>H4 — max declared uncompressed size summed over all zip entries in an XLSX.</summary>
    internal const long MaxXlsxTotalBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Case-insensitive aliases → canonical catalog field. Public so callers (e.g. the
    /// test-fetch honesty report) can compute mapped/unmapped columns without re-parsing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ColumnAliases => CatalogColumnAliases;

    /// <summary>Case-insensitive aliases → canonical catalog field.</summary>
    private static readonly Dictionary<string, string> CatalogColumnAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = "code", ["product_code"] = "code", ["productcode"] = "code",
            ["sku"] = "code", ["item_code"] = "code", ["itemcode"] = "code",
            ["supplier_code"] = "code", ["suppliercode"] = "code", ["supplier_item_code"] = "code",
            ["name"] = "name", ["product_name"] = "name", ["productname"] = "name",
            ["description"] = "name", ["product"] = "name", ["title"] = "name",
            ["unit"] = "unit", ["uom"] = "unit", ["unit_of_measure"] = "unit", ["unitname"] = "unit",
            ["price"] = "price", ["unit_price"] = "price", ["unitprice"] = "price", ["list_price"] = "price",
            ["currency"] = "currency", ["ccy"] = "currency",
            ["barcode"] = "barcode", ["gtin"] = "barcode", ["ean"] = "barcode", ["upc"] = "barcode", ["code2"] = "barcode",
            ["external_id"] = "external_id", ["externalid"] = "external_id", ["product_id"] = "external_id",
            ["productid"] = "external_id", ["erp_id"] = "external_id",
        };

    /// <summary>
    /// Routes on the file extension exactly like the original upload endpoint:
    /// <c>.xlsx</c>/<c>.xls</c> → XLSX, <c>.csv</c> → CSV, anything else (including a
    /// missing name) falls back to CSV parsing.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseByFileNameAsync(
        Stream stream, string? fileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty)?.ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xls" => ParseXlsx(stream),
            ".csv"            => await ParseCsvAsync(stream, ct),
            _                 => await ParseCsvAsync(stream, ct),
        };
    }

    private static SupplierProduct? RowToDraft(IReadOnlyDictionary<string, string?> fields)
    {
        var code = Pick(fields, "code");
        if (string.IsNullOrWhiteSpace(code)) return null;

        decimal? price = null;
        var priceStr = Pick(fields, "price");
        if (!string.IsNullOrWhiteSpace(priceStr)
            && decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var p))
            price = p;

        return new SupplierProduct
        {
            Code       = code!.Trim(),
            Name       = Pick(fields, "name"),
            Unit       = Pick(fields, "unit"),
            Price      = price,
            Currency   = Pick(fields, "currency"),
            Barcode    = Pick(fields, "barcode"),
            ExternalId = Pick(fields, "external_id"),
        };

        static string? Pick(IReadOnlyDictionary<string, string?> f, string key) =>
            f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    }

    /// <summary>Builds header-index → canonical-field map from a header row's cell names.</summary>
    public static Dictionary<int, string> MapHeaderColumns(IReadOnlyList<string> header)
    {
        var map = new Dictionary<int, string>();
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i]?.Trim().Trim('"') ?? string.Empty;
            if (name.Length == 0) continue;
            if (CatalogColumnAliases.TryGetValue(name, out var canonical) && !map.ContainsValue(canonical))
                map[i] = canonical;
        }
        return map;
    }

    public static async Task<CatalogFileParseResult> ParseCsvAsync(Stream stream, CancellationToken ct)
    {
        var drafts = new List<SupplierProduct>();
        using var reader = new StreamReader(stream);

        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null)
            return new CatalogFileParseResult(drafts, Array.Empty<string>(), "csv");

        var delimiter = headerLine.Contains(';') && !headerLine.Contains(',') ? ';'
                      : headerLine.Contains('\t') && !headerLine.Contains(',') ? '\t'
                      : ',';

        var header = headerLine.Split(delimiter).Select(h => h.Trim().Trim('"')).ToList();
        var colMap = MapHeaderColumns(header);
        if (!colMap.ContainsValue("code"))
            return new CatalogFileParseResult(drafts, header, "csv"); // no code column → nothing usable

        var dataRows = 0;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            // H4 row cap: abort instead of materializing an unbounded draft list.
            if (++dataRows > MaxCatalogRows)
                throw new CatalogTooLargeException(
                    RowCapMessage);

            var parts = line.Split(delimiter);
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (idx, canonical) in colMap)
                fields[canonical] = idx < parts.Length ? parts[idx].Trim().Trim('"') : null;

            var draft = RowToDraft(fields);
            if (draft is not null) drafts.Add(draft);
        }

        return new CatalogFileParseResult(drafts, header, "csv");
    }

    public static CatalogFileParseResult ParseXlsx(Stream stream)
    {
        // Both the zip pre-guard and ClosedXML need a seekable stream; buffer when it is not
        // (e.g. a raw network stream). The browser-upload form-file streams are seekable, so
        // the original behaviour there is untouched.
        if (!stream.CanSeek)
        {
            var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            stream = buffered;
        }

        // ── H4 zip-bomb pre-guard — runs BEFORE XLWorkbook touches the stream ────
        GuardXlsxZipArchive(stream);
        stream.Position = 0;

        var drafts = new List<SupplierProduct>();
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        var worksheet = workbook.Worksheets.FirstOrDefault();

        // H4 — reject by used-range row count BEFORE materializing/iterating cells.
        var lastRowUsed = worksheet?.LastRowUsed()?.RowNumber() ?? 0;
        if (lastRowUsed > MaxCatalogRows + 1) // +1 for the header row
            throw new CatalogTooLargeException(
                RowCapMessage);

        var rows = worksheet?.RangeUsed()?.RowsUsed().ToList();
        if (rows is null || rows.Count < 2)
            return new CatalogFileParseResult(drafts, Array.Empty<string>(), "xlsx");

        var headerCells = rows[0].Cells().ToList();
        // ColumnNumber (1-based) → canonical field.
        var header = new List<string>();
        var maxCol = headerCells.Count == 0 ? 0 : headerCells.Max(c => c.Address.ColumnNumber);
        for (var c = 1; c <= maxCol; c++) header.Add(string.Empty);
        foreach (var cell in headerCells)
            header[cell.Address.ColumnNumber - 1] = cell.GetString().Trim();

        var colMap = MapHeaderColumns(header); // 0-based index → canonical
        if (!colMap.ContainsValue("code"))
            return new CatalogFileParseResult(drafts, header, "xlsx");

        var dataRows = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.IsEmpty()) continue;

            // H4 backstop: count rows during iteration with early abort.
            if (++dataRows > MaxCatalogRows)
                throw new CatalogTooLargeException(
                    RowCapMessage);

            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (idx, canonical) in colMap)
                fields[canonical] = row.Cell(idx + 1).GetString().Trim();

            var draft = RowToDraft(fields);
            if (draft is not null) drafts.Add(draft);
        }

        return new CatalogFileParseResult(drafts, header, "xlsx");
    }

    // ── H4 — XLSX decompression-bomb guard ────────────────────────────────────

    /// <summary>Matches a worksheet's declared dimension, e.g. &lt;dimension ref="A1:G50001"/&gt;.</summary>
    private static readonly Regex DimensionRefRegex = new(
        "<dimension[^>]*\\sref=\"[A-Z]+\\d+:[A-Z]+(\\d+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Opens the (seekable) stream as a plain <see cref="ZipArchive"/> and rejects it when:
    ///  • any entry declares an uncompressed length over <see cref="MaxXlsxEntryBytes"/>,
    ///  • the declared lengths sum to over <see cref="MaxXlsxTotalBytes"/>, or
    ///  • a worksheet part declares a &lt;dimension&gt; spanning more rows than
    ///    <see cref="MaxCatalogRows"/> + 1 (forged-dimension attack — ClosedXML would
    ///    otherwise allocate the declared range while loading the workbook).
    /// A stream that is not a zip archive at all is left for <c>XLWorkbook</c> to reject
    /// with its own (caught upstream) error.
    /// </summary>
    private static void GuardXlsxZipArchive(Stream stream)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return; // not a zip — let XLWorkbook produce the ordinary "could not read" failure
        }

        using (archive)
        {
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > MaxXlsxEntryBytes)
                    throw new CatalogTooLargeException(
                        "Catalog file is not accepted: a worksheet part declares an uncompressed size over 64 MB.");

                total += entry.Length;
                if (total > MaxXlsxTotalBytes)
                    throw new CatalogTooLargeException(
                        "Catalog file is not accepted: the workbook declares more than 128 MB of uncompressed content.");

                if (entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                    && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    GuardWorksheetDimension(entry);
                }
            }
        }
    }

    /// <summary>
    /// Reads the leading bytes of a worksheet part and rejects a declared
    /// <c>&lt;dimension&gt;</c> whose last row exceeds the row cap. The dimension element
    /// sits at the top of the part, so 8 KB is more than enough; a part without a parsable
    /// dimension falls through to the post-load row-count check.
    /// </summary>
    private static void GuardWorksheetDimension(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        var buffer = new byte[8192];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = entryStream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        var head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        var match = DimensionRefRegex.Match(head);
        if (match.Success
            && long.TryParse(match.Groups[1].Value, out var lastRow)
            && lastRow > MaxCatalogRows + 1) // +1 for the header row
        {
            throw new CatalogTooLargeException(
                RowCapMessage);
        }
    }
}

/// <summary>
/// Result of parsing one catalog file: the product drafts plus the header columns and the
/// format that was actually parsed (consumed by the test-fetch honesty report).
/// </summary>
public sealed record CatalogFileParseResult(
    List<SupplierProduct> Drafts,
    IReadOnlyList<string> HeaderColumns,
    string Format);

/// <summary>
/// Thrown when a catalog file exceeds the row cap or trips the XLSX decompression-bomb
/// guard. The message is safe to show to users and to persist as a sync error.
/// </summary>
public sealed class CatalogTooLargeException : Exception
{
    public CatalogTooLargeException(string message) : base(message) { }
}
