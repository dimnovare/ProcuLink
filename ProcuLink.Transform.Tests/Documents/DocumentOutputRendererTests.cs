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

        // The XML writer's own sentence (here: an "xmlns" attribute redefining the element's namespace) is not shown: a plain one is.
        var xml = new OutputNodeTemplate { Format = OutputFormat.Xml, Root = OutputNode.Obj("Order",
            OutputNode.Attr("xmlns", new OutputFieldRule { OutputPath = "xmlns", CanonicalField = "invoice_no" })) };
        var xmlBad = DocumentOutputRenderer.Render("xml", xml, read, null);
        (xmlBad.Ok, xmlBad.Error, xmlBad.Detail).Should().Be((false, "output_invalid", "We couldn't write the XML file from these values."));
    }

    [Fact]
    public void Xml_names_the_table_element_by_the_flows_table_name_in_every_file()
    {
        ReadResult Read(string tableLabel) => new("Arve", [Field("arve_nr", "A-1")],
            [new ReadTable("invoice_lines", tableLabel, [new ReadColumn("toode", "Toode", "text", null)],
                [new ReadRow(new Dictionary<string, ReadCell> { ["toode"] = Cell("Kohv") })], true)]);
        OutputNodeTemplate Xml(string tableName) => new() { Format = OutputFormat.Xml, Root = OutputNode.Obj("document", Rule("Arve nr", "arve_nr"),
            OutputNode.Arr(tableName, OutputNode.Obj("row", Rule("Toode", "toode")), "invoice_lines")) };
        FlowTable[] tables = [new("invoice_lines", "Arve read", [])];

        // Two documents name their table differently; the file takes the flow's name for it both times.
        foreach (var read in new[] { Read("Arve read"), Read("Invoice lines") })
            Text(DocumentOutputRenderer.Render("xml", Xml("invoice_lines"), read, "invoice_lines", tables: tables))
                .Should().Contain("<ArveRead>").And.Contain("<Toode>Kohv</Toode>").And.NotContain("invoice_lines").And.NotContain("InvoiceLines");

        // A name the user gave the table element is kept; without the flow's tables the key stays.
        Text(DocumentOutputRenderer.Render("xml", Xml("Read"), Read("Arve read"), "invoice_lines", tables: tables)).Should().Contain("<Read>");
        Text(DocumentOutputRenderer.Render("xml", Xml("invoice_lines"), Read("Arve read"), "invoice_lines")).Should().Contain("<invoice_lines>");
    }

    [Fact]
    public void A_step_without_its_settings_says_what_it_needs()
    {
        var read = new ReadResult("Invoice", [Field("total", "100")]);
        var t = Csv(Lf, OutputNode.FieldOf("Total", new OutputFieldRule
        {
            OutputPath = "Total", CanonicalField = "total",
            FieldManipulators = [new ManipulatorEntry { Type = "Divide", Params = [] }],
        }));

        var r = DocumentOutputRenderer.Render("csv", t, read, null);

        (r.Ok, r.Error, r.Detail).Should().Be((false, "output_invalid", "“Divide the number” needs a number to divide by, such as 100."));
    }

    [Fact]
    public void A_text_file_numbers_a_repeated_label_like_the_csv_header()
    {
        var read = new ReadResult("Invoice", [Field("total", "120.00"), Field("net", "100.00")]);
        var t = Csv(Lf, Rule("Summa", "total"), Rule("summa", "net"));

        Text(DocumentOutputRenderer.Render("text", t, read, null)).Should().Contain("summa 2");
    }

    [Fact]
    public void Dates_and_amounts_go_out_one_way_and_uncertain_ones_as_read()
    {
        var estonian = new ReadResult("Invoice",
            [Field("total", "1.500", "number"), Field("tax", "1.234,50", "money"), Field("date", "18.11.2026", "date")]);
        var english = new ReadResult("Invoice",
            [Field("total", "285.00", "money"), Field("tax", "57.00", "money"), Field("date", "14 Nov 2026", "date")]);
        var t = Csv(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, Rule("Total", "total"), Rule("Tax", "tax"), Rule("Date", "date"));

        // "1.500" is one and a half or fifteen hundred, so it stays; the comma is always quoted by the shared escaper.
        Text(DocumentOutputRenderer.Render("csv", t, estonian, null)).Should().Be("Total;Tax;Date\n1.500;\"1234,50\";2026-11-18\n");
        Text(DocumentOutputRenderer.Render("csv", t, english, null)).Should().Be("Total;Tax;Date\n\"285,00\";\"57,00\";2026-11-14\n");
        // A text layout shows the document as printed; a json file takes the decimal point.
        Text(DocumentOutputRenderer.Render(FlowOutputFormats.Text, t, estonian, null)).Should().Contain("18.11.2026").And.Contain("1.234,50");
        Text(DocumentOutputRenderer.Render("json", t with { Format = OutputFormat.Json, CsvDialect = null }, estonian, null))
            .Should().Contain("\"1234.50\"").And.Contain("\"2026-11-18\"");
        // A step reads numbers the invariant way, so a file with one keeps the decimal point: 57.00 doubles, never 5700.
        var doubled = Csv(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, Rule("Total", "total"), OutputNode.FieldOf("Tax", new OutputFieldRule
        {
            OutputPath = "Tax", CanonicalField = "tax", FieldManipulators = [new ManipulatorEntry { Type = "Multiply", Params = ["2"] }],
        }));
        Text(DocumentOutputRenderer.Render("csv", doubled, english, null)).Should().StartWith("Total;Tax\n285.00;114").And.NotContain("11400");
    }

    private static ReadResult Lines(params (string Qty, string Price)[] rows) => new("Order",
        [Field("dokument", "TEST-CSV-77"), Field("kuupaev", "06.11.2026", "date")],
        [new ReadTable("lines", "Lines",
            [new ReadColumn("kood", "Kood", "text", null), new ReadColumn("kirjeldus", "Kirjeldus", "text", null),
             new ReadColumn("kogus", "Kogus", "number", null), new ReadColumn("hind", "Hind", "money", null)],
            rows.Select((r, i) => new ReadRow(new Dictionary<string, ReadCell>
            {
                ["kood"] = Cell($"LK-{11 + i}"), ["kirjeldus"] = Cell(i == 0 ? "Mutter M6" : "Seib, lai"), ["kogus"] = Cell(r.Qty), ["hind"] = Cell(r.Price),
            })).ToList(), true)]);

    [Fact]
    public void A_european_csv_reads_every_number_its_own_way_and_writes_them_one_way()
    {
        // The synthetic file: "0,04" and "1.234,50" prove the decimal comma, so the quantity 1.500 is fifteen hundred.
        var read = ReadNumbers.Settle(Lines(("1.500", "0,04"), ("20", "1.234,50")));
        var cells = read.Tables[0].Rows.Select(r => (r.Cells["kogus"].Value, r.Cells["hind"].Value, r.Cells["kogus"].Confidence)).ToList();
        cells.Should().Equal(("1500", "0,04", 1d), ("20", "1.234,50", 1d));

        var t = Csv(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, Rule("Dokument", "dokument"), Rule("Kuupäev", "kuupaev"),
            OutputNode.Arr("Lines", OutputNode.Obj("Line", Rule("Kood", "kood"), Rule("Kirjeldus", "kirjeldus"), Rule("Kogus", "kogus"), Rule("Hind", "hind"))));
        Text(DocumentOutputRenderer.Render("csv", t, read, "lines")).Should().Be(
            "Dokument;Kuupäev;Kood;Kirjeldus;Kogus;Hind\n" +
            "TEST-CSV-77;2026-11-06;LK-11;Mutter M6;1500;\"0,04\"\n" +
            "TEST-CSV-77;2026-11-06;LK-12;\"Seib, lai\";20;\"1234,50\"\n");

        // A US file the other way round; a decimal comma that is the number's own mark keeps it.
        ReadNumbers.Settle(Lines(("1,500", "12.50"))).Tables[0].Rows[0].Cells["kogus"].Value.Should().Be("1500");
        ReadNumbers.Settle(Lines(("1,500", "0,04"))).Tables[0].Rows[0].Cells["kogus"].Value.Should().Be("1,5");
        // Nothing settles it: the value stays as printed and is shown as one to check.
        var open = ReadNumbers.Settle(Lines(("1.500", "20"))).Tables[0].Rows[0].Cells["kogus"];
        (open.Value, open.Confidence).Should().Be(("1.500", ReadNumbers.UnsureConfidence));
    }

    [Fact]
    public void A_text_value_that_is_wholly_a_date_goes_out_as_a_date()
    {
        // A due date read as payment terms; a short-year text may be a version; a code stays as printed.
        var read = new ReadResult("Invoice",
            [Field("terms", "02.12.2026", "text"), Field("version", "1.2.26", "text"), Field("ref", "18.11.2026", "code")]);
        var t = Csv(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, Rule("Terms", "terms"), Rule("Version", "version"), Rule("Ref", "ref"));

        Text(DocumentOutputRenderer.Render("csv", t, read, null)).Should().Be("Terms;Version;Ref\n2026-12-02;1.2.26;18.11.2026\n");
    }

    [Fact]
    public void A_byte_order_mark_is_written_only_when_the_dialect_asks()
    {
        var read = new ReadResult("Invoice", [Field("total", "120.00", "money")]);
        byte[] Bytes(CsvDialect d)
        {
            var r = DocumentOutputRenderer.Render("csv", Csv(d, Rule("Summa", "total")), read, null);
            r.Ok.Should().BeTrue(r.Detail);
            using var ms = new MemoryStream();
            r.Result!.Content.CopyTo(ms);
            return ms.ToArray();
        }

        var bom = Bytes(new CsvDialect { Delimiter = ";", LineEnding = "\n", ByteOrderMark = true });
        bom.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        // The amount takes the decimal comma that goes with ';'.
        System.Text.Encoding.UTF8.GetString(bom, 3, bom.Length - 3).Should().Be("Summa\n\"120,00\"\n");

        Bytes(new CsvDialect { Delimiter = ";", LineEnding = "\n" })[0].Should().Be((byte)'S');
        Bytes(new CsvDialect { Delimiter = ";", LineEnding = "\n", Encoding = "windows-1252", ByteOrderMark = true })[0]
            .Should().Be((byte)'S');
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
