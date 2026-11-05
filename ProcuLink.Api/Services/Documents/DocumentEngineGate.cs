using Microsoft.Extensions.Options;

namespace ProcuLink.Api.Services.Documents;

public sealed class DocumentEngineOptions
{
    public string[] EnabledOrgIds { get; set; } = [];
    public bool EnabledForAll { get; set; }
}

/// <summary>Per-org switch for the document engine; off unless listed or enabled for all.</summary>
public sealed class DocumentEngineGate(IOptions<DocumentEngineOptions> options)
{
    public bool IsEnabled(Guid orgId) =>
        options.Value.EnabledForAll ||
        options.Value.EnabledOrgIds.Any(s => Guid.TryParse(s, out var g) && g == orgId);
}
