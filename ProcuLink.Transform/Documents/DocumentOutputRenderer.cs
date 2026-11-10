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
}

public static class DocumentOutputRenderer
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Render a read through <paramref name="template"/> as csv, json, xml, xlsx, pdf, txt or fixed, with <paramref name="lookups"/> in scope for its Lookup steps. <paramref name="layout"/> frames pdf and txt; <paramref name="logo"/> is for pdf.</summary>
    public static DocumentRender Render(string format, OutputNodeTemplate template, ReadResult read, string? tableKey, LookupTables? lookups = null, OutputLayout? layout = null, byte[]? logo = null)
    {
        var rows = DocumentRows.Build(read, tableKey);
        // A flat grid has nothing else to put the header values on: a read with no table rows is one row.
        var lines = rows.Lines.Count == 0 && FlowOutputFormats.TemplateFormat(format) == OutputFormat.Csv
            ? [rows.Header]
            : rows.Lines;
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
                FlowOutputFormats.Pdf => (LayoutPdfWriter.Write(DocumentLayout.From(template, grid, rows.Lines.Count > 0), layout, logo), "application/pdf", ".pdf"),
                FlowOutputFormats.Fixed => (FixedWidthWriter.Write(template, grid), "text/plain", ".txt"),
                _ => (XlsxGridWriter.Write(grid), XlsxContentType, ".xlsx"),
            };
            return new(new TransformResult(new MemoryStream(bytes, writable: false), contentType, extension), null, null, lookups.Misses);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TransformValidationException or XmlException)
        {
            return new(null, "output_invalid", ex.Message);
        }
        catch (Exception ex) when (ex is DocumentLayoutException or DocumentComposeException)
        {
            // QuestPDF's message is not approved copy, so no detail.
            return new(null, "output_invalid", null);
        }
    }
}
