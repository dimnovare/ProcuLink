using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Protocol-specific delivery dispatcher. One implementation per protocol
/// (http / sftp / ftp / erp_erply / erp_directo).
/// Registered as IEnumerable&lt;IDeliveryDispatcher&gt; in DI; DeliveryService resolves by Protocol.
/// </summary>
public interface IDeliveryDispatcher
{
    /// <summary>Protocol name this dispatcher handles.</summary>
    string Protocol { get; }

    /// <summary>
    /// Whether re-sending the same artifact after an UNKNOWN outcome can duplicate at the
    /// counterparty. Read by <c>DeliveryService</c> ONLY when a crash-recovery re-drive re-adopts
    /// an in-flight <c>dispatching</c> row: an <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/>
    /// channel is parked for a human decision instead of blindly re-sent.
    /// <para>
    /// Defaults to <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/> — the fail-safe
    /// direction. A dispatcher that has not declared its idempotency contract parks (conservative)
    /// rather than duplicates. Production dispatchers must still declare their tier explicitly;
    /// <c>DispatcherResendSafetyTests</c> enforces that.
    /// </para>
    /// </summary>
    ResendSafety ResendSafety => ResendSafety.Unsafe;

    /// <summary>
    /// Dispatches the artifact payload to the configured destination.
    /// Must not throw — return DeliveryResult(false, message) on failure.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Deterministic per-artifact delivery idempotency key (A3). Stable across a legitimate retry
    /// AND a crash-recovery re-send of the same artifact, so a channel that honours it lets the
    /// supplier de-duplicate a re-send: HTTP sets it as the <c>Idempotency-Key</c> header, email
    /// as a deterministic <c>Message-ID</c>. SFTP/FTPS ignore it (they are already idempotent via
    /// the deterministic overwrite filename). Null for test-fire / callers that do not supply one.
    /// </param>
    Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct,
        string? idempotencyKey = null);
}
