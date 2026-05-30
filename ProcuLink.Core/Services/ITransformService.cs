using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public enum OutputFormat
{
    // ── Entity-based outbound transforms (ITransformService) ──────────────────
    // Serialize a fully-resolved PurchaseOrderEntity from the delivery pipeline.
    Xml,
    Csv,
    CXml,
    Json,
    Ubl,
    X12,

    // ── Canonical-model outbound transforms (IParsedOrderTransform) ───────────
    // Serialize a ParsedOrder (the pre-resolution canonical PO model produced by
    // the inbound parsers). Selected via ParsedOrderTransformFactory. These are
    // intentionally distinct enum values from the entity-based Ubl / X12 above so
    // the two transform families never collide on the same value.
    UblOrder,
    X12_850,
    EdifactOrders,
}

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
