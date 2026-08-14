using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.Support;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Service-level tests for <see cref="OrderConfirmationService"/> over InMemory EF.
/// Covers: a confirmation links to the correct order/lines; a changed quantity (or price/date)
/// triggers NeedsReview; a rejected confirmation blocks the order from being "completed".
/// </summary>
public class OrderConfirmationServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderConfirmationService NewService(ProcuLinkDbContext db) =>
        new(db, new InMemoryFileStorage());

    /// <summary>Seed one order with two lines and return the ids needed by the tests.</summary>
    private static async Task<(Guid OrgId, Guid OrderId, Guid Line1Id, Guid Line2Id)> SeedOrderAsync(
        ProcuLinkDbContext db)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var line1Id    = Guid.NewGuid();
        var line2Id    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-OC-001",
            OrderDate  = DateOnly.FromDateTime(now),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Delivered,
            CreatedAt  = now,
            UpdatedAt  = now,
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = line1Id, OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "BUY-1", SupplierItemCode = "SUP-1",
            Quantity = 10m, UnitPrice = 5.00m,
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = line2Id, OrderId = orderId, LineNumber = 2,
            BuyerItemCode = "BUY-2", SupplierItemCode = "SUP-2",
            Quantity = 3m, UnitPrice = 20.00m,
        });
        await db.SaveChangesAsync();
        return (orgId, orderId, line1Id, line2Id);
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_AllLinesMatchOrdered_LinksToOrderAndLines_StatusAccepted()
    {
        await using var db = NewDb();
        var (orgId, orderId, line1Id, line2Id) = await SeedOrderAsync(db);

        var data = new ParsedConfirmationData(
            Status: null, SupplierReference: "SUP-ACK-77", Notes: null, ReceivedAt: null,
            Lines: new[]
            {
                // Confirm both lines exactly as ordered.
                new ParsedConfirmationLineData(line1Id, 1, ConfirmedQuantity: 10m, ConfirmedUnitPrice: 5.00m, ConfirmedDeliveryDate: null),
                new ParsedConfirmationLineData(line2Id, 2, ConfirmedQuantity: 3m,  ConfirmedUnitPrice: 20.00m, ConfirmedDeliveryDate: null),
            });

        var svc = NewService(db);
        var confirmation = await svc.RecordAsync(orgId, orderId, data, default);

        // Links to the correct order.
        confirmation.PurchaseOrderId.Should().Be(orderId);
        confirmation.OrgId.Should().Be(orgId);
        confirmation.SupplierReference.Should().Be("SUP-ACK-77");

        // Links to the correct ordered lines, snapshots ordered values, and confirms each.
        confirmation.Lines.Should().HaveCount(2);
        var l1 = confirmation.Lines.Single(l => l.LineNumber == 1);
        l1.PurchaseOrderLineId.Should().Be(line1Id);
        l1.OrderedQuantity.Should().Be(10m);
        l1.OrderedUnitPrice.Should().Be(5.00m);
        l1.BuyerItemCode.Should().Be("BUY-1");
        l1.State.Should().Be(OrderConfirmationLineState.Confirmed);

        // No changes ⇒ accepted.
        confirmation.Status.Should().Be(OrderConfirmationStatus.Accepted);

        // Persisted and retrievable, org-scoped.
        var reloaded = await svc.GetAsync(orgId, confirmation.Id, default);
        reloaded.Should().NotBeNull();
        reloaded!.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task RecordAsync_ChangedQuantity_MarksLineChanged_AndStatusNeedsReview()
    {
        await using var db = NewDb();
        var (orgId, orderId, line1Id, line2Id) = await SeedOrderAsync(db);

        var data = new ParsedConfirmationData(
            Status: null, SupplierReference: null, Notes: null, ReceivedAt: null,
            Lines: new[]
            {
                // Line 1: supplier confirms only 8 of the 10 ordered → changed.
                new ParsedConfirmationLineData(line1Id, 1, ConfirmedQuantity: 8m,  ConfirmedUnitPrice: 5.00m,  ConfirmedDeliveryDate: null),
                // Line 2: confirmed exactly.
                new ParsedConfirmationLineData(line2Id, 2, ConfirmedQuantity: 3m,  ConfirmedUnitPrice: 20.00m, ConfirmedDeliveryDate: null),
            });

        var svc = NewService(db);
        var confirmation = await svc.RecordAsync(orgId, orderId, data, default);

        var changed = confirmation.Lines.Single(l => l.LineNumber == 1);
        changed.State.Should().Be(OrderConfirmationLineState.Changed);
        changed.OrderedQuantity.Should().Be(10m);
        changed.ConfirmedQuantity.Should().Be(8m);

        var unchanged = confirmation.Lines.Single(l => l.LineNumber == 2);
        unchanged.State.Should().Be(OrderConfirmationLineState.Confirmed);

        // Any changed line (and no rejection) ⇒ needs_review.
        confirmation.Status.Should().Be(OrderConfirmationStatus.NeedsReview);

        // A needs-review confirmation does NOT block completion (only rejection does).
        var blocked = await svc.IsOrderBlockedFromCompletionAsync(orgId, orderId, default);
        blocked.Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_ChangedUnitPrice_MarksLineChanged_AndStatusNeedsReview()
    {
        await using var db = NewDb();
        var (orgId, orderId, line1Id, _) = await SeedOrderAsync(db);

        var data = new ParsedConfirmationData(
            Status: null, SupplierReference: null, Notes: null, ReceivedAt: null,
            Lines: new[]
            {
                // Same qty, different unit price → changed.
                new ParsedConfirmationLineData(line1Id, 1, ConfirmedQuantity: 10m, ConfirmedUnitPrice: 5.50m, ConfirmedDeliveryDate: null),
            });

        var svc = NewService(db);
        var confirmation = await svc.RecordAsync(orgId, orderId, data, default);

        confirmation.Lines.Single().State.Should().Be(OrderConfirmationLineState.Changed);
        confirmation.Status.Should().Be(OrderConfirmationStatus.NeedsReview);
    }

    [Fact]
    public async Task RecordAsync_RejectedConfirmation_BlocksOrderFromCompletion()
    {
        await using var db = NewDb();
        var (orgId, orderId, line1Id, line2Id) = await SeedOrderAsync(db);

        var data = new ParsedConfirmationData(
            Status: null, SupplierReference: null, Notes: "Out of stock, cannot fulfil.", ReceivedAt: null,
            Lines: new[]
            {
                // Supplier rejects line 1; line 2 confirmed. A single rejected line ⇒ whole confirmation rejected.
                new ParsedConfirmationLineData(line1Id, 1, ConfirmedQuantity: 0m, ConfirmedUnitPrice: 0m,    ConfirmedDeliveryDate: null, IsRejected: true),
                new ParsedConfirmationLineData(line2Id, 2, ConfirmedQuantity: 3m, ConfirmedUnitPrice: 20.00m, ConfirmedDeliveryDate: null),
            });

        var svc = NewService(db);
        var confirmation = await svc.RecordAsync(orgId, orderId, data, default);

        confirmation.Status.Should().Be(OrderConfirmationStatus.Rejected);
        confirmation.Lines.Single(l => l.LineNumber == 1).State.Should().Be(OrderConfirmationLineState.Rejected);

        // The rejection must prevent the order from being considered completed.
        var blocked = await svc.IsOrderBlockedFromCompletionAsync(orgId, orderId, default);
        blocked.Should().BeTrue("a supplier rejection must block the order from being completed");
    }

    [Fact]
    public async Task RecordAsync_ExplicitRejectedStatus_BlocksCompletion_EvenWhenLinesUnchanged()
    {
        await using var db = NewDb();
        var (orgId, orderId, line1Id, _) = await SeedOrderAsync(db);

        var data = new ParsedConfirmationData(
            Status: OrderConfirmationStatus.Rejected, SupplierReference: null, Notes: null, ReceivedAt: null,
            Lines: new[]
            {
                new ParsedConfirmationLineData(line1Id, 1, ConfirmedQuantity: 10m, ConfirmedUnitPrice: 5.00m, ConfirmedDeliveryDate: null),
            });

        var svc = NewService(db);
        var confirmation = await svc.RecordAsync(orgId, orderId, data, default);

        confirmation.Status.Should().Be(OrderConfirmationStatus.Rejected);
        (await svc.IsOrderBlockedFromCompletionAsync(orgId, orderId, default)).Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_UnknownOrder_Throws()
    {
        await using var db = NewDb();
        var (orgId, _, _, _) = await SeedOrderAsync(db);

        var data = new ParsedConfirmationData(
            Status: null, SupplierReference: null, Notes: null, ReceivedAt: null,
            Lines: new[] { new ParsedConfirmationLineData(null, 1, 1m, 1m, null) });

        var svc = NewService(db);
        var act = async () => await svc.RecordAsync(orgId, Guid.NewGuid(), data, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordAsync_IsScopedToOrganisation_OrderFromAnotherOrgNotFound()
    {
        await using var db = NewDb();
        var (_, orderId, line1Id, _) = await SeedOrderAsync(db);
        var otherOrg = Guid.NewGuid();

        var data = new ParsedConfirmationData(
            Status: null, SupplierReference: null, Notes: null, ReceivedAt: null,
            Lines: new[] { new ParsedConfirmationLineData(line1Id, 1, 10m, 5.00m, null) });

        var svc = NewService(db);
        var act = async () => await svc.RecordAsync(otherOrg, orderId, data, default);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "an order belonging to another organisation must not be reachable");
    }

    [Fact]
    public async Task RecordUploadStubAsync_StoresFile_CreatesNeedsReviewStubWithNoLines()
    {
        await using var db = NewDb();
        var (orgId, orderId, _, _) = await SeedOrderAsync(db);

        var svc   = NewService(db);
        var bytes = "raw supplier confirmation document"u8.ToArray();
        using var stream = new MemoryStream(bytes);

        var confirmation = await svc.RecordUploadStubAsync(
            orgId, orderId, stream, "ack.pdf", "application/pdf", default);

        confirmation.PurchaseOrderId.Should().Be(orderId);
        confirmation.Source.Should().Be("manual_upload");
        confirmation.Status.Should().Be(OrderConfirmationStatus.NeedsReview);
        confirmation.SourceFileName.Should().Be("ack.pdf");
        confirmation.SourceFileKey.Should().NotBeNullOrEmpty();
        confirmation.Lines.Should().BeEmpty();

        // An unparsed upload needs review but does not itself block completion (not a rejection).
        (await svc.IsOrderBlockedFromCompletionAsync(orgId, orderId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task ListForOrderAsync_ReturnsConfirmationsNewestFirst_OrgScoped()
    {
        await using var db = NewDb();
        var (orgId, orderId, line1Id, _) = await SeedOrderAsync(db);
        var svc = NewService(db);

        var older = await svc.RecordAsync(orgId, orderId, new ParsedConfirmationData(
            null, "FIRST", null, ReceivedAt: DateTime.UtcNow.AddHours(-2),
            new[] { new ParsedConfirmationLineData(line1Id, 1, 10m, 5.00m, null) }), default);

        var newer = await svc.RecordAsync(orgId, orderId, new ParsedConfirmationData(
            null, "SECOND", null, ReceivedAt: DateTime.UtcNow,
            new[] { new ParsedConfirmationLineData(line1Id, 1, 10m, 5.00m, null) }), default);

        var list = await svc.ListForOrderAsync(orgId, orderId, default);

        list.Should().HaveCount(2);
        list[0].Id.Should().Be(newer.Id, "newest confirmation should come first");
        list[1].Id.Should().Be(older.Id);

        // Another org sees nothing.
        (await svc.ListForOrderAsync(Guid.NewGuid(), orderId, default)).Should().BeEmpty();
    }

    // ── A moved delivery date is a change ─────────────────────────────────────────

    /// <summary>
    /// Seed one order whose single line carries an ORDERED delivery date — the shape
    /// <c>CxmlOrderParser</c>, <c>IDocOrders05Parser</c> and <c>OpenAiPdfOrderExtractor</c> all
    /// produce. Every other test in this file seeds lines with no date, which is why the date arm of
    /// <c>IsChanged</c> was never exercised in either direction.
    /// </summary>
    private static async Task<(Guid OrgId, Guid OrderId, Guid LineId)> SeedOrderWithDeliveryDateAsync(
        ProcuLinkDbContext db, DateOnly? orderedDeliveryDate)
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId  = Guid.NewGuid();
        var now     = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-OC-DATE-001",
            OrderDate  = DateOnly.FromDateTime(now),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Delivered,
            CreatedAt  = now,
            UpdatedAt  = now,
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = lineId, OrderId = orderId, LineNumber = 1,
            BuyerItemCode = "BUY-1", SupplierItemCode = "SUP-1",
            Quantity = 10m, UnitPrice = 5.00m,
            DeliveryDate = orderedDeliveryDate,
        });
        await db.SaveChangesAsync();
        return (orgId, orderId, lineId);
    }

    /// <summary>
    /// THE CONTROL FOR THE DEFECT. A supplier confirms the order at the ordered quantity and price
    /// but MOVES THE DELIVERY DATE — the single change a buyer most needs to see. It was recorded as
    /// an unchanged "accepted".
    ///
    /// <para>The cause was <c>OrderConfirmationService</c> hardcoding <c>orderedDate = null</c>
    /// under a comment claiming purchase-order lines carry no per-line delivery date. They do
    /// (<c>PurchaseOrderLineEntity.DeliveryDate</c>), and three parsers populate it — so the date arm
    /// of <c>IsChanged</c> was unreachable and the confirmation status derived from "nothing
    /// changed".</para>
    /// </summary>
    [Fact]
    public async Task RecordAsync_SupplierMovesTheDeliveryDate_IsChanged_NotAcceptedUnchanged()
    {
        await using var db = NewDb();
        var ordered  = new DateOnly(2026, 9, 1);
        var moved    = new DateOnly(2026, 9, 15);
        var (orgId, orderId, lineId) = await SeedOrderWithDeliveryDateAsync(db, ordered);
        var svc = NewService(db);

        // Quantity and price EXACTLY as ordered — the date is the only thing the supplier changed.
        var confirmation = await svc.RecordAsync(orgId, orderId, new ParsedConfirmationData(
            Status: null, SupplierReference: "SUP-ACK-DATE", Notes: null, ReceivedAt: null,
            Lines: new[]
            {
                new ParsedConfirmationLineData(
                    lineId, 1, ConfirmedQuantity: 10m, ConfirmedUnitPrice: 5.00m,
                    ConfirmedDeliveryDate: moved),
            }), default);

        confirmation.Lines.Should().ContainSingle();

        // The classification is the claim in this test's name, so it is asserted FIRST — a later
        // assertion never reports when an earlier one throws, and the defect is the classification,
        // not the baseline field that feeds it.
        confirmation.Lines[0].State.Should().Be(
            OrderConfirmationLineState.Changed,
            "the supplier moved delivery from {0} to {1}", ordered, moved);
        confirmation.Status.Should().Be(
            OrderConfirmationStatus.NeedsReview,
            "a changed line derives needs_review — this used to read 'accepted'");
        confirmation.Lines[0].OrderedDeliveryDate.Should().Be(
            ordered, "the ordered baseline must be READ from the line, not snapshotted as null");
    }

    /// <summary>
    /// The negative control. A date the supplier confirms UNCHANGED must still be an acceptance —
    /// otherwise the fix would flip every dated confirmation to needs_review.
    /// </summary>
    [Fact]
    public async Task RecordAsync_SupplierConfirmsTheSameDeliveryDate_IsAccepted()
    {
        await using var db = NewDb();
        var ordered = new DateOnly(2026, 9, 1);
        var (orgId, orderId, lineId) = await SeedOrderWithDeliveryDateAsync(db, ordered);
        var svc = NewService(db);

        var confirmation = await svc.RecordAsync(orgId, orderId, new ParsedConfirmationData(
            null, "SUP-ACK-SAME", null, null,
            new[] { new ParsedConfirmationLineData(lineId, 1, 10m, 5.00m, ConfirmedDeliveryDate: ordered) }), default);

        confirmation.Lines[0].State.Should().Be(OrderConfirmationLineState.Confirmed);
        confirmation.Status.Should().Be(OrderConfirmationStatus.Accepted);
    }

    /// <summary>
    /// The other negative control, and the one the fix most easily gets wrong. Eight of the eleven
    /// line-producing parsers never populate <c>DeliveryDate</c>, so a null ordered date is the
    /// COMMON case, not an edge case. A genuinely absent baseline means "we never parsed a date" —
    /// it must not be read as "the supplier changed the date", or every CSV, XLSX, UBL, EDIFACT and
    /// X12 order whose supplier states a date would flip to needs_review.
    /// </summary>
    [Fact]
    public async Task RecordAsync_NoOrderedDateParsed_SupplierStatesOne_IsNotTreatedAsAChange()
    {
        await using var db = NewDb();
        var (orgId, orderId, lineId) = await SeedOrderWithDeliveryDateAsync(db, orderedDeliveryDate: null);
        var svc = NewService(db);

        var confirmation = await svc.RecordAsync(orgId, orderId, new ParsedConfirmationData(
            null, "SUP-ACK-NODATE", null, null,
            new[] { new ParsedConfirmationLineData(lineId, 1, 10m, 5.00m,
                        ConfirmedDeliveryDate: new DateOnly(2026, 9, 15)) }), default);

        confirmation.Lines[0].OrderedDeliveryDate.Should().BeNull(
            "the order never carried a date — the confirmation must not invent one as the baseline");
        confirmation.Lines[0].State.Should().Be(
            OrderConfirmationLineState.Confirmed,
            "an absent baseline is not evidence of a change");
        confirmation.Status.Should().Be(OrderConfirmationStatus.Accepted);
    }

    /// <summary>
    /// A moved date on top of a changed quantity still reports changed — and, more usefully, the
    /// ordered date is recorded on the confirmation line either way, so the review UI can show what
    /// the supplier moved it FROM.
    /// </summary>
    [Fact]
    public async Task RecordAsync_MovedDate_RecordsTheOrderedBaseline_ForTheReviewer()
    {
        await using var db = NewDb();
        var ordered = new DateOnly(2026, 9, 1);
        var (orgId, orderId, lineId) = await SeedOrderWithDeliveryDateAsync(db, ordered);
        var svc = NewService(db);

        var confirmation = await svc.RecordAsync(orgId, orderId, new ParsedConfirmationData(
            null, "SUP-ACK-BOTH", null, null,
            new[] { new ParsedConfirmationLineData(lineId, 1, 8m, 5.00m,
                        ConfirmedDeliveryDate: new DateOnly(2026, 9, 20)) }), default);

        confirmation.Lines[0].OrderedDeliveryDate.Should().Be(ordered);
        confirmation.Lines[0].ConfirmedDeliveryDate.Should().Be(new DateOnly(2026, 9, 20));
        confirmation.Lines[0].State.Should().Be(OrderConfirmationLineState.Changed);
    }
}
