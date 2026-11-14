using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Core.Services.Google;

/// <summary>Exactly one of <c>Token</c> and <c>Failure</c> is set.</summary>
public sealed record LinkedAccessToken(string? Token, DeliveryResult? Failure);

/// <summary>Mints a fresh access token per call (R7): no cache, nothing written except a revoked account's status.</summary>
public interface ILinkedAccountTokens
{
    Task<LinkedAccessToken> GetGoogleAccessTokenAsync(Guid orgId, Guid accountId, CancellationToken ct);
}
