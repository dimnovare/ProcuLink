using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Erp;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

public sealed class ErplyDeliveryDispatcher : ErpDeliveryDispatcherBase
{
    public ErplyDeliveryDispatcher(IEnumerable<IErpConnector> connectors)
        : base(connectors, DeliveryProtocolConstants.ErpErply)
    {
    }
}

public sealed class DirectoDeliveryDispatcher : ErpDeliveryDispatcherBase
{
    public DirectoDeliveryDispatcher(IEnumerable<IErpConnector> connectors)
        : base(connectors, DeliveryProtocolConstants.ErpDirecto)
    {
    }
}

public abstract class ErpDeliveryDispatcherBase : IDeliveryDispatcher
{
    private readonly IErpConnector _connector;

    protected ErpDeliveryDispatcherBase(IEnumerable<IErpConnector> connectors, string protocol)
    {
        Protocol = protocol;
        _connector = connectors.First(x => string.Equals(x.Protocol, protocol, StringComparison.OrdinalIgnoreCase));
    }

    public string Protocol { get; }

    // No dedupe signal reaches the ERP: the connector contract accepts no idempotency key, and
    // both connectors are generic HTTP posts to a tenant-configured URL with no document model
    // or lookup API. A re-send after an unknown outcome creates a DUPLICATE ERP order.
    public ResendSafety ResendSafety => ResendSafety.Unsafe;

    // The connector reads the ERP's response body on every non-2xx and now carries it verbatim, so a
    // blank ResponseBody here really does mean "the endpoint said nothing" — which is what
    // SupplierResponseClassification's 400 split requires. It was NOT true before: this dispatcher
    // returned only (Success, ErrorMessage, ResponseCode), so a 400 naming a bad buyer code was
    // indistinguishable from a bare one, classified retryable, and re-POSTed to an endpoint that
    // cannot de-duplicate.
    public bool CapturesSupplierResponseBody => true;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        // A3 idempotency: the ERP connector contract accepts no idempotency key, and both
        // connectors are generic HTTP posts to a tenant-configured URL — there is no ERP document
        // model or lookup API to dedupe against. So idempotencyKey is intentionally unused here.
        //
        // The duplicate-order risk this used to carry is now handled upstream rather than ignored:
        // this dispatcher declares ResendSafety.Unsafe, so DeliveryService PARKS a crash-recovery
        // re-drive (delivery_unconfirmed) for an operator decision instead of blindly re-sending.
        // A duplicate is still possible if the operator chooses "Send again" — that is their
        // informed call, not something the system does behind their back.
        var result = await _connector.SendAsync(
            new ErpDeliveryRequest(content, fileName, contentType, config, decryptedCredentials),
            ct);

        return new DeliveryResult(
            result.Success,
            result.ErrorMessage,
            result.ResponseCode,
            ResponseBody: result.ResponseBody,
            SupplierReasonObservable: CapturesSupplierResponseBody,
            RetryAfter: result.RetryAfter);
    }
}
