using System.Globalization;
using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Override-aware output builder (heart-piece-flex Phase 2). When an order carries a per-order
/// <see cref="OrderMappingOverride"/> with a usable <see cref="OutputMappingConfig"/>, this service
/// builds the CSV or JSON document FROM the override instead of the fixed columns. Each
/// <see cref="OutputFieldRule"/> resolves its value (a canonical field, a custom field, or a fixed
/// literal) and runs it through <c>ManipulatorRegistry</c> with the EXACT same semantics as
/// <c>PoMappingEngine.ResolveField</c> — no new templating engine.
///
/// v1 supports CSV + JSON only (the two flat formats). XML/UBL/cXML/X12 are intentionally out of
/// scope and route to the existing fixed transformers. The same NeedsReview / null-SupplierItemCode
/// validation guard the fixed transforms enforce is applied here, so an override can never deliver
/// an unresolved order.
///
/// This service is NOT registered as an <see cref="ITransformService"/>: it is invoked explicitly by
/// the override-aware branch in <c>OrderTransformService.TransformAsync</c>, which guards the
/// "override present AND format supported" condition. Default (no override) behaviour is unchanged.
/// </summary>
public sealed class MappedTransformService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>True only for the flat formats an override can drive in v1.</summary>
    public static bool SupportsOverride(OutputFormat format) =>
        format is OutputFormat.Csv or OutputFormat.Json;

    /// <summary>
    /// Build the override-driven output document. Throws <see cref="TransformValidationException"/>
    /// if any line is unresolved (same guard as the fixed transforms), and
    /// <see cref="ArgumentException"/> if asked for an unsupported format (the caller guards this).
    ///
    /// When <paramref name="sourceTokens"/> is non-null and the override carries a
    /// <see cref="OrderMappingOverride.SourceMap"/>, the SourceMap re-derive step runs BEFORE
    /// the output rules: effective canonical values are computed from the token list first, then
    /// the output rules read those effective values. Passing <c>null</c> or an empty list is
    /// equivalent to no SourceMap — the default (no remap) path is unchanged.
    /// </summary>
    public TransformResult Build(
        PurchaseOrderEntity        order,
        OrderMappingOverride       @override,
        OutputFormat               format,
        IReadOnlyList<SourceToken>? sourceTokens = null)
    {
        ValidateOrder(order);

        var output = @override.Output
            ?? throw new ArgumentException("Override has no output mapping config.", nameof(@override));

        // Materialise a non-null token list once (empty if none supplied).
        IReadOnlyList<SourceToken> tokens = sourceTokens ?? Array.Empty<SourceToken>();

        return format switch
        {
            OutputFormat.Csv  => BuildCsv(order, @override, output, tokens),
            OutputFormat.Json => BuildJson(order, @override, output, tokens),
            _ => throw new ArgumentException(
                     $"MappedTransformService does not support format '{format}' (v1: CSV + JSON only).",
                     nameof(format)),
        };
    }

    // ── CSV ────────────────────────────────────────────────────────────────────

    private static TransformResult BuildCsv(
        PurchaseOrderEntity        order,
        OrderMappingOverride       @override,
        OutputMappingConfig        output,
        IReadOnlyList<SourceToken> tokens)
    {
        // Stable column order: header rules first (emitted once as a leading section is awkward for a
        // flat CSV, so header values are repeated on every line as leading columns), then line columns.
        // Preserve the dictionary insertion order so the editor's ordering round-trips.
        var headerCols = output.Header.ToList();
        var lineCols   = output.Lines.ToList();

        var sb = new StringBuilder();

        // Header row: header-field output paths, then line-field output paths.
        var headerNames = headerCols.Select(c => c.Value.OutputPath)
            .Concat(lineCols.Select(c => c.Value.OutputPath));
        sb.AppendLine(string.Join(",", headerNames.Select(Escape)));

        // Resolve header values once (header scope).
        // SourceMap re-derive runs first (no-op when SourceMap is absent/empty).
        var headerRow    = SourceMapReDerive.ApplyToHeaderRow(BuildHeaderRow(order, @override), @override, tokens);
        var headerValues = headerCols
            .Select(c => ResolveRule(c.Value, headerRow, lineScope: false) ?? string.Empty)
            .ToList();

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            // SourceMap re-derive for each line row.
            var lineRow = SourceMapReDerive.ApplyToLineRow(BuildLineRow(order, @override, line), @override, tokens);

            var lineValues = lineCols
                .Select(c => ResolveRule(c.Value, lineRow, lineScope: true) ?? string.Empty)
                .ToList();

            sb.AppendLine(string.Join(",", headerValues.Concat(lineValues).Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new TransformResult(new MemoryStream(bytes), "text/csv", ".csv");
    }

    // ── JSON ───────────────────────────────────────────────────────────────────

    private static TransformResult BuildJson(
        PurchaseOrderEntity        order,
        OrderMappingOverride       @override,
        OutputMappingConfig        output,
        IReadOnlyList<SourceToken> tokens)
    {
        // SourceMap re-derive for header row.
        var headerRow = SourceMapReDerive.ApplyToHeaderRow(BuildHeaderRow(order, @override), @override, tokens);

        var header = new Dictionary<string, string?>();
        foreach (var (_, rule) in output.Header)
            header[rule.OutputPath] = ResolveRule(rule, headerRow, lineScope: false) ?? string.Empty;

        var lines = new List<Dictionary<string, string?>>();
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            // SourceMap re-derive for each line row.
            var lineRow = SourceMapReDerive.ApplyToLineRow(BuildLineRow(order, @override, line), @override, tokens);
            var obj = new Dictionary<string, string?>();
            foreach (var (_, rule) in output.Lines)
                obj[rule.OutputPath] = ResolveRule(rule, lineRow, lineScope: true) ?? string.Empty;
            lines.Add(obj);
        }

        var payload = new
        {
            header,
            lines,
            generatedAt = DateTime.UtcNow.ToString("O"),
        };

        var json  = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return new TransformResult(new MemoryStream(bytes), "application/json", ".json");
    }

    // ── Value resolution (mirrors PoMappingEngine.ResolveField semantics) ────────

    /// <summary>
    /// Resolves one rule. Precedence for the source value:
    /// <list type="number">
    ///   <item><b>Expression</b> (Scriban) when non-blank — evaluated with <c>order</c> (and, when
    ///         <paramref name="lineScope"/> is true, <c>line</c>) in scope. A compile/eval error is
    ///         non-fatal: it falls back to the canonical/fixed value so the transform never crashes.</item>
    ///   <item><b>FixedValue</b>.</item>
    ///   <item><b>CanonicalField</b> looked up in <paramref name="row"/>.</item>
    /// </list>
    /// The chosen value is then run through the manipulator chain — identical order and semantics to
    /// <c>PoMappingEngine.ResolveField</c>. The expression layer is purely additive: with no
    /// <c>Expression</c> set, behaviour is byte-for-byte identical to before.
    /// </summary>
    private static string? ResolveRule(
        OutputFieldRule rule, IReadOnlyDictionary<string, string> row, bool lineScope)
    {
        string? value = ResolveExpressionOrField(
            rule.Expression,
            fallbackFixedValue: rule.FixedValue,
            fallbackCanonicalField: rule.CanonicalField,
            row: row,
            lineScope: lineScope);

        foreach (var m in rule.FieldManipulators ?? new List<ManipulatorEntry>())
        {
            var manipulator = ManipulatorRegistry.Resolve(m.Type, m.Params);
            value = manipulator.Apply(value, row);
        }

        return value;
    }

    /// <summary>
    /// Shared value-selection helper used by both the output-rule path here and the SourceMap
    /// re-derive path (<see cref="SourceMapReDerive"/>). Expression wins when present and non-blank;
    /// otherwise the supplied fixed value, then the named field in <paramref name="row"/>. On an
    /// expression compile/eval failure, falls back to the fixed/field value (never throws).
    /// </summary>
    internal static string? ResolveExpressionOrField(
        string? expression,
        string? fallbackFixedValue,
        string? fallbackCanonicalField,
        IReadOnlyDictionary<string, string> row,
        bool lineScope)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            var result = lineScope
                ? ScribanFieldEvaluator.EvaluateLine(expression, row)
                : ScribanFieldEvaluator.EvaluateHeader(expression, row);

            if (result.Ok)
                return result.Value;
            // Expression failed — fall through to the field/fixed value so the transform survives.
        }

        return fallbackFixedValue
            ?? (fallbackCanonicalField is not null && row.TryGetValue(fallbackCanonicalField, out var v) ? v : null);
    }

    /// <summary>
    /// Header-scope field bag: the recognised canonical header fields plus any header-scoped custom
    /// fields. Keys match the canonical names accepted in <see cref="OutputFieldRule.CanonicalField"/>.
    /// Manipulators that read sibling columns (Concat/Fallback) see this same bag as their row.
    /// </summary>
    private static Dictionary<string, string> BuildHeaderRow(
        PurchaseOrderEntity order, OrderMappingOverride @override)
    {
        var row = new Dictionary<string, string>
        {
            ["PoNumber"]     = order.PoNumber ?? string.Empty,
            ["OrderDate"]    = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["BuyerName"]    = ExtractBuyerName(order),
            ["Currency"]     = order.Currency ?? string.Empty,
            ["SupplierName"] = order.Supplier?.Name ?? order.SupplierName ?? string.Empty,
        };

        foreach (var cf in @override.CustomFields)
        {
            if (!string.Equals(cf.Scope, "header", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(cf.Key)) continue;
            row[cf.Key] = cf.Value ?? string.Empty;
        }

        return row;
    }

    /// <summary>
    /// Line-scope field bag for one line: the recognised canonical line fields, plus header fields
    /// (so a line rule can reference order-level values), plus any line-scoped custom field's value
    /// for this line number. Header custom fields are included too.
    /// </summary>
    private static Dictionary<string, string> BuildLineRow(
        PurchaseOrderEntity order, OrderMappingOverride @override, PurchaseOrderLineEntity line)
    {
        // Start from the header bag so line rules can reference order-level fields.
        var row = BuildHeaderRow(order, @override);

        row["LineNumber"]       = line.LineNumber.ToString(CultureInfo.InvariantCulture);
        row["BuyerItemCode"]    = line.BuyerItemCode ?? string.Empty;
        row["SupplierItemCode"] = line.SupplierItemCode ?? string.Empty;
        row["Description"]      = line.Description ?? string.Empty;
        row["Quantity"]         = line.Quantity.ToString(CultureInfo.InvariantCulture);
        row["Unit"]             = line.Unit ?? string.Empty;
        row["UnitPrice"]        = line.UnitPrice.ToString(CultureInfo.InvariantCulture);
        row["LineTotal"]        = (line.Quantity * line.UnitPrice).ToString(CultureInfo.InvariantCulture);

        foreach (var cf in @override.CustomFields)
        {
            if (!string.Equals(cf.Scope, "line", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(cf.Key)) continue;
            var val = cf.LineValues is not null && cf.LineValues.TryGetValue(line.LineNumber, out var lv)
                ? lv
                : string.Empty;
            row[cf.Key] = val;
        }

        return row;
    }

    // ── Shared with the fixed transforms ─────────────────────────────────────────

    /// <summary>Same guard as <c>CsvTransformService</c> / <c>JsonTransformService</c>.</summary>
    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    private static string ExtractBuyerName(PurchaseOrderEntity order)
    {
        if (!string.IsNullOrEmpty(order.BuyerName)) return order.BuyerName;
        if (order.CanonicalJson is null) return string.Empty;
        try
        {
            if (order.CanonicalJson.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (order.CanonicalJson.RootElement.TryGetProperty("buyerName", out var el))
                    return el.GetString() ?? string.Empty;
                if (order.CanonicalJson.RootElement.TryGetProperty("BuyerName", out var el2))
                    return el2.GetString() ?? string.Empty;
            }
        }
        catch { /* malformed JSON — ignore */ }
        return string.Empty;
    }

    /// <summary>RFC 4180: wrap in double-quotes if the value contains comma, quote, or newline.</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
