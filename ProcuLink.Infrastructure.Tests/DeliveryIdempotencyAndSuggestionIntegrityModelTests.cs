using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;

namespace ProcuLink.Infrastructure.Tests;

/// <summary>
/// Pins two structural guarantees that a 2026-08 read-only DB audit found were held shut by
/// application code alone:
///
/// <list type="number">
/// <item><c>delivery_attempts.idempotency_key</c> was added bare (migration
/// <c>AddDeliveryAttemptIdempotencyKey</c>) — no index, no uniqueness. Duplicate-send protection
/// lived entirely in <c>DeliveryService.OpenDispatchAttemptAsync</c>'s read-then-insert plus the
/// status CAS. Sound today, but nothing in the database enforced it, and
/// <c>DeliveryBounceHandler</c>'s lookup by key ran a sequential scan per bounce webhook.</item>
/// <item><c>order_supplier_suggestions</c> carried no FK to <c>purchase_orders</c> — only the org
/// FK — so a raw or future delete of an order silently orphans its suggestion rows, which embed
/// document-derived identity text (<c>SignalsJson</c>) and the deciding operator's Clerk user id.
/// This table has already produced exactly that GDPR orphan once; <c>DataErasureService</c> deletes
/// the rows explicitly, but the structural guarantee was absent.</item>
/// </list>
///
/// These are EF-model pins (no relational provider needed). The behavioural halves — the unique
/// index actually rejecting a duplicate in-flight row, and a raw <c>DELETE FROM purchase_orders</c>
/// actually cascading — run against real Postgres in
/// <c>ProcuLink.Api.Tests/Integration/DeliveryIdempotencyAndSuggestionIntegrityPostgresTests</c>.
/// </summary>
public class DeliveryIdempotencyAndSuggestionIntegrityModelTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void DeliveryAttempt_HasPartialUniqueIndex_OnOrgAndIdempotencyKey_ForInFlightRowsOnly()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(DeliveryAttempt));
        entity.Should().NotBeNull();

        var index = entity!.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "UX_delivery_attempts_org_id_idempotency_key_dispatching");
        index.Should().NotBeNull(
            "the single-in-flight-attempt-per-key invariant must be DB-enforced, not application-enforced");

        index!.IsUnique.Should().BeTrue();
        index.Properties.Select(p => p.Name).Should().Equal(
            nameof(DeliveryAttempt.OrgId), nameof(DeliveryAttempt.IdempotencyKey));

        // PARTIAL, and the filter is load-bearing in both directions:
        //   - idempotency_key IS NOT NULL: legacy/test-fire rows carry null keys and must not
        //     collide with each other.
        //   - status = 'dispatching': the delivery idempotency key is DETERMINISTIC per
        //     (order, artifact), so every retry of the same artifact legitimately inserts a NEW
        //     row with the SAME key once the previous row is terminal. Full uniqueness would
        //     break the retry ladder; uniqueness over in-flight rows is exactly the invariant
        //     OpenDispatchAttemptAsync's read-then-insert assumes.
        var filter = index.GetFilter();
        filter.Should().NotBeNull();
        filter.Should().Contain("idempotency_key IS NOT NULL");
        filter.Should().Contain("status = 'dispatching'");
    }

    [Fact]
    public void DeliveryAttempt_HasLookupIndex_OnIdempotencyKey_ForTheOrgBlindBounceCorrelation()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(DeliveryAttempt));

        // DeliveryBounceHandler correlates a provider webhook to an attempt by idempotency_key
        // ALONE (deliberately org-blind: the webhook payload names no tenant and none of it is
        // trusted to; the attempt row IS the tenant boundary). The unique index above cannot
        // serve that lookup — it leads on org_id and covers only in-flight rows, while a bounce
        // almost always lands on a terminal row. This one can.
        var index = entity!.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "IX_delivery_attempts_idempotency_key");
        index.Should().NotBeNull("every bounce webhook was a sequential scan over delivery_attempts");
        index!.Properties.Select(p => p.Name).Should().Equal(nameof(DeliveryAttempt.IdempotencyKey));
        index.IsUnique.Should().BeFalse("terminal retries legitimately share a key");
        index.GetFilter().Should().Contain("idempotency_key IS NOT NULL");
    }

    [Fact]
    public void OrderSupplierSuggestion_HasCascadingForeignKey_ToPurchaseOrders()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(OrderSupplierSuggestion));
        entity.Should().NotBeNull();

        var fk = entity!.GetForeignKeys()
            .SingleOrDefault(f => f.PrincipalEntityType.ClrType == typeof(PurchaseOrderEntity));
        fk.Should().NotBeNull(
            "without this FK, any delete of an order that bypasses DataErasureService re-opens the "
            + "GDPR orphan this table has already caused once");
        fk!.Properties.Select(p => p.Name).Should().Equal(nameof(OrderSupplierSuggestion.OrderId));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade,
            "suggestion rows are erasable order content — they must die with the order");
    }
}
