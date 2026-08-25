using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/exceptions")]
public sealed class ExceptionsController : ControllerBase
{
    private readonly IOrderExceptionService _exceptions;
    private readonly ICurrentTenantService  _tenant;

    public ExceptionsController(IOrderExceptionService exceptions, ICurrentTenantService tenant)
    {
        _exceptions = exceptions;
        _tenant     = tenant;
    }

    /// <summary>
    /// One page of the organisation's exceptions, newest first, optionally filtered by
    /// <paramref name="state"/> (<c>open</c> / <c>resolved</c> / <c>ignored</c>).
    ///
    /// <para><b>This used to return the whole history.</b> Exception rows accumulate
    /// monotonically — resolving or ignoring one flips its state and leaves the row — so the
    /// response size was a function of how long the account had existed, growing without
    /// limit from the first order onward. Paging is clamped to
    /// <see cref="OrderExceptionPaging.MinPageSize"/>..<see cref="OrderExceptionPaging.MaxPageSize"/>
    /// inside the service, matching the ceiling <c>GET /api/audit</c> uses.</para>
    ///
    /// <para><b>The body is still a bare JSON array, and must stay one.</b> The browser app
    /// reads it as <c>OrderException[]</c> and calls this endpoint with no paging parameters at
    /// all; wrapping the rows in an envelope would hand that caller an object where it expects
    /// an array, and it would render an empty page rather than fail visibly. So the total rides
    /// on <see cref="PaginationHeaders"/> instead, which is additive: the existing caller is
    /// untouched, and a caller that wants a pager has the number. The default page size is the
    /// clamp ceiling for the same reason — a smaller default would have truncated a live
    /// operator's work list on the day this shipped.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OrderExceptionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? state,
        [FromQuery] int page          = 1,
        [FromQuery] int pageSize      = OrderExceptionPaging.DefaultPageSize,
        CancellationToken ct          = default)
    {
        var result = await _exceptions.ListAsync(_tenant.OrganisationId, state, page, pageSize, ct);

        // The APPLIED window, read back off the result rather than echoed from the request, so a
        // caller that asked for pageSize=5000 is told the ceiling it actually got.
        Response.Headers[PaginationHeaders.TotalCount] = result.Total.ToString(CultureInfo.InvariantCulture);
        Response.Headers[PaginationHeaders.Page]       = result.Page.ToString(CultureInfo.InvariantCulture);
        Response.Headers[PaginationHeaders.PageSize]   = result.PageSize.ToString(CultureInfo.InvariantCulture);

        return Ok(result.Rows.Select(ToDto));
    }

    [HttpPatch("{id:guid}/resolve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(Guid id, CancellationToken ct)
        => await _exceptions.ResolveAsync(_tenant.OrganisationId, id, ct) ? NoContent() : NotFound();

    [HttpPatch("{id:guid}/ignore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Ignore(Guid id, CancellationToken ct)
        => await _exceptions.IgnoreAsync(_tenant.OrganisationId, id, ct) ? NoContent() : NotFound();

    private static OrderExceptionDto ToDto(Core.Entities.OrderException e) => new(
        e.Id, e.OrderId, e.LineId, e.Stage, e.Code, e.Severity, e.State, e.Message, e.CreatedAt, e.ResolvedAt);
}
