using ProcuLink.Core.Documents;

namespace ProcuLink.Core.Entities;

/// <summary>One document received by a <see cref="Flow"/>, with what was read from it.</summary>
public class FlowDocument
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid FlowId { get; set; }
    public Flow? Flow { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string SourceFileKey { get; set; } = "";
    /// <summary>Lowercase hex.</summary>
    public string Sha256 { get; set; } = "";

    public int? PageCount { get; set; }
    public int BillingUnits { get; set; }

    public string Status { get; set; } = DocumentStatus.Received;
    public string? StatusReason { get; set; }

    public string ReadJson { get; set; } = "{}";
    public string ChecksJson { get; set; } = "[]";

    /// <summary><c>upload</c> or an <c>IntakeKind</c>.</summary>
    public string Source { get; set; } = "upload";
    /// <summary>Sender address, remote path, object key or idempotency key.</summary>
    public string? SourceRef { get; set; }

    public string? DedupKey { get; set; }
    public bool IsSample { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}
