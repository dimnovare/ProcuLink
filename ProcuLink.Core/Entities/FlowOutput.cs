namespace ProcuLink.Core.Entities;

/// <summary>One file a flow produces from each document, optionally sent to a destination.</summary>
public class FlowOutput
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid FlowId { get; set; }
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string TemplateJson { get; set; } = "{}";
    public string? TableKey { get; set; }
    public Guid? DestinationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
