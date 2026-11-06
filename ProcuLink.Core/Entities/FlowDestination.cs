namespace ProcuLink.Core.Entities;

/// <summary>Where a flow's outputs are sent. Credentials are encrypted under <c>CredentialPurpose.FlowDestinationCredentials</c>.</summary>
public class FlowDestination
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
    public string? EncryptedCredentials { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
