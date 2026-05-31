using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/suppliers/{supplierId:guid}/acceptance-profile")]
public sealed class SupplierAcceptanceController : ControllerBase
{
    private readonly ISupplierAcceptanceService _service;
    private readonly ICurrentTenantService      _tenant;

    public SupplierAcceptanceController(ISupplierAcceptanceService service, ICurrentTenantService tenant)
    {
        _service = service;
        _tenant  = tenant;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AcceptanceProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(Guid supplierId, CancellationToken ct)
    {
        var p = await _service.GetActiveAsync(_tenant.OrganisationId, supplierId, ct);
        return p is null ? NotFound() : Ok(ToDto(p));
    }

    [HttpGet("versions")]
    [ProducesResponseType(typeof(IReadOnlyList<AcceptanceProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListVersions(Guid supplierId, CancellationToken ct)
    {
        var versions = await _service.ListVersionsAsync(_tenant.OrganisationId, supplierId, ct);
        return Ok(versions.Select(ToDto));
    }

    [HttpPost]
    [ProducesResponseType(typeof(AcceptanceProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateVersion(
        Guid supplierId, [FromBody] CreateAcceptanceProfileRequest request, CancellationToken ct)
    {
        var rules = (request.Rules ?? new List<AcceptanceRuleDto>())
            .Select(r => new AcceptanceRuleInput(r.Scope, r.FieldPath, r.Operator, r.ExpectedValue, r.Severity, r.BlockOnFail))
            .ToList();
        var created = await _service.CreateVersionAsync(
            _tenant.OrganisationId, supplierId, request.Protocol, request.OutputFormat,
            rules, User?.FindFirst("sub")?.Value, ct);
        return Ok(ToDto(created));
    }

    [HttpPost("{versionNo:int}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid supplierId, int versionNo, CancellationToken ct)
        => await _service.ActivateVersionAsync(_tenant.OrganisationId, supplierId, versionNo, ct)
            ? NoContent() : NotFound();

    private static AcceptanceProfileDto ToDto(SupplierAcceptanceProfile p) => new(
        p.Id, p.VersionNo, p.Status, p.Protocol, p.OutputFormat,
        p.EffectiveFrom, p.EffectiveTo, p.CreatedAt,
        p.Rules.Select(r => new AcceptanceRuleDto(
            r.Id, r.Scope, r.FieldPath, r.Operator, r.ExpectedValue, r.Severity, r.BlockOnFail)).ToList());
}
