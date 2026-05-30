using ProcuLink.Core.Services;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Serializes a <see cref="ParsedOrder"/> — the canonical, pre-resolution PO
/// model produced by the inbound parsers — into a standards document.
///
/// This is a deliberately separate abstraction from <see cref="ITransformService"/>:
/// <list type="bullet">
///   <item><see cref="ITransformService"/> consumes a fully-resolved
///   <c>PurchaseOrderEntity</c> from the delivery pipeline (it has a resolved
///   <c>SupplierItemCode</c>, org/supplier ids, and review state).</item>
///   <item><see cref="IParsedOrderTransform"/> consumes the canonical
///   <see cref="ParsedOrder"/> directly, so it can round-trip any parsed input
///   to any output format without persisting an entity first.</item>
/// </list>
///
/// Implementations are registered in DI and selected at runtime by
/// <see cref="ParsedOrderTransformFactory"/> via <see cref="CanTransform"/>.
/// </summary>
public interface IParsedOrderTransform
{
    /// <summary>Returns true if this implementation handles the given format.</summary>
    bool CanTransform(OutputFormat format);

    /// <summary>
    /// Serialize the canonical order. The returned bytes are the complete,
    /// ready-to-deliver document.
    /// </summary>
    ParsedOrderTransformResult Transform(ParsedOrder order, OutputFormat format);
}

/// <summary>The serialized document plus the metadata needed to deliver / persist it.</summary>
/// <param name="Content">UTF-8 encoded document bytes.</param>
/// <param name="ContentType">MIME content type, e.g. <c>application/xml</c>.</param>
/// <param name="FileExtension">File extension including the dot, e.g. <c>.xml</c>.</param>
public sealed record ParsedOrderTransformResult(
    byte[] Content,
    string ContentType,
    string FileExtension)
{
    /// <summary>Convenience: the content decoded as a UTF-8 string.</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Content);
}
