using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IOrderExceptionService"/>.
/// All exception generation flows through the idempotent <see cref="ReconcileAsync"/>:
/// callers never construct exceptions directly. Reconcile compares the order's current
/// status + lines against existing exceptions, opening new ones for current problems and
/// auto-resolving open ones whose problem no longer applies. Rows the operator has
/// <c>ignored</c> are never reopened or auto-resolved, and an ignored row for the current
/// problem suppresses opening a duplicate (don't nag about something deliberately dismissed).
/// </summary>
public sealed class OrderExceptionService : IOrderExceptionService
{
    private readonly ProcuLinkDbContext _db;

    public OrderExceptionService(ProcuLinkDbContext db) => _db = db;

    private static (string Code, string Stage, string Severity, string Message)? ProblemFor(
        string status, bool hasUnresolvedLines)
    {
        if (status == OrderStatusConstants.PendingReview || hasUnresolvedLines)
            return ("unresolved_mapping", "Map", "warning", "Order has lines that need a supplier item code.");
        if (status == OrderStatusConstants.TransformFailed)
            return ("transform_failed", "Transform", "error", "Transform failed for this order.");
        if (status == OrderStatusConstants.DeliveryFailed)
            return ("delivery_failed", "Deliver", "error", "Delivery to the supplier failed.");
        if (status == OrderStatusConstants.RejectedBySupplier)
            return ("supplier_rejected", "Deliver", "error", "The supplier rejected this order.");
        if (status == OrderStatusConstants.DeliveryDeadLetter)
            return ("dead_letter", "Deliver", "critical", "Delivery retries are exhausted (dead-letter).");
        return null;
    }

    public async Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .Select(o => new { o.Status })
            .FirstOrDefaultAsync(ct);
        if (order is null) return;

        // Query the lines directly rather than via the Lines navigation so this works
        // regardless of whether the caller's DbContext maps the collection navigation.
        var hasUnresolved = await _db.PurchaseOrderLines
            .AnyAsync(l => l.OrderId == orderId && l.NeedsReview, ct);
        var problem = ProblemFor(order.Status, hasUnresolved);

        // Load every non-resolved exception (open + ignored) so we can dedup against both:
        //  - open rows whose problem no longer applies are auto-resolved;
        //  - an ignored row for the current problem suppresses opening a duplicate.
        var activeExceptions = await _db.OrderExceptions
            .Where(e => e.OrgId == orgId && e.OrderId == orderId
                     && (e.State == "open" || e.State == "ignored"))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        // Auto-resolve open exceptions whose problem no longer applies. Ignored rows are
        // never touched — the operator dismissed them deliberately.
        foreach (var ex in activeExceptions)
        {
            if (ex.State != "open") continue;
            if (problem is null || ex.Code != problem.Value.Code)
            {
                ex.State      = "resolved";
                ex.ResolvedAt = now;
            }
        }

        // Open a new exception only when the current problem has no existing open OR ignored
        // row for the same code.
        if (problem is not null &&
            !activeExceptions.Any(e => e.Code == problem.Value.Code
                                    && (e.State == "open" || e.State == "ignored")))
        {
            _db.OrderExceptions.Add(new OrderException
            {
                Id        = Guid.NewGuid(),
                OrgId     = orgId,
                OrderId   = orderId,
                Stage     = problem.Value.Stage,
                Code      = problem.Value.Code,
                Severity  = problem.Value.Severity,
                State     = "open",
                Message   = problem.Value.Message,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OrderException>> ListAsync(Guid orgId, string? state, CancellationToken ct)
    {
        var q = _db.OrderExceptions.AsNoTracking().Where(e => e.OrgId == orgId);
        if (!string.IsNullOrWhiteSpace(state))
            q = q.Where(e => e.State == state);
        return await q.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
        await _db.OrderExceptions.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.OrderId == orderId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

    public Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
        => SetStateAsync(orgId, exceptionId, "resolved", ct);

    public Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
        => SetStateAsync(orgId, exceptionId, "ignored", ct);

    private async Task<bool> SetStateAsync(Guid orgId, Guid exceptionId, string state, CancellationToken ct)
    {
        var ex = await _db.OrderExceptions
            .Where(e => e.Id == exceptionId && e.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (ex is null) return false;
        ex.State = state;
        if (state == "resolved") ex.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
