namespace ProcuLink.Core.Services.Delivery;

public sealed record UpsertDeliveryConfigRequest(
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    string? CredentialsJson,
    string? OutputFormat = null);

public sealed record DeliveryConfigResponse(
    Guid SupplierId,
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    bool HasCredentials,
    string? CredentialsDisplay,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? OutputFormat,
    // Honest governance signal: when revision authority routes this supplier's delivery via a
    // published connection revision, the editor must tell the operator delivery follows that
    // versioned snapshot — not the raw row below. RevisionGoverned=false ⇒ the live row governs.
    bool RevisionGoverned = false,
    int? ActiveRevisionVersionNo = null,
    // Null unless RevisionGoverned. False ⇒ the live row below differs from the version that
    // currently governs delivery (save to publish a new revision and bring them in sync).
    bool? LiveMatchesActiveRevisionDelivery = null);

public sealed record DeliveryTestResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode);
