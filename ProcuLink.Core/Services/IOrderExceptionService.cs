using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

public interface IOrderExceptionService
{
    /// <summary>
    /// Idempotently reconcile open exceptions for an order against its current
    /// status and lines: open new exceptions for current problems, auto-resolve
    /// open exceptions whose problem no longer applies. Never touches ignored rows.
    /// </summary>
    Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct);

    Task<IReadOnlyList<OrderException>> ListAsync(Guid orgId, string? state, CancellationToken ct);
    Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>Returns false when the exception does not exist for this org.</summary>
    Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct);
    Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct);
}
