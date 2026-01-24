using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public interface ISupplierProfileRepository
{
    Task<SupplierProfile?> GetByNameAsync(string supplierName, CancellationToken ct = default);
    Task<IReadOnlyList<SupplierProfile>> ListAsync(CancellationToken ct = default);
}
