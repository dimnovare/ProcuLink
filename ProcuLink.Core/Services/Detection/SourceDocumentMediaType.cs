namespace ProcuLink.Core.Services.Detection;

/// <summary>
/// Turns a CONTENT-detected format token — the <see cref="DetectedFormat.Format"/> vocabulary —
/// into the media type the API serves an original source document with, plus whether that type
/// is one we are willing to let a browser render inline.
///
/// <para><b>Why the input is a detected format and never a storage key.</b> The source key is
/// <c>{orgId}/{orderId}/{sanitisedFilename}</c>, so its extension is whatever the uploader (or an
/// email/SFTP/S3 sender) happened to call the file. Three copies of a
/// <c>Path.GetExtension(key) switch</c> already exist in this codebase for the *logical* format,
/// and the frontend shipped the same idea as <c>sourceTypeFromKey()</c> — which splits on
/// <c>'.'</c>, is handed an order id, and therefore returns <c>undefined</c> forever without ever
/// failing. A media type derived from a name is a guess that renders as a fact, so this map takes
/// only an answer that came from the bytes (or, failing that, from the format the parser recorded
/// at ingest). It deliberately exposes no key/filename overload — there is nothing to misuse.</para>
///
/// <para><b>The map is a closed allowlist, and that is a security property.</b> Every branch
/// returns a type that cannot execute script in a browser. Nothing here can produce
/// <c>text/html</c>, <c>image/svg+xml</c>, <c>application/xhtml+xml</c> or a JavaScript type, so a
/// file whose bytes look like markup cannot be reflected back as a live document on the API
/// origin. Anything the detector does not recognise collapses to
/// <see cref="Unknown"/> — <c>application/octet-stream</c>, never rendered inline.</para>
///
/// <para><b>No charset is declared.</b> The detector identifies a format, not an encoding. An
/// EDIFACT interchange is routinely ISO-8859-1 and a CSV exported from a local ERP is routinely
/// Windows-1252; stamping <c>charset=utf-8</c> on either would be a guess presented as a
/// measurement, which is the exact failure this file exists to avoid. Callers that need text
/// decode with their own default.</para>
/// </summary>
/// <param name="ContentType">The media type to send in <c>Content-Type</c>.</param>
/// <param name="RenderInline">
/// True when <c>Content-Disposition: inline</c> is appropriate. False for types a browser cannot
/// display anyway (spreadsheets) and for anything unrecognised, both of which are sent as
/// <c>attachment</c> so a direct navigation downloads the bytes instead of handing them to a
/// content handler.
/// </param>
public sealed record SourceDocumentMediaType(string ContentType, bool RenderInline)
{
    /// <summary>
    /// What an unrecognised — or absent — format resolves to. Opaque bytes, never inline.
    /// </summary>
    public static readonly SourceDocumentMediaType Unknown =
        new("application/octet-stream", RenderInline: false);

    /// <summary>
    /// Maps a <see cref="DetectedFormat.Format"/> token to a media type, or returns
    /// <see cref="Unknown"/> for <c>"unknown"</c>, null, blank, and any token this map has not
    /// been taught. Unrecognised input is never an error: an honest "opaque bytes" answer is
    /// always available, so no caller needs a fallback that invents a type.
    /// </summary>
    public static SourceDocumentMediaType For(string? detectedFormat) =>
        (detectedFormat ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pdf"  => new("application/pdf", RenderInline: true),
            "csv"  => new("text/csv", RenderInline: true),

            // Spreadsheets are an OOXML zip. Honest type, but no browser renders one, so it is an
            // attachment on a direct navigation; a JS viewer fetching the bytes is unaffected.
            "xlsx" => new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                          RenderInline: false),

            // cXML and UBL are XML documents; the detector distinguishes them by namespace for
            // parser selection, but the media type is the same for all three.
            "cxml" or "ubl" or "xml" => new("application/xml", RenderInline: true),

            // EDIFACT and X12 interchanges are plain text with no registered media type in
            // common browser use; text/plain is the honest, inert answer.
            "edifact" or "x12" => new("text/plain", RenderInline: true),

            _ => Unknown,
        };
}
