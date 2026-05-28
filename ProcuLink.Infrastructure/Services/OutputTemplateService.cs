using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class OutputTemplateService : IOutputTemplateService
{
    private readonly ProcuLinkDbContext _db;

    public OutputTemplateService(ProcuLinkDbContext db) { _db = db; }

    public Task<IReadOnlyList<OutputTemplate>> ListAsync(Guid orgId, CancellationToken ct) => throw new NotImplementedException();
    public Task<OutputTemplate> CreateAsync(Guid orgId, CreateTemplateRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<OutputTemplate?> UpdateAsync(Guid orgId, Guid id, CreateTemplateRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct) => throw new NotImplementedException();
}
