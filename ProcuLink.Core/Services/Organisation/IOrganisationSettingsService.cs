namespace ProcuLink.Core.Services.Organisation;

public interface IOrganisationSettingsService
{
    Task<OrgSettingsResponse> GetAsync(Guid orgId, CancellationToken ct);

    Task<OrgSettingsResponse> UpdateDirectionAsync(
        Guid orgId,
        UpdateOrderDirectionRequest req,
        CancellationToken ct);
}
