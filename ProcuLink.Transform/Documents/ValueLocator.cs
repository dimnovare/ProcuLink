using System.Text.RegularExpressions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Documents;

/// <summary>Ties a value the AI reported back to the page words or sheet cell it came from. First match wins.</summary>
public static class ValueLocator
{
    public static SourceLocation? Locate(string? value, IReadOnlyList<PageWord> words)
    {
        var tokens = Tokens(value);
        if (tokens.Length == 0) return null;
        var numbers = FieldValueParser.NumberCandidates(value);

        for (var i = 0; i + tokens.Length <= words.Count; i++)
        {
            var page = words[i].Page;
            var run = true;
            for (var k = 0; k < tokens.Length && run; k++)
            {
                var w = words[i + k];
                run = w.Page == page && (Norm(w.Text) == tokens[k]
                    || (tokens.Length == 1 && numbers.Intersect(FieldValueParser.NumberCandidates(w.Text)).Any()));
            }
            if (!run) continue;

            return Union(page, words.Skip(i).Take(tokens.Length).Select(w => w.Box).ToList());
        }
        return tokens.Length > 1 ? LocateWrapped(tokens, words) : null;
    }

    private const int MaxWordsBetweenLines = 12;
    private const double ColumnSlack = 0.02;

    /// <summary>
    /// A table cell that wraps: words are ordered line by line, so the cell's second line comes after the rest of
    /// its first line. The next token may skip up to <see cref="MaxWordsBetweenLines"/> words, but only onto the
    /// line just below, inside the column the matched words span.
    /// </summary>
    private static SourceLocation? LocateWrapped(string[] tokens, IReadOnlyList<PageWord> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (Norm(words[i].Text) != tokens[0]) continue;
            var matched = new List<Box> { words[i].Box };
            var prev = i;
            for (var k = 1; k < tokens.Length && prev >= 0; k++)
            {
                var p = words[prev].Box;
                var next = -1;
                for (var j = prev + 1; j < words.Count && j <= prev + 1 + MaxWordsBetweenLines && next < 0; j++)
                {
                    var w = words[j];
                    if (w.Page != words[i].Page || Norm(w.Text) != tokens[k]) continue;
                    // Boxes hug the glyphs ("mm" is shorter than "Kontori"), so lines are compared by their bottoms,
                    // and the next line sits about 1.2 to 2.5 glyph heights under the one above.
                    var (drop, h) = (w.Box.Y + w.Box.Height - (p.Y + p.Height), Math.Max(w.Box.Height, p.Height));
                    var inColumn = w.Box.X >= matched[0].X - ColumnSlack && w.Box.X <= matched.Max(b => b.X + b.Width);
                    // Same line: only the very next word. Below: the next line only.
                    if (drop > h / 2 ? inColumn && drop < 2.5 * h : j == prev + 1 && Math.Abs(drop) <= h / 2)
                        next = j;
                }
                if (next >= 0) matched.Add(words[next].Box);
                prev = next;
            }
            if (prev >= 0) return Union(words[i].Page, matched);
        }
        return null;
    }

    private static SourceLocation Union(int page, List<Box> matched)
    {
        var x = matched.Min(b => b.X);
        var y = matched.Min(b => b.Y);
        return new SourceLocation(page,
            new Box(x, y, matched.Max(b => b.X + b.Width) - x, matched.Max(b => b.Y + b.Height) - y), null);
    }

    public static SourceLocation? LocateInCells(string? value, IReadOnlyList<SheetCell> cells)
    {
        var target = string.Join(' ', Tokens(value));
        if (target.Length == 0) return null;
        var hit = cells.FirstOrDefault(c => string.Join(' ', Tokens(c.Text)) == target);
        return hit is null ? null : new SourceLocation(null, null, $"cell:r{hit.Row}c{hit.Column}");
    }

    /// <summary>A whole-span match first (trimmed, case-insensitive), else the first span that contains the value.</summary>
    public static SourceLocation? LocateInSpans(string? value, IReadOnlyList<TextSpan> spans)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        var hit = spans.FirstOrDefault(s => string.Equals(s.Text.Trim(), v, StringComparison.OrdinalIgnoreCase))
            ?? spans.FirstOrDefault(s => s.Text.Contains(v, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : new SourceLocation(null, null, hit.Address);
    }

    private static string[] Tokens(string? s) =>
        string.IsNullOrWhiteSpace(s) ? [] : Regex.Split(s.Trim(), @"\s+").Select(Norm).ToArray();

    private static string Norm(string s) => string.Concat(s.Trim().Trim('.', ',', ';', ':', '(', ')', '"', '\'').ToLowerInvariant()
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark));
}
