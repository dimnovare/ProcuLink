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

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OrderExceptionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? state, CancellationToken ct)
    {
        var rows = await _exceptions.ListAsync(_tenant.OrganisationId, state, ct);
        return Ok(rows.Select(ToDto));
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
