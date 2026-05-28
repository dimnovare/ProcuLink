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
}
