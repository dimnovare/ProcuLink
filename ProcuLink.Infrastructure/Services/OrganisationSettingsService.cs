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
        var settings = await _db.Organisations
            .AsNoTracking()
            .Where(x => x.Id == orgId)
            .Select(x => new { x.OrderDirection, x.Slug, x.InvoiceDestinationSupplierId })
            .FirstOrDefaultAsync(ct);

        return new OrgSettingsResponse(
            settings?.OrderDirection ?? default,
            settings?.Slug ?? string.Empty,
            settings?.InvoiceDestinationSupplierId);
    }

    /// <summary>Applies only the fields the request carries. The caller has already checked that
    /// an invoice destination is one of this org's live suppliers.</summary>
    public async Task<OrgSettingsResponse> UpdateAsync(
        Guid orgId,
        UpdateOrgSettingsRequest req,
        CancellationToken ct)
    {
        var org = await _db.Organisations
            .Where(x => x.Id == orgId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Organisation {orgId} not found.");

        if (req.Direction is { } direction) org.OrderDirection = direction;
        if (req.InvoiceDestinationSet) org.InvoiceDestinationSupplierId = req.InvoiceDestinationSupplierId;
        await _db.SaveChangesAsync(ct);

        return new OrgSettingsResponse(org.OrderDirection, org.Slug, org.InvoiceDestinationSupplierId);
    }
}
