namespace ProcuLink.Api.Contracts;

/// <summary>
/// Generic paginated response envelope used by list endpoints that support
/// server-side pagination.
/// </summary>
/// <typeparam name="T">The item type for each page.</typeparam>
public record PaginatedResult<T>(
    IReadOnlyList<T> Items,
    int              TotalCount,
    int              Page,
    int              PageSize
);
