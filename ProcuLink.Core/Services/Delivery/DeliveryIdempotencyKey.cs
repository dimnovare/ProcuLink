using System.Globalization;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// THE format of the deterministic per-artifact delivery idempotency key — build and parse in
/// one place so the two can never drift.
///
/// <para>
/// The key is a stable function of (orderId, artifactId): every dispatch of the same artifact —
/// a backoff retry AND a crash-recovery re-send after a lost ACK — produces the SAME key, which
/// is what lets a supplier de-duplicate a re-send (HTTP <c>Idempotency-Key</c> header, email
/// <c>Message-ID</c>). A re-transform mints a new artifact id → a new key → a legitimately new
/// delivery.
/// </para>
///
/// <para>
/// It is therefore also the ONLY durable record of WHICH artifact a given delivery attempt
/// dispatched: <c>delivery_attempts</c> has no artifact foreign key, and an order can hold
/// several artifacts and several attempts. <see cref="TryParseArtifactId"/> reads that link back
/// out for read models (the passport's "download what we sent"). It is a pure parse of persisted
/// dispatch evidence — it never guesses, and callers must still verify the artifact row exists
/// and is org-scoped before acting on it.
/// </para>
/// </summary>
public static class DeliveryIdempotencyKey
{
    private const string Prefix = "plk-dlv-";

    /// <summary>Length of a GUID in "N" format (32 hex chars, no dashes).</summary>
    private const int GuidNLength = 32;

    /// <summary>
    /// Builds the deterministic key for dispatching <paramref name="artifactId"/> of
    /// <paramref name="orderId"/>. Format: <c>plk-dlv-{orderId:N}-{artifactId:N}</c>.
    /// </summary>
    public static string Build(Guid orderId, Guid artifactId)
        => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{orderId:N}-{artifactId:N}");

    /// <summary>
    /// Recovers the artifact id from a key produced by <see cref="Build"/>. Returns false —
    /// leaving <paramref name="artifactId"/> as <see cref="Guid.Empty"/> — for null, legacy or
    /// test-fire rows, and for anything that is not exactly this format. A caller that gets
    /// false must present NO artifact link rather than inventing one.
    /// </summary>
    public static bool TryParseArtifactId(string? key, out Guid artifactId)
    {
        artifactId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        // Exactly: prefix + 32 hex + '-' + 32 hex. Length-checking first keeps a hostile value
        // from reaching Guid.TryParseExact with a surprising shape.
        var body = key.AsSpan(Prefix.Length);
        if (body.Length != GuidNLength + 1 + GuidNLength) return false;
        if (body[GuidNLength] != '-') return false;

        return Guid.TryParseExact(body[(GuidNLength + 1)..], "N", out artifactId);
    }
}
