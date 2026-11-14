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
    ProcuLinkDbContext db, DeliveryEncryptionService encryption, IGoogleOAuthClient google, ILogger<LinkedAccountTokens> logger)
    : ILinkedAccountTokens
{
    public const string ReconnectMessage = "Google access was removed. Reconnect the Google account.";
    public const string UnavailableMessage = "Google could not be reached.";

    private static CredentialScope Scope(LinkedAccount a) => new(a.OrgId, CredentialPurpose.LinkedAccountTokens, a.Id);

    /// <summary>Encrypts <paramref name="refreshToken"/> into <see cref="LinkedAccount.EncryptedTokens"/>; the account's Id and OrgId must be set.</summary>
    public static void SealRefreshToken(DeliveryEncryptionService encryption, LinkedAccount account, string refreshToken) =>
        account.EncryptedTokens = encryption.Encrypt(JsonSerializer.Serialize(new { refreshToken }), Scope(account));

    public static string ReadRefreshToken(DeliveryEncryptionService encryption, LinkedAccount account)
    {
        using var doc = JsonDocument.Parse(encryption.Decrypt(account.EncryptedTokens, Scope(account)));
        return doc.RootElement.GetProperty("refreshToken").GetString()!;
    }

    public async Task<LinkedAccessToken> GetGoogleAccessTokenAsync(Guid orgId, Guid accountId, CancellationToken ct)
    {
        var account = await db.LinkedAccounts.FirstOrDefaultAsync(
            a => a.OrgId == orgId && a.Id == accountId && a.Provider == LinkedAccountProviders.Google, ct);
        if (account is null || account.Status != LinkedAccountStatus.Active) return Failed(ReconnectMessage, DeliveryOutcome.NotRetryable);

        GoogleRefresh refresh;
        try { refresh = await google.RefreshAsync(ReadRefreshToken(encryption, account), ct); }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Google token refresh failed for linked account {AccountId} (HTTP {Status}).", accountId, (int?)ex.StatusCode);
            return Failed(UnavailableMessage, DeliveryOutcome.Dispatched, (int?)ex.StatusCode);
        }
        catch (CredentialUnbindableException) { refresh = new GoogleRefresh(null, Revoked: true); } // only a reconnect re-seals it
        if (refresh.AccessToken is { } token) return new LinkedAccessToken(token, null);

        // Delivery dispatches with no pending changes on this scoped context, so this saves only the status.
        account.Status = LinkedAccountStatus.Reconnect;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Failed(ReconnectMessage, DeliveryOutcome.NotRetryable);
    }

    private static LinkedAccessToken Failed(string message, DeliveryOutcome outcome, int? code = null) =>
        new(null, new DeliveryResult(false, message, code, Outcome: outcome));
}
