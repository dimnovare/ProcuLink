using System.Text.RegularExpressions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Documents;

/// <summary>The text at a spot a person points to on the original document, and the label beside it.</summary>
public sealed record TeachSpot(string? Value, string? Near, SourceLocation Location);

public static class TeachSpots
{
    private const int NearWords = 4;
    private static readonly Regex Address = new(@"^(cell:r(?<r>\d+)c(?<c>\d+)|line:(?<l>\d+))$", RegexOptions.Compiled);

    public static TeachSpot FromWords(IReadOnlyList<PageWord> words, int page, Box box)
    {
        var onPage = words.Where(w => w.Page == page).ToList();
        var inside = onPage.Where(w => Inside(box, w.Box)).ToList();
        if (inside.Count == 0) return new(null, null, new(page, box, null));

        var left = inside.Min(w => w.Box.X);
        var top = inside.Min(w => w.Box.Y);
        var right = inside.Max(w => w.Box.X + w.Box.Width);
        var bottom = inside.Max(w => w.Box.Y + w.Box.Height);
        var bounds = new Box(left, top, right - left, bottom - top);
        var rest = onPage.Except(inside).ToList();

        var near = rest.Where(w => CentreY(w.Box) >= top && CentreY(w.Box) <= bottom && w.Box.X + w.Box.Width <= left)
            .OrderBy(w => left - (w.Box.X + w.Box.Width)).Take(NearWords).ToList();
        if (near.Count == 0)
        {
            var above = rest.Where(w => CentreY(w.Box) < top && top - CentreY(w.Box) <= 2 * bounds.Height
                                        && w.Box.X + w.Box.Width >= left - bounds.Width && w.Box.X <= right).ToList();
            var lines = ReadingOrder(above);
            near = lines.Count == 0 ? [] : lines[^1].OrderBy(w => Math.Abs(w.Box.X - left)).Take(NearWords).ToList();
        }
        return new(Join(inside), near.Count == 0 ? null : Join(near), new(page, bounds, null));
    }

    public static TeachSpot FromCells(IReadOnlyList<SheetCell> cells, int row, int column)
    {
        string? At(int r, int c) => cells.FirstOrDefault(x => x.Row == r && x.Column == c)?.Text;
        var near = At(row, column - 1);
        if (string.IsNullOrWhiteSpace(near)) near = row > 1 ? At(1, column) : null;
        return new(At(row, column), string.IsNullOrWhiteSpace(near) ? null : near, new(null, null, $"cell:r{row}c{column}"));
    }

    public static TeachSpot FromLine(string text, int line, string? value)
    {
        var lines = Regex.Split(text, @"\r\n|\r|\n");
        string? near = null;
        if (line >= 1 && line <= lines.Length && !string.IsNullOrWhiteSpace(value))
        {
            var at = lines[line - 1].IndexOf(value, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) near = lines[line - 1][..at].Trim() is { Length: > 0 } s ? s : null;
        }
        return new(value, near, new(null, null, $"line:{line}"));
    }

    public static TeachSpot? Read(string fileName, byte[] bytes, int? page, Box? box, string? address, string? value)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var m = address is null ? null : Address.Match(address);
        TeachSpot? spot = null;
        // A .docx is a zip: its bytes are not the lines the viewer shows.
        if (m is { Success: true } && m.Groups["l"].Success && DocumentFormats.IsMore(fileName) && ext != ".docx")
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            spot = FromLine(reader.ReadToEnd(), int.Parse(m.Groups["l"].Value), value);
        }
        else if (m is { Success: true } && m.Groups["r"].Success && ext is ".csv" or ".xlsx")
            spot = FromCells(SheetCellReader.Read(new MemoryStream(bytes), ext), int.Parse(m.Groups["r"].Value), int.Parse(m.Groups["c"].Value));
        else if (page is not null && box is not null && ext == ".pdf")
            spot = FromWords(PdfWordExtractor.Extract(bytes).Words, page.Value, box);
        else if (page == 1 && box is not null && DocumentFormats.IsImage(fileName))
            spot = new(null, null, new(1, box, null));

        return spot is not null && !string.IsNullOrWhiteSpace(value) ? spot with { Value = value } : spot;
    }

    private static double CentreY(Box b) => b.Y + b.Height / 2;

    private static bool Inside(Box area, Box w)
    {
        var cx = w.X + w.Width / 2;
        var cy = CentreY(w);
        return cx >= area.X && cx <= area.X + area.Width && cy >= area.Y && cy <= area.Y + area.Height;
    }

    /// <summary>Lines top to bottom; a word joins the current line while its centre is within half the line's first word's height.</summary>
    private static List<List<PageWord>> ReadingOrder(IEnumerable<PageWord> words)
    {
        var lines = new List<List<PageWord>>();
        foreach (var w in words.OrderBy(w => CentreY(w.Box)))
        {
            var cur = lines.Count == 0 ? null : lines[^1];
            if (cur is not null && Math.Abs(CentreY(w.Box) - CentreY(cur[0].Box)) <= cur[0].Box.Height / 2) cur.Add(w);
            else lines.Add([w]);
        }
        return lines.Select(l => l.OrderBy(w => w.Box.X).ToList()).ToList();
    }

    private static string Join(IEnumerable<PageWord> words) =>
        string.Join(' ', ReadingOrder(words).SelectMany(l => l).Select(w => w.Text));
}
