using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Api.Services.Documents;

/// <summary>The starting template of an output: every confirmed flow field and one table's columns, already bound.</summary>
public static class DefaultOutputTemplate
{
    public static OutputNodeTemplate For(FlowFieldList fields, string format, string? tableKey)
    {
        // json and xml name a field by its key; csv and xlsx head the column with its label.
        var byKey = format is FlowOutputFormats.Json or FlowOutputFormats.Xml;
        OutputNode Leaf(FlowField f)
        {
            var name = byKey ? f.Key : f.Label;
            return OutputNode.FieldOf(name, new OutputFieldRule
            {
                OutputPath = name, CanonicalField = f.Key,
                Width = format == FlowOutputFormats.Fixed ? Math.Clamp(name.Length, 40, 1000) : null,
            });
        }

        var children = fields.Fields.Select(Leaf).ToList();
        var table = tableKey is null ? fields.Tables.FirstOrDefault() : fields.Tables.FirstOrDefault(t => t.Key == tableKey);
        if (table is not null)
            children.Add(OutputNode.Arr(table.Key, OutputNode.Obj("row", table.Columns.Select(Leaf).ToArray()), table.Key));

        return new OutputNodeTemplate
        {
            Format = FlowOutputFormats.TemplateFormat(format),
            Root = OutputNode.Obj("document", children.ToArray()),
        };
    }
}
