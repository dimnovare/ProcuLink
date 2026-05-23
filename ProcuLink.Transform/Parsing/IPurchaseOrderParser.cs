namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses a purchase order file stream into a <see cref="ParsedOrder"/>.
/// Implementations are registered in DI and selected by file extension via
/// <see cref="OrderParserFactory"/>.
/// </summary>
public interface IPurchaseOrderParser
{
    /// <summary>Parse the file stream and return the structured order.</summary>
    Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct);

    /// <summary>Returns true if this parser handles the given file extension (e.g. ".csv").</summary>
    bool CanParse(string fileExtension);
}
