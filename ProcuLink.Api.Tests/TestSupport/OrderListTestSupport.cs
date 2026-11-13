using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.TestSupport;

/// <summary>
/// Shared helpers for the order-list / pagination tests. Centralises the two pieces that
/// were copy-pasted across <c>OrderServiceListPagedTests</c>,
/// <c>OrdersControllerListPagingTests</c>, and <c>OrdersListPagingPostgresTests</c>:
/// constructing a real <see cref="OrderQueryService"/>, and seeding purchase-order rows.
/// Each test still owns its own DbContext (InMemory vs Postgres) and org/supplier setup —
/// only the duplicated mechanics live here.
/// </summary>
internal static class OrderListTestSupport
{
    /// <summary>
    /// A real <see cref="OrderQueryService"/> over <paramref name="db"/> with a no-op file
    /// store. (2026-11-13, plan 13b: this built the deleted <c>OrderService</c> facade; the
    /// list/window queries it delegated to are <see cref="OrderQueryService"/>'s.)
    /// </summary>
    public static OrderQueryService BuildOrderService(ProcuLinkDbContext db) =>
        new(db, new Mock<IFileStorageService>().Object);

    /// <summary>
    /// Adds <paramref name="count"/> purchase orders for (<paramref name="orgId"/>,
    /// <paramref name="supplierId"/>) to <paramref name="db"/> WITHOUT saving — the caller saves
    /// (so it can add the parent org/supplier in the same unit of work). Returns the inserted ids
    /// in insertion order (index 0 = oldest).
    /// </summary>
    /// <param name="sharedCreatedAt">
    /// When null (default), each order is staggered one minute apart so <c>CreatedAt</c> is unique
    /// and newest-first ordering is unambiguous. When supplied, EVERY order gets that exact
    /// timestamp — the bulk-ingest tie that exercises the <c>Id</c>-DESC pagination tiebreaker.
    /// </param>
    public static List<Guid> AddOrders(
        ProcuLinkDbContext db,
        Guid orgId,
        Guid supplierId,
        int count,
        string status = "ready",
        DateTime? sharedCreatedAt = null)
    {
        var baseTime = sharedCreatedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            var createdAt = sharedCreatedAt ?? baseTime.AddMinutes(i);
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id            = id,
                OrgId         = orgId,
                SupplierId    = supplierId,
                PoNumber      = $"PO-{i + 1:D4}",
                OrderDate     = DateOnly.FromDateTime(createdAt),
                Currency      = "EUR",
                Status        = status,
                SourceFileKey = $"{orgId}/{id}/order.csv",
                CreatedAt     = createdAt,
                UpdatedAt     = createdAt,
            });
        }
        return ids;
    }
}
