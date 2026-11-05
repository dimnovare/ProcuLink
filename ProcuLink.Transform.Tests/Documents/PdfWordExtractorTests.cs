using FluentAssertions;
using ProcuLink.Transform.Parsing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProcuLink.Transform.Tests.Documents;

public class PdfWordExtractorTests
{
    [Fact]
    public void Extract_returns_words_with_top_left_normalised_boxes()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var pdf = Document.Create(c =>
        {
            c.Page(p =>
            {
                p.Size(PageSizes.A4);
                p.Margin(40);
                p.Content().Text("Invoice No. 4471");
            });
            c.Page(p =>
            {
                p.Size(PageSizes.A4);
                p.Margin(40);
                p.Content().AlignBottom().Text("Total 2,380.00");
            });
        }).GeneratePdf();

        var result = PdfWordExtractor.Extract(pdf);

        result.PageCount.Should().Be(2);
        var first = result.Words.Single(w => w.Text == "4471");
        first.Page.Should().Be(1);
        first.Box.Y.Should().BeLessThan(0.3);
        var second = result.Words.Single(w => w.Text == "2,380.00");
        second.Page.Should().Be(2);
        second.Box.Y.Should().BeGreaterThan(0.6);
        result.Words.Should().OnlyContain(w =>
            w.Box.X >= 0 && w.Box.Y >= 0 && w.Box.Width <= 1 && w.Box.Height <= 1
            && w.Box.X + w.Box.Width <= 1.0001 && w.Box.Y + w.Box.Height <= 1.0001);
        result.Text.Should().Contain("=== Page 2 ===").And.Contain("Invoice No. 4471");
    }
}
