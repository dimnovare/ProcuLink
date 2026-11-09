using ProcuLink.Core.Services;

namespace ProcuLink.Core.Documents;

public static class FlowOutputFormats
{
    public const string Csv = "csv";
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Xlsx = "xlsx";
    public const string Pdf = "pdf";
    public const string Text = "txt";
    public const string Fixed = "fixed";

    public static readonly IReadOnlyList<string> All = [Csv, Json, Xml, Xlsx, Pdf, Text, Fixed];

    /// <summary>The template format an output renders through; xlsx, pdf, txt and fixed are all rendered from the CSV template's grid.</summary>
    public static OutputFormat TemplateFormat(string format) => format switch
    {
        Json => OutputFormat.Json,
        Xml => OutputFormat.Xml,
        _ => OutputFormat.Csv,
    };
}
