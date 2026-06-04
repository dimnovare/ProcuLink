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
    string? OutputFormat);

public sealed record DeliveryTestResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode);
