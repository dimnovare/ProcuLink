using System.Xml;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

public sealed record DocumentRender(TransformResult? Result, string? Error, string? Detail)
{
    public bool Ok => Error is null;
}

public static class DocumentOutputRenderer
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Render a read through <paramref name="template"/> as csv, json, xml or xlsx.</summary>
    public static DocumentRender Render(string format, OutputNodeTemplate template, ReadResult read, string? tableKey)
    {
        var rows = DocumentRows.Build(read, tableKey);
        // A flat grid has nothing else to put the header values on: a read with no table rows is one row.
        var lines = rows.Lines.Count == 0 && FlowOutputFormats.TemplateFormat(format) == OutputFormat.Csv
            ? [rows.Header]
            : rows.Lines;
        var emitter = new OutputTemplateEmitter();
        try
        {
            if (format != FlowOutputFormats.Xlsx)
                return new(emitter.EmitRows(template, rows.Header, lines), null, null);

            var bytes = XlsxGridWriter.Write(emitter.GridRows(template, rows.Header, lines));
            return new(new TransformResult(new MemoryStream(bytes, writable: false), XlsxContentType, ".xlsx"), null, null);
        }
        catch (Exception ex) when (ex is ArgumentException or TransformValidationException or XmlException)
        {
            return new(null, "output_invalid", ex.Message);
        }
    }
}
