using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-23 — the resolve status guard on the two endpoints that share the status recompute.
///
/// <para><b>The defect.</b> <c>OrderResolutionService.ResolveAsync</c> (:242) and
/// <c>AcceptAiSuggestionsAsync</c> (:393) both end with an unconditional
/// <c>Status = Lines.Any(NeedsReview) ? pending_review : ready</c>, and neither endpoint checked the
/// order's status first — <c>OrdersController.Resolve</c> validated the request BODY only, and
/// <c>AcceptAiSuggestions</c> validated nothing at all. The single guard on the path was
/// <c>IsFinished</c> = <see cref="OrderStatusMachine.DeclaredTerminal"/> = {failed}, so a resolve
/// reached 14 from-states. c61fe30 reconciled the resulting edges into both status maps — correctly,
/// because the write was real — and NAMED the two from-states where the write destroys something:
/// <c>unrouted</c> (recomputes the order out of the routing hold with SupplierId still null, after
/// which <c>AssignSupplier</c>'s <c>Status == Unrouted</c> claim answers 409 forever) and
/// <c>delivering</c> (writes over a live dispatch claim).
///
/// <para><b>Derived, not transcribed.</b> Every theory below takes its rows from
/// <see cref="OrderStatusMachine"/> — the refusals from <see cref="OrderStatusMachine.ResolveHeldFrom"/>
/// and the positive controls from <see cref="OrderStatusMachine.AllStatuses"/> minus that set. A
/// status added to the machine tomorrow gets a "must NOT be refused" row for free; a status added to
/// the held set gets a "must be refused, with its own actionable sentence" row for free. Neither can
/// silently escape the guard, which is the failure mode a hand-written list has every time.</para>
/// </summary>
public class OrdersControllerResolveStatusGuardTests
{
    // ── Derived theory sources — the single reason this file cannot rot ────────

    /// <summary>Every status an operator-issued resolve must be REFUSED from.</summary>
    public static TheoryData<string> HeldStatuses()
    {
        var data = new TheoryData<string>();
        foreach (var s in OrderStatusMachine.ResolveHeldFrom.OrderBy(s => s, StringComparer.Ordinal))
            data.Add(s);
        return data;
    }

    /// <summary>
    /// Every other status the machine knows — the positive control. c61fe30 deliberately made the
    /// recompute legal from delivered / delivery_failed / rejected_by_supplier / ready_to_deliver /
    /// delivery_held / delivery_unconfirmed / delivery_dead_letter, so a guard that blocked any of
    /// them would undo that packet. <c>failed</c> is in here too, on purpose: it is refused one layer
    /// down by the service's terminal guard, and this endpoint guard must not take that case over.
    ///
    /// <para><b>WP-23a moved two rows out of here, and that is the whole point of deriving them.</b>
    /// <c>parsing</c> and <c>transforming</c> used to sit in this list asserting that THIS GUARD does
    /// not refuse them — true at the time, and never the same claim as "should not". Both were
    /// evidenced holes recorded on <see cref="OrderStatusMachine.ResolveHeldFrom"/> and left open for
    /// a product decision about removing an operator control; WP-23a made that decision and added
    /// them to the set, at which point these two rows became <see cref="HeldStatuses"/> rows with no
    /// edit in this file. <c>transforming</c> also left the c61fe30 sentence above for the same
    /// reason: an endpoint that refuses a status does not prune its map edges, and
    /// <c>OrderStatusMachineTests.EveryResolveHeldStatus_KeepsBothRecomputeEdges</c> is what keeps
    /// that honest.</para>
    /// </summary>
    public static TheoryData<string> NotHeldStatuses()
    {
        var data = new TheoryData<string>();
        foreach (var s in OrderStatusMachine.AllStatuses
                     .Except(OrderStatusMachine.ResolveHeldFrom, StringComparer.Ordinal)
                     .OrderBy(s => s, StringComparer.Ordinal))
            data.Add(s);
        return data;
    }

    // ── POST /api/orders/{id}/resolve ─────────────────────────────────────────

