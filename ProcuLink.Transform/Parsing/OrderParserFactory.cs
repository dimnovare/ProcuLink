namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Selects the correct <see cref="IPurchaseOrderParser"/> for a given file extension
/// or — when the extension alone is ambiguous (e.g. <c>.xml</c> for UBL vs cXML, or
/// <c>.txt</c> for EDIFACT vs free text) — by peeking the content stream.
/// Parsers are injected via DI as <c>IEnumerable&lt;IPurchaseOrderParser&gt;</c>.
/// </summary>
public sealed class OrderParserFactory
{
    private readonly IEnumerable<IPurchaseOrderParser> _parsers;

    public OrderParserFactory(IEnumerable<IPurchaseOrderParser> parsers)
    {
        _parsers = parsers;
    }

    /// <summary>
    /// Returns a parser that can handle <paramref name="fileExtension"/> by extension only.
    /// Use this for validation paths where no stream is available; for actual parsing,
    /// prefer the stream-aware overload <see cref="GetParser(string, Stream)"/>.
    /// </summary>
    /// <exception cref="UnsupportedFileFormatException">
    /// Thrown when no registered parser supports the extension.
    /// </exception>
    public IPurchaseOrderParser GetParser(string fileExtension)
    {
        var parser = _parsers.FirstOrDefault(p => p.CanParse(fileExtension));
        if (parser is null)
            throw new UnsupportedFileFormatException(fileExtension);

        return parser;
    }

    /// <summary>
    /// Returns a parser that can handle the given file, disambiguating same-extension
    /// formats by peeking the first bytes of <paramref name="peek"/>.
    ///
    /// Disambiguation rules:
    /// <list type="bullet">
    /// <item><c>.xml</c> — distinguishes UBL Order from cXML by root local-name + namespace.</item>
    /// <item><c>.txt</c> — promotes to EDIFACT when content starts with <c>UNA</c> or <c>UNB</c>.</item>
    /// </list>
    /// The stream position is restored before returning regardless of outcome.
    /// </summary>
    /// <exception cref="UnsupportedFileFormatException">
    /// Thrown when no registered parser supports the file.
    /// </exception>
    public IPurchaseOrderParser GetParser(string fileExtension, Stream peek)
    {
        var ext = (fileExtension ?? string.Empty).ToLowerInvariant();

        // ── .xml — UBL vs cXML disambiguation ─────────────────────────────────
        if (ext == ".xml" && peek.CanSeek)
        {
            if (UblOrderParser.IsUblOrderDocument(peek))
            {
                var ubl = _parsers.OfType<UblOrderParser>().FirstOrDefault();
                if (ubl is not null) return ubl;
            }
            // Fall through to extension dispatch — CxmlOrderParser handles plain .xml
        }

        // ── .txt — promote to EDIFACT when content matches ────────────────────
        if (ext == ".txt" && peek.CanSeek)
        {
            var startPos = peek.Position;
            var head = ReadHead(peek, 16);
            peek.Position = startPos;
            if (EdifactOrderParser.LooksLikeEdifact(head))
            {
                var edi = _parsers.OfType<EdifactOrderParser>().FirstOrDefault();
                if (edi is not null) return edi;
            }
        }

        // ── Fallback: extension-based dispatch ────────────────────────────────
        return GetParser(ext);
    }

    /// <summary>
    /// Reads up to <paramref name="bytes"/> bytes from <paramref name="s"/> and decodes
    /// them as UTF-8 without advancing past the requested window. Intended only for
    /// content sniffing — never returns more than the requested bytes, never throws on
    /// short reads.
    /// </summary>
    private static string ReadHead(Stream s, int bytes)
    {
        var buf = new byte[bytes];
        var read = s.Read(buf, 0, bytes);
        return System.Text.Encoding.UTF8.GetString(buf, 0, read);
    }
}
