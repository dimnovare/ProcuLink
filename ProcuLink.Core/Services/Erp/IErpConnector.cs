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

/// <param name="ResponseBody">
/// The ERP's own response text, VERBATIM. The connectors already read this to build
/// <paramref name="ErrorMessage"/>; carrying the original too is what lets
/// <c>SupplierResponseClassification</c> tell a 400 that refuses the DOCUMENT ("unknown buyer code
/// BC-9") from a 400 that refuses the REQUEST. Without it every ERP 400 looked unexplained and was
/// re-dispatched to an endpoint that declares <c>ResendSafety.Unsafe</c>.
/// </param>
/// <param name="RetryAfter">The wait the ERP asked for (<c>Retry-After</c>), when it sent one.</param>
public sealed record ErpDeliveryResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode = null,
    string? ResponseBody = null,
    TimeSpan? RetryAfter = null);
