namespace ProcuLink.Core.Entities;

/// <summary>A flow: a named set of fields documents are read into.</summary>
public class Flow
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = "";
    public string FieldsJson { get; set; } = "{}";
    public bool FieldsConfirmed { get; set; }
    /// <summary>A JSON list of <see cref="Documents.FieldHint"/>.</summary>
    public string HintsJson { get; set; } = "[]";
    public string Automation { get; set; } = Documents.FlowAutomation.Review;
    /// <summary>The published <see cref="FlowVersion.VersionNo"/>; null means setup mode.</summary>
    public int? PublishedVersionNo { get; set; }
    /// <summary>The workspace's hidden flow that documents land in when no flow is named.</summary>
    public bool IsInbox { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
