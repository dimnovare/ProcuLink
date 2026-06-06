using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services.Organisation;

namespace ProcuLink.Infrastructure.Services;

public sealed class OrganisationSettingsService : IOrganisationSettingsService
{
    private readonly ProcuLinkDbContext _db;

    public OrganisationSettingsService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    public async Task<OrgSettingsResponse> GetAsync(Guid orgId, CancellationToken ct)
    {
        var direction = await _db.Organisations
            .AsNoTracking()
            .Where(x => x.Id == orgId)
            .Select(x => x.OrderDirection)
            .FirstOrDefaultAsync(ct);

        return new OrgSettingsResponse(direction);
    }

    public async Task<OrgSettingsResponse> UpdateDirectionAsync(
        Guid orgId,
        UpdateOrderDirectionRequest req,
        CancellationToken ct)
    {
        var org = await _db.Organisations
            .Where(x => x.Id == orgId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");

        org.OrderDirection = req.Direction;
        await _db.SaveChangesAsync(ct);

        return new OrgSettingsResponse(org.OrderDirection);
    }
}
