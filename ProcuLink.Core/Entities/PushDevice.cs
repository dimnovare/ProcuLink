namespace ProcuLink.Core.Entities;

/// <summary>One person's browser push subscription for phone alerts, in one workspace.</summary>
public class PushDevice
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string ClerkUserId { get; set; } = string.Empty;
    /// <summary>The push service address the browser handed us. Never logged.</summary>
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    /// <summary>Alerts cover events after this, so a newly saved device sees nothing older.</summary>
    public DateTime LastCheckedAt { get; set; }
    /// <summary>The UTC day the daily summary was last considered for this device.</summary>
    public DateOnly? SummaryOn { get; set; }
}
