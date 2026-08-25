using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// One page of an organisation's exception history, plus the total number of rows the
/// filter matched so a caller can render a pager.
///
/// <para><c>Page</c> and <c>PageSize</c> are the values that were actually APPLIED, after
/// clamping — not the ones the caller asked for. Echoing the request back would let a
/// caller that asked for <c>pageSize=5000</c> be told it got 5000 rows while holding
/// <see cref="OrderExceptionPaging.MaxPageSize"/> of them.</para>
/// </summary>
public sealed record OrderExceptionPage(
    IReadOnlyList<OrderException> Rows,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// The paging bounds for <see cref="IOrderExceptionService.ListAsync"/>, defined ONCE.
///
/// <para>The clamp lives on the service rather than on the controller so that no caller —
/// the HTTP endpoint, a future job, a test — can ask for an unbounded read. That matters
/// here more than it does for a list that is naturally bounded: exception rows accumulate
/// monotonically. Nothing deletes them; resolving or ignoring one only flips its
/// <c>State</c>, so an organisation's history only ever grows.</para>
/// </summary>
public static class OrderExceptionPaging
{
    /// <summary>Matches the ceiling <c>GET /api/audit</c> already uses.</summary>
    public const int MaxPageSize = 200;

    public const int MinPageSize = 1;

    /// <summary>
    /// What a caller that supplies no <c>pageSize</c> gets — deliberately the ceiling.
    ///
    /// <para>The endpoint used to return the entire history in one response, and the browser
    /// app still calls it with no paging parameters at all and reads the body as a bare
    /// array. Defaulting to the largest page the endpoint can serve keeps that caller
    /// working on every realistic workspace while still bounding the response, which is the
    /// whole point. A smaller "tidier" default (25, 50) would have silently truncated a
    /// live operator's work list the day it shipped.</para>
    /// </summary>
    public const int DefaultPageSize = MaxPageSize;
}

public interface IOrderExceptionService
{
    /// <summary>
    /// Idempotently reconcile open exceptions for an order against its current
    /// status and lines: open new exceptions for current problems, auto-resolve
    /// open exceptions whose problem no longer applies. Never touches ignored rows.
    /// </summary>
    Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// One page of the organisation's exceptions, newest first, optionally filtered by
    /// <paramref name="state"/>. <paramref name="page"/> is 1-based and
    /// <paramref name="pageSize"/> is clamped to
    /// <see cref="OrderExceptionPaging.MinPageSize"/>..<see cref="OrderExceptionPaging.MaxPageSize"/>;
    /// the applied values come back on the result.
    /// </summary>
    Task<OrderExceptionPage> ListAsync(Guid orgId, string? state, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Every exception on ONE order. Unpaginated on purpose: an order's exception count is
    /// bounded by the number of distinct problem codes the pipeline can raise, not by how
    /// long the workspace has existed.
    /// </summary>
    Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>Returns false when the exception does not exist for this org.</summary>
    Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct);
    Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct);
}
