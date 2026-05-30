namespace ProcuLink.Api.Services.StarterTemplates;

/// <summary>
/// Provides access to the static, read-only PO mapping starter templates shipped
/// with the API. Templates are loaded from embedded JSON fixtures once and cached.
/// </summary>
public interface IStarterTemplateService
{
    /// <summary>Returns all available starter templates.</summary>
    IReadOnlyList<StarterTemplateDto> GetAll();
}
