using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class ValidationRuleService : IValidationRuleService
{
    private readonly ProcuLinkDbContext _db;

    public ValidationRuleService(ProcuLinkDbContext db) { _db = db; }

    public Task<IReadOnlyList<ValidationRule>> ListAsync(Guid orgId, CancellationToken ct) => throw new NotImplementedException();
    public Task<ValidationRule> CreateAsync(Guid orgId, CreateRuleRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<ValidationRule?> UpdateAsync(Guid orgId, Guid id, CreateRuleRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<ValidationRule?> ToggleAsync(Guid orgId, Guid id, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct) => throw new NotImplementedException();
}
