using ProcuLink.Core.Services;

namespace ProcuLink.Core.Documents;

public static class FlowOutputFormats
{
    public const string Csv = "csv";
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Xlsx = "xlsx";

    public static readonly IReadOnlyList<string> All = [Csv, Json, Xml, Xlsx];

    /// <summary>The template format an output renders through; xlsx is rendered from the CSV template.</summary>
    public static OutputFormat TemplateFormat(string format) => format switch
    {
        Json => OutputFormat.Json,
        Xml => OutputFormat.Xml,
        _ => OutputFormat.Csv,
    };
}
