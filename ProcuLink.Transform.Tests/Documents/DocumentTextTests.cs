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
                new Table(new TableRow(Cell("A"), Cell("3"), Cell("9.50")))));
        }

        var r = await DocumentText.ReadAsync("po.docx", ms.ToArray(), Tokenizer, default);

        r.Spans[0].Should().Be(new TextSpan("para:1", "Order no: PO-77"));
        r.Spans[1].Should().Be(new TextSpan("para:2", "A | 3 | 9.50"));
        r.Text.Should().Contain("A | 3 | 9.50");
    }

    [Fact]
    public async Task Text_file_drops_the_bom_and_keeps_physical_line_numbers()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("Tõnis Äär\n\nPO-9")).ToArray();

        var r = await DocumentText.ReadAsync("note.txt", bytes, Tokenizer, default);

        r.Spans.Should().Equal(new TextSpan("line:1", "Tõnis Äär"), new TextSpan("line:3", "PO-9"));
        r.Spans.Should().OnlyContain(s => !s.Text.Contains('﻿'));
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
