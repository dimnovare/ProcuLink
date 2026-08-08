namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// THE set of output formats ProcuLink can actually BUILD a document for — DERIVED, not typed, from
/// the <see cref="ITransformService"/> implementations the host registers.
///
/// <para>Why derived. <see cref="OutputFormat"/> carries more values than there are transforms:
/// <see cref="OutputFormat.UblOrder"/>, <see cref="OutputFormat.X12_850"/> and
/// <see cref="OutputFormat.EdifactOrders"/> exist to NAME a standards profile for the conformance
/// layer, and EDIFACT is inbound-only — <c>EdifactOrderParser</c> reads it and no transform writes
/// it. A format-shaped string is therefore not evidence that a document can be produced for it, and
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
    /// <summary>
    /// How much of a caller-supplied format string is quoted back in a refusal. The token is the
    /// point of the message (an operator cannot fix "something was wrong" ), but the value is
    /// unvalidated request input and does not belong in a log line or an error body at arbitrary
    /// length.
    /// </summary>
    private const int MaxQuotedFormatLength = 64;

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
        AllowedListForMessage = string.Join(", ", Buildable.Select(Token));
    }

    /// <summary>Every format some registered transform answers <c>CanTransform</c> true for, in enum order.</summary>
    public IReadOnlyList<OutputFormat> Buildable { get; }

    /// <summary>The persisted lowercase token for each buildable format. Case-insensitive.</summary>
    public IReadOnlySet<string> Tokens { get; }

    /// <summary>The buildable tokens as an operator-facing list: <c>xml, csv, cxml, json, ubl, x12</c>.</summary>
    public string AllowedListForMessage { get; }

    /// <summary>The persisted token for a format. The one spelling; everything else derives from it.</summary>
    public static string Token(OutputFormat format) => format.ToString().ToLowerInvariant();

    public bool IsBuildable(OutputFormat format) => Buildable.Contains(format);

    /// <summary>True when the token names a format some registered transform can build.</summary>
    public bool IsBuildableToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && Tokens.Contains(token.Trim());

    /// <summary>
    /// The single normalisation every write path runs on a caller-supplied output format.
    ///
    /// <para>Null/blank is NOT a refusal — it means "not set", which both the delivery-config row and
    /// the connection revision use to mean "fall back to the caller's / the default format". A
    /// non-blank value that no registered transform can build is refused HERE, at write time, with a
    /// message naming the format and the supported set — the alternative is discovering it when an
    /// order dies at transform.</para>
    /// </summary>
    /// <returns>Null for null/blank input, else the lowercase persisted token.</returns>
    /// <exception cref="UnsupportedOutputFormatException">The format names nothing this build can produce.</exception>
    public string? Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;

        var normalized = format.Trim().ToLowerInvariant();
        if (!Tokens.Contains(normalized))
            throw new UnsupportedOutputFormatException(Quote(format), AllowedListForMessage);

        return normalized;
    }

    private static string Quote(string format)
    {
        var trimmed = format.Trim();
        return trimmed.Length <= MaxQuotedFormatLength
            ? trimmed
            : trimmed[..MaxQuotedFormatLength] + "…";
    }
}

/// <summary>
/// Thrown by every write path that accepts a caller-supplied output format when the value names no
/// format this build can produce a document for.
///
/// <para>Derives from <see cref="ArgumentException"/> so a handler that is not updated still answers
/// 400 rather than 500, exactly as <see cref="CredentialHeaderInConfigException"/> and
/// <see cref="OutboundUrlPolicyException"/> do — and so the delivery-config endpoint's existing
/// <c>catch (ArgumentException)</c> keeps returning the message it always has.
/// <see cref="Code"/> and <see cref="PolicyMessage"/> let a handler that IS updated return the same
/// machine-readable shape those two refusals use.</para>
///
/// <para><see cref="ArgumentException.Message"/> deliberately stays the sentence the delivery-config
/// path has always returned, so that path's 400 body does not change; <see cref="PolicyMessage"/> is
/// the longer one that NAMES the offending format, for handlers that surface it.</para>
/// </summary>
public sealed class UnsupportedOutputFormatException : ArgumentException
{
    public const string Code = "unsupported_output_format";

    /// <summary>The offending format token, trimmed and length-capped.</summary>
    public string Format { get; }

    public string PolicyMessage { get; }

    public UnsupportedOutputFormatException(string format, string allowedList)
        : base($"Output format must be one of: {allowedList}.")
    {
        Format = format;
        PolicyMessage =
            $"Output format '{format}' is not supported — no ProcuLink transform can build it, so an " +
            $"order sent with it would fail at transform. Output format must be one of: {allowedList}.";
    }
}
