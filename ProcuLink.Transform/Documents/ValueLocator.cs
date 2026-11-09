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

            var matched = words.Skip(i).Take(tokens.Length).Select(w => w.Box).ToList();
            var x = matched.Min(b => b.X);
            var y = matched.Min(b => b.Y);
            return new SourceLocation(page,
                new Box(x, y, matched.Max(b => b.X + b.Width) - x, matched.Max(b => b.Y + b.Height) - y), null);
        }
        return null;
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
