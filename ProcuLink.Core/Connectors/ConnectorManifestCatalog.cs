using ProcuLink.Core.Constants;

namespace ProcuLink.Core.Connectors;

/// <summary>
/// Group V7 — Connector SDK: static catalog of every connector type that is ACTUALLY wired as a
/// delivery dispatcher or ERP connector in the running application.
///
/// <para>
/// OFFER-EQUALS-WORKS: a manifest is declared here ONLY when a real
/// <see cref="ProcuLink.Core.Services.Delivery.IDeliveryDispatcher"/> or
/// <see cref="ProcuLink.Core.Services.Erp.IErpConnector"/> implementation exists and is
/// registered in DI. The field names in each manifest match the JSON keys the corresponding
/// dispatcher deserialises from <c>SupplierDeliveryConfig.ConfigJson</c> /
/// <c>SupplierDeliveryConfig.CredentialsJson</c>.
/// </para>
///
/// <para>
/// Wired dispatchers as of Group V7 (verified in <c>ProcuLink.Api/Program.cs</c> and
/// <c>ProcuLink.Infrastructure/Services/Dispatchers/</c> + <c>Erp/</c>):
/// <list type="bullet">
///   <item><c>http</c>     — <c>HttpDeliveryDispatcher</c> (Protocol = "http")</item>
///   <item><c>sftp</c>     — <c>SftpDeliveryDispatcher</c> (Protocol = "sftp")</item>
///   <item><c>ftps</c>     — <c>FtpsDeliveryDispatcher</c> (Protocol = "ftps")</item>
///   <item><c>email</c>    — <c>EmailApiDeliveryDispatcher</c> (Protocol = "email", HTTP email API)</item>
///   <item><c>erp_erply</c>   — <c>ErplyDeliveryDispatcher</c> → <c>ErplyConnector</c></item>
///   <item><c>erp_directo</c> — <c>DirectoDeliveryDispatcher</c> → <c>DirectoConnector</c></item>
/// </list>
/// NOTE: <c>ftp</c> is declared in <c>DeliveryProtocolConstants</c> but has NO registered
/// dispatcher — it is intentionally omitted from this catalog. <c>smtp</c> is likewise omitted:
/// the legacy raw-SMTP dispatcher is RETIRED from offered channels (outbound SMTP ports are blocked
/// on the cloud host) and superseded by <c>email</c>; it remains a self-host-only opt-in.
/// </para>
///
/// <para>
/// Mirrors the <see cref="ProcuLink.Core.Entities.RuleCatalog"/> pattern: static sealed class,
/// public <c>IReadOnlyDictionary</c> keyed by the protocol constant value, factory helpers kept
/// private, pure value model, no DI required.
/// </para>
/// </summary>
public static class ConnectorManifestCatalog
{
    // ── Field helper ──────────────────────────────────────────────────────────

    private static ConnectorConfigField Field(
        string name, string label, string type,
        bool required = false, bool secret = false, string? help = null)
        => new(name, label, type, required, secret, help);

    // ── Manifests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// HTTP / REST dispatcher (<c>HttpDeliveryDispatcher</c>).
    /// Config fields mirror the private <c>HttpConfig</c> record inside the dispatcher.
    /// Auth fields (type, header/value/token/username/password/tokenUrl/…) are stored
    /// in the ENCRYPTED credentials blob.
    /// </summary>
    private static readonly ConnectorManifest Http = new(
        Key: DeliveryProtocolConstants.Http,
        DisplayName: "HTTP / REST",
        Transport: "http",
        AuthType: "none, apikey, bearer, basic, oauth2_client_credentials",
        Fields:
        [
            Field("url",            "Endpoint URL",    "url",    required: true,
                help: "The supplier's HTTP endpoint that will receive the generated artifact."),
            Field("method",         "HTTP Method",     "string",
                help: "HTTP verb to use when posting the artifact (default: POST)."),
            Field("timeoutSeconds", "Timeout (s)",     "number",
                help: "Request timeout in seconds (default: 30)."),
            Field("headers",        "Extra headers",   "string",
                help: "JSON object of additional HTTP request headers to include."),
            // ── Credentials (encrypted) ────────────────────────────────────────
            Field("type",           "Auth type",       "string", secret: true,
                help: "One of: none, apikey, bearer, basic, oauth2_client_credentials."),
            Field("header",         "API key header",  "string", secret: true,
                help: "Header name for apikey auth (e.g. X-Api-Key)."),
            Field("value",          "API key value",   "secret", secret: true,
                help: "Secret value for apikey auth."),
            Field("token",          "Bearer token",    "secret", secret: true,
                help: "Static bearer token for bearer auth."),
            Field("username",       "Username",        "string", secret: true,
                help: "Username for basic auth."),
            Field("password",       "Password",        "secret", secret: true,
                help: "Password for basic auth."),
            Field("tokenUrl",       "Token URL",       "url",    secret: true,
                help: "OAuth2 token endpoint URL for oauth2_client_credentials auth."),
            Field("clientId",       "Client ID",       "string", secret: true,
                help: "OAuth2 client_id."),
            Field("clientSecret",   "Client secret",   "secret", secret: true,
                help: "OAuth2 client_secret."),
            Field("scope",          "OAuth scope",     "string", secret: true,
                help: "OAuth2 scope(s), space-separated."),
        ],
        Capabilities: "Supports multipart + raw body; apikey, bearer, basic, and OAuth2 client-credentials auth; per-request timeout; extra header injection.",
        DocsRef: "https://docs.proculink.eu/connectors/http");

