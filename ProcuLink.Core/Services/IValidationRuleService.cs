using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IValidationRuleService
{
    Task<IReadOnlyList<ValidationRule>> ListAsync(Guid orgId, CancellationToken ct);
    Task<ValidationRule> CreateAsync(Guid orgId, CreateRuleRequest req, CancellationToken ct);
    Task<ValidationRule?> UpdateAsync(Guid orgId, Guid id, CreateRuleRequest req, CancellationToken ct);
    Task<ValidationRule?> ToggleAsync(Guid orgId, Guid id, CancellationToken ct);
    Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct);
}

public record CreateRuleRequest(
    string Name, string Description, string Severity,
    string Entity, bool Enabled, bool AutoBlock);
