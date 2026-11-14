namespace ProcuLink.Core.Services;

public interface IOrderExceptionService
{
    /// <summary>
    /// Idempotently reconcile open exceptions for an order against its current
    /// status and lines: open new exceptions for current problems, auto-resolve
    /// open exceptions whose problem no longer applies. Never touches ignored rows.
    /// </summary>
    Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct);
}
