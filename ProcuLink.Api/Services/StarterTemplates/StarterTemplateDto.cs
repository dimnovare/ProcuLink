using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Api.Services.StarterTemplates;

/// <summary>
/// A read-only pre-built PO mapping starter template. Shipped as a JSON fixture
/// embedded in the API assembly — no DB required.
/// </summary>
public sealed record StarterTemplateDto
{
    /// <summary>Stable kebab-case identifier, e.g. "erply".</summary>
    public required string Id { get; init; }
    /// <summary>ERP brand name, e.g. "Erply".</summary>
    public required string Erp { get; init; }
    /// <summary>Short display name shown in the template picker.</summary>
    public required string Name { get; init; }
    /// <summary>One-sentence description shown below the name.</summary>
    public required string Description { get; init; }
    /// <summary>The ready-to-apply mapping config — load into PoMappingEditor on select.</summary>
    public required PoMappingConfig Config { get; init; }
}
