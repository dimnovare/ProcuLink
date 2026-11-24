using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services.Google;

/// <summary>R7: refreshes on every call. The only write is flipping a revoked account to <c>reconnect</c>.</summary>
public sealed class LinkedAccountTokens(
    ProcuLinkDbContext db, DeliveryEncryptionService encryption, IEnumerable<ILinkedAccountOAuth> oauth, ILogger<LinkedAccountTokens> logger)
    : ILinkedAccountTokens
{
    public static string ReconnectMessage(string provider) =>
        $"{LinkedAccountProviders.DisplayName(provider)} access was removed. Reconnect the {LinkedAccountProviders.DisplayName(provider)} account.";

    public static string UnavailableMessage(string provider) => $"{LinkedAccountProviders.DisplayName(provider)} could not be reached.";

    private static CredentialScope Scope(LinkedAccount a) => new(a.OrgId, CredentialPurpose.LinkedAccountTokens, a.Id);

    /// <summary>Encrypts <paramref name="refreshToken"/> into <see cref="LinkedAccount.EncryptedTokens"/>; the account's Id and OrgId must be set.</summary>
    public static void SealRefreshToken(DeliveryEncryptionService encryption, LinkedAccount account, string refreshToken) =>
        account.EncryptedTokens = encryption.Encrypt(JsonSerializer.Serialize(new { refreshToken }), Scope(account));

    public static string ReadRefreshToken(DeliveryEncryptionService encryption, LinkedAccount account)
    {
        using var doc = JsonDocument.Parse(encryption.Decrypt(account.EncryptedTokens, Scope(account)));
        return doc.RootElement.GetProperty("refreshToken").GetString()!;
    }

    public async Task<LinkedAccessToken> GetAccessTokenAsync(Guid orgId, Guid accountId, string provider, CancellationToken ct)
    {
        var account = await db.LinkedAccounts.FirstOrDefaultAsync(
            a => a.OrgId == orgId && a.Id == accountId && a.Provider == provider, ct);
        var client = oauth.FirstOrDefault(o => o.Provider == provider);
        if (account is null || client is null || account.Status != LinkedAccountStatus.Active)
            return Failed(ReconnectMessage(provider), DeliveryOutcome.NotRetryable);

        OAuthRefresh refresh;
        try { refresh = await client.RefreshAsync(ReadRefreshToken(encryption, account), ct); }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{Provider} token refresh failed for linked account {AccountId} (HTTP {Status}).", provider, accountId, (int?)ex.StatusCode);
            return Failed(UnavailableMessage(provider), DeliveryOutcome.Dispatched, (int?)ex.StatusCode);
        }
        catch (CredentialUnbindableException) { refresh = new OAuthRefresh(null, Revoked: true); } // only a reconnect re-seals it
        if (refresh.AccessToken is { } token) return new LinkedAccessToken(token, null);

        // Delivery dispatches with no pending changes on this scoped context, so this saves only the status.
        account.Status = LinkedAccountStatus.Reconnect;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Failed(ReconnectMessage(provider), DeliveryOutcome.NotRetryable);
    }

    private static LinkedAccessToken Failed(string message, DeliveryOutcome outcome, int? code = null) =>
        new(null, new DeliveryResult(false, message, code, Outcome: outcome));
}
