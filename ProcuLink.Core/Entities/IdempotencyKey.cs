namespace ProcuLink.Core.Entities;

/// <summary>
/// Maps a client-supplied idempotency key (typically a UUID) to the order that
/// was created the first time the key was seen, scoped per organisation.
/// Used to deduplicate retried POST /api/orders/upload requests within 24 hours.
/// </summary>
public class IdempotencyKey
{
    /// <summary>Primary key — the client-supplied Idempotency-Key header value.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Organisation that owns this key. Two different orgs may reuse the same string.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The order that was created when the key was first seen.</summary>
    public Guid OrderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
