namespace ProcuLink.Core.Services.Delivery;

/// <summary>The envelope a generated document travels in: its media type and its file extension.</summary>
/// <param name="ContentType">
/// The media type sent on the wire (HTTP <c>Content-Type</c>, email attachment type, ERP part type).
/// </param>
/// <param name="FileExtension">The extension, INCLUDING the leading dot (e.g. <c>.xml</c>).</param>
public readonly record struct DeliveryMediaType(string ContentType, string FileExtension);

/// <summary>
/// THE table mapping every <see cref="OutputFormat"/> to the media type and file extension the
/// document is actually delivered and stored as.
///
/// <para>
/// Why a table and not a switch: the content type and the extension used to be re-derived by
/// hand-written switches in several places, each with a silent <c>_ =&gt; "application/octet-stream"</c>
/// arm that only knew xml/json/csv. cXML, UBL and X12 fell through it, so byte-perfect documents
/// reached real suppliers as <c>application/octet-stream</c> named <c>PO-1234.dat</c> — and many
/// receivers gate on exactly those two things. A dictionary has no default arm: a format without a
/// row cannot be silently mis-typed, and <c>DeliveryMediaTypeTableTests</c> enumerates
/// <see cref="OutputFormat"/> so adding a value without a row fails the build's test run.
/// </para>
///
/// <para>
/// Callers that hold the enum use <see cref="For(OutputFormat)"/>. Callers that hold the persisted
/// lowercase format token (<c>OutboundArtifact.Format</c>) use <see cref="TryForToken"/>: the token
/// index is DERIVED from the same rows, so the persisted string can never drift from the enum.
/// </para>
/// </summary>
public static class DeliveryMediaTypes
{
    // ── The rows. One per OutputFormat. No default arm — that is the point. ────
    private static readonly IReadOnlyDictionary<OutputFormat, DeliveryMediaType> Rows =
        new Dictionary<OutputFormat, DeliveryMediaType>
        {
            // Generic ProcuLink XML.
            [OutputFormat.Xml]  = new("application/xml", ".xml"),
            [OutputFormat.Csv]  = new("text/csv", ".csv"),
            // cXML 1.2 is an XML document. Receivers key on application/xml + .xml; a ".cxml"
            // extension is not registered anywhere and is rejected by extension allow-lists.
            [OutputFormat.CXml] = new("application/xml", ".xml"),
            [OutputFormat.Json] = new("application/json", ".json"),
            // UBL 2.1 / Peppol BIS Order 3 is likewise an XML document.
            [OutputFormat.Ubl]  = new("application/xml", ".xml"),
            // IANA-registered media type for ASC X12 interchanges (case is as registered; media
            // types compare case-insensitively, so a receiver matching "application/edi-x12" matches).
            [OutputFormat.X12]  = new("application/EDI-X12", ".x12"),

            // Named standards-profile identifiers (conformance layer). Same documents, same envelopes.
            [OutputFormat.UblOrder]      = new("application/xml", ".xml"),
            [OutputFormat.X12_850]       = new("application/EDI-X12", ".x12"),
            [OutputFormat.EdifactOrders] = new("application/EDIFACT", ".edi"),
        };

    /// <summary>
    /// Format tokens that are NOT an <see cref="OutputFormat"/> name but denote the same document.
    /// Kept explicit so the token index stays a pure projection of <see cref="Rows"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, OutputFormat> Aliases =
        new Dictionary<string, OutputFormat>(StringComparer.OrdinalIgnoreCase)
        {
            // Peppol BIS (Order and Billing) documents are UBL 2.1 XML.
            ["peppol"]  = OutputFormat.Ubl,
            ["edifact"] = OutputFormat.EdifactOrders,
        };

    // Token index PROJECTED from the rows: the token is exactly what callers persist,
    // OutputFormat.ToString().ToLowerInvariant(). Deriving it means a new row automatically
    // gains its token, and a token can never name a format that has no row.
    private static readonly IReadOnlyDictionary<string, DeliveryMediaType> ByToken =
        Rows.ToDictionary(
                kv => kv.Key.ToString().ToLowerInvariant(),
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase)
            .Concat(Aliases.Select(a => new KeyValuePair<string, DeliveryMediaType>(a.Key, Rows[a.Value])))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The envelope for a format token this code has never seen — a legacy artifact row, or a
    /// hand-written format string. The ONE place <c>application/octet-stream</c> is still correct:
    /// we genuinely do not know what the bytes are. Every format ProcuLink can PRODUCE has a row
    /// above and never reaches this.
    /// </summary>
    public static DeliveryMediaType Unknown { get; } = new("application/octet-stream", ".dat");

    /// <summary>Every row — the surface the exhaustiveness guard test enumerates against.</summary>
    public static IReadOnlyDictionary<OutputFormat, DeliveryMediaType> All => Rows;

    /// <summary>
    /// The envelope for a format. Throws for a format with no row rather than inventing one:
    /// the guard test makes that unreachable in a shipped build, and the delivery call site treats
    /// a throw as an ordinary failed attempt (normal retry path), never as a silent mis-typing.
    /// </summary>
    public static DeliveryMediaType For(OutputFormat format) =>
        Rows.TryGetValue(format, out var media)
            ? media
            : throw new NotSupportedException(
                $"No delivery media type is defined for output format '{format}'. " +
                "Add a row to DeliveryMediaTypes.Rows — every OutputFormat must declare the " +
                "content type and file extension it is delivered as.");

    /// <summary>Non-throwing lookup by enum.</summary>
    public static bool TryFor(OutputFormat format, out DeliveryMediaType media) =>
        Rows.TryGetValue(format, out media);

    /// <summary>
    /// Lookup by the persisted lowercase format token (<c>OutboundArtifact.Format</c>,
    /// <c>SupplierDeliveryConfig.OutputFormat</c>). False for null/blank/unrecognised tokens —
    /// callers fall back to <see cref="Unknown"/>.
    /// </summary>
    public static bool TryForToken(string? token, out DeliveryMediaType media)
    {
        if (!string.IsNullOrWhiteSpace(token))
            return ByToken.TryGetValue(token.Trim(), out media);

        media = default;
        return false;
    }

    /// <summary>Lookup by token with the honest unknown envelope as the fallback.</summary>
    public static DeliveryMediaType ForTokenOrUnknown(string? token) =>
        TryForToken(token, out var media) ? media : Unknown;

    /// <summary>
    /// Reverse lookup: the extension for a media type this table knows. Used where the content type
    /// is the input (operator-supplied Scriban template content types) so a template declaring
    /// <c>application/EDI-X12</c> is named the same way the built-in X12 transform names its output.
    /// </summary>
    public static bool TryExtensionForContentType(string? contentType, out string extension)
    {
        extension = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType)) return false;

        // Strip any parameters ("application/xml; charset=utf-8") before matching.
        var bare = contentType.Split(';')[0].Trim();
        foreach (var media in Rows.Values)
        {
            if (string.Equals(media.ContentType, bare, StringComparison.OrdinalIgnoreCase))
            {
                extension = media.FileExtension;
                return true;
            }
        }

        return false;
    }
}
