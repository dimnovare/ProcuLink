using System.Text;
using System.Xml;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;

namespace ProcuLink.Transform.Tokenizing;

/// <summary>
/// Concrete implementation of <see cref="ISourceTokenizer"/> covering CSV and XML source files.
///
/// <list type="bullet">
///   <item>
///     <b>CSV</b> — every cell in every row (including the header row) is emitted as a token
///     with id <c>"cell:r{row}c{col}"</c> (1-based). The header row cell labels are included
///     in the human-readable label. Supports comma- and semicolon-delimited files (same delimiter
///     detection as <c>CsvOrderParser</c>).
///   </item>
///   <item>
///     <b>XML</b> — every leaf element text node and every attribute value is emitted.
///     The id is the XPath from the document root, with 1-based position predicates on
///     repeated element names, e.g. <c>"/Order/Lines/Line[2]/Qty"</c>.
///   </item>
///   <item>
///     <b>All other formats</b> — returns an empty list (tokenisation not yet implemented).
///     This is a deliberate no-op: the engine never throws for an unsupported format; the
///     SourceMap simply falls through to the existing parsed value for any field whose
///     SourceToken id cannot be resolved.
///   </item>
/// </list>
/// </summary>
public sealed class SourceTokenizer : ISourceTokenizer
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<SourceToken>> TokenizeAsync(
        byte[] sourceBytes,
        string fileExtension,
        CancellationToken ct = default)
    {
        var ext = (fileExtension ?? string.Empty).Trim().ToLowerInvariant();

        IReadOnlyList<SourceToken> result = ext switch
        {
            ".csv" => TokenizeCsv(sourceBytes),
            ".xml" => TokenizeXml(sourceBytes),
            // Tokenisation not yet implemented for other formats (XLSX, PDF, EDI, cXML…).
            // Return an empty list so downstream code falls through to the existing parsed value.
            _ => Array.Empty<SourceToken>(),
        };

        return Task.FromResult(result);
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SourceToken> TokenizeCsv(byte[] bytes)
    {
        // Detect delimiter using the same peek logic as CsvOrderParser.
        var delimiter = DetectCsvDelimiter(bytes);

        var config = new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
        {
            Delimiter         = delimiter,
            HeaderValidated   = null!,
            MissingFieldFound = null!,
            HasHeaderRecord   = false, // read raw rows so we control row/col addressing
        };

        var tokens  = new List<SourceToken>();
        var headers = new List<string>(); // column header labels (from row 1)
        int rowIndex = 0;

        using var ms     = new MemoryStream(bytes);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var csv    = new CsvReader(reader, config);

        while (csv.Read())
        {
            rowIndex++;
            var fieldCount = csv.Parser.Count;

            for (int col = 0; col < fieldCount; col++)
            {
                var value = csv.GetField(col) ?? string.Empty;
                var colLabel = col < headers.Count ? headers[col] : $"col{col + 1}";

                if (rowIndex == 1)
                {
                    // First row is the header; capture it for labelling subsequent rows.
                    headers.Add(value);
                    var label = string.IsNullOrWhiteSpace(value)
                        ? $"Header col {col + 1}"
                        : $"Header: {value}";
                    tokens.Add(new SourceToken(
                        Id:    $"cell:r{rowIndex}c{col + 1}",
                        Label: label,
                        Value: value,
                        Group: "header"
                    ));
                }
                else
                {
                    tokens.Add(new SourceToken(
                        Id:    $"cell:r{rowIndex}c{col + 1}",
                        Label: $"Row {rowIndex}, {colLabel}",
                        Value: value,
                        Group: "line"
                    ));
                }
            }
        }

        return tokens;
    }

    private static string DetectCsvDelimiter(byte[] bytes)
    {
        using var ms     = new MemoryStream(bytes);
        using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var firstLine = reader.ReadLine() ?? string.Empty;
        return firstLine.Contains(';') && !firstLine.Contains(',') ? ";" : ",";
    }

    // ── XML ───────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SourceToken> TokenizeXml(byte[] bytes)
    {
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(bytes);
            doc = XDocument.Load(ms, LoadOptions.None);
        }
        catch (XmlException)
        {
            // Malformed XML — return empty, caller sees no tokens.
            return Array.Empty<SourceToken>();
        }

        var tokens = new List<SourceToken>();
        if (doc.Root is null) return tokens;

        // Walk the document depth-first; emit leaf text + all attributes.
        WalkXmlElement(doc.Root, path: "/" + XmlLocalName(doc.Root), tokens: tokens);
        return tokens;
    }

    private static void WalkXmlElement(XElement element, string path, List<SourceToken> tokens)
    {
        // Emit attributes on this element.
        foreach (var attr in element.Attributes())
        {
            var attrPath = $"{path}/@{attr.Name.LocalName}";
            tokens.Add(new SourceToken(
                Id:    attrPath,
                Label: $"{path}/@{attr.Name.LocalName}",
                Value: attr.Value,
                Group: null
            ));
        }

        var children = element.Elements().ToList();

        if (children.Count == 0)
        {
            // Leaf element — emit its text value.
            var text = element.Value ?? string.Empty;
            tokens.Add(new SourceToken(
                Id:    path,
                Label: path,
                Value: text,
                Group: null
            ));
        }
        else
        {
            // Group children by local name so we can add positional predicates for repeating siblings.
            var nameGroups = children
                .GroupBy(c => c.Name.LocalName)
                .ToDictionary(g => g.Key, g => g.Count());

            // Track per-name occurrence index for stable predicate assignment.
            var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var child in children)
            {
                var localName  = child.Name.LocalName;
                var isRepeated = nameGroups[localName] > 1;

                if (!nameIndex.TryGetValue(localName, out var idx))
                    idx = 0;
                nameIndex[localName] = idx + 1;

                var childPath = isRepeated
                    ? $"{path}/{localName}[{idx + 1}]"
                    : $"{path}/{localName}";

                WalkXmlElement(child, childPath, tokens);
            }
        }
    }

    private static string XmlLocalName(XElement element) => element.Name.LocalName;
}
