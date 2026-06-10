using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V1 active-revision resolver. Reads the supplier's <c>SupplierConnection.ActiveRevisionId</c>
/// (the published, live pointer) so it can be stamped onto an order at ingest. Org-scoped.
/// </summary>
public sealed class ConnectionResolver : IConnectionResolver
{
    private readonly ProcuLinkDbContext _db;
    public ConnectionResolver(ProcuLinkDbContext db) => _db = db;

    public async Task<Guid?> ResolveActiveRevisionAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierConnections
            .Where(c => c.OrgId == orgId && c.SupplierId == supplierId)
            .Select(c => c.ActiveRevisionId)
            .FirstOrDefaultAsync(ct);
}
