namespace ProcuLink.Transform.Parsing;

/// <summary>
/// EDIFACT DESADV parser — stub implementation.
/// Requires EdiFabric licence (https://support.edifabric.com/).
/// </summary>
public sealed class EdifactDesadvParser : IDesadvParser
{
    public bool CanParse(string fileExtension, string? contentType = null)
        => fileExtension.Equals(".edi", StringComparison.OrdinalIgnoreCase)
        || (contentType?.Contains("edifact", StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<ParsedAsn> ParseAsync(Stream fileStream, CancellationToken ct)
        => throw new NotImplementedException(
            "EDIFACT DESADV parsing requires an EdiFabric licence. " +
            "Obtain a licence from https://support.edifabric.com/, add the EdiFabric.Core " +
            "and EdiFabric.Templates.Edifact packages, then implement this method.");
}
