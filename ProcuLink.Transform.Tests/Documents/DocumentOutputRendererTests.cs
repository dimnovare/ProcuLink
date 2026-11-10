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

    private static OutputNode Wide(string name, string field, int width) =>
        OutputNode.FieldOf(name, new OutputFieldRule { OutputPath = name, CanonicalField = field, Width = width });

    private static ReadResult TwoRows() => new("Order", [Field("po", "A1")],
        [new ReadTable("items", "Items", [new ReadColumn("qty", "Qty", "number", null)],
            [new ReadRow(new Dictionary<string, ReadCell> { ["qty"] = Cell("5") }),
             new ReadRow(new Dictionary<string, ReadCell> { ["qty"] = Cell("12") })], true)]);

    [Fact]
    public void Fixed_pads_each_column_and_writes_no_header()
    {
        var t = Csv(new CsvDialect(), Wide("po", "po", 6), OutputNode.Arr("Lines", OutputNode.Obj("Line", Wide("qty", "qty", 3))));

        Text(DocumentOutputRenderer.Render(FlowOutputFormats.Fixed, t, TwoRows(), "items")).Should().Be("A1    5  \r\nA1    12 \r\n");
    }

    [Fact]
    public void Fixed_value_longer_than_width_fails_naming_the_column()
    {
        var t = Csv(new CsvDialect(), Wide("po", "po", 1));

        var r = DocumentOutputRenderer.Render(FlowOutputFormats.Fixed, t, TwoRows(), null);

        (r.Ok, r.Error, r.Detail).Should().Be((false, "output_invalid", "\"po\" is longer than 1 characters. Make the column wider."));
    }

    [Fact]
    public void Text_layout_lists_fields_and_skips_the_empty_table()
    {
        var t = Csv(new CsvDialect(), Rule("po", "po"), OutputNode.Arr("Lines", OutputNode.Obj("Line", Rule("qty", "qty"))));

        Text(DocumentOutputRenderer.Render(FlowOutputFormats.Text, t, new ReadResult("Order", [Field("po", "A1")]), null,
            layout: new OutputLayout("Order", null, null))).Should().Be("Order\n\npo: A1\n");
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public void Pdf_layout_is_real_text_with_title_fields_and_rows()
    {
        var t = Csv(new CsvDialect(), Rule("po", "po"), OutputNode.Arr("Lines", OutputNode.Obj("Line", Rule("qty", "qty"))));

        var r = DocumentOutputRenderer.Render(FlowOutputFormats.Pdf, t, TwoRows(), "items",
            layout: new OutputLayout("Order", null, "Thanks"), logo: TinyPng);

        r.Ok.Should().BeTrue(r.Detail);
        (r.Result!.ContentType, r.Result.FileExtension).Should().Be(("application/pdf", ".pdf"));
        using var ms = new MemoryStream();
        r.Result.Content.CopyTo(ms);
        var bytes = ms.ToArray();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
        var text = ProcuLink.Transform.Parsing.PdfWordExtractor.Extract(bytes).Text;
        text.Should().Contain("Order").And.Contain("A1").And.Contain("12").And.Contain("Thanks");
        LayoutPdfWriter.IsUsableLogo(TinyPng).Should().BeTrue();
        LayoutPdfWriter.IsUsableLogo(new byte[] { 1, 2, 3 }).Should().BeFalse();
        // A header declaring 5000 x 5000 is refused before it is decoded.
        var huge = TinyPng.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 5000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(20), 5000);
        LayoutPdfWriter.IsUsableLogo(huge).Should().BeFalse();
        // No table rows: fields only, no crash.
        DocumentOutputRenderer.Render(FlowOutputFormats.Pdf, t, new ReadResult("Order", [Field("po", "A1")]), null).Ok.Should().BeTrue();
    }

    [Fact]
    public void Fixed_width_counts_encoded_bytes_and_sanitises_control_characters()
    {
        string Detail(string value, int width) => DocumentOutputRenderer.Render(FlowOutputFormats.Fixed,
            Csv(new CsvDialect { Encoding = "utf-8" }, Wide("n", "n", width)), new ReadResult("O", [Field("n", value)]), null).Detail!;
        byte[] Bytes(string value, int width)
        {
            var r = DocumentOutputRenderer.Render(FlowOutputFormats.Fixed, Csv(new CsvDialect { Encoding = "utf-8", LineEnding = "" },
                Wide("n", "n", width)), new ReadResult("O", [Field("n", value)]), null);
            r.Ok.Should().BeTrue(r.Detail);
            using var ms = new MemoryStream();
            r.Result!.Content.CopyTo(ms);
            return ms.ToArray();
        }

        Detail("Mö", 2).Should().Be("\"n\" is longer than 2 characters. Make the column wider.");
        Bytes("Mö", 3).Should().Equal(System.Text.Encoding.UTF8.GetBytes("Mö"));
        Bytes("a" + (char)0x2028 + "b", 3).Should().Equal(System.Text.Encoding.UTF8.GetBytes("a b"));
        Detail("x", 1001).Should().Be("Give every column a width from 1 to 1000.");
    }
}
