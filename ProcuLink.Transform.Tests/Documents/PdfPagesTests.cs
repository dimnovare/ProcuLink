using FluentAssertions;
using ProcuLink.Transform.Parsing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ProcuLink.Transform.Tests.Documents;

public class PdfPagesTests
{
    [Fact]
    public void Take_keeps_only_the_asked_pages()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in new[] { "PAGE-ONE", "PAGE-TWO", "PAGE-THREE" })
            builder.AddPage(595, 842).AddText(text, 12, new PdfPoint(50, 700), font);

        var result = PdfWordExtractor.Extract(PdfPages.Take(builder.Build(), 2, 3));

        result.PageCount.Should().Be(2);
        result.Text.Should().Contain("PAGE-TWO").And.Contain("PAGE-THREE").And.NotContain("PAGE-ONE");
    }
}
