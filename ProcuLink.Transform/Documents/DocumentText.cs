using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Documents;

public sealed record TextSpan(string Address, string Text);

public sealed record DocumentTextResult(string Text, IReadOnlyList<TextSpan> Spans);

/// <summary>Turns a Word, text or structured file into text for the reader, plus the addressed spans a value can be located in.</summary>
public static class DocumentText
{
    private static string ParagraphText(Paragraph p) => string.Concat(p.Descendants().Select(e => e switch
    {
        Text t => t.Text,
        TabChar or Break => " ",
        _ => "",
    }));

    /// <summary>Throws when the file cannot be opened or yields no text; the job maps that to <c>unreadable_file</c>.</summary>
    public static async Task<DocumentTextResult> ReadAsync(string fileName, byte[] bytes, ISourceTokenizer tokenizer, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        List<TextSpan> spans;
        switch (ext)
        {
            case ".docx":
                using (var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false))
                {
                    var n = 0;
                    spans = (doc.MainDocumentPart?.Document?.Body?.ChildElements.AsEnumerable() ?? [])
                        .Select(e => e switch
                        {
                            Paragraph p => ParagraphText(p),
                            Table t => string.Join('\n', t.Elements<TableRow>().Select(r =>
                                string.Join(" | ", r.Elements<TableCell>().Select(c =>
                                    string.Join(' ', c.Elements<Paragraph>().Select(ParagraphText)).Trim())))),
                            _ => "",
                        })
                        .SelectMany(s => s.Split('\n'))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => new TextSpan($"para:{++n}", s.Trim())).ToList();
                }
                break;
            case ".txt":
                spans = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split('\n')
                    .Select((l, i) => new TextSpan($"line:{i + 1}", l.TrimEnd('\r')))
                    .Where(s => s.Text.Length > 0).ToList();
                break;
            default:
                var tokens = await tokenizer.TokenizeAsync(bytes, ext, ct);
                if (tokens.Count == 0) throw new InvalidDataException("The file has no readable text.");
                return new(string.Join('\n', tokens.Select(t => $"{t.Id} {t.Label}: {t.Value}")),
                    tokens.Select(t => new TextSpan(t.Id, t.Value)).ToList());
        }
        if (spans.Count == 0) throw new InvalidDataException("The file has no readable text.");
        return new(string.Join('\n', spans.Select(s => s.Text)), spans);
    }
}
