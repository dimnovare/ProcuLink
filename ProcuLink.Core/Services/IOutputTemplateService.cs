using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IOutputTemplateService
{
    Task<IReadOnlyList<OutputTemplate>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Lists the org's output templates together with the real number of suppliers
    /// currently using each template. A supplier "uses" a template when that supplier's
    /// delivery config requires the same output format as the template (case-insensitive).
    /// This is the only supplier↔template relationship the store models — there is no
    /// direct foreign key — so the count is derived from <c>supplier_delivery_configs.output_format</c>.
    /// </summary>
    Task<IReadOnlyList<TemplateView>> ListWithUsageAsync(Guid orgId, CancellationToken ct);

    Task<OutputTemplate> CreateAsync(Guid orgId, CreateTemplateRequest req, CancellationToken ct);
    Task<OutputTemplate?> UpdateAsync(Guid orgId, Guid id, CreateTemplateRequest req, CancellationToken ct);
    Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct);
}

public record CreateTemplateRequest(
    string Name, string Format, string Version,
    System.Text.Json.JsonDocument? ConfigJson);

/// <summary>
/// A stored output template paired with the live count of suppliers using its format.
/// </summary>
public record TemplateView(OutputTemplate Template, int SuppliersCount);
