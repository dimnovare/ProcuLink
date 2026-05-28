namespace ProcuLink.Transform.Parsing;

public interface IInvoiceParser
{
    bool CanParse(string fileExtension, string? contentType = null);
    Task<ParsedInvoice> ParseAsync(Stream fileStream, CancellationToken ct);
}
