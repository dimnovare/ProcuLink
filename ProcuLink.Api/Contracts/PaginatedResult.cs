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
    int              PageSize,

    /// <summary>
    /// How many of the <see cref="TotalCount"/> rows are onboarding practice orders rather than
    /// real work. Defaults to 0 for list endpoints where the distinction does not exist.
    ///
    /// <para>Without this, <c>TotalCount</c> silently described a different population from every
    /// count shown beside it — billing quota, dashboard KPIs and onboarding milestones all exclude
    /// practice orders — so a first-run org read "Received 0" next to a table listing one order.
    /// <c>TotalCount - SampleCount</c> is the metered population.</para>
    /// </summary>
    int SampleCount = 0
);
