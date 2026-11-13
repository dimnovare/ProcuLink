namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// THE set of output formats ProcuLink can actually BUILD a document for — DERIVED, not typed, from
/// the <see cref="ITransformService"/> implementations the host registers.
///
/// <para>Why derived. <see cref="OutputFormat"/> carries more values than there are transforms:
/// <see cref="OutputFormat.UblOrder"/>, <see cref="OutputFormat.X12_850"/> and
/// <see cref="OutputFormat.EdifactOrders"/> exist to NAME a standards profile for the conformance
/// layer, and no supplier transform writes EDIFACT — the flow standard output does.
/// A format-shaped string is therefore not evidence that a document can be produced for it, and
/// <c>Enum.TryParse(ignoreCase: true)</c> happily re-hydrates every one of those names. A config
/// pinned to one of them reaches
/// <c>OrderTransformService</c>'s "No transform service registered for format '…'" and the order
/// dies there, terminally, long after whoever typed it has gone.</para>
///
/// <para>Why not a hand-typed list of six names: a seventh transform would have to be remembered in
/// every place that list was copied to, and the copies are exactly what this type replaces. The
/// buildable set is computed by ASKING each registered transform
/// <see cref="ITransformService.CanTransform"/> for every enum value, so registering a transform is
/// the only edit a new format needs.</para>
///
/// <para>The token spelling is <c>OutputFormat.ToString().ToLowerInvariant()</c> — the same
/// projection <see cref="DeliveryMediaTypes"/> uses for its token index, and the same value the
/// delivery-config and connection-revision columns persist.</para>
/// </summary>
public sealed class OutputFormatCatalog
{
    public OutputFormatCatalog(IEnumerable<ITransformService> transformers)
    {
        ArgumentNullException.ThrowIfNull(transformers);

        // Materialise once: the argument is typically the DI container's IEnumerable<ITransformService>,
        // and this enumerates it |OutputFormat| times.
        var registered = transformers.ToList();

        Buildable = Enum.GetValues<OutputFormat>()
            .Where(format => registered.Any(t => t.CanTransform(format)))
            .ToArray();

        // A catalog that can build nothing is a BROKEN DERIVATION, not a strict allow-list — it would
        // refuse every write on every path, including the formats production delivers on today. Fail
        // at composition, where the empty transformer set is visible, rather than at the first save.
        if (Buildable.Count == 0)
            throw new InvalidOperationException(
                $"OutputFormatCatalog was built from {registered.Count} transform service(s), none of " +
                "which can build any OutputFormat. That is a broken registration, not an empty " +
                "allow-list: register the ITransformService implementations before constructing the catalog.");

        Tokens = Buildable.Select(Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every format some registered transform answers <c>CanTransform</c> true for, in enum order.</summary>
    public IReadOnlyList<OutputFormat> Buildable { get; }

    /// <summary>The persisted lowercase token for each buildable format. Case-insensitive.</summary>
    public IReadOnlySet<string> Tokens { get; }

    /// <summary>The persisted token for a format. The one spelling; everything else derives from it.</summary>
    public static string Token(OutputFormat format) => format.ToString().ToLowerInvariant();

    public bool IsBuildable(OutputFormat format) => Buildable.Contains(format);

    /// <summary>True when the token names a format some registered transform can build.</summary>
    public bool IsBuildableToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && Tokens.Contains(token.Trim());
}
