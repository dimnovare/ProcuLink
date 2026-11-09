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

    /// <summary>A Word part may hold at most this many characters. A 10 MB .docx can inflate to gigabytes, so the SDK stops reading past this instead of the cap running after the whole body is in memory.</summary>
    public const long MaxDocxPartChars = 10_000_000;

    /// <summary>Throws when the file cannot be opened or yields no text; the job maps that to <c>unreadable_file</c>.</summary>
    public static async Task<DocumentTextResult> ReadAsync(string fileName, byte[] bytes, ISourceTokenizer tokenizer, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        List<TextSpan> spans;
        switch (ext)
        {
            case ".docx":
                using (var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false, new OpenSettings { MaxCharactersInPart = MaxDocxPartChars }))
                {
                    var n = 0;
                    spans = (doc.MainDocumentPart?.Document?.Body?.Descendants().Where(e => e is Paragraph or Table && !e.Ancestors<Table>().Any()) ?? [])
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
                spans = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd().Split('\n')
                    .Select((l, i) => new TextSpan($"line:{i + 1}", l.TrimEnd('\r')))
                    .Where(s => s.Text.Length > 0).ToList();
                break;
            default:
                // X12 is often saved as .edi; the tokenizer would find no EDIFACT segments in it.
                if (ext == ".edi" && Encoding.UTF8.GetString(bytes).TrimStart().StartsWith("ISA", StringComparison.Ordinal)) ext = ".x12";
                var tokens = await tokenizer.TokenizeAsync(bytes, ext, ct);
                if (tokens.Count == 0) throw new InvalidDataException("The file has no readable text.");
                return new(string.Join('\n', tokens.Select(t => $"{t.Id} {t.Label}: {t.Value}")),
                    tokens.Select(t => new TextSpan(t.Id, t.Value)).ToList());
        }
        if (spans.Count == 0) throw new InvalidDataException("The file has no readable text.");
        return new(string.Join('\n', spans.Select(s => s.Text)), spans);
    }
}
