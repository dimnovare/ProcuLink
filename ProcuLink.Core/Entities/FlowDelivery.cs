using ProcuLink.Core.Documents;

namespace ProcuLink.Core.Entities;

/// <summary>One output of one document, and what happened to it. Survives deletion of its output or destination.</summary>
public class FlowDelivery
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? OutputId { get; set; }
    public string OutputName { get; set; } = "";
    public Guid? DestinationId { get; set; }
    /// <summary>Empty for download-only outputs.</summary>
    public string Protocol { get; set; } = "";
    public string Status { get; set; } = FlowDeliveryStatus.Queued;
    public int Attempts { get; set; }
    /// <summary>Fresh on every (re)queue: names the artifact, the file name, and the dispatch idempotency key, so a re-send is never mistaken for the failed attempt.</summary>
    public Guid DispatchKey { get; set; }
    public string? ArtifactKey { get; set; }
    public string ContentType { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? LastError { get; set; }
    public int? ResponseCode { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
