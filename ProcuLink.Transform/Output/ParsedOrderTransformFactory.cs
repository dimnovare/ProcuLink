using ProcuLink.Core.Services;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Selects the correct <see cref="IParsedOrderTransform"/> for a given
/// <see cref="OutputFormat"/> and serializes a <see cref="ParsedOrder"/> with it.
/// Transforms are injected via DI as <c>IEnumerable&lt;IParsedOrderTransform&gt;</c>,
/// mirroring the <see cref="OrderParserFactory"/> dispatch pattern on the inbound
/// side.
/// </summary>
public sealed class ParsedOrderTransformFactory
{
    private readonly IEnumerable<IParsedOrderTransform> _transforms;

    public ParsedOrderTransformFactory(IEnumerable<IParsedOrderTransform> transforms)
    {
        _transforms = transforms;
    }

    /// <summary>
    /// Returns the transform registered for <paramref name="format"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when no registered transform handles the format.
    /// </exception>
    public IParsedOrderTransform GetTransform(OutputFormat format)
    {
        var transform = _transforms.FirstOrDefault(t => t.CanTransform(format));
        if (transform is null)
            throw new NotSupportedException(
                $"No ParsedOrder transform is registered for output format '{format}'.");

        return transform;
    }

    /// <summary>
    /// Convenience: locate the transform for <paramref name="format"/> and run it
    /// against <paramref name="order"/> in one call.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when no registered transform handles the format.
    /// </exception>
    public ParsedOrderTransformResult Transform(ParsedOrder order, OutputFormat format) =>
        GetTransform(format).Transform(order, format);
}
