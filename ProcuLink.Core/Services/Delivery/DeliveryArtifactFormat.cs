using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// How one outbound artifact format is labelled ON THE WIRE to the supplier: the MIME type sent
/// with the payload and the extension of the file the supplier receives.
/// </summary>
/// <param name="ContentType">MIME type (HTTP <c>Content-Type</c>, email attachment type, …).</param>
/// <param name="FileExtension">Supplier-facing file extension, leading dot included.</param>
public readonly record struct DeliveryFormatDescriptor(string ContentType, string FileExtension);

/// <summary>
/// THE table mapping an <see cref="Entities.OutboundArtifact.Format"/> identifier to the wire
/// labelling the supplier receives, plus the filename policy built on it. Single source of truth —
/// every delivery caller resolves through here rather than re-deriving a switch.
///
/// <para>
/// Scope: this is the SUPPLIER-FACING labelling. It is deliberately NOT the same thing as
/// <see cref="ITransformService"/>'s <c>TransformResult.FileExtension</c>, which names the INTERNAL
/// R2 artifact blob (<c>OrderTransformService</c> builds the storage key from it) and stores cXML
/// as <c>.cxml</c>. A supplier receiving a cXML OrderRequest expects <c>application/xml</c> and a
/// <c>.xml</c> file; the internal blob suffix is a storage detail and stays as it is.
/// </para>
///
/// <para>
/// The identifier set is the six values the product actually persists — see
/// <c>DeliveryConfigService</c>'s <c>OutputFormat</c> allow-list, <c>OrdersController</c>'s
/// transform allow-list and <see cref="Entities.SupplierDeliveryConfig.OutputFormat"/>. The extra
/// <c>OutputFormat</c> enum members (<c>UblOrder</c> / <c>X12_850</c> / <c>EdifactOrders</c>) are
/// conformance-profile names only and never reach artifact creation, so they are not listed here.
/// </para>
/// </summary>
public static class DeliveryArtifactFormat
{
    /// <summary>
    /// Fallback for a format identifier this table does not know. Callers MUST surface it (log a
    /// warning naming the format) rather than shipping an unlabelled payload silently: a receiver
    /// that gates on content type or extension rejects <c>application/octet-stream</c>/<c>.dat</c>,
    /// and the delivery would otherwise be reported as a success.
    /// </summary>
    public static readonly DeliveryFormatDescriptor Unknown = new("application/octet-stream", ".dat");

    /// <summary>Canonical format identifiers, in a stable order (for log/diagnostic messages).</summary>
    public static readonly IReadOnlyList<string> KnownFormats = ["xml", "csv", "cxml", "json", "ubl", "x12"];

    private static readonly IReadOnlyDictionary<string, DeliveryFormatDescriptor> Table =
        new Dictionary<string, DeliveryFormatDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["xml"] = new("application/xml", ".xml"),
            ["csv"] = new("text/csv", ".csv"),
            ["json"] = new("application/json", ".json"),
            // cXML IS XML on the wire. Ariba/Coupa-style receivers accept application/xml + .xml;
            // the .cxml suffix is our internal blob naming, not the supplier's.
            ["cxml"] = new("application/xml", ".xml"),
            // UBL / Peppol Order is an XML document.
            ["ubl"] = new("application/xml", ".xml"),
            // RFC 1767 registers the type as application/EDI-X12 (MIME types compare
            // case-insensitively, so a receiver matching lower-case still matches).
            ["x12"] = new("application/EDI-X12", ".x12"),
        };

    /// <summary>Resolves the wire labelling for a format identifier. False = unknown format.</summary>
    public static bool TryResolve(string? format, out DeliveryFormatDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(format) && Table.TryGetValue(format.Trim(), out descriptor))
            return true;

        descriptor = Unknown;
        return false;
    }

    /// <summary>
    /// Resolves the wire labelling, falling back to <see cref="Unknown"/>. Prefer
    /// <see cref="TryResolve"/> where the caller can log the unknown format.
    /// </summary>
    public static DeliveryFormatDescriptor Resolve(string? format) =>
        TryResolve(format, out var descriptor) ? descriptor : Unknown;

    /// <summary>
    /// True for channels that drop the artifact as a FILE into a directory the supplier shares
    /// across orders. Those need a collision-proof filename; HTTP / email / ERP channels carry the
    /// order identity in the request or message and keep the human-readable PO filename.
    /// </summary>
    public static bool IsFileDropProtocol(string? protocol) =>
        protocol is not null
        && (protocol.Equals(DeliveryProtocolConstants.Sftp, StringComparison.OrdinalIgnoreCase)
            || protocol.Equals(DeliveryProtocolConstants.Ftps, StringComparison.OrdinalIgnoreCase)
            || protocol.Equals(DeliveryProtocolConstants.Ftp, StringComparison.OrdinalIgnoreCase));

    /// <summary>Human-readable <c>{PO}{ext}</c> name — used by channels that cannot collide.</summary>
    public static string FileNameFor(string? poNumber, DeliveryFormatDescriptor descriptor) =>
        $"{SanitizeFileToken(poNumber)}{descriptor.FileExtension}";

    /// <summary>
    /// Collision-proof <c>{PO}-{orderId}{ext}</c> name for file-drop channels.
    ///
    /// <para>
    /// <c>po_number</c> has NO uniqueness constraint (<c>ProcuLinkDbContext</c> declares it
    /// <c>IsRequired()</c> only), so two orders legitimately share one PO number — under the bare
    /// PO filename the second upload silently replaced the first in the supplier's directory.
    /// </para>
    ///
    /// <para>
    /// The suffix is the FULL order id and therefore DETERMINISTIC: re-sending the same order
    /// targets the same remote path. That is load-bearing — <c>SftpDeliveryDispatcher</c> and
    /// <c>FtpsDeliveryDispatcher</c> declare <c>ResendSafety.Safe</c> because a crash-recovery
    /// re-drive overwrites its own file instead of creating a second copy. A timestamp suffix would
    /// break that; a truncated id would reintroduce the collision this method exists to remove.
    /// </para>
    /// </summary>
    public static string UniqueFileNameFor(string? poNumber, Guid orderId, DeliveryFormatDescriptor descriptor) =>
        $"{SanitizeFileToken(poNumber)}-{orderId:N}{descriptor.FileExtension}";

    /// <summary>
    /// Replaces characters that are illegal in a filename. Unchanged from the behaviour this
    /// consolidated: file-drop dispatchers apply their own stricter sanitiser on top.
    /// </summary>
    public static string SanitizeFileToken(string? value)
    {
        if (value is null) return "order";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "order" : sanitized;
    }
}
