using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.Documents;

public class DocumentTextTests
{
    private static readonly SourceTokenizer Tokenizer = new();

    [Fact]
    public async Task Docx_paragraphs_and_table_rows_come_out_in_order()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            TableCell Cell(string t) => new(new Paragraph(new Run(new Text(t))));
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Order no:"), new TabChar(), new Text("PO-77"))),
                new Table(new TableRow(Cell("A"), Cell("3"), Cell("9.50"))),
                new SdtBlock(new SdtContentBlock(new Paragraph(new Run(new Text("In a control"))))),
                new Paragraph(new Run(new Text("Ship to:"), new Picture(new DocumentFormat.OpenXml.Vml.Shape(new DocumentFormat.OpenXml.Vml.TextBox(
                    new TextBoxContent(new Paragraph(new Run(new Text("Dock 4")))))))))));
        }

        var r = await DocumentText.ReadAsync("po.docx", ms.ToArray(), Tokenizer, default);

        r.Spans[0].Should().Be(new TextSpan("para:1", "Order no: PO-77"));
        r.Spans[1].Should().Be(new TextSpan("para:2", "A | 3 | 9.50"));
        r.Spans[2].Should().Be(new TextSpan("para:3", "In a control"));
        // A text box is its own span, read once, and not repeated inside the paragraph that holds it.
        r.Spans.Skip(3).Should().Equal(new TextSpan("para:4", "Ship to:"), new TextSpan("para:5", "Dock 4"));
        r.Text.Should().Contain("A | 3 | 9.50");
    }

    [Fact]
    public async Task Text_file_drops_the_bom_and_keeps_physical_line_numbers()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("Tõnis Äär\n\nPO-9")).ToArray();

        var r = await DocumentText.ReadAsync("note.txt", bytes, Tokenizer, default);

        r.Spans.Should().Equal(new TextSpan("line:1", "Tõnis Äär"), new TextSpan("line:3", "PO-9"));
        r.Spans.Should().OnlyContain(s => !s.Text.Contains('\uFEFF'));
    }

    [Fact]
    public async Task Utf16_text_file_with_a_bom_is_decoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Tõnis Äär")).ToArray();

        var r = await DocumentText.ReadAsync("note.txt", bytes, Tokenizer, default);

        r.Spans.Should().Equal(new TextSpan("line:1", "Tõnis Äär"));
    }

    [Fact]
    public async Task X12_saved_as_edi_is_read_as_x12()
    {
        const string x12 = "ISA*00*          *00*          *ZZ*TESTBUYER      *ZZ*TESTSUPPLIER   *260528*1200*U*00401*000000001*0*P*>~GS*PO*B*S*20260528*1200*1*X*004010~ST*850*0001~BEG*00*NE*PO-X12-000123**20260528~SE*3*0001~";

        var r = await DocumentText.ReadAsync("po.edi", Encoding.UTF8.GetBytes(x12), Tokenizer, default);

        r.Spans.Should().Contain(s => s.Text == "PO-X12-000123");
    }

    [Fact]
    public async Task A_word_file_nested_too_deep_is_refused_not_overflowed()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            DocumentFormat.OpenXml.OpenXmlElement inner = new Paragraph(new Run(new Text("deep")));
            for (var i = 0; i < 200; i++) inner = new SdtBlock(new SdtContentBlock(inner));
            doc.AddMainDocumentPart().Document = new Document(new Body(inner));
        }

        await FluentActions.Awaiting(() => DocumentText.ReadAsync("deep.docx", ms.ToArray(), Tokenizer, default)).Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_word_part_over_the_character_cap_is_refused_not_inflated()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            doc.AddMainDocumentPart().Document = new Document(new Body(
                new Paragraph(new Run(new Text(new string('a', (int)DocumentText.MaxDocxPartChars + 1))))));

        await FluentActions.Awaiting(() => DocumentText.ReadAsync("big.docx", ms.ToArray(), Tokenizer, default)).Should().ThrowAsync<Exception>();
    }

    [Theory]
    [InlineData("a.txt", "\n\n")]
    [InlineData("a.json", "{")]
    [InlineData("a.docx", null)]
    public async Task A_file_with_no_text_throws(string name, string? content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? "");
        if (content is null)
        {
            var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
                doc.AddMainDocumentPart().Document = new Document(new Body());
            bytes = ms.ToArray();
        }

        await FluentActions.Awaiting(() => DocumentText.ReadAsync(name, bytes, Tokenizer, default)).Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Json_tokens_become_addressed_spans()
    {
        var r = await DocumentText.ReadAsync("o.json", Encoding.UTF8.GetBytes("{\"po\":\"PO-9\"}"), Tokenizer, default);

        r.Spans.Should().ContainSingle(s => s.Address == "json:/po" && s.Text == "PO-9");
        r.Text.Should().Contain("/po").And.Contain("PO-9");
    }
}
