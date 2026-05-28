using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IApiKeyService
{
    /// <summary>
    /// Creates a new API key. Returns (entity, rawKey).
    /// Raw key is shown once to the user — never stored.
    /// </summary>
    Task<(TenantApiKey Key, string RawKey)> CreateAsync(
        Guid organisationId, string label, DateTime? expiresAt, CancellationToken ct);

    Task<IReadOnlyList<TenantApiKey>> ListAsync(Guid organisationId, CancellationToken ct);

    /// <summary>Soft-delete: sets IsActive = false. Returns false if key not found or wrong org.</summary>
    Task<bool> RevokeAsync(Guid organisationId, Guid keyId, CancellationToken ct);
}
