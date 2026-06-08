using System.Globalization;
using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;

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
    /// </summary>
    public TransformResult Build(PurchaseOrderEntity order, OrderMappingOverride @override, OutputFormat format)
    {
        ValidateOrder(order);

        var output = @override.Output
            ?? throw new ArgumentException("Override has no output mapping config.", nameof(@override));

        return format switch
        {
            OutputFormat.Csv  => BuildCsv(order, @override, output),
            OutputFormat.Json => BuildJson(order, @override, output),
            _ => throw new ArgumentException(
                     $"MappedTransformService does not support format '{format}' (v1: CSV + JSON only).",
                     nameof(format)),
        };
    }

    // ── CSV ────────────────────────────────────────────────────────────────────

    private static TransformResult BuildCsv(
        PurchaseOrderEntity order, OrderMappingOverride @override, OutputMappingConfig output)
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
        var headerRow    = BuildHeaderRow(order, @override);
        var headerValues = headerCols
            .Select(c => ResolveRule(c.Value, headerRow) ?? string.Empty)
            .ToList();

        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            var lineRow = BuildLineRow(order, @override, line);

            var lineValues = lineCols
                .Select(c => ResolveRule(c.Value, lineRow) ?? string.Empty)
                .ToList();

            sb.AppendLine(string.Join(",", headerValues.Concat(lineValues).Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new TransformResult(new MemoryStream(bytes), "text/csv", ".csv");
    }

    // ── JSON ───────────────────────────────────────────────────────────────────

    private static TransformResult BuildJson(
        PurchaseOrderEntity order, OrderMappingOverride @override, OutputMappingConfig output)
    {
        var headerRow = BuildHeaderRow(order, @override);

        var header = new Dictionary<string, string?>();
        foreach (var (_, rule) in output.Header)
            header[rule.OutputPath] = ResolveRule(rule, headerRow) ?? string.Empty;

        var lines = new List<Dictionary<string, string?>>();
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber))
        {
            var lineRow = BuildLineRow(order, @override, line);
            var obj = new Dictionary<string, string?>();
            foreach (var (_, rule) in output.Lines)
                obj[rule.OutputPath] = ResolveRule(rule, lineRow) ?? string.Empty;
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
    /// Resolves one rule: pick the source value (fixed value, else the named canonical/custom field
    /// from <paramref name="row"/>), then run the manipulator chain. Identical order of precedence
    /// and manipulator application as <c>PoMappingEngine.ResolveField</c>.
    /// </summary>
    private static string? ResolveRule(OutputFieldRule rule, IReadOnlyDictionary<string, string> row)
    {
        string? value = rule.FixedValue
            ?? (rule.CanonicalField is not null && row.TryGetValue(rule.CanonicalField, out var v) ? v : null);

        foreach (var m in rule.FieldManipulators ?? new List<ManipulatorEntry>())
        {
            var manipulator = ManipulatorRegistry.Resolve(m.Type, m.Params);
            value = manipulator.Apply(value, row);
        }

        return value;
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
