using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IDesadvService
{
    Task<AdvanceShippingNoticeEntity> CreateStubAsync(
        Guid orgId, Guid? supplierId, Stream stream,
        string fileName, string contentType, CancellationToken ct);

    Task<AdvanceShippingNoticeEntity?> GetAsync(Guid orgId, Guid asnId, CancellationToken ct);

    Task<IReadOnlyList<AdvanceShippingNoticeEntity>> ListAsync(Guid orgId, CancellationToken ct);
}
