namespace ProcuLink.Core.Entities;

/// <summary>A flow: a named set of fields documents are read into.</summary>
public class Flow
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = "";
    public string FieldsJson { get; set; } = "{}";
    public bool FieldsConfirmed { get; set; }
    public string Automation { get; set; } = Documents.FlowAutomation.Review;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
