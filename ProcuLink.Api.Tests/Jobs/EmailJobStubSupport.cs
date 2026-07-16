using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Helpers for the EmailPollOrgJob unit tests: a Moq <see cref="IStubOrderCreator"/> setup must now
/// SELF-COMMIT the order under the caller-supplied id (find-or-create on the primary key), because
/// the poller's resume-on-conflict decision checks <c>purchase_orders</c> by that id. A non-persisting
/// stub would make every re-poll RESUME (and thus duplicate) instead of skipping.
/// </summary>
internal static class EmailJobStub
{
    /// <summary>Self-commit a minimal order under <paramref name="orderId"/> if it does not already exist.</summary>
    public static Result<PurchaseOrderEntity> CreateAndPersist(
        ProcuLinkDbContext db, Guid orgId, Guid? supplierId, Guid orderId)
    {
        if (!db.PurchaseOrders.Any(o => o.Id == orderId))
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId,
                PoNumber = "PO-STUB", Currency = "EUR", Status = "parsing",
                OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        return Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-STUB", Currency = "EUR", Status = "parsing",
        });
    }
}
