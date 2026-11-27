using System.Xml;
using QuestPDF.Drawing.Exceptions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

public sealed record DocumentRender(TransformResult? Result, string? Error, string? Detail, IReadOnlyList<LookupMiss>? Misses = null)
{
    public bool Ok => Error is null;
    /// <summary>On a standard's needs refusal: the meanings it lacks (<see cref="FieldMeanings"/> keys).</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];
}

public static class DocumentOutputRenderer
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Render a read through <paramref name="template"/> as csv, json, xml, xlsx, pdf, txt or fixed, with <paramref name="lookups"/> in scope for its Lookup steps. <paramref name="layout"/> frames pdf and txt; <paramref name="logo"/> is for pdf.</summary>
    /// <param name="tables">The flow's tables: an XML table element still named by its key takes the flow's name for it, the same in every file.</param>
    public static DocumentRender Render(string format, OutputNodeTemplate template, ReadResult read, string? tableKey, LookupTables? lookups = null, OutputLayout? layout = null, byte[]? logo = null,
        IReadOnlyList<FlowTable>? tables = null)
    {
        var rows = DocumentRows.Build(read, tableKey, DecimalMark(format, template));
        // A flat grid has nothing else to put the header values on: a read with no table rows is one row.
        var lines = rows.Lines.Count == 0 && FlowOutputFormats.TemplateFormat(format) == OutputFormat.Csv
            ? [rows.Header]
            : rows.Lines;
        if (format == FlowOutputFormats.Xml && tables is { Count: > 0 }) template = template with { Root = TableNamed(template.Root, tables) };
        var emitter = new OutputTemplateEmitter();
        lookups ??= LookupTables.Empty;
        using var scope = LookupScope.Use(lookups);
        try
        {
            if (format is not (FlowOutputFormats.Xlsx or FlowOutputFormats.Pdf or FlowOutputFormats.Text or FlowOutputFormats.Fixed))
                return new(emitter.EmitRows(template, rows.Header, lines), null, null, lookups.Misses);

            var grid = emitter.GridRows(template, rows.Header, lines);
            var (bytes, contentType, extension) = format switch
            {
                FlowOutputFormats.Text => (LayoutTextWriter.Write(DocumentLayout.From(template, grid, rows.Lines.Count > 0), layout), "text/plain", ".txt"),
                FlowOutputFormats.Pdf => (LayoutPdfWriter.Write(DocumentLayout.From(template, grid, rows.Lines.Count > 0), layout, logo, DocumentLanguage.Of(read)), "application/pdf", ".pdf"),
                FlowOutputFormats.Fixed => (FixedWidthWriter.Write(template, grid), "text/plain", ".txt"),
                _ => (XlsxGridWriter.Write(grid), XlsxContentType, ".xlsx"),
            };
            return new(new TransformResult(new MemoryStream(bytes, writable: false), contentType, extension), null, null, lookups.Misses);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TransformValidationException)
        {
            return new(null, "output_invalid", Plain(ex));
        }
        catch (Exception ex) when (ex is XmlException or DocumentLayoutException or DocumentComposeException)
        {
            // The XML writer's and QuestPDF's messages are not approved copy: a plain sentence instead.
            return new(null, "output_invalid", $"We couldn't write the {format.ToUpperInvariant()} file from these values.");
        }
    }

    /// <summary>
    /// How a file writes dates and amounts: null keeps them as printed (pdf and txt show the document; a fixed-width
    /// receiver sized its columns to them). A grid that splits on ';', the European Excel separator, takes the decimal comma
    /// that goes with it, unless a step, expression or condition reads the numbers, which reads them the invariant way.
    /// </summary>
    private static char? DecimalMark(string format, OutputNodeTemplate template) => format switch
    {
        FlowOutputFormats.Pdf or FlowOutputFormats.Text or FlowOutputFormats.Fixed => null,
        _ when FlowOutputFormats.TemplateFormat(format) == OutputFormat.Csv && template.CsvDialect?.Delimiter == ";" && !Computes(template.Root) => ',',
        _ => '.',
    };

    /// <summary>A table element still named by its internal key ("invoice_lines") takes the flow's name for the table ("Arve read"),
    /// as its columns do; the XML writer makes it a valid tag. A name the user gave it, or a table with no name, stays.</summary>
    private static OutputNode TableNamed(OutputNode n, IReadOnlyList<FlowTable> tables) => n with
    {
        Name = n.NodeType == OutputNodeType.Array && n.Name == n.Collection
               && tables.FirstOrDefault(t => t.Key == n.Collection)?.Label is { Length: > 0 } label ? label : n.Name,
        Children = n.Children.Select(c => TableNamed(c, tables)).ToList(),
    };

    private static bool Computes(OutputNode n) =>
        !string.IsNullOrWhiteSpace(n.IncludeWhen)
        || (n.Rule is { } r && (!string.IsNullOrWhiteSpace(r.Expression) || r.FieldManipulators.Any(m => m.Type != "Lookup")))
        || n.Children.Any(Computes);

    /// <summary>The message without .NET's " (Parameter 'x')" suffix, which is not a sentence for a person.</summary>
    private static string Plain(Exception ex) =>
        ex is ArgumentException { ParamName: { } p } && ex.Message.EndsWith($" (Parameter '{p}')")
            ? ex.Message[..^$" (Parameter '{p}')".Length] : ex.Message;
}
