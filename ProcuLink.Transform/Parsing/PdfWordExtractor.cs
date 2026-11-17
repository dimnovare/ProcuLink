using System.Text;
using ProcuLink.Core.Documents;
using UglyToad.PdfPig;

namespace ProcuLink.Transform.Parsing;

/// <summary><c>PageTexts[i]</c> is page <c>i + 1</c>'s block of <see cref="Text"/>, header line included.</summary>
public sealed record PdfWords(int PageCount, IReadOnlyList<PageWord> Words, string Text, IReadOnlyList<string> PageTexts);

/// <summary>Words of a PDF with page boxes normalised to 0..1, origin top-left (PdfPig is bottom-left, in points).</summary>
public static class PdfWordExtractor
{
    public static PdfWords Extract(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);
        var words = new List<PageWord>();
        var pageTexts = new List<string>();

        foreach (var page in document.GetPages())
        {
            // One line grouping per page feeds both Words and Text, so the two share a reading order.
            var lines = page.GetWords()
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 4.0) * 4.0)
                .OrderByDescending(g => g.Key)
                .Select(g => g.OrderBy(w => w.BoundingBox.Left).ToList())
                .ToList();
            foreach (var w in lines.SelectMany(l => l))
            {
                var b = w.BoundingBox;
                var x = Clamp(b.Left / page.Width);
                var y = Clamp(1 - b.Top / page.Height);
                words.Add(new PageWord(TextRepair.RepairMojibake(w.Text), page.Number, new Box(
                    x, y,
                    Math.Min(Clamp((b.Right - b.Left) / page.Width), 1 - x),
                    Math.Min(Clamp((b.Top - b.Bottom) / page.Height), 1 - y))));
            }

            var text = new StringBuilder().Append("=== Page ").Append(page.Number).Append(" ===\n");
            foreach (var line in lines) text.Append(string.Join(" ", line.Select(w => TextRepair.RepairMojibake(w.Text)))).Append('\n');
            pageTexts.Add(text.ToString());
        }

        return new PdfWords(document.NumberOfPages, words, string.Concat(pageTexts), pageTexts);
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
