using System.Globalization;
using System.Text;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// The value bags the engine hands <see cref="OutputTemplateEmitter.EmitRows"/>: one header bag, and
/// one bag per line that is the header overlaid by that line's own cells — the shape
/// <c>DocumentRows.Build</c> produces. Values are the strings the emitter tests always rendered
/// (<c>3m</c> is <c>"3"</c>, a date is <c>yyyy-MM-dd</c>).
/// </summary>
internal static class EmitterRows
{
    /// <summary>The header the emitter tests share: PO-1, EUR, 2026-06-15.</summary>
    public static Dictionary<string, string> Header(string poNumber = "PO-1") => new()
    {
        ["PoNumber"] = poNumber, ["Currency"] = "EUR", ["OrderDate"] = "2026-06-15",
    };

    public static Dictionary<string, string> Line(int number, string sku, decimal quantity, decimal unitPrice) => new()
    {
        ["LineNumber"]       = number.ToString(CultureInfo.InvariantCulture),
        ["SupplierItemCode"] = sku,
        ["Quantity"]         = quantity.ToString(CultureInfo.InvariantCulture),
        ["UnitPrice"]        = unitPrice.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>S-1 × 3 @ 10 and S-2 × 2 @ 5.</summary>
    public static Dictionary<string, string>[] TwoLines() => [Line(1, "S-1", 3m, 10m), Line(2, "S-2", 2m, 5m)];

    /// <summary>The PO-9001 header the rule-resolution tests share.</summary>
    public static Dictionary<string, string> Po9001Header() => new()
    {
        ["PoNumber"] = "PO-9001", ["BuyerName"] = "Acme Buyer Ltd", ["OrderDate"] = "2026-05-01", ["Currency"] = "EUR",
    };

    /// <summary>SUP-1 Widget × 3 @ 10 and SUP-2 Gadget × 2 @ 5.5.</summary>
    public static Dictionary<string, string>[] Po9001Lines() =>
    [
        new() { ["LineNumber"] = "1", ["BuyerItemCode"] = "B-1", ["SupplierItemCode"] = "SUP-1", ["Description"] = "Widget",
                ["Quantity"] = "3", ["Unit"] = "EA", ["UnitPrice"] = "10" },
        new() { ["LineNumber"] = "2", ["BuyerItemCode"] = "B-2", ["SupplierItemCode"] = "SUP-2", ["Description"] = "Gadget",
                ["Quantity"] = "2", ["Unit"] = "EA", ["UnitPrice"] = "5.5" },
    ];

    /// <summary>A flat {header, lines} rule set drawn as a CSV tree (header rules, then line rules) over the PO-9001 rows.</summary>
    public static TransformResult FlatCsv(OutputMappingConfig output) => FlatCsv(output, Po9001Header(), Po9001Lines());

    public static TransformResult FlatCsv(
        OutputMappingConfig output, IReadOnlyDictionary<string, string> header, params IReadOnlyDictionary<string, string>[] lines) =>
        Emit(new OutputNodeTemplate
        {
            Format = OutputFormat.Csv,
            Root = OutputNode.Obj("root",
            [
                .. output.Header.Values.Select(r => OutputNode.FieldOf(r.OutputPath, r)),
                OutputNode.Arr("lines", OutputNode.Obj("line", [.. output.Lines.Values.Select(r => OutputNode.FieldOf(r.OutputPath, r))])),
            ]),
        }, header, lines);

    public static TransformResult Emit(OutputNodeTemplate template) => Emit(template, Header(), TwoLines());

    public static TransformResult Emit(
        OutputNodeTemplate template, IReadOnlyDictionary<string, string> header, params IReadOnlyDictionary<string, string>[] lines) =>
        new OutputTemplateEmitter().EmitRows(template, header, lines.Select(cells =>
        {
            var bag = new Dictionary<string, string>(header);
            foreach (var (key, value) in cells) bag[key] = value;
            return (IReadOnlyDictionary<string, string>)bag;
        }).ToList());

    /// <summary>The document as text, read the way the tests always read it (a UTF-8 BOM is dropped).</summary>
    public static string Text(this TransformResult result)
    {
        result.Content.Position = 0;
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public static byte[] Bytes(this TransformResult result)
    {
        result.Content.Position = 0;
        using var ms = new MemoryStream();
        result.Content.CopyTo(ms);
        return ms.ToArray();
    }
}
