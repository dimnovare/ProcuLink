namespace ProcuLink.Core.Constants;

public static class DeliveryProtocolConstants
{
    public const string Http = "http";
    public const string Sftp = "sftp";
    public const string Ftp = "ftp";
    public const string Ftps = "ftps";

    /// <summary>
    /// Legacy raw-SMTP delivery (per-supplier relay host/port/credentials). RETIRED from offered
    /// channels: outbound SMTP ports (25/465/587) are blocked on the cloud host (Railway), so this
    /// cannot work there. Use <see cref="Email"/> (HTTP email API) instead. The const + dispatcher
    /// class are kept for (a) already-saved configs and (b) self-hosted deploys that opt in via
    /// <c>Delivery:EnableSmtp=true</c>. Not advertised in the connector catalog or the UI.
    /// </summary>
    public const string Smtp = "smtp";

    /// <summary>
    /// Email delivery via an HTTP email API (Postmark) over HTTPS — the offered replacement for
    /// <see cref="Smtp"/>. Works on hosts that block outbound SMTP. Supplier supplies only recipient
    /// addresses; mail is sent FROM ProcuLink's verified sender. See <c>EmailApiDeliveryDispatcher</c>.
    /// </summary>
    public const string Email = "email";

    public const string ErpErply = "erp_erply";
    public const string ErpDirecto = "erp_directo";

    public static readonly string[] All =
    [
        Http,
        Sftp,
        Ftp,
        Ftps,
        Smtp,   // accepted for legacy/self-host configs; not offered (see Email)
        Email,
        ErpErply,
        ErpDirecto
    ];

    /// <summary>
    /// Protocols whose <c>config_json</c> carries an outbound URL under the <c>url</c> key, and are
    /// therefore subject to <see cref="T:ProcuLink.Core.Security.OutboundUrlPolicy"/> at save time.
    /// The remaining protocols are host+port based (sftp/ftp/ftps/smtp) or address a ProcuLink-owned
    /// HTTPS email API (email), so there is no tenant-supplied URL scheme to police.
    ///
    /// <para>Declared here rather than restated at each call site: a new url-bearing protocol that
    /// is not added to this list would ship without transport validation, and the delivery-config
    /// transport tests walk this list precisely so that omission fails the build.</para>
    /// </summary>
    public static readonly string[] UrlBased =
    [
        Http,
        ErpErply,
        ErpDirecto
    ];

    // Human-facing allowed list for validation messages: lists OFFERED protocols (smtp omitted —
    // it is accepted for legacy/self-host but no longer advertised).
    public static string AllowedListForMessage => "http, sftp, ftp, ftps, email, erp_erply, or erp_directo";
}
