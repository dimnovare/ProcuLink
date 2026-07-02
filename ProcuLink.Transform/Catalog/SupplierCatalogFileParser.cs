using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Parsing;

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
public static partial class SupplierCatalogFileParser
{
    /// <summary>
    /// Maximum accepted DATA rows (excluding the header) per catalog file. Raised
    /// 50k → 200k (plan 2026-07-02 P0.8) to admit real distributor feeds: REDACTED-NAME
    /// ships 72,349 lines and 100MEGA 33,633; 200k covers every measured feed with headroom.
    /// </summary>
    public const int MaxCatalogRows = 200_000;

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
            // CIF 3.0 spaced field names (plan 2026-07-02 D2/3.4). CIF ships FIELDNAMES like
            // "Supplier Part ID" / "Item Description" — alias them so the standard mapping picks
            // them up with zero per-source config.
            ["supplier part id"] = "code", ["item description"] = "name",
            ["unit price"] = "price", ["unit of measure"] = "unit",
            ["manufacturer part id"] = "external_id",
        };

    /// <summary>Canonical catalog fields a per-source mapping may target.</summary>
    public static readonly IReadOnlyCollection<string> CanonicalFields =
        new[] { "code", "name", "unit", "price", "currency", "barcode", "external_id" };

    /// <summary>
    /// Routes on the file extension exactly like the original upload endpoint:
    /// <c>.xlsx</c>/<c>.xls</c> → XLSX, <c>.json</c> → JSON, <c>.csv</c> → CSV, anything else
    /// (including a missing name) falls back to CSV parsing.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseByFileNameAsync(
        Stream stream, string? fileName, CancellationToken ct,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty)?.ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xls" => ParseXlsx(stream, overrides),
            ".json"           => await ParseJsonAsync(stream, ct, overrides),
            ".xml"            => await ParseXmlAsync(stream, ct, overrides),
            ".cif"            => await ParseCifAsync(stream, ct, overrides),
            ".csv"            => await ParseCsvAsync(stream, ct, overrides, null),
            _                 => await ParseAutoAsync(stream, contentType: null, fileName, ct, overrides),
        };
    }

    /// <summary>
    /// Routes on a content-type / declared format hint (http catalog pull): a hint containing
    /// <c>json</c> → JSON, <c>spreadsheet</c>/<c>xlsx</c>/<c>excel</c> → XLSX, otherwise CSV.
    /// Used when the HTTP source declares <c>auto</c> and a Content-Type is available; the
    /// caller falls back to <see cref="ParseByFileNameAsync"/> (extension routing) when no
    /// content-type is present.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseByContentTypeAsync(
        Stream stream, string? contentType, string? fileName, CancellationToken ct,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var ct0 = contentType?.ToLowerInvariant() ?? string.Empty;
        if (ct0.Contains("json"))
            return await ParseJsonAsync(stream, ct, overrides);
        if (ct0.Contains("spreadsheet") || ct0.Contains("xlsx") || ct0.Contains("excel"))
            return ParseXlsx(stream, overrides);
        if (ct0.Contains("csv"))
            return await ParseCsvAsync(stream, ct, overrides, null);
        if (ct0.Contains("xml"))
            return await ParseXmlAsync(stream, ct, overrides);
        // No decisive content-type — content sniff (then extension routing, then CSV).
        return await ParseAutoAsync(stream, contentType, fileName, ct, overrides);
    }

    /// <summary>
    /// Content-sniffing router for 'auto' (plan 2026-07-2 4.2). Inspects the leading bytes AFTER
    /// any zip unwrap: <c>CIF_I_V</c> → CIF; a leading <c>&lt;</c> → XML (cXML Index by LocalName
    /// → dedicated parser, else generic repeating-element XML); <c>[</c>/<c>{</c> → JSON; anything
    /// else → extension routing then CSV. The saint-gobain case (a CIF file named <c>.xml</c>) is
    /// why we sniff CONTENT, not the extension.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseAutoAsync(
        Stream stream, string? contentType, string? fileName, CancellationToken ct,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        // Buffer so we can peek without consuming (network streams are forward-only).
        Stream seekable = stream;
        if (!stream.CanSeek)
        {
            var buf = new MemoryStream();
            await stream.CopyToAsync(buf, ct);
            buf.Position = 0;
            seekable = buf;
        }

        var peek = new byte[64];
        var read = await seekable.ReadAsync(peek.AsMemory(0, peek.Length), ct);
        seekable.Position = 0;

        // Skip a UTF-8 BOM + leading whitespace when classifying the first meaningful char.
        var start = 0;
        if (read >= 3 && peek[0] == 0xEF && peek[1] == 0xBB && peek[2] == 0xBF) start = 3;
        while (start < read && (peek[start] == (byte)' ' || peek[start] == (byte)'\t'
               || peek[start] == (byte)'\r' || peek[start] == (byte)'\n')) start++;

        var headText = System.Text.Encoding.ASCII.GetString(peek, 0, read);
        if (headText.Contains("CIF_I_V", StringComparison.OrdinalIgnoreCase))
            return await ParseCifAsync(seekable, ct, overrides);

        if (start < read)
        {
            var c = (char)peek[start];
            if (c == '<') return await ParseXmlAsync(seekable, ct, overrides);
            if (c == '[' || c == '{') return await ParseJsonAsync(seekable, ct, overrides);
        }

        // Not decisively CIF/XML/JSON → extension routing (xlsx/csv), then CSV fallback.
        var ext = Path.GetExtension(fileName ?? string.Empty)?.ToLowerInvariant();
        if (ext is ".xlsx" or ".xls") return ParseXlsx(seekable, overrides);
        return await ParseCsvAsync(seekable, ct, overrides, null);
    }

    private static SupplierProduct? RowToDraft(IReadOnlyDictionary<string, string?> fields)
    {
        var code = Pick(fields, "code");
        if (string.IsNullOrWhiteSpace(code)) return null;

        var price = ParseCatalogDecimal(Pick(fields, "price"));

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

    /// <summary>
    /// Locale-tolerant decimal parse for catalog prices (plan 2026-07-02, locale-bug memory).
    /// Real EU distributor feeds ship comma decimals (REDACTED-PARTY <c>674,68</c>); invariant
    /// <see cref="System.Globalization.NumberStyles.Any"/> would read that as <c>67468</c>
    /// (comma = thousands). Heuristic: the LAST of <c>. ,</c> is the decimal separator, the
    /// other is grouping — so <c>1.234,56</c>→1234.56, <c>1,234.56</c>→1234.56,
    /// <c>674,68</c>→674.68, <c>9,99</c>→9.99, <c>0.04</c>→0.04. A single separator with ≤2
    /// trailing digits is treated as a decimal (money), otherwise as grouping.
    /// </summary>
    public static decimal? ParseCatalogDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');

        char? decimalSep;
        if (lastDot >= 0 && lastComma >= 0)
            decimalSep = lastDot > lastComma ? '.' : ','; // both present → the later one is the decimal
        else if (lastComma >= 0)
        {
            // Only commas: a lone comma with 1-2 trailing digits is a decimal comma (674,68);
            // "1,234" (exactly 3 trailing, no other separators) is an ambiguous thousands group
            // — keep it as an integer to avoid inventing decimals.
            var after = s.Length - lastComma - 1;
            var single = s.IndexOf(',') == lastComma;
            decimalSep = single && after is >= 1 and <= 2 ? ',' : (char?)null;
            if (decimalSep is null && single) { /* "1,234" → strip as grouping below */ }
        }
        else if (lastDot >= 0)
        {
            var after = s.Length - lastDot - 1;
            var single = s.IndexOf('.') == lastDot;
            decimalSep = (single && after == 3) ? (char?)null : '.'; // "1.234" ambiguous → grouping
        }
        else decimalSep = null;

        // Normalize to invariant: drop the grouping separator, force '.' as the decimal point,
        // and strip any stray currency/space characters.
        var normalized = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsDigit(ch) || ch == '-' || ch == '+') normalized.Append(ch);
            else if ((ch == '.' || ch == ',') && decimalSep.HasValue && ch == decimalSep.Value) normalized.Append('.');
            // any other '.'/','/currency/space is grouping/noise → dropped
        }

        return decimal.TryParse(normalized.ToString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var p)
            ? p : (decimal?)null;
    }

    /// <summary>Sentinel keys in the per-source mapping that are directives, not column mappings.</summary>
    internal static bool IsMappingDirective(string key) =>
        key.StartsWith("__", StringComparison.Ordinal) && key.EndsWith("__", StringComparison.Ordinal);

    private static bool _codePagesRegistered;

    /// <summary>
    /// Resolves the per-source <c>__encoding__</c> sentinel (e.g. <c>windows-1252</c>) to an
    /// <see cref="System.Text.Encoding"/>, registering the CodePages provider on first use
    /// (legacy code pages are not in the default .NET Core provider). Returns null when no
    /// sentinel is set or the name is unknown (caller defaults to UTF-8).
    /// </summary>
    private static System.Text.Encoding? ResolveEncoding(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || !overrides.TryGetValue("__encoding__", out var name) || string.IsNullOrWhiteSpace(name))
            return null;
        try
        {
            if (!_codePagesRegistered)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                _codePagesRegistered = true;
            }
            return System.Text.Encoding.GetEncoding(name.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null; // unknown code page → default UTF-8
        }
    }

    /// <summary>
    /// Picks the delimiter for a CSV line by counting <c>; \t ,</c> OUTSIDE quoted fields and
    /// choosing the most frequent (tab &gt; semicolon &gt; comma on ties, favouring the less
    /// ambiguous separators). Counting outside quotes fixes the Also/Actebis case: a tab-delimited
    /// line whose data contains a stray comma no longer mis-detects as comma-delimited. A line with
    /// none of them → comma (a single-column file).
    /// </summary>
    internal static char DetectDelimiter(string line)
    {
        int semis = 0, tabs = 0, commas = 0;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (inQuotes) continue;
            else if (c == ';') semis++;
            else if (c == '\t') tabs++;
            else if (c == ',') commas++;
        }
        if (tabs >= semis && tabs >= commas && tabs > 0) return '\t';
        if (semis >= commas && semis > 0) return ';';
        return ',';
    }

    /// <summary>
    /// RFC-4180-ish delimited-line splitter (plan 2026-07-02 D6): quoted fields may contain the
    /// delimiter, newlines are already stripped by the line reader, and a doubled quote
    /// (<c>""</c>) inside a quoted field is an escaped quote. Used by the CSV path (bug fix:
    /// naive <c>line.Split(delimiter)</c> shredded quoted fields containing the delimiter) and
    /// the CIF path (mandatory — descriptions embed commas).
    /// </summary>
    public static List<string> SplitDelimitedLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } // escaped quote
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>
    /// Builds header-index → canonical-field map from a header row's cell names, honoring an
    /// optional per-source <paramref name="overrides"/> map (plan 2026-07-02 D3). The overrides
    /// map keys are matched against a column two ways so the SAME map serves headered AND
    /// headerless feeds:
    ///  • by header NAME (case-insensitive, trimmed) — for feeds with a header row;
    ///  • by 0-based column INDEX (a numeric key, e.g. <c>"3"</c>) — for the headerless feeds
    ///    (Ingram, Also) whose columns are positional. A numeric override key wins for that index.
    /// Only canonical targets (<see cref="CanonicalFields"/>) are honored; the first mapping to a
    /// given canonical field wins (so an explicit override still can't double-map).
    /// </summary>
    public static Dictionary<int, string> MapHeaderColumns(
        IReadOnlyList<string> header, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var map = new Dictionary<int, string>();

        // Positional overrides first (numeric keys) — they address indexes, valid with or without a header.
        if (overrides is not null)
        {
            foreach (var (key, target) in overrides)
            {
                if (!CanonicalFields.Contains(target)) continue;
                if (int.TryParse(key.Trim(), out var idx) && idx >= 0
                    && !map.ContainsKey(idx) && !map.ContainsValue(target))
                    map[idx] = target;
            }
        }

        for (var i = 0; i < header.Count; i++)
        {
            if (map.ContainsKey(i)) continue; // a positional override already claimed this index
            var name = header[i]?.Trim().Trim('"') ?? string.Empty;
            if (name.Length == 0) continue;

            // Name override wins over the global aliases.
            if (overrides is not null
                && overrides.TryGetValue(name, out var overrideTarget)
                && CanonicalFields.Contains(overrideTarget))
            {
                if (!map.ContainsValue(overrideTarget)) map[i] = overrideTarget;
                continue;
            }

            if (CatalogColumnAliases.TryGetValue(name, out var canonical) && !map.ContainsValue(canonical))
                map[i] = canonical;
        }
        return map;
    }

    public static Task<CatalogFileParseResult> ParseCsvAsync(Stream stream, CancellationToken ct)
        => ParseCsvAsync(stream, ct, overrides: null, encoding: null);

    /// <summary>
    /// CSV parser with an optional per-source column mapping and encoding (plan 2026-07-02).
    /// Quoted-field aware (<see cref="SplitDelimitedLine"/> — D6 bug fix). Headerless feeds:
    /// when <paramref name="overrides"/> carries the sentinel <c>__noheader__=true</c>, the
    /// first line is treated as DATA (positional mapping only) — for the Ingram/Also feeds that
    /// ship no header row. Otherwise the first line is the header (existing contract).
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseCsvAsync(
        Stream stream, CancellationToken ct,
        IReadOnlyDictionary<string, string>? overrides, System.Text.Encoding? encoding)
    {
        var drafts = new List<SupplierProduct>();

        // Per-source encoding hint (__encoding__ sentinel) — e.g. Also/Actebis ships cp1252.
        // Explicit `encoding` argument wins; otherwise resolve the sentinel; default UTF-8.
        var effectiveEncoding = encoding ?? ResolveEncoding(overrides) ?? System.Text.Encoding.UTF8;
        using var reader = new StreamReader(stream, effectiveEncoding,
                                            detectEncodingFromByteOrderMarks: true);

        var noHeader = overrides is not null
            && overrides.TryGetValue("__noheader__", out var nh)
            && string.Equals(nh, "true", StringComparison.OrdinalIgnoreCase);

        var firstLine = await reader.ReadLineAsync(ct);
        if (firstLine is null)
            return new CatalogFileParseResult(drafts, Array.Empty<string>(), "csv");

        var delimiter = DetectDelimiter(firstLine);

        List<string> header;
        var dataRows = 0;

        if (noHeader)
        {
            // Positional feed: synthesize an index header ("col0","col1"…) for the honesty report
            // and parse the first line as data.
            var firstParts = SplitDelimitedLine(firstLine, delimiter);
            header = Enumerable.Range(0, firstParts.Count).Select(i => "col" + i).ToList();
            var colMapNh = MapHeaderColumns(header, overrides);
            if (!colMapNh.ContainsValue("code"))
                return new CatalogFileParseResult(drafts, header, "csv");
            AddRow(firstParts, colMapNh, drafts);
            dataRows = 1;

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (++dataRows > MaxCatalogRows) throw new CatalogTooLargeException(RowCapMessage);
                AddRow(SplitDelimitedLine(line, delimiter), colMapNh, drafts);
            }
            return new CatalogFileParseResult(drafts, header, "csv");
        }

        header = SplitDelimitedLine(firstLine, delimiter).Select(h => h.Trim().Trim('"')).ToList();
        var colMap = MapHeaderColumns(header, overrides);
        if (!colMap.ContainsValue("code"))
            return new CatalogFileParseResult(drafts, header, "csv"); // no code column → nothing usable

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            // H4 row cap: abort instead of materializing an unbounded draft list.
            if (++dataRows > MaxCatalogRows)
                throw new CatalogTooLargeException(RowCapMessage);

            AddRow(SplitDelimitedLine(line, delimiter), colMap, drafts);
        }

        return new CatalogFileParseResult(drafts, header, "csv");

        static void AddRow(List<string> parts, Dictionary<int, string> map, List<SupplierProduct> sink)
        {
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (idx, canonical) in map)
                fields[canonical] = idx < parts.Count ? parts[idx].Trim().Trim('"') : null;
            var draft = RowToDraft(fields);
            if (draft is not null) sink.Add(draft);
        }
    }

    public static CatalogFileParseResult ParseXlsx(
        Stream stream, IReadOnlyDictionary<string, string>? overrides = null)
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

        try
        {
            return ParseXlsxCore(stream, overrides);
        }
        catch (Exception ex) when (XlsxCompressionFallback.ShouldAttemptRepack(ex))
        {
            // The workbook parts use a compression method the BCL can't read; even the
            // zip-bomb pre-guard fails on entry.Open(). Best-effort repack to a standard
            // Stored/Deflate zip, then re-run the FULL guard + parse on the repacked stream so
            // the zip-bomb protections still apply. If the file is not a repackable zip
            // (truly corrupt), surface the original failure unchanged. CatalogTooLargeException
            // is a distinct type and never reaches this filter, so size guards still throw.
            if (XlsxCompressionFallback.TryRepackToStandardZip(stream, out var repacked))
            {
                using (repacked)
                    return ParseXlsxCore(repacked, overrides);
            }
            throw;
        }
    }

    private static CatalogFileParseResult ParseXlsxCore(
        Stream stream, IReadOnlyDictionary<string, string>? overrides)
    {
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

        var colMap = MapHeaderColumns(header, overrides); // 0-based index → canonical
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

    // ── JSON catalog parser (http API pull, plan 2026-06-12 v2 — B4) ───────────

    /// <summary>
    /// Property names a catalog API commonly wraps its array of products under, when the
    /// response root is an object rather than a bare array. Case-insensitive.
    /// </summary>
    private static readonly string[] JsonArrayWrapperKeys =
        { "items", "products", "data", "results", "catalog", "rows", "records" };

    /// <summary>
    /// Parses a JSON catalog: a top-level array of objects, OR an object wrapping that array
    /// under a common key (<c>items</c>/<c>products</c>/<c>data</c>/…). Each object's property
    /// names are alias-detected to the same canonical catalog fields the CSV/XLSX paths use
    /// (code/name/unit/price/currency/barcode/external_id). The header column list reported is
    /// the union of property names seen across the (capped) rows, so the test-fetch honesty
    /// report can show mapped/unmapped fields exactly like the tabular formats.
    ///
    /// Hardening: the element count is bounded by <see cref="MaxCatalogRows"/> — iteration
    /// aborts with <see cref="CatalogTooLargeException"/> rather than materializing an unbounded
    /// draft list (mirrors the CSV/XLSX row cap). A non-array / non-object root, or a root with
    /// no recognizable array, yields an empty result (no code column → nothing usable), exactly
    /// like a CSV without a code column.
    /// </summary>
    public static async Task<CatalogFileParseResult> ParseJsonAsync(
        Stream stream, CancellationToken ct, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var drafts = new List<SupplierProduct>();

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            // Surface as the same parse-failure family the CSV/XLSX paths raise on bad input.
            throw new InvalidDataException("Catalog JSON could not be parsed.", ex);
        }

        using (doc)
        {
            if (!TryGetArray(doc.RootElement, out var array))
                return new CatalogFileParseResult(drafts, Array.Empty<string>(), "json");

            // Preserve first-seen order of property names across rows for an honest header report.
            var headerOrder = new List<string>();
            var headerSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dataRows = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                if (++dataRows > MaxCatalogRows)
                    throw new CatalogTooLargeException(RowCapMessage);

                var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in element.EnumerateObject())
                {
                    var name = prop.Name.Trim();
                    if (name.Length == 0) continue;
                    if (headerSeen.Add(name)) headerOrder.Add(name);

                    // Per-source override wins over the global aliases (REDACTED-PARTY enabler: {"Id":"code",…}).
                    string? canonical = null;
                    if (overrides is not null && overrides.TryGetValue(name, out var ov)
                        && CanonicalFields.Contains(ov))
                        canonical = ov;
                    else if (CatalogColumnAliases.TryGetValue(name, out var aliased))
                        canonical = aliased;

                    if (canonical is not null && !fields.ContainsKey(canonical)) // first match wins, like the tabular paths
                        fields[canonical] = JsonValueToString(prop.Value);
                }

                var draft = RowToDraft(fields);
                if (draft is not null) drafts.Add(draft);
            }

            return new CatalogFileParseResult(drafts, headerOrder, "json");
        }
    }

    /// <summary>
    /// Resolves the products array: the root itself when it is an array, otherwise the first
    /// recognized wrapper property whose value is an array.
    /// </summary>
    private static bool TryGetArray(JsonElement root, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in JsonArrayWrapperKeys)
                if (root.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                {
                    array = candidate;
                    return true;
                }

            // Case-insensitive second pass (TryGetProperty is case-sensitive).
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Array
                    && JsonArrayWrapperKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                {
                    array = prop.Value;
                    return true;
                }
        }

        array = default;
        return false;
    }

    /// <summary>
    /// Flattens a scalar JSON value to the string the shared <c>RowToDraft</c> expects.
    /// Numbers/booleans use their raw text; null/objects/arrays map to null (unmappable).
    /// </summary>
    private static string? JsonValueToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        _                    => null,
    };

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
