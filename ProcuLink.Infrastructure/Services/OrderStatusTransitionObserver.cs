using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// P2 hardening — LOG-ONLY observer for the purchase-order state machine.
///
/// <para>Order status is written from many scattered call sites (parse, resolve, transform,
/// delivery, stuck-detection, ops requeue, operator settlement). Rather than wiring a
/// check into each one, this <see cref="SaveChangesInterceptor"/> watches the single
/// chokepoint every tracked write flows through — <c>SaveChanges</c> — and logs a WARNING
/// whenever an order moves through a transition outside the allowed map below.</para>
///
/// <para><b>It never blocks, never throws, and never mutates anything.</b> An unexpected
/// transition is operational telemetry (it means a code path moved an order in a way the
/// documented pipeline does not anticipate), not a reason to fail the user's operation.
/// Every code path here is wrapped so that even an internal bug in the observer cannot
/// break a save.</para>
///
/// <para>Honest limitation: writes issued via <c>ExecuteUpdateAsync</c> bypass the EF change
/// tracker and are NOT observed. This doc deliberately does NOT enumerate those writers. An
/// earlier revision did, and its list was already wrong when written — it named four when there
/// were eight — because a list in a comment is a second copy of a fact that lives in the code,
/// and the copy drifts. Worse, a closed list reads as complete, which is how an existential gets
/// sold as a universal (the defect that produced this subsystem's last several Criticals).
/// To get the CURRENT set, ask the code:</para>
/// <code>grep -rn "SetProperty(o =&gt; o.Status" --include=*.cs</code>
/// <para>One consequence IS worth naming, because the map below would otherwise mislead: any writer
/// that claims via <c>ExecuteUpdateAsync</c> bypasses the tracker, so those transitions never reach
/// this observer — even though they ARE listed in the map, which serves the non-relational path and
/// documentation. The tracked-entity writes (transform, resolution, stuck/ops, mapping-edit flows)
/// are the majority of transitions and are all covered.</para>
/// </summary>
public sealed class OrderStatusTransitionObserver : SaveChangesInterceptor
{
    private readonly ILogger<OrderStatusTransitionObserver> _logger;

    public OrderStatusTransitionObserver(ILogger<OrderStatusTransitionObserver> logger)
        => _logger = logger;

    /// <summary>
    /// Allowed transitions, derived from every real <c>Status</c> write point in the
    /// codebase (OrderIngestionService / OrderResolutionService / OrderTransformService /
    /// DeliveryService / Stuck*DetectionService / OrdersController / OpsController).
    /// Self-transitions (from == to) are always allowed and
    /// are not listed. Generous by design: this map must stay SILENT for every legit
    /// flow — false-positive warnings would train operators to ignore it.
    ///
    /// <para><c>internal</c> (not private) so <c>OrderStatusMachineTests</c> can assert the
    /// superset invariant <see cref="OrderStatusMachine.Transitions"/> documents — every edge
    /// this map calls expected must be an edge the machine calls allowed. See that test for why
    /// the invariant is enforced structurally rather than trusted to review.</para>
    /// </summary>
    // ── The resolve recompute ("+ RR" below) ─────────────────────────────────────────────────
    // OrderResolutionService.ResolveAsync (:242) and AcceptAiSuggestionsAsync (:393) both end with
    // `Status = Lines.Any(NeedsReview) ? pending_review : ready`, a TRACKED write with no
    // from-status guard on the service OR either endpoint (OrdersController.Resolve :483 validates
    // the request body only). The one guard is OrderResolutionService.IsFinished =
    // OrderStatusMachine.DeclaredTerminal = {failed}. Tracked means these writes come through THIS
    // observer, so every from-state a resolve can be issued from owed both edges — every status
    // except 'failed' and the unwritten 'pending_parse'. The ones that were missing made this
    // observer log "Unexpected order status transition" on ordinary operator actions (an operator
    // resolving a delivered order whose price-variance guard re-flagged a line was the reported
    // case), which is the false positive the generosity note above exists to prevent.
    // Rationale and call-site evidence live once, in OrderStatusMachine.Transitions' own "+ RR"
    // block — both maps or neither (the d4d6eac drift lesson).

    internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // Stub created → parse job picks it up (or fails fast). Stuck-detection can
            // requeue parsing back to pending_parse; a failed parse can be retried.
            [OrderStatusConstants.PendingParse] = Set(
                OrderStatusConstants.Parsing, OrderStatusConstants.PendingReview,
                OrderStatusConstants.Ready, OrderStatusConstants.Failed,
                OrderStatusConstants.Unrouted),
            [OrderStatusConstants.Parsing] = Set(
                OrderStatusConstants.PendingParse, OrderStatusConstants.PendingReview,
                OrderStatusConstants.Ready, OrderStatusConstants.Failed,
                OrderStatusConstants.Unrouted),
            // Routing hold → re-enters the parse flow once a supplier is assigned, or is discarded.
            // + RR: an unrouted order has lines, so a resolve (or a header-only edit) recomputes it
            // out of the hold with SupplierId still null. Real, and a real hole — see the machine's
            // Unrouted row for why the fix is an endpoint guard, not a map entry.
            [OrderStatusConstants.Unrouted] = Set(
                OrderStatusConstants.Parsing, OrderStatusConstants.PendingParse,
                OrderStatusConstants.Failed, OrderStatusConstants.RejectedBySupplier,
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready),
            // Review loop: resolving lines recomputes pending_review|ready; a reviewer can
            // also mark the order rejected.
            [OrderStatusConstants.PendingReview] = Set(
                OrderStatusConstants.Ready, OrderStatusConstants.Failed,
                OrderStatusConstants.RejectedBySupplier),
            [OrderStatusConstants.Ready] = Set(
                OrderStatusConstants.PendingReview, OrderStatusConstants.Transforming,
                OrderStatusConstants.RejectedBySupplier, OrderStatusConstants.Failed),
            // Transform: success → ready_to_deliver; a TERMINAL validation/template failure parks the
            // order in transform_failed (visible), where it stays recoverable; stuck-detection fails a
            // hung transform. ready: the compensating release when a re-transform enqueue fails.
            // + RR: → pending_review.
            [OrderStatusConstants.Transforming] = Set(
                OrderStatusConstants.Ready, OrderStatusConstants.ReadyToDeliver,
                OrderStatusConstants.TransformFailed, OrderStatusConstants.Failed,
                OrderStatusConstants.PendingReview),
            // Recovery out of a failed transform: fix the template/mapping and re-transform (the
            // transform claim accepts transform_failed), or re-resolve back into the review loop.
            [OrderStatusConstants.TransformFailed] = Set(
                OrderStatusConstants.Ready, OrderStatusConstants.Transforming,
                OrderStatusConstants.PendingReview),
            // Delivery: manual/automatic dispatch, re-transform, a pre-claim failure, and a
            // billing hold (delivery_held) when the org can't process orders at delivery time.
            // → delivered is GONE (WP-09): it existed for the retired inbound supplier-status
            // webhook, which could report a terminal state straight from ready_to_deliver. Nothing
            // else can write 'delivered' from here — MarkDelivered admits delivery_unconfirmed only,
            // and DeliveryService writes the outcome only after its claim moved the row to
            // 'delivering'. Listing an edge with no writer costs the WARNING that should fire the
            // day something starts performing it. Mirrors OrderStatusMachine — both maps or neither
            // (the d4d6eac drift lesson); pinned by
            // OrderStatusMachineTests.EveryInboundDeliveredEdge_HasAProductionWriter.
            // + RR: → pending_review AND → ready. 'ready' was missing here for a SECOND writer as
            // well — OrderMappingOverrideService.IsPastReady includes ready_to_deliver, so an MV-1
            // mapping edit performs ready_to_deliver → ready as a tracked write
            // (OrderMappingOverrideService.cs:86-87, :111) and this observer warned on every one.
            // The machine already allowed it; only this map was wrong, which is a shape the
            // machine-superset invariant cannot see.
            [OrderStatusConstants.ReadyToDeliver] = Set(
                OrderStatusConstants.Delivering, OrderStatusConstants.Transforming,
                OrderStatusConstants.DeliveryFailed,
                OrderStatusConstants.DeliveryHeld, OrderStatusConstants.RejectedBySupplier,
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready),
            // Billing hold → released back to ready_to_deliver (re-driven) on reactivation. A held
            // PARK (HeldFromStatus == delivery_unconfirmed) is instead RESTORED to
            // delivery_unconfirmed on release — never re-driven. Mirrors OrderStatusMachine —
            // both maps or neither (the d4d6eac drift lesson).
            // → delivered is GONE (WP-09) — it was the "late supplier ACK for an order sent before
            // the hold landed" edge, and the webhook that performed it no longer exists.
            // + RR: → pending_review.
            [OrderStatusConstants.DeliveryHeld] = Set(
                OrderStatusConstants.ReadyToDeliver, OrderStatusConstants.Ready,
                OrderStatusConstants.DeliveryUnconfirmed,
                OrderStatusConstants.RejectedBySupplier,
                OrderStatusConstants.PendingReview),
            // + RR: → pending_review AND → ready. A row SITS in 'delivering' while a dispatch is in
            // flight or stranded (StuckDeliveryDetectionService exists for the stranded case), and
            // the resolve endpoint has no guard, so an operator can write straight over the claim.
            // Listed because it happens, not because it is desirable — see the machine's Delivering
            // row for the follow-up.
            [OrderStatusConstants.Delivering] = Set(
                OrderStatusConstants.Delivered, OrderStatusConstants.DeliveryFailed,
                OrderStatusConstants.DeliveryUnconfirmed,
                OrderStatusConstants.RejectedBySupplier, OrderStatusConstants.DeliveryDeadLetter,
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready),
            // Unknown-outcome park: the operator sends again or confirms delivery. A "Send again"
            // for an org that lapsed since the park is held → delivery_held (the delivery_failed
            // sibling below). Mirrors OrderStatusMachine — both maps or neither (the d4d6eac
            // drift lesson).
            // + RR: → pending_review.
            [OrderStatusConstants.DeliveryUnconfirmed] = Set(
                OrderStatusConstants.Delivering, OrderStatusConstants.Delivered,
                OrderStatusConstants.DeliveryFailed, OrderStatusConstants.DeliveryDeadLetter,
                OrderStatusConstants.DeliveryHeld,
                OrderStatusConstants.Ready, OrderStatusConstants.RejectedBySupplier,
                OrderStatusConstants.PendingReview),
            // A delivered order can still be rejected later (supplier business ACK ≠ HTTP 200), and
            // MV-1 resets it to ready: OrderMappingOverrideService.IsPastReady includes 'delivered',
            // so a mapping edit on a delivered order performs delivered → ready as a TRACKED write
            // (OrderMappingOverrideService.cs:86-87) — i.e. straight through this observer. Without
            // 'ready' here, every such edit logged a false "unexpected transition" WARNING.
            // + RR: → pending_review — the edge that started this packet. An operator resolves a
            // delivered order, ResolveAsync's connection-level price-variance guard re-flags a line
            // (OrderResolutionService.cs:206-239), and the recompute lands on pending_review; both
            // maps called that impossible, so this observer warned on a legitimate flow.
            [OrderStatusConstants.Delivered] = Set(
                OrderStatusConstants.RejectedBySupplier, OrderStatusConstants.Ready,
                OrderStatusConstants.PendingReview),
            // Failed deliveries: retry, dead-letter, re-transform, or return to the review loop
            // after corrections. A5: a backoff retry that fires after the org lapsed
            // (read_only/past_due) is held via HoldForBillingAsync → delivery_held.
            // + RR: → pending_review and → ready were ALREADY here and needed no edit — this row is
            // where the observer was right and the machine was wrong. The machine now allows
            // pending_review too, so the KnownObserverOnlyEdges exemption that hid the drift is
            // deleted; 'ready' was the MV-1 edge and the two maps already agreed on it.
            // → delivered is GONE (WP-09) — the "late positive ACK after a failed attempt" edge,
            // whose only writer was the retired inbound webhook.
            [OrderStatusConstants.DeliveryFailed] = Set(
                OrderStatusConstants.Delivering, OrderStatusConstants.DeliveryDeadLetter,
                OrderStatusConstants.RejectedBySupplier,
                OrderStatusConstants.Transforming, OrderStatusConstants.PendingReview,
                OrderStatusConstants.Ready, OrderStatusConstants.DeliveryHeld),
            // Dead-letter: ops requeue back into delivery.
            // → delivered is GONE (WP-09) — the "late supplier ACK may still land" edge, whose only
            // writer was the retired inbound webhook.
            // + RR: → pending_review AND → ready. As with ready_to_deliver, 'ready' also had a
            // second missing writer — IsPastReady includes delivery_dead_letter, so an MV-1 mapping
            // edit warned here too. The machine allowed it already; only this map was wrong.
            [OrderStatusConstants.DeliveryDeadLetter] = Set(
                OrderStatusConstants.Delivering,
                OrderStatusConstants.DeliveryFailed, OrderStatusConstants.RejectedBySupplier,
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready),
            // Rejected: corrected and re-driven through the loop.
            //
            // WP-19 reconciled three of these into OrderStatusMachine, which had called the status
            // TERMINAL while this map already listed them. The observer was right and the machine
            // was wrong: → pending_review / ready are written by OrderResolutionService.ResolveAsync
            // (no from-status guard) and were live in production the whole time, and → transforming
            // is now real too because the transform claim admits rejected_by_supplier
            // (OrderStatusMachine.ClaimableForTransformFrom) — the transform_failed precedent. Only
            // → delivering and → delivery_failed remain observer-only; see KnownObserverOnlyEdges.
            //
            // → delivered is GONE: no dispatch can write 'delivered' onto a rejected order — the
            // delivery claim (DispatchArtifactAsync / RetryDeliveryAsync) never admits
            // rejected_by_supplier, and OrdersController.MarkDelivered admits only
            // delivery_unconfirmed (OrderStatusMachine.ManuallyDeliverableFrom). With no writer left,
            // listing it would bless an impossible move. (This comment used to justify the removal by
            // the inbound supplier-status webhook's own status gate; that whole subsystem, gate
            // included, was retired in WP-09, so nothing un-rejects an order any more.)
            //
            // → delivery_failed STAYS: it is still reachable WITHOUT the webhook. DeliveryService's
            // PRE-CLAIM failure paths (FailMissingConfigAsync / FailBeforeDispatchAsync) write
            // delivery_failed unconditionally, with no from-status check, racing the enqueue-time
            // guards in OrdersController.Redeliver / OpsController.RequeueDelivery. Rare, but real —
            // and listing it here is what keeps this observer SILENT when it fires, which is
            // deliberate (see KnownObserverOnlyEdges in OrderStatusMachineTests, which exempts the
            // resulting machine/observer drift on exactly that basis).
            [OrderStatusConstants.RejectedBySupplier] = Set(
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready,
                OrderStatusConstants.Transforming, OrderStatusConstants.Delivering,
                OrderStatusConstants.DeliveryFailed),
            // Failed parse: re-parse (ParseOrderJob runs again for status == failed).
            [OrderStatusConstants.Failed] = Set(
                OrderStatusConstants.PendingParse, OrderStatusConstants.Parsing,
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready),
        };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    /// <summary>
    /// Pure transition check (unit-testable without a DbContext). A transition is
    /// expected when from == to, or the map lists it. Null/empty endpoints and
    /// unknown statuses are NOT expected (they indicate a write outside the
    /// documented state machine) — but the observer still only logs.
    /// </summary>
    public static bool IsExpected(string? from, string? to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return false;
        if (string.Equals(from, to, StringComparison.Ordinal)) return true;
        return AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ObserveSafely(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ObserveSafely(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>Log-only inspection. MUST NEVER throw — a logging bug must not fail a save.</summary>
    private void ObserveSafely(DbContext? context)
    {
        try
        {
            if (context is null) return;

            foreach (var entry in context.ChangeTracker.Entries<PurchaseOrderEntity>())
            {
                if (entry.State != EntityState.Modified) continue;

                var statusProp = entry.Property(o => o.Status);
                if (!statusProp.IsModified) continue;

                var from = statusProp.OriginalValue;
                var to   = statusProp.CurrentValue;
                if (string.Equals(from, to, StringComparison.Ordinal)) continue;

                if (!IsExpected(from, to))
                {
                    _logger.LogWarning(
                        "Unexpected order status transition '{FromStatus}' -> '{ToStatus}' for order {OrderId} (org {OrgId}). " +
                        "The save proceeds unchanged — this is observability only, but it means a code path moved the order " +
                        "outside the documented state machine.",
                        from, to, entry.Entity.Id, entry.Entity.OrgId);
                }
            }
        }
        catch (Exception ex)
        {
            // Swallow EVERYTHING — the observer must never block or fail a save.
            try
            {
                _logger.LogDebug(ex, "OrderStatusTransitionObserver inspection failed (non-fatal, save unaffected).");
            }
            catch
            {
                // Even the logger failing must not surface.
            }
        }
    }
}