    [Theory]
    [MemberData(nameof(HeldStatuses))]
    public async Task Resolve_FromAHeldStatus_Returns409_AndNeverReachesTheRecompute(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.Resolve(orderId, WithLine(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    /// <summary>
    /// The header-only resolve is the shape that made <c>unrouted</c> a real hole rather than a
    /// theoretical one: the endpoint accepts a body with no line resolutions at all, so
    /// <c>{"poNumber":"X"}</c> was enough to recompute an order out of the routing hold.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeldStatuses))]
    public async Task Resolve_HeaderOnlyEditFromAHeldStatus_IsRefusedToo(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.Resolve(
            orderId,
            new ResolveRequest { PoNumber = "PO-CORRECTED-1" },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(NotHeldStatuses))]
    public async Task Resolve_FromEveryOtherStatus_IsNotRefusedByThisGuard(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.Resolve(orderId, WithLine(), CancellationToken.None);

        Assert.IsNotType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Once);
    }

    // ── POST /api/orders/{id}/accept-ai-suggestions ───────────────────────────
    //
    // The same writer, reached by a different door. AcceptAiSuggestionsAsync's recompute runs even
    // when NOTHING is accepted — "zero suggestions over zero lines still writes 'ready'", as its own
    // comment says (OrderResolutionService.cs:358) — so guarding /resolve alone would leave both
    // named holes wide open here.

    [Theory]
    [MemberData(nameof(HeldStatuses))]
    public async Task AcceptAiSuggestions_FromAHeldStatus_Returns409_AndNeverReachesTheRecompute(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.AcceptAiSuggestions(orderId, 0.85, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        orders.Verify(o => o.AcceptAiSuggestionsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [MemberData(nameof(NotHeldStatuses))]
    public async Task AcceptAiSuggestions_FromEveryOtherStatus_IsNotRefusedByThisGuard(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.AcceptAiSuggestions(orderId, 0.85, CancellationToken.None);

        Assert.IsNotType<ConflictObjectResult>(result);
        orders.Verify(o => o.AcceptAiSuggestionsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── WP-23a — the two machine-owned steps ──────────────────────────────────

    /// <summary>
    /// WP-23a — <c>parsing</c> and <c>transforming</c> are refused too, asserted as LITERALS.
    ///
    /// <para><b>Why literals here, when every other theory in this file derives its rows.</b> The
    /// derived rows move with <see cref="OrderStatusMachine.ResolveHeldFrom"/> by design: whatever
    /// the set says, they assert. That is what keeps them from rotting, and it is also why they can
    /// never prove the set CONTAINS a status — remove <c>parsing</c> from it and
    /// <see cref="NotHeldStatuses"/> silently grows a row asserting the opposite, green either way.
    /// These two rows are the decision itself, pinned at the endpoint where an operator meets it.
    /// (<c>OrderStatusMachineTests.ResolveHeldFrom_IsExactlyTheStatusesThisGuardRefuses</c> pins the
    /// same decision one layer down, on the set's membership rather than on the 409.)</para>
    ///
    /// <para><b>What each one destroys.</b>
    /// <br/><c>parsing</c> — both parse-persist claims require <c>Status == Parsing</c>
    /// (<c>ParseStoredFileAsync</c>'s and <c>ResolvePersistedLinesAsync</c>'s;
    /// <c>OrderIngestionService.cs:1188</c> / <c>:1479</c> on <c>main</c> @ <c>c8ae076</c>) and return
    /// <c>Fail</c> on 0 rows BEFORE the lines are inserted (<c>:1238</c> / <c>:1490</c>). Worse than a
    /// discarded parse: the <c>Fail</c> makes <c>ParseOrderJob</c> throw (<c>:55</c>), Hangfire
    /// retries, the retry hits <c>ParseStoredFileAsync</c>'s re-entry guard
    /// (<c>if (entity.Status != "parsing")</c>, <c>:843-844</c>) which returns <c>Ok</c> for an order
    /// that has left <c>parsing</c>, and the terminal-failure guard at <c>ParseOrderJob.cs:67</c>
    /// re-throws only for <c>failed</c> — so the job lands GREEN and nothing anywhere reports the
    /// loss.
    /// <br/><c>transforming</c> — the transform CLAIM is atomic
    /// (<c>OrderTransformService.cs:361-368</c>) but its completion write is not
    /// (<c>:733</c>, a tracked <c>Status = ready_to_deliver</c> on an entity with no concurrency
    /// token), so it lands unconditionally over the correction — and the artifact it commits was
    /// built from the graph loaded BEFORE the correction, with
    /// <c>TransformOrderJob.cs:120</c> enqueueing delivery immediately after. A correction that
    /// leaves a line needing review is overwritten the same way, so the recompute's own
    /// <c>pending_review</c> hold does not survive either.</para>
    /// </summary>
    [Theory]
    [InlineData(OrderStatusConstants.Parsing)]
    [InlineData(OrderStatusConstants.Transforming)]
    public async Task Resolve_WhileAMachineOwnedStepIsRunning_Returns409_AndNeverReachesTheRecompute(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.Resolve(orderId, WithLine(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    /// <summary>
    /// The header-only shape, on the two new statuses. It is the one that makes <c>parsing</c>
    /// reachable with nothing to resolve at all: an order is created with ZERO lines and
    /// <c>Status = parsing</c> (<c>OrderIngestionService.cs:341</c>), so <c>{"poNumber":"X"}</c>
    /// recomputes it to <c>ready</c> over an empty line set — after which
    /// <c>OrdersController.Transform</c>'s <c>Lines.Count == 0</c> guard (<c>:1599</c>) refuses it
    /// with "Order is still parsing", a sentence that is no longer true and names no way out.
    /// </summary>
    [Theory]
    [InlineData(OrderStatusConstants.Parsing)]
    [InlineData(OrderStatusConstants.Transforming)]
    public async Task Resolve_HeaderOnlyEditWhileAMachineOwnedStepIsRunning_IsRefusedToo(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.Resolve(
            orderId,
            new ResolveRequest { PoNumber = "PO-CORRECTED-1" },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Never);
    }

    /// <summary>
    /// The second door, on the two new statuses. <c>AcceptAiSuggestionsAsync</c>'s recompute runs even
    /// when nothing is accepted (<c>OrderResolutionService.cs:358</c>), so leaving this door open
    /// would leave both steps destroyable by a POST that changes nothing.
    /// </summary>
    [Theory]
    [InlineData(OrderStatusConstants.Parsing)]
    [InlineData(OrderStatusConstants.Transforming)]
    public async Task AcceptAiSuggestions_WhileAMachineOwnedStepIsRunning_Returns409_AndNeverReachesTheRecompute(string status)
    {
        var (ctrl, orders, orderId) = await BuildWithOrderAsync(status);

        var result = await ctrl.AcceptAiSuggestions(orderId, 0.85, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        orders.Verify(o => o.AcceptAiSuggestionsAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── The refusal an operator actually reads ────────────────────────────────

    /// <summary>
    /// R5 proof obligation for <c>ResolveHoldMessage</c>'s claim that every sentence names what to DO,
    /// echoes no status token, and hardcodes no party noun. Asserted over the DERIVED set, so a
    /// status added to <see cref="OrderStatusMachine.ResolveHeldFrom"/> tomorrow cannot ship the
    /// generic fallback or a status-code echo.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeldStatuses))]
    public async Task Resolve_RefusalMessage_IsPlainLanguageAnOperatorCanActOn(string status)
    {
        var (ctrl, _, orderId) = await BuildWithOrderAsync(status);

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.Resolve(orderId, WithLine(), CancellationToken.None));

        var message = ErrorOf(conflict);

        // A sentence, not a code.
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.True(message.Length >= 60, $"'{message}' is too terse to be an explanation.");
        Assert.EndsWith(".", message.Trim(), StringComparison.Ordinal);

        // Not a status-code echo: no raw status token, as a whole word, anywhere in the sentence.
        foreach (var token in OrderStatusMachine.AllStatuses)
            Assert.False(
                Regex.IsMatch(message, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase),
                $"the refusal for '{status}' echoes the raw status token '{token}': {message}");

        // Founder decision — buyers first, inbound stays supported. A user-facing sentence may not
        // hardcode which side of the exchange the counterparty is on.
        foreach (var party in new[] { "supplier", "buyer", "vendor", "customer" })
            Assert.False(
                Regex.IsMatch(message, $@"\b{Regex.Escape(party)}s?\b", RegexOptions.IgnoreCase),
                $"the refusal for '{status}' hardcodes the party noun '{party}': {message}");

        // Actionable: it tells the operator what to do next, not merely that they may not.
        Assert.True(
            Regex.IsMatch(message, @"\b(then|first|wait|try again)\b", RegexOptions.IgnoreCase),
            $"the refusal for '{status}' does not tell the operator what to do next: {message}");
    }

    /// <summary>
    /// Each held status gets its OWN sentence. Without this, adding a status to
    /// <see cref="OrderStatusMachine.ResolveHeldFrom"/> would silently ship the generic fallback —
    /// which passes every check above while telling the operator nothing specific.
    /// </summary>
    [Fact]
    public async Task EveryHeldStatus_GetsItsOwnRefusal_NotTheGenericFallback()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallback = OrderStatusMachine.ResolveHoldMessage("a-status-the-machine-has-never-heard-of");

        foreach (var status in OrderStatusMachine.ResolveHeldFrom)
        {
            var (ctrl, _, orderId) = await BuildWithOrderAsync(status);
            var conflict = Assert.IsType<ConflictObjectResult>(
                await ctrl.Resolve(orderId, WithLine(), CancellationToken.None));

            var message = ErrorOf(conflict);
            Assert.False(string.Equals(message, fallback, StringComparison.Ordinal),
                $"'{status}' ships the generic fallback instead of a sentence about its own case.");
            seen[status] = message;
        }

        Assert.Equal(OrderStatusMachine.ResolveHeldFrom.Count, seen.Values.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Scoping ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The guard reads the order's status, so it must be org-scoped like every other read in this
    /// controller. Another tenant's held order must not answer 409 — that would confirm the row
    /// exists. It falls through to the normal not-found path instead.
    /// </summary>
    [Fact]
    public async Task Resolve_AHeldOrderInAnotherOrg_IsNotRefusedByThisGuard()
    {
        var status  = OrderStatusMachine.ResolveHeldFrom.OrderBy(s => s, StringComparer.Ordinal).First();
        var orderId = Guid.NewGuid();

        var (ctrl, orders, _) = Build(out var db, out _);
        db.PurchaseOrders.Add(NewOrder(orderId, Guid.NewGuid(), status)); // a DIFFERENT org
        await db.SaveChangesAsync();

        var result = await ctrl.Resolve(orderId, WithLine(), CancellationToken.None);

        Assert.IsNotType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Once);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static string ErrorOf(ConflictObjectResult conflict)
    {
        var value = conflict.Value!;
        var prop  = value.GetType().GetProperty("error");
        Assert.NotNull(prop);
        return (string)prop!.GetValue(value)!;
    }

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PurchaseOrderEntity NewOrder(Guid id, Guid orgId, string status) => new()
    {
        Id        = id,
        OrgId     = orgId,
        Status    = status,
        PoNumber  = "PO-1",
        Currency  = "EUR",
        OrderDate = new DateOnly(2026, 7, 31),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ResolveRequest WithLine() => new()
    {
        LineResolutions = new() { new Contracts.LineResolution { LineNumber = 1, SupplierItemCode = "SUP-1" } },
    };

    private static async Task<(OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrderId)>
        BuildWithOrderAsync(string status)
    {
        var orderId = Guid.NewGuid();
        var (ctrl, orders, _) = Build(out var db, out var orgId);

        db.PurchaseOrders.Add(NewOrder(orderId, orgId, status));
        await db.SaveChangesAsync();

        return (ctrl, orders, orderId);
    }

    private static (OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrgId)
        Build(out ProcuLinkDbContext db, out Guid organisationId)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        // The service is mocked and answers success, so ANY result other than 409 means the guard
        // let the request through — which is exactly what the positive controls need to observe.
        var orders = new Mock<IOrderService>();
        orders
            .Setup(s => s.ResolveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(NewOrder(Guid.NewGuid(), orgId, "ready")));
        orders
            .Setup(s => s.AcceptAiSuggestionsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Ok(0));

        db = NewDb();
        organisationId = orgId;

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            new Mock<Hangfire.IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService>().Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        return (ctrl, orders, orgId);
    }
}
