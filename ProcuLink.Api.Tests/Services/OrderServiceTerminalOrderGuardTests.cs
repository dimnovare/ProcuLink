using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// <c>OrderStatusMachine.DeclaredTerminal</c> says <c>failed</c> is finished, and the whole
/// dead-end invariant rests on that declaration — <c>NoNonTerminalStatus_IsADeadEnd</c> only bites
/// because <c>DeclaredTerminal</c> is stated INDEPENDENTLY of the map.
///
/// <para><b>It was not true.</b> The declaration's own justification covers re-parse
/// (<c>ParseOrderJob</c> refuses to re-drive a failed order) and transform
/// (<c>OrdersController.Transform</c> answers "Upload a corrected file"). It never covered RESOLVE
/// — and all three of <c>OrderResolutionService</c>'s status writers carried NO from-status guard:
/// <c>ResolveAsync</c> wrote <c>pending_review|ready</c> unconditionally, and so did
/// <c>AcceptAiSuggestionsAsync</c>; <c>MarkRejectedAsync</c> wrote <c>rejected_by_supplier</c>. On a
/// lineless <c>failed</c> order (a source file that never parsed produces no lines) each of them
/// evaluates <c>Lines.Any(NeedsReview)</c> over an empty collection, lands on <c>ready</c> — and
/// <c>ready</c> is transformable and then deliverable. A header-only resolve was enough:
/// <c>POST /api/orders/{failedId}/resolve {"poNumber":"X"}</c>.</para>
///
/// <para><b>Mark-rejected is the worst of the three</b>, because WP-19 deliberately gave
/// <c>rejected_by_supplier</c> exits (resolve / re-transform). Marking a failed order rejected
/// therefore launders it into a status that has a documented way back to <c>ready</c> — around any
/// guard placed on resolve alone.</para>
///
/// <para>The product's answer is the one its own copy already gives: a bad SOURCE FILE cannot be
/// fixed in place, and recovery is a NEW order row. So the guard belongs on the writers, and the
/// declaration becomes true rather than aspirational. These tests pin that, and the positive
/// controls pin that the guard is narrow — it must refuse exactly the statuses the product declares
/// finished, and nothing else.</para>
/// </summary>
public class OrderServiceTerminalOrderGuardTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OrderService BuildService(ProcuLinkDbContext db)
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.UpsertAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<MappingSource>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            new Mock<IFileStorageService>().Object,
            new OrderParserFactory(new IPurchaseOrderParser[]
            {
                new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser(),
            }),
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    /// <summary>
    /// A source file that never parsed leaves NO lines behind — which is exactly why every
    /// <c>Lines.Any(l =&gt; l.NeedsReview)</c> recompute lands on <c>ready</c> for one of these.
    /// </summary>
    private static async Task<(ProcuLinkDbContext Db, Guid OrgId, Guid OrderId)> SeedLinelessOrderAsync(
        string status)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-TERMINAL-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
        return (db, orgId, orderId);
    }

    private static ResolveHeaderFields HeaderOnlyEdit() =>
        new(OrderDate: null, BuyerName: null, Currency: null, PoNumber: "PO-EDITED", SupplierName: null);

    // ── The three writers ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_OnAFailedOrder_IsRefused_AndLeavesTheStatusAlone()
    {
        var (db, orgId, orderId) = await SeedLinelessOrderAsync(OrderStatusConstants.Failed);
        var svc = BuildService(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            Array.Empty<LineResolution>(),
            saveMappings: false,
            CancellationToken.None,
            HeaderOnlyEdit());

        result.IsSuccess.Should().BeFalse(
            "'failed' means the SOURCE FILE could not be read. A header-only resolve recomputes the " +
            "status from lines that do not exist, lands on 'ready', and hands the rest of the " +
            "pipeline an order whose document never parsed — one that transform and delivery both " +
            "admit, because they only look at the status");

        (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId))
            .Status.Should().Be(OrderStatusConstants.Failed);
    }

    [Fact]
    public async Task MarkRejectedAsync_OnAFailedOrder_IsRefused_AndLeavesTheStatusAlone()
    {
        var (db, orgId, orderId) = await SeedLinelessOrderAsync(OrderStatusConstants.Failed);
        var svc = BuildService(db);

        var result = await svc.MarkRejectedAsync(orgId, orderId, "no supplier ever saw this", CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "no supplier read a document that never parsed, so the claim is false on its face — and " +
            "WP-19 gave rejected_by_supplier exits to 'ready' and 'transforming', which turns this " +
            "into a laundering route around any guard placed on resolve alone");

        (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId))
            .Status.Should().Be(OrderStatusConstants.Failed);
    }

    [Fact]
    public async Task AcceptAiSuggestionsAsync_OnAFailedOrder_IsRefused_AndLeavesTheStatusAlone()
    {
        var (db, orgId, orderId) = await SeedLinelessOrderAsync(OrderStatusConstants.Failed);
        var svc = BuildService(db);

        var result = await svc.AcceptAiSuggestionsAsync(orgId, orderId, minConfidence: 0.5, CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "the bulk-accept recompute is the SAME unconditional status write as resolve's, and it " +
            "runs even when it accepts nothing at all — zero suggestions on zero lines still writes " +
            "'ready'");

        (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId))
            .Status.Should().Be(OrderStatusConstants.Failed);
    }

    // ── The guard must be NARROW: only what the product declares finished ──────────────────────

    [Fact]
    public async Task ResolveAsync_OnAPendingReviewOrder_StillRecomputesTheStatus()
    {
        var (db, orgId, orderId) = await SeedLinelessOrderAsync(OrderStatusConstants.PendingReview);
        var svc = BuildService(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            Array.Empty<LineResolution>(),
            saveMappings: false,
            CancellationToken.None,
            HeaderOnlyEdit());

        result.IsSuccess.Should().BeTrue("the review loop is the path resolve exists for");
        result.Value!.Status.Should().Be(OrderStatusConstants.Ready);
        result.Value.PoNumber.Should().Be("PO-EDITED");
    }

    /// <summary>
    /// The exit WP-19 gave <c>rejected_by_supplier</c> is a CORRECTION loop driven by resolve. If
    /// the terminal guard swallowed that, this packet would have re-created the dead end it exists
    /// to end — with a guard instead of a missing edge.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OnARejectedOrder_StillOpensTheCorrectionLoop()
    {
        var (db, orgId, orderId) = await SeedLinelessOrderAsync(OrderStatusConstants.RejectedBySupplier);
        var svc = BuildService(db);

        var result = await svc.ResolveAsync(
            orgId, orderId,
            Array.Empty<LineResolution>(),
            saveMappings: false,
            CancellationToken.None,
            HeaderOnlyEdit());

        result.IsSuccess.Should().BeTrue(
            "resolve is one of the two named production writers behind rejected_by_supplier's exit " +
            "(OrderStatusMachineTests.RejectedBySupplier_ExitsThroughACorrectionLoop_NotARedelivery); " +
            "refusing it here would restore the dead end WP-19 removed");
        result.Value!.Status.Should().Be(OrderStatusConstants.Ready);
    }
}
