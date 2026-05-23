namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Selects the correct <see cref="IPurchaseOrderParser"/> for a given file extension.
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
    /// Returns a parser that can handle <paramref name="fileExtension"/>.
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
}
