using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public enum OutputFormat { Xml, Csv }

/// <summary>
/// Builds a formatted outbound document from a fully-resolved purchase order.
/// Implementations must validate that no line has <c>NeedsReview = true</c> or
/// a null <c>SupplierItemCode</c> before generating output.
/// </summary>
public interface ITransformService
{
    /// <summary>Returns true if this implementation handles the given format.</summary>
    bool CanTransform(OutputFormat format);

    /// <summary>
    /// Generate the outbound document. The returned <see cref="TransformResult.Content"/>
    /// stream is positioned at the beginning and ready to upload.
    /// </summary>
    Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct);
}

/// <summary>The generated document plus the metadata needed to persist it.</summary>
public record TransformResult(
    Stream Content,
    string ContentType,
    string FileExtension
);
