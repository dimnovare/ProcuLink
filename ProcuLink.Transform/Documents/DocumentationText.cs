using System.Text;
using System.Text.RegularExpressions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Documents;

/// <summary>A receiver's documentation as text. A paged text (PDF) has a <c>[page N]</c> line at the start of each page.</summary>
public sealed record DocumentationText(string Text, bool Truncated, bool Paged = false)
{
    public const int MaxChars = 150_000;
    private static readonly string[] Plain = [".json", ".yaml", ".yml", ".xsd", ".xml", ".txt", ".csv"];

    public static bool Supports(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".pdf" or ".html" or ".htm" || Plain.Contains(ext);
    }

    /// <summary>The text of the file, or null when it can't be read. Never throws.</summary>
    public static DocumentationText? Read(string fileName, byte[] bytes)
    {
        try
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            string? text;
            if (ext == ".pdf")
            {
                var pdf = PdfWordExtractor.Extract(bytes);
                text = pdf.Words.Count == 0 ? null : Regex.Replace(pdf.Text, @"(?m)^=== Page (\d+) ===$", "[page $1]");
            }
            else if (ext is ".html" or ".htm") text = EmailBodyText.Resolve(null, Encoding.UTF8.GetString(bytes));
            else text = Plain.Contains(ext) ? Encoding.UTF8.GetString(bytes).TrimStart('﻿') : null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new DocumentationText(text.Length > MaxChars ? text[..MaxChars] : text, text.Length > MaxChars, ext == ".pdf");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"page N" (PDF) or "line N" where the quote is found, ignoring case and runs of whitespace; null if too short or absent.</summary>
    public string? Locate(string quote)
    {
        var want = Normalise(quote ?? "", out _);
        if (want.Length < 8) return null;
        var at = Normalise(Text, out var map).IndexOf(want, StringComparison.Ordinal);
        if (at < 0) return null;
        var pos = map[at];
        if (!Paged) return $"line {Text.Take(pos).Count(c => c == '\n') + 1}";
        var mark = Text.LastIndexOf("[page ", pos, StringComparison.Ordinal);
        var digits = mark < 0 ? "" : new string(Text[(mark + 6)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? "page 1" : $"page {digits}";
    }

    private static string Normalise(string s, out List<int> map)
    {
        var sb = new StringBuilder(s.Length);
        map = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                if (sb.Length == 0 || sb[^1] == ' ') continue;
                sb.Append(' ');
            }
            else sb.Append(char.ToLowerInvariant(s[i]));
            map.Add(i);
        }
        while (sb.Length > 0 && sb[^1] == ' ') { sb.Length--; map.RemoveAt(map.Count - 1); }
        return sb.ToString();
    }
}