    /// <summary>
    /// SFTP dispatcher (<c>SftpDeliveryDispatcher</c>).
    /// Config fields mirror <c>SftpConfig</c>; credentials fields mirror <c>SftpCredentials</c>.
    /// </summary>
    private static readonly ConnectorManifest Sftp = new(
        Key: DeliveryProtocolConstants.Sftp,
        DisplayName: "SFTP",
        Transport: "sftp",
        AuthType: "password, private_key",
        Fields:
        [
            Field("host",                 "Host / IP",           "string", required: true,
                help: "SFTP server hostname or IP address."),
            Field("port",                 "Port",                "number",
                help: "SFTP port (default: 22)."),
            Field("remotePath",           "Remote directory",    "string",
                help: "Target directory on the SFTP server (e.g. /orders/inbound)."),
            Field("makeDirectories",      "Auto-create dirs",    "bool",
                help: "Create the remote directory hierarchy if it does not exist."),
            Field("timeoutSeconds",       "Timeout (s)",         "number",
                help: "Connection and upload timeout in seconds (default: 30)."),
            // ── Credentials (encrypted) ────────────────────────────────────────
            Field("username",             "Username",            "string", required: true, secret: true,
                help: "SFTP login username."),
            Field("password",             "Password",            "secret", secret: true,
                help: "Password for password-based auth (leave blank when using a private key)."),
            Field("privateKey",           "Private key (PEM)",   "secret", secret: true,
                help: "PEM-encoded private key for key-based auth."),
            Field("privateKeyPassphrase", "Key passphrase",      "secret", secret: true,
                help: "Passphrase for an encrypted private key (optional)."),
        ],
        Capabilities: "Password and private-key (PEM) auth; auto-create remote directories; SSRF-guard validates host before each connection.",
        DocsRef: "https://docs.proculink.eu/connectors/sftp");

    /// <summary>
    /// FTPS (explicit-TLS FTP) dispatcher (<c>FtpsDeliveryDispatcher</c>).
    /// Config fields mirror the private <c>FtpsConfig</c> + <c>FtpsCredentials</c> records.
    /// </summary>
    private static readonly ConnectorManifest Ftps = new(
        Key: DeliveryProtocolConstants.Ftps,
        DisplayName: "FTPS (explicit TLS)",
        Transport: "ftps",
        AuthType: "password",
        Fields:
        [
            Field("host",                    "Host / IP",              "string", required: true,
                help: "FTPS server hostname or IP address."),
            Field("port",                    "Port",                   "number",
                help: "FTPS port (default: 21)."),
            Field("remotePath",              "Remote directory",       "string",
                help: "Target directory on the FTPS server."),
            Field("makeDirectories",         "Auto-create dirs",       "bool",
                help: "Let FluentFTP create the remote directory tree if absent."),
            Field("timeoutSeconds",          "Timeout (s)",            "number",
                help: "Connection and upload timeout in seconds (default: 30)."),
            Field("allowInvalidCertificate", "Allow invalid cert",     "bool",
                help: "Accept self-signed or expired TLS certificates (operator opt-in per supplier)."),
            // ── Credentials (encrypted) ────────────────────────────────────────
            Field("username",                "Username",               "string", required: true, secret: true,
                help: "FTPS login username."),
            Field("password",                "Password",               "secret", secret: true,
                help: "FTPS login password."),
        ],
        Capabilities: "Explicit-TLS FTP (AUTH TLS on port 21); secure by default — invalid certificates rejected unless operator opts in per supplier; SSRF guard validates host before each connection.",
        DocsRef: "https://docs.proculink.eu/connectors/ftps");

    /// <summary>
    /// Email dispatcher via HTTP email API (<c>EmailApiDeliveryDispatcher</c>, Postmark over HTTPS).
    /// Config fields mirror the private <c>EmailDeliveryConfig</c> record inside the dispatcher.
    /// No credential fields: mail is sent FROM ProcuLink's provider-verified sender, not the
    /// supplier's relay — so there is no host/port/SSL/username/password to configure.
    /// </summary>
    private static readonly ConnectorManifest Email = new(
        Key: DeliveryProtocolConstants.Email,
        DisplayName: "Email",
        Transport: "email",
        AuthType: "none",
        Fields:
        [
            Field("toAddresses",        "Recipient(s)",        "string", required: true,
                help: "Supplier email address(es) — JSON array or comma-separated string."),
            Field("replyTo",            "Reply-to",            "string",
                help: "Buyer contact address set as Reply-To (optional)."),
            Field("fromAddress",        "From address",        "string",
                help: "Override the sender (optional; must be a provider-verified domain — defaults to ProcuLink's verified sender)."),
            Field("subjectTemplate",    "Subject template",    "string",
                help: "Email subject; supports {poNumber} and {fileName} placeholders."),
            Field("bodyTemplate",       "Body template",       "string",
                help: "Plain-text email body; supports {poNumber} and {fileName} placeholders."),
            Field("attachmentFileName", "Attachment name",     "string",
                help: "Override the attachment filename (default: the generated artifact filename)."),
        ],
        Capabilities: "Sends the artifact as an email attachment via a managed HTTP email API (HTTPS/443 — works where outbound SMTP is blocked); SPF/DKIM/DMARC handled by the provider; {poNumber} and {fileName} template placeholders; no SMTP server or credentials required.",
        DocsRef: "https://docs.proculink.eu/connectors/email");

