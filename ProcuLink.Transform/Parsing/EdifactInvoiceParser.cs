namespace ProcuLink.Transform.Parsing;

/// <summary>
/// EDIFACT INVOIC parser — stub implementation.
/// Requires EdiFabric licence (https://support.edifabric.com/).
/// CanParse returns true for .edi files so routing works correctly.
/// ParseAsync always throws NotImplementedException until licence is added.
/// </summary>
public sealed class EdifactInvoiceParser : IInvoiceParser
{
    public bool CanParse(string fileExtension, string? contentType = null)
        => fileExtension.Equals(".edi", StringComparison.OrdinalIgnoreCase)
        || (contentType?.Contains("edifact", StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<ParsedInvoice> ParseAsync(Stream fileStream, CancellationToken ct)
        => throw new NotImplementedException(
            "EDIFACT INVOIC parsing requires an EdiFabric licence. " +
            "Obtain a licence from https://support.edifabric.com/, add the EdiFabric.Core " +
            "and EdiFabric.Templates.Edifact packages, then implement this method. " +
            "See docs/integrations/SUBMISSION.md for roadmap context.");
}
