using System.Text;
using ProcuLink.Core.Documents;
using UglyToad.PdfPig;

namespace ProcuLink.Transform.Parsing;

public sealed record PdfWords(int PageCount, IReadOnlyList<PageWord> Words, string Text);

/// <summary>Words of a PDF with page boxes normalised to 0..1, origin top-left (PdfPig is bottom-left, in points).</summary>
public static class PdfWordExtractor
{
    public static PdfWords Extract(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);
        var words = new List<PageWord>();
        var text = new StringBuilder();

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
                words.Add(new PageWord(w.Text, page.Number, new Box(
                    x, y,
                    Math.Min(Clamp((b.Right - b.Left) / page.Width), 1 - x),
                    Math.Min(Clamp((b.Top - b.Bottom) / page.Height), 1 - y))));
            }

            text.Append("=== Page ").Append(page.Number).Append(" ===\n");
            foreach (var line in lines) text.Append(string.Join(" ", line.Select(w => w.Text))).Append('\n');
        }

        return new PdfWords(document.NumberOfPages, words, text.ToString());
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
