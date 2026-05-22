using System.Text.Json;

namespace ProcuLink.Core.Entities;

public class AuditEvent
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }

    /// <summary>Null for system-generated events that have no user actor.</summary>
    public Guid? UserId { get; set; }

    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public JsonDocument? Payload { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public AppUser? User { get; set; }
}
