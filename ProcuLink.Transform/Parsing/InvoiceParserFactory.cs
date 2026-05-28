namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Selects the correct <see cref="IInvoiceParser"/> for a given file extension and stream.
/// For .xml files, peeks the stream to distinguish UBL Invoice from other XML formats.
/// </summary>
public sealed class InvoiceParserFactory
{
    private readonly IEnumerable<IInvoiceParser> _parsers;

    public InvoiceParserFactory(IEnumerable<IInvoiceParser> parsers)
        => _parsers = parsers;

    /// <summary>Get parser by extension. For .xml files, also inspects the stream.</summary>
    public IInvoiceParser GetParser(string fileExtension, Stream? stream = null)
    {
        var ext = fileExtension.ToLowerInvariant();

        // For XML, peek the stream to confirm it's a UBL Invoice (not a UBL Order etc.)
        if (ext == ".xml" && stream is not null && stream.CanSeek)
        {
            if (UblInvoiceParser.IsUblInvoiceDocument(stream))
                return _parsers.OfType<UblInvoiceParser>().First();
            throw new InvoiceParseException(
                "XML file is not a UBL 2.1 Invoice document (root must be <Invoice> in UBL Invoice namespace).");
        }

        var parser = _parsers.FirstOrDefault(p => p.CanParse(ext));
        if (parser is null)
            throw new InvoiceParseException(
                $"No invoice parser supports '{fileExtension}'. Supported: .xml (UBL 2.1), .edi (requires EdiFabric licence).");
        return parser;
    }
}
