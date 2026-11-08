using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Mapping;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class DocumentOutputRendererTests
{
    private static ReadField Field(string key, string value, string type = "text") =>
        new(key, key, value, type, null, 1, null, false, true, false);

    private static ReadCell Cell(string v) => new(v, 1, null, false, false);

    private static OutputNode Rule(string name, string field) =>
        OutputNode.FieldOf(name, new OutputFieldRule { OutputPath = name, CanonicalField = field });

    private static OutputNodeTemplate Csv(CsvDialect dialect, params OutputNode[] rootChildren) =>
        new() { Format = OutputFormat.Csv, CsvDialect = dialect, Root = OutputNode.Obj("root", rootChildren) };

    private static readonly CsvDialect Lf = new() { LineEnding = "\n" };

    private static string Text(DocumentRender r)
    {
        r.Ok.Should().BeTrue(r.Detail);
        using var reader = new StreamReader(r.Result!.Content);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Csv_renders_fields_then_one_line_per_table_row()
    {
        var read = new ReadResult("Invoice",
            [Field("invoice_no", "INV-7"), Field("sku", "HEADER-SKU")],
            [new ReadTable("items", "Items",
                [new ReadColumn("sku", "SKU", "text", null), new ReadColumn("qty", "Qty", "number", null)],
                [new ReadRow(new Dictionary<string, ReadCell> { ["sku"] = Cell("A1"), ["qty"] = Cell("2") }),
                 new ReadRow(new Dictionary<string, ReadCell> { ["sku"] = Cell("B2"), ["qty"] = Cell("5") }),
                 new ReadRow(new Dictionary<string, ReadCell> { ["sku"] = Cell(""), ["qty"] = Cell("7") })],
                true)]);
        var t = Csv(Lf, Rule("No", "invoice_no"),
            OutputNode.Arr("Lines", OutputNode.Obj("Line", Rule("SKU", "sku"), Rule("Qty", "qty"))));

        Text(DocumentOutputRenderer.Render("csv", t, read, "items"))
            .Should().Be("No,SKU,Qty\nINV-7,A1,2\nINV-7,B2,5\nINV-7,,7\n");

        // An emitter InvalidOperationException (unknown text encoding) is a refusal, not a crash.
        var bad = DocumentOutputRenderer.Render("csv", Csv(new CsvDialect { Encoding = "no-such-encoding" }, Rule("No", "invoice_no")), read, null);
        (bad.Ok, bad.Error).Should().Be((false, "output_invalid"));
    }

    [Fact]
    public void Values_go_out_exactly_as_read()
    {
        var read = new ReadResult("Invoice", [Field("total", "1.500", "number"), Field("tax", "1.234,50", "money")]);
        var t = Csv(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, Rule("Total", "total"), Rule("Tax", "tax"));

        // The comma is always quoted by the shared escaper (even with ';' as delimiter); the value itself is untouched.
        Text(DocumentOutputRenderer.Render("csv", t, read, null)).Should().Be("Total;Tax\n1.500;\"1.234,50\"\n");
    }

    [Fact]
    public void Formula_text_is_neutralised_in_csv_and_stored_as_text_in_xlsx()
    {
        var read = new ReadResult("Invoice", [Field("note", "=1+1")]);
        var t = Csv(Lf, Rule("Note", "note"));

        // Line 0 is the header; the data line is what must not start a formula.
        Text(DocumentOutputRenderer.Render("csv", t, read, null)).Split('\n')[1].Should().Be("'=1+1");

        var xlsx = DocumentOutputRenderer.Render("xlsx", t, read, null);
        xlsx.Ok.Should().BeTrue(xlsx.Detail);
        xlsx.Result!.FileExtension.Should().Be(".xlsx");
        var cell = new XLWorkbook(xlsx.Result.Content).Worksheet(1).Cell(2, 1);
        cell.HasFormula.Should().BeFalse();
        cell.GetString().Should().Be("=1+1");
    }

    [Fact]
    public void Lookup_SwapsTrimmedCaseInsensitive_KeepsOrBlanksMisses_AndReportsThem()
    {
        var id = Guid.NewGuid();
        var read = new ReadResult("Invoice", [],
            [new ReadTable("items", "Items", [new ReadColumn("code", "Code", "text", null)],
                [.. new[] { " blt-10 ", "00123", "NUT-4" }.Select(c => new ReadRow(new Dictionary<string, ReadCell> { ["code"] = Cell(c) }))],
                true)]);
        LookupTables Tables() => new(new Dictionary<Guid, IReadOnlyDictionary<string, string>>
            { [id] = new Dictionary<string, string> { ["BLT-10"] = "A-7", ["00123"] = "A-0042" } });
        OutputNodeTemplate Template(string ifMissing) => Csv(Lf, OutputNode.Arr("Lines", OutputNode.Obj("Line",
            OutputNode.FieldOf("Code", new OutputFieldRule
            {
                OutputPath = "Code", CanonicalField = "code",
                FieldManipulators = [new ManipulatorEntry { Type = "Lookup", Params = [id.ToString(), ifMissing] }],
            }))));

        var kept = DocumentOutputRenderer.Render("csv", Template("keep"), read, "items", Tables());
        Text(kept).Should().Be("Code\nA-7\nA-0042\nNUT-4\n");
        kept.Misses!.Select(m => (m.LookupId, m.Value)).Should().Equal((id, "NUT-4"));

        Text(DocumentOutputRenderer.Render("csv", Template("blank"), read, "items", Tables())).Should().Be("Code\nA-7\nA-0042\n\n");

        // No tables in scope: the step cannot run, which is a refusal of the output, not a crash.
        DocumentOutputRenderer.Render("csv", Template("keep"), read, "items").Error.Should().Be("output_invalid");
    }
}
