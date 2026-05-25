namespace ProcuLink.Core.Services.Delivery;

public sealed record UpsertDeliveryConfigRequest(
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    string? CredentialsJson);

public sealed record DeliveryConfigResponse(
    Guid SupplierId,
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    bool HasCredentials,
    string? CredentialsDisplay,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DeliveryTestResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode);
