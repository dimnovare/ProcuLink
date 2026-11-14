namespace ProcuLink.Core.Entities;

/// <summary>One AI call made while reading a document, and what it cost in tokens.</summary>
public class FlowReadCall
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid DocumentId { get; set; }
    /// <summary>1-based part of the document this call read.</summary>
    public int Part { get; set; }
    public string Model { get; set; } = "";
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public DateTime CreatedAt { get; set; }
}
