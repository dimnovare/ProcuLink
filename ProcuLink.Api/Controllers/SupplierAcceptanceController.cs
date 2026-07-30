using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
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
    private readonly IBillingService            _billing;

    public SupplierAcceptanceController(
        ISupplierAcceptanceService service,
        ICurrentTenantService tenant,
        IBillingService billing)
    {
        _service = service;
        _tenant  = tenant;
        _billing = billing;
    }

    /// <summary>
    /// 403 when the plan does not include per-supplier custom acceptance rules (the
    /// Enterprise "custom transformation rules"), else null. Authoring a new version and
    /// activating one are both gated; reading existing versions is not, so an org that
    /// downgrades can still see what its suppliers enforce.
    /// </summary>
    private async Task<IActionResult?> GateAsync(CancellationToken ct)
    {
        if (await _billing.HasFeatureAsync(_tenant.OrganisationId, BillingFeature.CustomSupplierRules, ct))
            return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = BillingGateErrors.RequiresPlan("custom_supplier_rules", BillingFeature.CustomSupplierRules),
            upgradeUrl = "/settings",
        });
    }

    [HttpGet]
    [ProducesResponseType(typeof(AcceptanceProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(Guid supplierId, CancellationToken ct)
    {
        var p = await _service.GetLatestAsync(_tenant.OrganisationId, supplierId, ct);
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
        if (await GateAsync(ct) is { } denied) return denied;

        var rules = (request.Rules ?? new List<AcceptanceRuleDto>())
            .Select(r => new AcceptanceRuleInput(r.Scope, r.FieldPath, r.Operator, r.ExpectedValue, r.Severity, r.BlockOnFail))
            .ToList();

        // Fail closed: reject unknown/typo'd operators at create time so a misconfigured rule can
        // never be persisted (the runtime evaluator now also fails closed on an unknown operator).
        var unknownOperators = rules
            .Where(r => !SupplierAcceptanceService.KnownOperators.Contains(r.Operator ?? string.Empty))
            .Select(r => r.Operator)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownOperators.Count > 0)
            return BadRequest(new { error = $"Unsupported acceptance rule operator(s): {string.Join(", ", unknownOperators)}." });

        var created = await _service.CreateVersionAsync(
            _tenant.OrganisationId, supplierId, request.Protocol, request.OutputFormat,
            rules, User?.FindFirst("sub")?.Value, ct);
        return Ok(ToDto(created));
    }

    [HttpPost("{versionNo:int}/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid supplierId, int versionNo, CancellationToken ct)
    {
        // Gated too: activating a stored version is how a rule set actually starts blocking
        // orders, so leaving it open would make the create gate cosmetic.
        if (await GateAsync(ct) is { } denied) return denied;

        return await _service.ActivateVersionAsync(_tenant.OrganisationId, supplierId, versionNo, ct)
            ? NoContent() : NotFound();
    }

    private static AcceptanceProfileDto ToDto(SupplierAcceptanceProfile p) => new(
        p.Id, p.SupplierId, p.VersionNo, p.Status, p.Protocol, p.OutputFormat,
        p.EffectiveFrom, p.EffectiveTo, p.CreatedAt,
        p.Rules.Select(r => new AcceptanceRuleDto(
            r.Id, r.Scope, r.FieldPath, r.Operator, r.ExpectedValue, r.Severity, r.BlockOnFail)).ToList());
}
