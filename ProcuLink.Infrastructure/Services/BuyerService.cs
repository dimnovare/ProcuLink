using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class BuyerService : IBuyerService
{
    private readonly ProcuLinkDbContext _db;

    public BuyerService(ProcuLinkDbContext db) { _db = db; }

    public Task<IReadOnlyList<BuyerSummary>> ListAsync(Guid orgId, CancellationToken ct) => throw new NotImplementedException();
    public Task<Buyer> CreateAsync(Guid orgId, string name, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<Buyer?> UpdateAsync(Guid orgId, Guid id, string name, string code, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct) => throw new NotImplementedException();
}
