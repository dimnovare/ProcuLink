namespace ProcuLink.Core.Entities;

public class Membership
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}
