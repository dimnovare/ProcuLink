using Microsoft.Extensions.Options;

namespace ProcuLink.Api.Services.Documents;

public sealed class DocumentEngineOptions
{
    public string[] EnabledOrgIds { get; set; } = [];
    public bool EnabledForAll { get; set; }
    public string[] LookupSuggestionOrgIds { get; set; } = [];
    public bool LookupSuggestionsForAll { get; set; }
}

/// <summary>Per-org switch for the document engine; off unless listed or enabled for all.</summary>
public sealed class DocumentEngineGate(IOptions<DocumentEngineOptions> options)
{
    public bool IsEnabled(Guid orgId) =>
        options.Value.EnabledForAll ||
        options.Value.EnabledOrgIds.Any(s => Guid.TryParse(s, out var g) && g == orgId);

    /// <summary>AI-suggested lookup matches: the engine must be on, then the org is listed or all are on.</summary>
    public bool LookupSuggestionsOn(Guid orgId) =>
        IsEnabled(orgId) &&
        (options.Value.LookupSuggestionsForAll ||
         options.Value.LookupSuggestionOrgIds.Any(s => Guid.TryParse(s, out var g) && g == orgId));
}
