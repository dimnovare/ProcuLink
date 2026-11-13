using System.Globalization;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// THE format of the deterministic per-artifact delivery idempotency key.
///
/// <para>
/// The key is a stable function of (orderId, artifactId): every dispatch of the same artifact —
/// a backoff retry AND a crash-recovery re-send after a lost ACK — produces the SAME key, which
/// is what lets a supplier de-duplicate a re-send (HTTP <c>Idempotency-Key</c> header, email
/// <c>Message-ID</c>). A re-transform mints a new artifact id → a new key → a legitimately new
/// delivery.
/// </para>
/// </summary>
public static class DeliveryIdempotencyKey
{
    private const string Prefix = "plk-dlv-";

    /// <summary>
    /// Builds the deterministic key for dispatching <paramref name="artifactId"/> of
    /// <paramref name="orderId"/>. Format: <c>plk-dlv-{orderId:N}-{artifactId:N}</c>.
    /// </summary>
    public static string Build(Guid orderId, Guid artifactId)
        => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{orderId:N}-{artifactId:N}");
}
