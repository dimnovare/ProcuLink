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
    /// <summary>A serialized <c>SendWhen</c>; null means the output sends for every document.</summary>
    public string? SendWhenJson { get; set; }
    /// <summary>A serialized <c>OutputLayout</c> (pdf and txt only); null means none.</summary>
    public string? LayoutJson { get; set; }
    /// <summary>The PNG or JPEG printed at the top of a pdf output; null means none.</summary>
    public byte[]? Logo { get; set; }
    /// <summary>The standard order file this output is (a <c>StandardOutputs</c> id); null means a template output. Set on create only.</summary>
    public string? Standard { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
