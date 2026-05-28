namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Selects the correct <see cref="IDesadvParser"/> for a given file extension.
/// </summary>
public sealed class DesadvParserFactory
{
    private readonly IEnumerable<IDesadvParser> _parsers;

    public DesadvParserFactory(IEnumerable<IDesadvParser> parsers)
        => _parsers = parsers;

    public IDesadvParser GetParser(string fileExtension)
    {
        var parser = _parsers.FirstOrDefault(p =>
            p.CanParse(fileExtension.ToLowerInvariant()));
        if (parser is null)
            throw new InvalidOperationException(
                $"No DESADV parser supports '{fileExtension}'. Supported: .edi (requires EdiFabric licence).");
        return parser;
    }
}
