using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Api.Services.Google;

public sealed record LinkedAccountDto(Guid Id, string Provider, string Email, string Status, DateTimeOffset CreatedAt);

/// <summary>A refused connect step: <c>Status</c> is the HTTP status, <c>Code</c> the body's <c>error</c>.</summary>
public sealed class GoogleConnectException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Connects (R4/R5) and disconnects (R8) an organisation's Google account. The connect state is a
/// 10-minute DataProtection blob binding org, user, PKCE verifier and redirect URI, so no BE endpoint
/// is anonymous and the verifier never reaches the browser in clear.
/// </summary>
public sealed class GoogleConnectService(
    ProcuLinkDbContext db, DeliveryEncryptionService encryption, IGoogleOAuthClient google,
    IDataProtectionProvider protection, IConfiguration config)
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private sealed record State(Guid OrgId, string UserId, string Verifier, string RedirectUri, string? ReturnTo);

    private ITimeLimitedDataProtector Protector() =>
        protection.CreateProtector("linked-accounts.google.state").ToTimeLimitedDataProtector();

    private static LinkedAccountDto ToDto(LinkedAccount a) =>
        new(a.Id, a.Provider, a.Email, a.Status, new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)));

    public async Task<List<LinkedAccountDto>> ListAsync(Guid orgId, CancellationToken ct) =>
        (await db.LinkedAccounts.Where(a => a.OrgId == orgId).OrderBy(a => a.Email).ToListAsync(ct)).Select(ToDto).ToList();

    /// <summary>The Google authorize URL. <paramref name="origin"/> must be one of <c>Frontend:Url</c>'s entries (R5).</summary>
    public string Start(Guid orgId, string userId, string origin, string? returnTo)
    {
        var allowed = (config["Frontend:Url"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.TrimEnd('/'));
        if (!allowed.Contains(origin, StringComparer.Ordinal)) throw new GoogleConnectException(422, "invalid_origin");

        // Same-site paths only: "//host" and "/\host" are both protocol-relative to a browser.
        if (returnTo is null || !returnTo.StartsWith('/') || returnTo.StartsWith("//") || returnTo.StartsWith("/\\")) returnTo = null;
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var redirectUri = origin + "/settings/google-callback";
        var state = Protector().Protect(JsonSerializer.Serialize(new State(orgId, userId, verifier, redirectUri, returnTo)), StateLifetime);
        return google.BuildAuthorizeUrl(redirectUri, state, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <exception cref="GoogleConnectException">400 <c>invalid_state</c>, 422 <c>google_scope_missing</c>.</exception>
    /// <exception cref="GoogleAuthException">Google refused the exchange or could not be reached.</exception>
    public async Task<(LinkedAccountDto Account, string? ReturnTo)> CompleteAsync(
        Guid orgId, string userId, string code, string state, CancellationToken ct)
    {
        var s = ReadState(state);
        if (s is null || s.OrgId != orgId || s.UserId != userId) throw new GoogleConnectException(400, "invalid_state");

        var grant = await google.ExchangeCodeAsync(code, s.RedirectUri, s.Verifier, ct);
        // Granular consent lets the user untick Drive; without it no destination could work, so nothing is saved.
        if (!grant.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(GoogleOAuthClient.DriveFileScope))
            throw new GoogleConnectException(422, "google_scope_missing");
        var user = await google.GetUserAsync(grant.AccessToken, ct);

        var account = await db.LinkedAccounts.FirstOrDefaultAsync(
            a => a.OrgId == orgId && a.Provider == LinkedAccountProviders.Google && a.Subject == user.Subject, ct);
        if (account is null)
        {
            // prompt=consent always returns one; a new account without it would have nothing to refresh with.
            if (grant.RefreshToken is null) throw new GoogleAuthException(GoogleAuthException.Unavailable);
            account = new LinkedAccount { Id = Guid.NewGuid(), OrgId = orgId, Subject = user.Subject, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow };
            db.LinkedAccounts.Add(account);
        }
        // A reconnect that returns no refresh token keeps the stored one (R6: reconnect heals the row).
        if (grant.RefreshToken is { } refreshToken) LinkedAccountTokens.SealRefreshToken(encryption, account, refreshToken);
        account.Email = user.Email;
        account.Status = LinkedAccountStatus.Active;
        account.UpdatedAt = DateTime.UtcNow;
        Audit(account, "linked_account.connected");
        await db.SaveChangesAsync(ct);
        return (ToDto(account), s.ReturnTo);
    }

    /// <summary>Null: no such account in this org. False: a destination still names it (R8). True: revoked at Google (best effort) and deleted.</summary>
    public async Task<bool?> DeleteAsync(Guid orgId, Guid accountId, CancellationToken ct)
    {
        var account = await db.LinkedAccounts.FirstOrDefaultAsync(a => a.OrgId == orgId && a.Id == accountId, ct);
        if (account is null) return null;

        var configs = await db.Set<FlowDestination>()
            .Where(d => d.OrgId == orgId && (d.Protocol == DeliveryProtocolConstants.GoogleDrive || d.Protocol == DeliveryProtocolConstants.GoogleSheets))
            .Select(d => d.ConfigJson)
            .ToListAsync(ct);
        if (configs.Any(c => NamesAccount(c, accountId))) return false;

        string? refreshToken = null;
        try { refreshToken = LinkedAccountTokens.ReadRefreshToken(encryption, account); }
        catch (Exception) { } // an unreadable token cannot be revoked; deleting our copy is the obligation and must not be blocked
        if (refreshToken is not null) await google.RevokeAsync(refreshToken, ct);

        db.LinkedAccounts.Remove(account);
        Audit(account, "linked_account.disconnected");
        await db.SaveChangesAsync(ct);
        return true;
    }

    private State? ReadState(string state)
    {
        try { return JsonSerializer.Deserialize<State>(Protector().Unprotect(state)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException) { return null; }
    }

    private static bool NamesAccount(string configJson, Guid accountId)
    {
        using var doc = JsonDocument.Parse(configJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("accountId", out var v) && v.ValueKind == JsonValueKind.String
            && Guid.TryParse(v.GetString(), out var id) && id == accountId;
    }

    /// <summary>R13: provider only; never the email or a token.</summary>
    private void Audit(LinkedAccount a, string action) => db.AuditEvents.Add(new AuditEvent
    {
        Id = Guid.NewGuid(), OrgId = a.OrgId, EntityType = "LinkedAccount", EntityId = a.Id, Action = action,
        CreatedAt = DateTime.UtcNow, Payload = JsonSerializer.SerializeToDocument(new { provider = a.Provider }),
    });
}
