namespace ProcuLink.Api.Contracts;

/// <summary>
/// Group V7 — Connector SDK: API representation of a connector manifest.
/// Returned by <c>GET /api/connector-manifests</c> and <c>GET /api/connector-manifests/{key}</c>.
/// </summary>
public sealed record ConnectorManifestDto(
    /// <summary>Protocol key (e.g. "http", "sftp", "erp_erply"). Matches DeliveryProtocolConstants.</summary>
    string Key,
    /// <summary>Human-readable display name (e.g. "HTTP / REST", "SFTP").</summary>
    string DisplayName,
    /// <summary>Transport category (e.g. "http", "sftp", "ftps", "smtp", "erp").</summary>
    string Transport,
    /// <summary>Supported authentication types (free-text description).</summary>
    string AuthType,
    /// <summary>Ordered configuration field descriptors.</summary>
    IReadOnlyList<ConnectorConfigFieldDto> Fields,
    /// <summary>Short capability notes, or null.</summary>
    string? Capabilities,
    /// <summary>Optional documentation URL, or null.</summary>
    string? DocsRef);

/// <summary>
/// Group V7 — descriptor for a single configuration field within a connector manifest.
/// </summary>
public sealed record ConnectorConfigFieldDto(
    /// <summary>JSON key the dispatcher reads (e.g. "url", "host", "port").</summary>
    string Name,
    /// <summary>Human-readable label for UI display.</summary>
    string Label,
    /// <summary>Data type hint: "string", "number", "bool", "secret", or "url".</summary>
    string Type,
    /// <summary>True when the dispatcher validates this field as required.</summary>
    bool Required,
    /// <summary>True when the field is stored in encrypted credentials rather than plain config JSON.</summary>
    bool Secret,
    /// <summary>Optional one-sentence help text for UI, or null.</summary>
    string? HelpText);

/// <summary>
/// Group V7 — result of <c>POST /api/connector-manifests/{key}/validate-config</c>.
/// Pure validation only — nothing is persisted.
/// </summary>
public sealed record ValidateConfigResultDto(
    /// <summary>True when all required fields are present and no unknown keys were posted.</summary>
    bool Valid,
    /// <summary>Required field names that are absent from the posted config object.</summary>
    IReadOnlyList<string> Missing,
    /// <summary>Keys present in the posted config object that are not declared in the manifest.</summary>
    IReadOnlyList<string> Unknown);
