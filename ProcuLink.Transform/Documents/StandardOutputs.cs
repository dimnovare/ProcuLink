using System.Xml;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>A standard order file an output can be: its id (the FE catalog id), the writer that makes it, the stored format, and the meanings it needs.</summary>
public sealed record StandardOutput(string Id, OutputFormat Writer, string Format, IReadOnlyList<string> Needs);

/// <summary>The standard order files a flow output can be, filled by meaning and written by the existing writers. No EDIFACT: nothing writes it.</summary>
public static class StandardOutputs
{
    private static readonly IReadOnlyList<string> OrderNeeds =
    [
        FieldMeanings.DocumentNumber, FieldMeanings.IssueDate, FieldMeanings.Currency,
        FieldMeanings.LineQuantity, FieldMeanings.LineUnitPrice, FieldMeanings.LineItemCode,
    ];

    // UBL also needs both parties: its writer otherwise names them with placeholders.
    public static readonly IReadOnlyList<StandardOutput> All =
    [
        new("cxml-1-2", OutputFormat.CXml, FlowOutputFormats.Xml, OrderNeeds),
        new("ubl-2-1-order", OutputFormat.Ubl, FlowOutputFormats.Xml, [.. OrderNeeds, FieldMeanings.SenderName, FieldMeanings.ReceiverName]),
        new("x12-850", OutputFormat.X12, FlowOutputFormats.Text, OrderNeeds),
    ];

    public static StandardOutput? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// The standard file, or <c>output_invalid</c>: with the needs sentence when the read lacks a need, without a detail
    /// for an unknown id or anything a writer still refuses, an amount too large for its arithmetic included (its messages are not approved copy). Never throws for either.
    /// </summary>
    public static async Task<DocumentRender> RenderAsync(string id, ReadResult read, FlowFieldList fields, string? tableKey, Guid orgId, CancellationToken ct)
    {
        if (Find(id) is not { } standard) return new(null, "output_invalid", null);
        var (order, missing) = StandardOrderAdapter.ToOrder(read, fields, tableKey, standard.Needs, orgId);
        if (order is null)
        {
            var items = missing.Count == 1 ? missing[0] : $"{string.Join(", ", missing.SkipLast(1))} and {missing[^1]}";
            return new(null, "output_invalid", $"This standard needs {items}.");
        }
        try
        {
            var writer = OutputTransformRegistry.All.First(w => w.CanTransform(standard.Writer));
            return new(await writer.TransformAsync(order, standard.Writer, ct), null, null);
        }
        catch (Exception ex) when (ex is TransformValidationException or ArgumentException or InvalidOperationException or XmlException or OverflowException)
        {
            return new(null, "output_invalid", null);
        }
    }
}
