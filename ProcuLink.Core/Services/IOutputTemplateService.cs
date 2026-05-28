using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IOutputTemplateService
{
    Task<IReadOnlyList<OutputTemplate>> ListAsync(Guid orgId, CancellationToken ct);
    Task<OutputTemplate> CreateAsync(Guid orgId, CreateTemplateRequest req, CancellationToken ct);
    Task<OutputTemplate?> UpdateAsync(Guid orgId, Guid id, CreateTemplateRequest req, CancellationToken ct);
    Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct);
}

public record CreateTemplateRequest(
    string Name, string Format, string Version,
    System.Text.Json.JsonDocument? ConfigJson);
