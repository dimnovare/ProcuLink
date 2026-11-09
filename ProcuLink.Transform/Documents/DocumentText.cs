using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Documents;

public sealed record TextSpan(string Address, string Text);

public sealed record DocumentTextResult(string Text, IReadOnlyList<TextSpan> Spans);

/// <summary>Turns a Word, text or structured file into text for the reader, plus the addressed spans a value can be located in.</summary>
public static class DocumentText
{
    // One pass over the body, so every element is visited once. A paragraph gives only its own runs; a text box
    // inside it is walked afterwards as blocks of its own, so nested boxes are never repeated by each enclosing
    // paragraph. Content controls and other containers are walked through. Of an AlternateContent, only the
    // first branch is read, since Word stores the same text box twice (DrawingML and a VML fallback).
    // Real documents nest a handful of levels; a crafted one could nest deep enough to overflow the stack.
    private const int MaxDepth = 64;

    private static void Blocks(OpenXmlElement e, List<string> lines, int depth = 0)
    {
        if (depth > MaxDepth) throw new InvalidDataException("The document nests too deeply.");
        foreach (var c in e.ChildElements)
        {
            var boxes = new List<OpenXmlElement>();
            switch (c)
            {
                case Paragraph p:
                    lines.Add(Inline(p, boxes, depth));
                    break;
                case Table t:
                    foreach (var r in t.Elements<TableRow>())
                        lines.Add(string.Join(" | ", r.Elements<TableCell>().Select(cell =>
                            string.Join(' ', cell.Elements<Paragraph>().Select(cp => Inline(cp, boxes, depth))).Trim())));
                    break;
                default:
                    Blocks(c, lines, depth + 1);
                    break;
            }
            foreach (var b in boxes) Blocks(b, lines, depth + 1);
        }
    }

    private static string Inline(OpenXmlElement e, List<OpenXmlElement> boxes, int depth)
    {
        var sb = new StringBuilder();
        void Walk(OpenXmlElement x, int d)
        {
            if (d > MaxDepth) throw new InvalidDataException("The document nests too deeply.");
            foreach (var c in x.ChildElements)
                switch (c)
                {
                    case Text t: sb.Append(t.Text); break;
                    case TabChar or Break: sb.Append(' '); break;
                    case TextBoxContent box: boxes.Add(box); break;
                    case AlternateContent ac: if (ac.FirstChild is { } first) Walk(first, d + 1); break;
                    default: Walk(c, d + 1); break;
                }
        }
        Walk(e, depth);
        return sb.ToString();
    }

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
                    var lines = new List<string>();
                    if (doc.MainDocumentPart?.Document?.Body is { } body) Blocks(body, lines);
                    spans = lines
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