    /// <summary>
    /// Erply ERP connector (<c>ErplyDeliveryDispatcher</c> → <c>ErplyConnector</c>).
    /// Config fields mirror the private <c>ErplyConfig</c> record; credentials mirror ErplyConnector.ApplyAuth.
    /// </summary>
    private static readonly ConnectorManifest ErpErply = new(
        Key: DeliveryProtocolConstants.ErpErply,
        DisplayName: "Erply ERP",
        Transport: "erp",
        AuthType: "none, apikey, bearer",
        Fields:
        [
            Field("url",            "Erply API endpoint",  "url",    required: true,
                help: "The Erply REST API URL that will receive the PO artifact."),
            Field("clientCode",     "Client code",         "string",
                help: "Erply client code sent as X-Erply-Client-Code header."),
            Field("timeoutSeconds", "Timeout (s)",         "number",
                help: "HTTP request timeout in seconds (default: 30)."),
            // ── Credentials (encrypted) ────────────────────────────────────────
            Field("type",           "Auth type",           "string", secret: true,
                help: "One of: none, apikey, bearer."),
            Field("header",         "API key header",      "string", secret: true,
                help: "Header name for apikey auth."),
            Field("value",          "API key value",       "secret", secret: true,
                help: "Secret value for apikey auth."),
            Field("token",          "Bearer token",        "secret", secret: true,
                help: "Static bearer token for bearer auth."),
        ],
        Capabilities: "Delivers generated PO artifacts to the Erply REST API; apikey and bearer auth; X-Erply-Client-Code header; per-request timeout.",
        DocsRef: "https://docs.proculink.eu/connectors/erp-erply");

    /// <summary>
    /// Directo ERP connector (<c>DirectoDeliveryDispatcher</c> → <c>DirectoConnector</c>).
    /// Config fields mirror the private <c>DirectoConfig</c> + <c>DirectoCredentials</c> records.
    /// Directo uses form-encoded POST (database + xmldata fields).
    /// </summary>
    private static readonly ConnectorManifest ErpDirecto = new(
        Key: DeliveryProtocolConstants.ErpDirecto,
        DisplayName: "Directo ERP",
        Transport: "erp",
        AuthType: "user_password, key",
        Fields:
        [
            Field("url",            "Directo API endpoint", "url",    required: true,
                help: "The Directo XML API URL (form-POST endpoint)."),
            Field("database",       "Database name",        "string", required: true,
                help: "Directo database identifier sent as the 'database' form field."),
            Field("timeoutSeconds", "Timeout (s)",          "number",
                help: "HTTP request timeout in seconds (default: 30)."),
            // ── Credentials (encrypted) ────────────────────────────────────────
            Field("user",           "Username",             "string", secret: true,
                help: "Directo login user."),
            Field("password",       "Password",             "secret", secret: true,
                help: "Directo login password."),
            Field("key",            "API key",              "secret", secret: true,
                help: "Directo API key (alternative to user/password)."),
        ],
        Capabilities: "Delivers generated PO artifacts as form-encoded XML to the Directo XML API; user/password or API key auth; per-request timeout.",
        DocsRef: "https://docs.proculink.eu/connectors/erp-directo");

    // ── Public catalog ────────────────────────────────────────────────────────

    /// <summary>
    /// All manifests, keyed by the protocol constant value (case-sensitive, matches
    /// <see cref="DeliveryProtocolConstants"/> exactly). Only connectors with a real
    /// registered dispatcher are included.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ConnectorManifest> ByKey =
        new Dictionary<string, ConnectorManifest>(StringComparer.OrdinalIgnoreCase)
        {
            [DeliveryProtocolConstants.Http]        = Http,
            [DeliveryProtocolConstants.Sftp]        = Sftp,
            [DeliveryProtocolConstants.Ftps]        = Ftps,
            [DeliveryProtocolConstants.Email]       = Email,
            [DeliveryProtocolConstants.ErpErply]    = ErpErply,
            [DeliveryProtocolConstants.ErpDirecto]  = ErpDirecto,
        };

    /// <summary>All manifests in stable display order.</summary>
    public static readonly IReadOnlyList<ConnectorManifest> All =
    [
        Http,
        Sftp,
        Ftps,
        Email,
        ErpErply,
        ErpDirecto,
    ];
}
