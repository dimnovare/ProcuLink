namespace ProcuLink.Api.Contracts;

/// <summary>
/// Query parameters accepted by <c>GET /api/orders</c>.
/// All filters are optional; omitting them returns all orders for the org.
/// </summary>
public record OrderListQuery
{
    /// <summary>1-based page number. Defaults to 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// Number of items per page. Defaults to 25, capped at 100.
    /// Values below 1 are treated as 1.
    /// </summary>
    public int PageSize { get; init; } = 25;

    /// <summary>
    /// Row offset (number of rows to skip), 0-based. REST-style alternative to
    /// <see cref="Page"/>/<see cref="PageSize"/>. When either <see cref="Offset"/> or
    /// <see cref="Limit"/> is supplied, the offset/limit window takes precedence over
    /// page/pageSize. Negative values are treated as 0.
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// Maximum number of rows to return. REST-style alternative to
    /// <see cref="PageSize"/>; capped at the same 100-row ceiling. When supplied (with or
    /// without <see cref="Offset"/>), the offset/limit window takes precedence over
    /// page/pageSize. Values below 1 are treated as 1.
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>Filter by order status (e.g. "pending_review", "ready", "delivered").</summary>
    public string? Status { get; init; }

    /// <summary>Filter by supplier id.</summary>
    public Guid? SupplierId { get; init; }

    /// <summary>
    /// Full-text filter across PO number, supplier name, and buyer name (from
    /// canonical_json). Buyer name lives in JSON, so this is a case-insensitive
    /// in-memory post-filter on buyer name; PO number and supplier name are
    /// filtered in the EF query.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>Inclusive lower bound on <c>CreatedAt</c> (UTC).</summary>
    public DateTime? DateFrom { get; init; }

    /// <summary>Inclusive upper bound on <c>CreatedAt</c> (UTC).</summary>
    public DateTime? DateTo { get; init; }
}
