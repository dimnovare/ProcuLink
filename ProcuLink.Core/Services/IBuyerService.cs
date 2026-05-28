using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IBuyerService
{
    Task<IReadOnlyList<BuyerSummary>> ListAsync(Guid orgId, CancellationToken ct);
    Task<Buyer> CreateAsync(Guid orgId, string name, string code, CancellationToken ct);
    Task<Buyer?> UpdateAsync(Guid orgId, Guid id, string name, string code, CancellationToken ct);
    Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct);
}

public record BuyerSummary(
    Guid Id, string Name, string Code,
    int OrderCount, string? LastOrderAge, string[] Formats);
