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

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        // A3 idempotency: the ERP connector contract does not currently accept an idempotency key;
        // the attempt-started row (DeliveryService) remains the universal crash backstop for this
        // channel. idempotencyKey is intentionally unused here.
        var result = await _connector.SendAsync(
            new ErpDeliveryRequest(content, fileName, contentType, config, decryptedCredentials),
            ct);

        return new DeliveryResult(result.Success, result.ErrorMessage, result.ResponseCode);
    }
}
