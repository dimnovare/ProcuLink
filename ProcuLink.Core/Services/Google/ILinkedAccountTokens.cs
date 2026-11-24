using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Core.Services.Google;

/// <summary>Exactly one of <c>Token</c> and <c>Failure</c> is set.</summary>
public sealed record LinkedAccessToken(string? Token, DeliveryResult? Failure);

/// <summary>Mints a fresh access token per call (R7): no cache. It writes only a revoked account's status and a rotated refresh token.</summary>
public interface ILinkedAccountTokens
{
    /// <param name="provider">The account's <c>LinkedAccount.Provider</c>; an account of another provider is not found.</param>
    Task<LinkedAccessToken> GetAccessTokenAsync(Guid orgId, Guid accountId, string provider, CancellationToken ct);
}
