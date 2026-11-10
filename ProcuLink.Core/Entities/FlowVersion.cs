namespace ProcuLink.Core.Entities;

/// <summary>An immutable published snapshot of a flow's fields and outputs.</summary>
public class FlowVersion
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid FlowId { get; set; }
    public int VersionNo { get; set; }
    /// <summary>A serialized <see cref="Documents.FlowSnapshot"/>.</summary>
    public string SnapshotJson { get; set; } = "{}";
    public DateTime PublishedAt { get; set; }
    /// <summary>Clerk user id of the publisher.</summary>
    public string? PublishedBy { get; set; }
}
