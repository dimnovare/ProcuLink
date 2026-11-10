namespace ProcuLink.Core.Entities;

/// <summary>One person's opt-in to the document digest email. Both switches default off.</summary>
public class NotificationSetting
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    /// <summary><see cref="AppUser.Id"/>.</summary>
    public Guid UserId { get; set; }
    public bool OnReview { get; set; }
    public bool OnFailure { get; set; }
    /// <summary>The digest covers events after this; set when a switch turns on so older events never mail.</summary>
    public DateTime? LastSentAt { get; set; }
}
