namespace ProcuLink.Core.Entities;

public class TenantApiKey
{
    public Guid      Id             { get; set; }
    public Guid      OrganisationId { get; set; }
    public string    Label          { get; set; } = string.Empty;
    /// <summary>HMAC-SHA256 hash — see ApiKeyHasher.ComputeHash.</summary>
    public string    KeyHash        { get; set; } = string.Empty;
    /// <summary>First 8 chars of raw key (e.g. "plk_abcd") for display in lists.</summary>
    public string    KeyPrefix      { get; set; } = string.Empty;
    public bool      IsActive       { get; set; } = true;
    public DateTime  CreatedAt      { get; set; }
    public DateTime? LastUsedAt     { get; set; }
    public DateTime? ExpiresAt      { get; set; }
    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
