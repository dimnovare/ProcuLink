namespace ProcuLink.Core.Entities;

/// <summary>
/// A third-party account an organisation connected once and its destinations reuse (plan 9b: Google).
/// <see cref="EncryptedTokens"/> holds <c>{"refreshToken":"…"}</c> under
/// <c>CredentialPurpose.LinkedAccountTokens</c>, scoped to <see cref="Id"/>; it is never returned.
/// Unique per (OrgId, Provider, Subject): reconnecting the same account updates this row.
/// </summary>
public class LinkedAccount
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Provider { get; set; } = LinkedAccountProviders.Google;
    /// <summary>The provider's stable user id (Google <c>sub</c>).</summary>
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string EncryptedTokens { get; set; } = "";
    public string Status { get; set; } = LinkedAccountStatus.Active;
    /// <summary>The Clerk user id of whoever connected it; production creates no AppUser rows.</summary>
    public string CreatedByUserId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Persisted values: never rename.</summary>
public static class LinkedAccountProviders
{
    public const string Google = "google";
}

/// <summary>Persisted values: never rename.</summary>
public static class LinkedAccountStatus
{
    public const string Active = "active";
    /// <summary>The provider refused the refresh token (access removed); only a reconnect heals it.</summary>
    public const string Reconnect = "reconnect";
}
