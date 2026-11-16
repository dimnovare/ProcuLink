namespace ProcuLink.Core.Entities;

/// <summary>Where a person last put documents of one kind from one sender.</summary>
public sealed class FlowSortMemory
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    /// <summary>A <c>vat:</c> or <c>name:</c> sender key.</summary>
    public string SenderKey { get; set; } = "";
    /// <summary>A normalised kind label.</summary>
    public string Kind { get; set; } = "";
    public Guid FlowId { get; set; }
    public Flow? Flow { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}