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
    /// Whether this dispatcher can put the counterparty's own refusal text into
    /// <see cref="DeliveryResult.ResponseBody"/> — i.e. whether a BLANK body is evidence of
    /// "they said nothing" or merely of "we never looked".
    ///
    /// <para><b>Why this is a capability and not a protocol list.</b>
    /// <c>SupplierResponseClassification</c> splits a 400 on whether the supplier explained
    /// themselves: with a reason it is a business rejection and the queue stops; bare it is an
    /// unexplained refusal that keeps being re-sent to the real endpoint up to the cap. That rule is
    /// only sound if a blank body MEANS something. It did not on <c>erp_erply</c>, <c>erp_directo</c>
    /// and <c>email</c>: both connectors read the body, folded it into a summary string and threw the
    /// original away, so every 400 on the canonical email path and on the two Unsafe ERP channels
    /// classified bare no matter what the supplier said. A hard-coded protocol list would have fixed
    /// those three and left the fourth dispatcher — the one that does not exist yet — to inherit the
    /// same wrong answer in silence.</para>
    ///
    /// <para>Defaults to <c>false</c>: a channel that has not thought about this is treated as unable
    /// to see the reason, which routes its unexplained 4xx to a human instead of re-firing it.
    /// Production dispatchers must still declare it explicitly —
    /// <c>DispatcherResponseCaptureTests</c> enforces that, exactly as
    /// <c>DispatcherResendSafetyTests</c> does for <see cref="ResendSafety"/>.</para>
    /// </summary>
    bool CapturesSupplierResponseBody => false;

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
