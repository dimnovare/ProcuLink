namespace ProcuLink.Core.Services;

/// <summary>
/// Maps a client-supplied idempotency key to the order created on the first
/// matching upload. Lookup is scoped per organisation so two tenants can
/// independently reuse the same key string. Outside the
/// <see cref="IdempotencyWindow"/> a stored row is treated as expired and the
/// caller is expected to create a fresh order.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// The window inside which a repeat call with the same (orgId, key) pair
    /// returns the original order id. Defaults to 24 hours.
    /// </summary>
    TimeSpan IdempotencyWindow { get; }

    /// <summary>
    /// Returns the order id previously bound to <paramref name="key"/> for
    /// <paramref name="orgId"/> if the binding exists and is younger than
    /// <see cref="IdempotencyWindow"/>. Returns null otherwise (no row, or row
    /// older than the window — caller should create a new order).
    /// </summary>
    Task<Guid?> TryGetExistingOrderIdAsync(string key, Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Bind or refresh <paramref name="key"/> for <paramref name="orgId"/> so
    /// that it points at <paramref name="orderId"/> and resets the window.
    /// Used after a fresh order is created.
    /// </summary>
    Task BindAsync(string key, Guid orgId, Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Atomically CLAIMS <paramref name="key"/> for <paramref name="orgId"/>, committing the ledger
    /// row BEFORE the order is created, and reports whether this caller won the claim.
    ///
    /// <para>This replaces a check-then-create pair (<see cref="TryGetExistingOrderIdAsync"/> then
    /// <see cref="BindAsync"/>), under which two concurrent requests carrying the same key both saw
    /// "no row" and both created an order — and a crash between create and bind duplicated on the
    /// next at-least-once retry. The composite primary key <c>(org_id, key)</c> on
    /// <c>idempotency_keys</c> is the real guard: the loser of the insert race gets a unique
    /// violation and is handed the winner's claim instead.</para>
    ///
    /// <para>The claim carries a PRE-GENERATED order id, stored atomically with the row, and the
    /// order is created under that exact primary key. That is what makes a crash between claim and
    /// create resumable rather than permanently blocking: "does an order with this id exist?"
    /// separates a real duplicate (return it) from an abandoned claim (recreate under the same id,
    /// a find-or-create). An expired row (older than <see cref="IdempotencyWindow"/>) is taken over
    /// with a fresh id and reported as a new claim.</para>
    /// </summary>
    /// <returns>
    /// <see cref="IdempotencyClaim.IsNew"/> is true when this caller won a fresh claim and must
    /// create the order; false when a live claim already existed. Either way
    /// <see cref="IdempotencyClaim.OrderId"/> is the id the order does or will live under.
    /// </returns>
    Task<IdempotencyClaim> ClaimAsync(string key, Guid orgId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of an atomic <see cref="IIdempotencyService.ClaimAsync"/>.
/// </summary>
/// <param name="IsNew">
/// True when this caller inserted the claim (or took over an expired one) and therefore owns
/// creating the order. False when a live claim already existed — this caller is a replay or the
/// loser of a concurrent race.
/// </param>
/// <param name="OrderId">
/// The pre-generated order id bound to the claim. The order either already exists under it, or must
/// be created under it.
/// </param>
public sealed record IdempotencyClaim(bool IsNew, Guid OrderId);
