namespace ProcuLink.Core.Canonical;

/// <summary>
/// Metadata for an outbound transform artifact
/// </summary>
public class OutboundArtifact
{
    public Guid OrderId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty; // "xml" or "csv"
    public string FileName { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
