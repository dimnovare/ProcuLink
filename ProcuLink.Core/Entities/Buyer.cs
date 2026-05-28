namespace ProcuLink.Core.Entities;

public class Buyer
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Short uppercase identifier, e.g. "HEI".</summary>
    public string Code { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
}
