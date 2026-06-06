using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IOrderExceptionService"/>.
/// All exception generation flows through the idempotent <see cref="ReconcileAsync"/>:
/// callers never construct exceptions directly. Reconcile is the single source of truth
/// for clearing — it compares the order's current status + lines against existing
/// exceptions and decides resolve-vs-recreate per (orderId, code):
///   • condition still holds + no open/ignored row for that code → OPEN a new one;
///   • condition still holds + an open/ignored row already exists  → leave it (no dup);
///   • condition no longer holds + an open row exists              → AUTO-RESOLVE it;
///   • condition no longer holds + only a resolved row exists      → do NOTHING
///     (a resolved row for a cleared condition is never resurrected).
/// Rows the operator has <c>ignored</c> are never auto-resolved or reopened, and an
/// ignored row for the current problem suppresses opening a duplicate (don't nag about
/// something deliberately dismissed). A manual resolve on a still-broken order is the
/// one case Reconcile deliberately overrides: because the underlying condition still
/// holds, a fresh <c>open</c> row is recreated so the unresolved problem stays visible.
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
        if (status == OrderStatusConstants.Failed)
            return ("parse_failed", "Parse", "error", "Parsing the source file failed for this order.");
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

        // Load EVERY exception for this order — open, ignored AND resolved — so dedup can
        // consider resolved rows too. Considering resolved rows is what stops a resolved
        // exception for a now-cleared condition from being silently resurrected as a fresh
        // open one on the next pipeline touch.
        var existing = await _db.OrderExceptions
            .Where(e => e.OrgId == orgId && e.OrderId == orderId)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        // Auto-resolve OPEN exceptions whose condition no longer holds (so fixing the order
        // really clears them). Ignored rows are never touched — the operator dismissed them
        // deliberately. Resolved rows are already terminal and left alone.
        foreach (var ex in existing)
        {
            if (ex.State != "open") continue;
            if (problem is null || ex.Code != problem.Value.Code)
            {
                ex.State      = "resolved";
                ex.ResolvedAt = now;
            }
        }

        // Recreate decision (resolve-vs-recreate): open a fresh exception ONLY when the
        // current condition holds AND there is no open OR ignored row for that code.
        //  - A still-present condition with no live row → recreate (covers a manual resolve
        //    on a still-broken order: the problem is real, so it must stay visible).
        //  - A condition that is gone → `problem` won't match any code, so nothing is
        //    recreated and a resolved row stays resolved (no resurrection).
        // Resolved rows are intentionally NOT a recreate-blocker here: when the condition is
        // gone they can't match `problem.Code`, and when the condition persists the problem
        // is genuine and should reappear.
        if (problem is not null &&
            !existing.Any(e => e.Code == problem.Value.Code
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
