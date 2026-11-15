using FluentAssertions;
using ProcuLink.Transform.Parsing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

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

    [Fact]
    public void Page_texts_join_to_the_whole_text_and_a_page_without_words_is_only_its_header()
    {
        var result = PdfWordExtractor.Extract(ThreePagesWithABlankMiddle());

        result.PageTexts.Should().HaveCount(3);
        string.Concat(result.PageTexts).Should().Be(result.Text);
        result.PageTexts[1].Should().Be("=== Page 2 ===\n");
        result.PageTexts[2].Should().StartWith("=== Page 3 ===\n").And.Contain("2,380.00");
    }

    private static byte[] ThreePagesWithABlankMiddle()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(c =>
        {
            c.Page(p => { p.Size(PageSizes.A4); p.Margin(40); p.Content().Text("Invoice No. 4471"); });
            c.Page(p => { p.Size(PageSizes.A4); p.Content().Width(200).Height(200).Background(Colors.Grey.Medium); });
            c.Page(p => { p.Size(PageSizes.A4); p.Margin(40); p.Content().Text("Total 2,380.00"); });
        }).GeneratePdf();
    }

    [Fact]
    public void Words_on_one_line_with_uneven_baselines_read_left_to_right()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText("Acme", 12, new PdfPoint(50, 699), font);
        page.AddText("Total", 12, new PdfPoint(50, 600), font);
        page.AddText("Parts", 12, new PdfPoint(120, 701), font);
        page.AddText("Sum", 12, new PdfPoint(120, 600), font);

        var result = PdfWordExtractor.Extract(builder.Build());

        result.Words.Select(w => w.Text).Should().Equal("Acme", "Parts", "Total", "Sum");
        result.Text.Should().Contain("Acme Parts").And.Contain("Total Sum");
    }
}
