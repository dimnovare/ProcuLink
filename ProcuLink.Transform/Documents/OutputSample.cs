using System.Text;
using ClosedXML.Excel;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

public static class OutputSample
{
    public static string? FormatFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".csv" or ".tsv" => FlowOutputFormats.Csv,
        ".xlsx" => FlowOutputFormats.Xlsx,
        ".json" => FlowOutputFormats.Json,
        ".xml" => FlowOutputFormats.Xml,
        _ => null,
    };

    /// <summary>A draft template from a sample file, or null when it can't be read. Never throws.</summary>
    public static OutputNodeTemplate? Infer(string fileName, byte[] bytes)
    {
        try
        {
            var format = FormatFor(fileName);
            if (format is null) return null;
            if (format == FlowOutputFormats.Xlsx) return Flat(HeaderCsv(bytes), ',');
            var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            return format switch
            {
                FlowOutputFormats.Csv => Flat(text, Sniff(text)),
                FlowOutputFormats.Json => Cleared(OutputNodeTemplateInferrer.FromSample(text, OutputFormat.Json)),
                _ => Cleared(OutputNodeTemplateInferrer.FromSample(text, OutputFormat.Xml)),
            };
        }
        catch
        {
            return null;
        }
    }

    // The inferrer only reads the header row, so that is all an xlsx needs to hand it.
    private static string? HeaderCsv(byte[] bytes)
    {
        using var book = new XLWorkbook(new MemoryStream(bytes));
        var row = book.Worksheets.FirstOrDefault()?.FirstRowUsed();
        if (row is null) return null;
        var last = row.LastCellUsed().Address.ColumnNumber;
        return string.Join(",", Enumerable.Range(1, last).Select(i => "\"" + row.Cell(i).GetFormattedString().Replace("\"", "\"\"") + "\""));
    }

    private static char Sniff(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var best = ',';
        foreach (var d in new[] { ';', '\t' })
            if (line.Count(c => c == d) > line.Count(c => c == best)) best = d;
        return best;
    }

    private static OutputNodeTemplate? Flat(string? csv, char delimiter)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var t = OutputNodeTemplateInferrer.FromSample(csv, OutputFormat.Csv, delimiter);
        // Flat: the columns sit directly under the root, as header values until a name binds them to a table.
        var columns = t.Root.Children[0].Children[0].Children;
        t = t with { Root = OutputNode.Obj("document", columns.ToArray()) };
        return Cleared(delimiter == ',' ? t : t with { CsvDialect = new CsvDialect { Delimiter = delimiter.ToString() } });
    }

    // The inferrer pre-binds to ORDER fields, which mean nothing for a flow: keep only the output path.
    private static OutputNodeTemplate Cleared(OutputNodeTemplate t) => t with { Root = Clear(t.Root) };

    private static OutputNode Clear(OutputNode n) => n with
    {
        Rule = n.Rule is null ? null : new OutputFieldRule { OutputPath = n.Rule.OutputPath },
        Children = n.Children.Select(Clear).ToList(),
    };
}
