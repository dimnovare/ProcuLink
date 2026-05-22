namespace ProcuLink.Core.Entities;

public class AppUser
{
    public Guid Id { get; set; }
    public string ClerkUserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public List<Membership> Memberships { get; set; } = new();
    public List<AuditEvent> AuditEvents { get; set; } = new();
}
