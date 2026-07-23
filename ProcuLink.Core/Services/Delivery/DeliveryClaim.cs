using System.Linq.Expressions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// The ONE delivery-claim predicate.
///
/// <para>DeliveryService claims an order by flipping it to 'delivering' in a single guarded
/// statement. That claim is written twice per method — once as a relational
/// <c>ExecuteUpdateAsync</c> predicate, once as an EF-InMemory emulation, because the InMemory
/// provider cannot translate <c>ExecuteUpdateAsync</c>. Both consume this factory, so the two
/// cannot disagree. They previously disagreed in exactly the way that matters: a status added to
/// one and not the other makes the claim match 0 rows, which the caller reads as a benign
/// "someone else has it" and reports as SUCCESS having sent nothing.</para>
///
/// <para>Keep the results returned here <c>Expression</c>s: EF inlines a
/// <c>LambdaExpression</c> passed to <c>DbSet.Where</c> (including inside
/// <c>ExecuteUpdateAsync</c>), while a <c>.Compile()</c>d delegate is opaque to it and fails with
/// a misleading "Invoke ... could not be translated" error. <c>.Compile()</c> belongs only in the
/// InMemory branches, evaluated against the already-loaded entity.</para>
/// </summary>
public static class DeliveryClaim
{
    /// <summary>
    /// The general claim shape: an idle status from <paramref name="idleClaimable"/>, OR a
    /// 'delivering' row gone stale (crash recovery). Prefer the intent-named wrappers
    /// (<see cref="ClaimableForDispatch"/> / <see cref="ClaimableForRetry"/>) at call sites.
    /// </summary>
    /// <param name="idleClaimable">
    /// The idle statuses this claim accepts — one of <see cref="OrderStatusMachine"/>'s canonical
    /// claim sets. Never a literal.
    /// </param>
    /// <param name="staleBefore">
    /// A 'delivering' row older than this is a crashed worker's orphan and is reclaimable. A
    /// fresher one belongs to the worker that stamped it and must NOT be claimed — the staleness
    /// gate is what makes "abandoned" a provable property rather than a presumed one, and it is
    /// the mutual-exclusion mechanism between concurrent claimants.
    /// </param>
    public static Expression<Func<PurchaseOrderEntity, bool>> Claimable(
        Guid orgId,
        Guid orderId,
        IReadOnlySet<string> idleClaimable,
        DateTime staleBefore)
    {
        // An empty set yields `= ANY('{}')`, which matches nothing: the claim would affect 0 rows
        // and the caller would read that as "already claimed" and skip the dispatch — a silent
        // strand, the exact bug class this type exists to close. Benign-by-luck is not a
        // contract; fail loud.
        if (idleClaimable.Count == 0)
            throw new ArgumentException(
                "A claim with no claimable statuses can never claim; it would silently match 0 rows.",
                nameof(idleClaimable));

        // Verified on Postgres 16 / EF 8.0.16 / Npgsql 8.0.11 (2026-07-16 spike, recorded in the
        // claim-predicate design doc): this parameterises the set as `= ANY(@p)`, keeping the
        // claim SQL TEXT identical no matter which set a caller passes. Do NOT "simplify" it to
        // idleClaimable.Contains(x.Status): that also translates (the array hop is not needed for
        // translatability) but inlines the set's CONTENTS as SQL literals — `IN ('…', …)` —
        // minting a distinct query-cache entry and Postgres plan per distinct set, on delivery's
        // hottest write path. The array is about plan stability, not translatability.
        var arr = idleClaimable.ToArray();

        return x => x.Id == orderId
                 && x.OrgId == orgId
                 && (arr.Contains(x.Status)
                  || (x.Status == OrderStatusConstants.Delivering && x.UpdatedAt < staleBefore));
    }

    /// <summary>
    /// <c>DispatchArtifactAsync</c>'s claim. The idle set depends on WHO drove the activation
    /// (#42): an operator (<paramref name="requireAutoDeliver"/> <c>false</c> — every such
    /// enqueue is <c>DeliverOrderJob.EnqueueRedeliver</c>) may claim a
    /// <c>delivery_unconfirmed</c> park; an automatic activation
    /// (<c>true</c> — <c>TransformOrderJob</c>, the stranded-ready sweep, or a Hangfire refetch
    /// of either) must never, or a refetch racing the stuck sweep re-sends the parked PO the park
    /// exists to protect. Encoding the switch HERE, not at call sites, is what keeps the
    /// relational and InMemory branches on the same conditional member.
    /// </summary>
    public static Expression<Func<PurchaseOrderEntity, bool>> ClaimableForDispatch(
        Guid orgId,
        Guid orderId,
        bool requireAutoDeliver,
        DateTime staleBefore)
        => Claimable(
            orgId,
            orderId,
            requireAutoDeliver
                ? OrderStatusMachine.ClaimableForAutomaticDispatchFrom
                : OrderStatusMachine.ClaimableForDispatchFrom,
            staleBefore);

    /// <summary>
    /// <c>RetryDeliveryAsync</c>'s claim — the backoff queue is always automatic, so there is no
    /// operator variant: a park is claimed only through <see cref="ClaimableForDispatch"/> with
    /// <c>requireAutoDeliver: false</c>.
    /// </summary>
    public static Expression<Func<PurchaseOrderEntity, bool>> ClaimableForRetry(
        Guid orgId,
        Guid orderId,
        DateTime staleBefore)
        => Claimable(orgId, orderId, OrderStatusMachine.ClaimableForRetryFrom, staleBefore);
}
