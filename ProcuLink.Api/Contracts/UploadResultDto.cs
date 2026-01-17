using ProcuLink.Core.Canonical;

namespace ProcuLink.Api.Contracts;

/// <summary>
/// Result of uploading a purchase order file
/// </summary>
public class UploadResultDto
{
    /// <summary>
    /// The parsed and persisted purchase order
    /// </summary>
    public PurchaseOrder Order { get; set; } = null!;

    /// <summary>
    /// Validation messages and warnings (not errors that prevented processing)
    /// </summary>
    public List<string> ValidationMessages { get; set; } = new();
}
