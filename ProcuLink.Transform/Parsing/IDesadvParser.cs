namespace ProcuLink.Transform.Parsing;

public interface IDesadvParser
{
    bool CanParse(string fileExtension, string? contentType = null);
    Task<ParsedAsn> ParseAsync(Stream fileStream, CancellationToken ct);
}
