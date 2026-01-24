using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public interface IOutboundRepository
{
    Task SaveArtifactAsync(OutboundArtifact artifact, byte[] content, CancellationToken ct = default);
    Task<OutboundArtifact?> GetArtifactAsync(Guid orderId, CancellationToken ct = default);
    Task<byte[]?> GetArtifactContentAsync(Guid orderId, CancellationToken ct = default);
    Task SaveDeliveryRecordAsync(DeliveryRecord record, CancellationToken ct = default);
    Task<DeliveryRecord?> GetDeliveryRecordAsync(Guid orderId, CancellationToken ct = default);
}
