namespace ProcuLink.Core.Entities;

/// <summary>One way documents arrive in a <see cref="Flow"/>: its email address, file API, or a pickup from a server or mailbox.</summary>
public class FlowIntake
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid FlowId { get; set; }

    /// <summary>See <c>IntakeKind</c>.</summary>
    public string Kind { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
    /// <summary>AES-GCM envelope bound to <c>CredentialPurpose.FlowIntakeCredentials</c>; never returned by the API.</summary>
    public string? EncryptedCredentials { get; set; }
    public bool IsEnabled { get; set; } = true;

    public DateTime? LastRunAt { get; set; }
    /// <summary><c>ok</c>, <c>paused</c> or <c>failed</c>.</summary>
    public string? LastStatus { get; set; }
    /// <summary>User-facing; printed as is.</summary>
    public string? LastMessage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
