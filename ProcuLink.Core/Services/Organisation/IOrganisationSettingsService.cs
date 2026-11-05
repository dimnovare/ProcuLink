namespace ProcuLink.Core.Services.Organisation;

public interface IOrganisationSettingsService
{
    Task<OrgSettingsResponse> GetAsync(Guid orgId, CancellationToken ct);

    Task<OrgSettingsResponse> UpdateAsync(
        Guid orgId,
        UpdateOrgSettingsRequest req,
        CancellationToken ct);
}
