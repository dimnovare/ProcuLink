namespace ProcuLink.Core.Services.Email;

public interface IEmailSettingsService
{
    Task<EmailSettingsResponse> GetAsync(Guid orgId, CancellationToken ct);

    Task<EmailSettingsResponse> UpdateAsync(
        Guid orgId,
        UpdateEmailSettingsRequest request,
        CancellationToken ct);

    Task MarkPolledAsync(Guid orgId, DateTime polledAt, CancellationToken ct);
}
