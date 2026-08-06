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
using ProcuLink.Core.Services.Mapping;
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
///
/// <para><b>MV-2 added the third endpoint.</b> <c>PUT /api/orders/{id}/mapping-override</c> was the
/// last door onto an in-flight order with no from-status gate, and it did the same damage by a
/// different mechanism: the edit was ACCEPTED and stored, then discarded, while a document built
/// before it went to the counterparty and the automatic retry shipped that same document again. It
/// is gated on <see cref="OrderStatusMachine.MappingEditRefusedFrom"/> — a strict subset of the set
/// above, because this endpoint's writer is <c>OrderMappingOverrideService.UpsertAsync</c> rather
/// than the status recompute — and answered from the same
/// <see cref="OrderStatusMachine.ResolveHoldMessage"/> table, so there is one set of operator
/// sentences across all three endpoints rather than three copies of two of them.</para>
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

        var (ctrl, orders, _, _) = Build(out var db, out _);
        db.PurchaseOrders.Add(NewOrder(orderId, Guid.NewGuid(), status)); // a DIFFERENT org
        await db.SaveChangesAsync();

        var result = await ctrl.Resolve(orderId, WithLine(), CancellationToken.None);

        Assert.IsNotType<ConflictObjectResult>(result);
        orders.Verify(o => o.ResolveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Core.Services.LineResolution>>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ResolveHeaderFields?>()), Times.Once);
    }

    // ── MV-2 — PUT /api/orders/{id}/mapping-override ──────────────────────────
    //
    // The THIRD door onto the same order, and the last one with no from-status gate. MV-1 named
    // 'delivering' as a live gap it could not close with a status reset: the edit was accepted,
    // stored, and discarded while the artifact already handed to the dispatcher went out — after
    // which the automatic retry (or the stuck-delivery sweep, with no human in the loop) shipped
    // that same pre-edit artifact again. 'transforming' was worse-disguised: it WAS in
    // MappingEditInvalidatesArtifactFrom, so the edit appeared to reset the order, but
    // OrderTransformService's completion write is a tracked Status = ready_to_deliver with no
    // status predicate and no concurrency token, so it lands over that reset every time — and the
    // artifact it commits was built from the graph loaded BEFORE the edit.
    //
    // These rows derive from OrderStatusMachine.MappingEditRefusedFrom for the same reason every
    // other theory here derives from ResolveHeldFrom: a status added to the set tomorrow gets its
    // "must be refused" row for free, and one removed cannot silently keep passing.

    /// <summary>Every status a mapping override must be REFUSED from.</summary>
    public static TheoryData<string> MappingEditRefusedStatuses()
    {
        var data = new TheoryData<string>();
        foreach (var s in OrderStatusMachine.MappingEditRefusedFrom.OrderBy(s => s, StringComparer.Ordinal))
            data.Add(s);
        return data;
    }

    /// <summary>
    /// Every other status — the positive control, and the half that keeps this gate STRICT. MV-2
    /// deliberately refused a subset of <see cref="OrderStatusMachine.ResolveHeldFrom"/>: 'parsing'
    /// and 'unrouted' are refused by the recompute endpoints and must NOT be refused here, because
    /// this endpoint's writer cannot do the harm that put them in that set (the parse does not write
    /// canonical_json, and this endpoint writes no status on an unrouted order). Widening the gate
    /// to match its sibling would remove two operator controls, and these rows are what fails when
    /// someone does it for symmetry.
    /// </summary>
    public static TheoryData<string> MappingEditNotRefusedStatuses()
    {
        var data = new TheoryData<string>();
        foreach (var s in OrderStatusMachine.AllStatuses
                     .Except(OrderStatusMachine.MappingEditRefusedFrom, StringComparer.Ordinal)
                     .OrderBy(s => s, StringComparer.Ordinal))
            data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(MappingEditRefusedStatuses))]
    public async Task PutMappingOverride_FromARefusedStatus_Returns409_AndNeverReachesTheWriter(string status)
    {
        var (ctrl, _, overrides, orderId) = await BuildWithOrderAndOverridesAsync(status);

        var result = await ctrl.PutMappingOverride(orderId, WithCorrection(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        overrides.Verify(o => o.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The deliverable this packet was written for, stated as the operator sees it: a mapping
    /// correction saved while the order is being SENT is refused, with a sentence they can act on —
    /// not accepted, stored, and dropped on the floor while the pre-edit document reaches the
    /// counterparty. Asserted as a LITERAL for the reason
    /// <c>Resolve_WhileAMachineOwnedStepIsRunning_...</c> gives: the derived rows above assert
    /// whatever the set says and so can never prove it CONTAINS 'delivering' — remove it and
    /// <see cref="MappingEditNotRefusedStatuses"/> silently grows a row asserting the opposite.
    /// </summary>
    [Fact]
    public async Task PutMappingOverride_SavedWhileTheOrderIsBeingSent_IsRefusedWithAnActionableSentence()
    {
        var (ctrl, _, overrides, orderId) =
            await BuildWithOrderAndOverridesAsync(OrderStatusConstants.Delivering);

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.PutMappingOverride(orderId, WithCorrection(), CancellationToken.None));

        // The correction never reached the store, so there is nothing to be silently discarded.
        overrides.Verify(o => o.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
            It.IsAny<CancellationToken>()), Times.Never);

        var message = ErrorOf(conflict);
        Assert.Equal(OrderStatusMachine.ResolveHoldMessage(OrderStatusConstants.Delivering), message);
        Assert.NotEqual(OrderStatusMachine.ResolveHoldMessage("a-status-the-machine-has-never-heard-of"), message);

        // Actionable, and about their next move rather than about the server's opinion.
        Assert.True(
            Regex.IsMatch(message, @"\b(then|first|wait|try again)\b", RegexOptions.IgnoreCase),
            $"the refusal does not tell the operator what to do next: {message}");
        foreach (var token in OrderStatusMachine.AllStatuses)
            Assert.False(
                Regex.IsMatch(message, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase),
                $"the refusal echoes the raw status token '{token}': {message}");
    }

    /// <summary>
    /// The transform half of the same decision, pinned as a literal for the same reason. Its
    /// sentence — "a correction saved now would be left out of the file that goes out" — is exactly
    /// what the untokened completion write does, so the operator is told the truth rather than a
    /// generic busy-signal.
    /// </summary>
    [Fact]
    public async Task PutMappingOverride_SavedWhileTheOutgoingDocumentIsBeingBuilt_IsRefused()
    {
        var (ctrl, _, overrides, orderId) =
            await BuildWithOrderAndOverridesAsync(OrderStatusConstants.Transforming);

        var conflict = Assert.IsType<ConflictObjectResult>(
            await ctrl.PutMappingOverride(orderId, WithCorrection(), CancellationToken.None));

        overrides.Verify(o => o.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(
            OrderStatusMachine.ResolveHoldMessage(OrderStatusConstants.Transforming),
            ErrorOf(conflict));
    }

    [Theory]
    [MemberData(nameof(MappingEditNotRefusedStatuses))]
    public async Task PutMappingOverride_FromEveryOtherStatus_IsNotRefusedByThisGuard(string status)
    {
        var (ctrl, _, overrides, orderId) = await BuildWithOrderAndOverridesAsync(status);

        var result = await ctrl.PutMappingOverride(orderId, WithCorrection(), CancellationToken.None);

        Assert.IsNotType<ConflictObjectResult>(result);
        overrides.Verify(o => o.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The guard runs BEFORE the body is validated, because the answer does not depend on it — the
    /// same placement, and the same reason, as the guard on <c>/resolve</c>. A malformed override on
    /// a sending order must read as "wait, then correct it", not as "your JSON is wrong": the second
    /// invites the operator to fix the body and retry, which is not the problem.
    /// </summary>
    [Fact]
    public async Task PutMappingOverride_FromARefusedStatus_IsRefusedBeforeTheBodyIsValidated()
    {
        var (ctrl, _, overrides, orderId) =
            await BuildWithOrderAndOverridesAsync(OrderStatusConstants.Delivering);

        // A body that WOULD earn a 400: '::' is the reserved source-token namespace separator.
        var bad = new OrderMappingOverride
        {
            CustomFields = new() { new CustomField { Key = "src::spoofed", Label = "x", Scope = "header" } },
        };

        Assert.IsType<ConflictObjectResult>(
            await ctrl.PutMappingOverride(orderId, bad, CancellationToken.None));
        overrides.Verify(o => o.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Org-scoped like every other read in this controller: another tenant's sending order must not
    /// answer 409, because that would confirm the row exists. It falls through to the normal
    /// not-found path instead (<c>UpsertAsync</c> is reached and answers false → 404).
    /// </summary>
    [Fact]
    public async Task PutMappingOverride_ARefusedOrderInAnotherOrg_IsNotRefusedByThisGuard()
    {
        var orderId = Guid.NewGuid();
        var (ctrl, _, overrides, _) = Build(out var db, out _);
        db.PurchaseOrders.Add(NewOrder(orderId, Guid.NewGuid(), OrderStatusConstants.Delivering));
        await db.SaveChangesAsync();

        var result = await ctrl.PutMappingOverride(orderId, WithCorrection(), CancellationToken.None);

        Assert.IsNotType<ConflictObjectResult>(result);
        overrides.Verify(o => o.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
            It.IsAny<CancellationToken>()), Times.Once);
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

    /// <summary>A real correction — the thing that must not be accepted and then discarded.</summary>
    private static OrderMappingOverride WithCorrection() => new()
    {
        CustomFields = new()
        {
            new CustomField
            {
                Key   = "corrected-item-code",
                Label = "Corrected item code",
                Scope = "header",
                Value = "SUP-CORRECTED-1",
            },
        },
    };

    private static async Task<(OrdersController Ctrl, Mock<IOrderService> Orders, Guid OrderId)>
        BuildWithOrderAsync(string status)
    {
        var (ctrl, orders, _, orderId) = await BuildWithOrderAndOverridesAsync(status);
        return (ctrl, orders, orderId);
    }

    /// <summary>
    /// The same harness, also handing back the mapping-override mock — the MV-2 rows assert on
    /// whether <c>UpsertAsync</c> was reached, which is the only thing that distinguishes "refused"
    /// from "accepted and then silently discarded".
    /// </summary>
    private static async Task<(OrdersController Ctrl, Mock<IOrderService> Orders,
                               Mock<IOrderMappingOverrideService> Overrides, Guid OrderId)>
        BuildWithOrderAndOverridesAsync(string status)
    {
        var orderId = Guid.NewGuid();
        var (ctrl, orders, overrides, orgId) = Build(out var db, out _);

        db.PurchaseOrders.Add(NewOrder(orderId, orgId, status));
        await db.SaveChangesAsync();

        return (ctrl, orders, overrides, orderId);
    }

    private static (OrdersController Ctrl, Mock<IOrderService> Orders,
                    Mock<IOrderMappingOverrideService> Overrides, Guid OrgId)
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

        // Same discipline for the override writer: it answers SUCCESS, so any result other than 409
        // means the guard let the write through — which is what the positive controls must observe.
        var overrides = new Mock<IOrderMappingOverrideService>();
        overrides
            .Setup(s => s.UpsertAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<OrderMappingOverride>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        overrides
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderMappingOverride());

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
            overrides.Object,
            new Mock<ProcuLink.Core.Services.Mapping.IPromoteMappingService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ProcuLink.Core.Services.ITransformService>());

        return (ctrl, orders, overrides, orgId);
    }
}
