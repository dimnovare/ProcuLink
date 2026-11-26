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
    /// <summary>Why it last dead-lettered (<see cref="FlowDeliveryFailureKind"/>), meaningful only while Status is dead_letter; null on rows dead-lettered before it was recorded.</summary>
    public string? FailureKind { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    /// <summary>The receiving system's own number for what was sent (a KSeF number), with the environment it was issued in.</summary>
    public string? RemoteReference { get; set; }
    public string? RemoteEnvironment { get; set; }
    /// <summary>Storage key of the receipt the receiving system returned (a KSeF UPO), when there is one.</summary>
    public string? ReceiptKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
