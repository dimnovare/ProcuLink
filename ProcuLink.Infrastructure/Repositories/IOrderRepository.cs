using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public interface IOrderRepository
{
    Task SaveAsync(PurchaseOrder po, CancellationToken ct = default);
    Task<PurchaseOrder?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseOrder>> ListAsync(CancellationToken ct = default);
}
