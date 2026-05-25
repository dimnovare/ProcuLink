using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Erp;

public interface IErpConnector
{
    string Protocol { get; }

    Task<ErpDeliveryResult> SendAsync(ErpDeliveryRequest request, CancellationToken ct);
}

public sealed record ErpDeliveryRequest(
    byte[] Content,
    string FileName,
    string ContentType,
    SupplierDeliveryConfig Config,
    string DecryptedCredentials);

public sealed record ErpDeliveryResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode = null);
