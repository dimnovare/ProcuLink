namespace ProcuLink.Core.Connectors;

/// <summary>
/// Group V7 — Connector SDK: describes ONE connector TYPE that is actually wired as a delivery
/// dispatcher or ERP connector in the running application. Static; never persisted.
///
/// <para>
/// The <c>Key</c> value is the same string that <see cref="ProcuLink.Core.Constants.DeliveryProtocolConstants"/>
/// uses for <c>SupplierDeliveryConfig.Protocol</c>, so a consumer can look up a manifest by the
/// protocol value stored on any delivery config row.
/// </para>
/// </summary>
public sealed record ConnectorManifest(
    /// <summary>Stable machine key; equals the DeliveryProtocolConstants value (e.g. "http", "sftp", "erp_erply").</summary>
    string Key,

    /// <summary>Human-readable display name shown in the UI (e.g. "HTTP / REST", "SFTP", "Erply ERP").</summary>
    string DisplayName,

    /// <summary>Transport category, e.g. "http", "sftp", "ftps", "smtp", "erp".</summary>
    string Transport,

    /// <summary>Authentication type(s) supported, e.g. "none, apikey, bearer, basic, oauth2_client_credentials".</summary>
    string AuthType,

    /// <summary>
    /// Ordered list of per-supplier configuration field descriptors. These are the fields the
    /// dispatcher reads from <c>SupplierDeliveryConfig.ConfigJson</c> and
    /// <c>SupplierDeliveryConfig.CredentialsJson</c> (encrypted). Each name matches the JSON key
    /// the dispatcher deserialises.
    /// </summary>
    IReadOnlyList<ConnectorConfigField> Fields,

    /// <summary>Short capability notes for display (e.g. "Supports multipart upload, OAuth2 refresh").</summary>
    string? Capabilities,

    /// <summary>Optional URL to connector-specific documentation.</summary>
    string? DocsRef);

/// <summary>
/// Group V7 — descriptor for a single configuration field in a connector manifest.
/// </summary>
/// <param name="Name">JSON key the dispatcher reads (e.g. "url", "host", "port", "fromAddress").</param>
/// <param name="Label">Human-readable label (e.g. "Endpoint URL", "Host / IP", "Port", "From address").</param>
/// <param name="Type">Data type hint: "string", "number", "bool", "secret", or "url".</param>
/// <param name="Required">True when the dispatcher validates this field as required.</param>
/// <param name="Secret">True when the field is stored in encrypted credentials rather than plain config JSON.</param>
/// <param name="HelpText">Optional one-sentence description to display next to the input field.</param>
public sealed record ConnectorConfigField(
    string Name,
    string Label,
    string Type,
    bool Required,
    bool Secret,
    string? HelpText);
