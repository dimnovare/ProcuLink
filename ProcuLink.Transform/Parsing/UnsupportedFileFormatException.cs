namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Thrown when <see cref="OrderParserFactory"/> receives a file extension
/// for which no registered <see cref="IPurchaseOrderParser"/> returns true from
/// <see cref="IPurchaseOrderParser.CanParse"/>.
/// </summary>
public sealed class UnsupportedFileFormatException : Exception
{
    public string FileExtension { get; }

    public UnsupportedFileFormatException(string fileExtension)
        : base($"No parser is registered for file extension '{fileExtension}'. Supported formats: .csv, .xlsx")
    {
        FileExtension = fileExtension;
    }
}
