namespace ProcuLink.Core.Entities;

/// <summary>One person's opt-in to the document digest email. Both switches default off.</summary>
public class NotificationSetting
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    /// <summary>The Clerk user id (the session sub); production creates no AppUser rows.</summary>
    public string ClerkUserId { get; set; } = string.Empty;
    /// <summary>Where the digest goes; remembered from the session email claim, which only requests carry.</summary>
    public string? Email { get; set; }
    public bool OnReview { get; set; }
    public bool OnFailure { get; set; }
    /// <summary>The digest covers events after this; set when a switch turns on so older events never mail.</summary>
    public DateTime? LastSentAt { get; set; }
}
